using System;
using System.Threading;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterPetitPlanet.GameTask.AutoPick;

public sealed class ItemNameRecognitionService : IDisposable
{
    private readonly IConfigService _configService;
    private readonly Func<IItemNameRecognizer> _factory;
    private readonly ILogger<AutoPickTrigger>? _logger;
    private IItemNameRecognizer? _recognizer;
    private int _busy;
    private int _disposed;

    public ItemNameRecognitionService(IConfigService configService, Func<IItemNameRecognizer> factory,
        ILogger<AutoPickTrigger>? logger = null)
    {
        _configService = configService;
        _factory = factory;
        _logger = logger;
    }

    // Called only after the original pickup key task has been submitted.
    // The caller owns the capture frame; the worker owns only its cropped clone.
    public bool TryQueue(Mat frame)
    {
        if (!Enabled || Volatile.Read(ref _disposed) != 0 ||
            Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return false;

        Mat? snapshot = null;
        try
        {
            if (!Enabled || Volatile.Read(ref _disposed) != 0 || frame.Empty())
            {
                ReleaseSlot();
                return false;
            }

            var area = ItemNameSearch.Calculate(frame.Size(), _configService.Config.AutoPick.ItemNameRecognition);
            if (area.Bounds.Width <= 0 || area.Bounds.Height <= 0)
            {
                ReleaseSlot();
                return false;
            }

            using var crop = new Mat(frame, area.Bounds);
            snapshot = crop.Clone();
            var ownedSnapshot = snapshot;
            _ = Task.Run(() => Process(ownedSnapshot, area));
            return true;
        }
        catch (Exception)
        {
            snapshot?.Dispose();
            ReleaseSlot();
            return false;
        }
    }

    private bool Enabled => _configService.Config.AutoPick.ItemNameRecognition?.Enabled == true;

    private void Process(Mat snapshot, ItemNameSearchArea area)
    {
        try
        {
            if (!Enabled || Volatile.Read(ref _disposed) != 0) return;
            _recognizer ??= _factory();
            var name = _recognizer.Recognize(snapshot, area).Trim();
            if (!string.IsNullOrWhiteSpace(name) && Enabled && Volatile.Read(ref _disposed) == 0)
                _logger?.LogInformation("【自动拾取】拾取到物品: {ItemName}", name);
        }
        catch (Exception)
        {
            // Auxiliary diagnostics must never produce pickup errors or retries on the capture thread.
        }
        finally
        {
            snapshot.Dispose();
            ReleaseSlot();
        }
    }

    private void ReleaseSlot()
    {
        Volatile.Write(ref _busy, 0);
        if (Volatile.Read(ref _disposed) != 0) DisposeIdleRecognizer();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        DisposeIdleRecognizer();
    }

    private void DisposeIdleRecognizer()
    {
        // Never block shutdown on inference or free a session still in use.
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
        catch (Exception)
        {
            // Shutdown must remain best-effort, just like recognition.
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }
}

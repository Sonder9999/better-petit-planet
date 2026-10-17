using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace BetterPetitPlanet.ViewModel.Windows;

public partial class MusicOverlaySettingsViewModel
{
    private DispatcherTimer? _autoSaveTimer;

    private void StartOrResetAutoSaveTimer()
    {
        if (_disposed || _dispatcher == null || _dispatcher.HasShutdownStarted) return;
        if (_autoSaveTimer == null)
        {
            _autoSaveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, OnAutoSaveTick, _dispatcher);
        }
        else
        {
            _autoSaveTimer.Stop();
        }
        _autoSaveTimer.Start();
    }

    private void StopAutoSaveTimer()
    {
        if (_autoSaveTimer == null) return;
        _autoSaveTimer.Stop();
    }

    private void OnAutoSaveTick(object? sender, EventArgs e)
    {
        StopAutoSaveTimer();
        if (_disposed || _isApplying) return;
        if (IsDirty && !HasValidationError)
        {
            _ = ApplyCoreAsync(isAutoSave: true);
        }
    }

    public void FlushAutoSave()
    {
        if (_disposed || _isApplying) return;
        if (_autoSaveTimer == null || !_autoSaveTimer.IsEnabled) return;
        StopAutoSaveTimer();
        if (!IsDirty || HasValidationError) return;

        try
        {
            string? error = ValidateDraft();
            if (error != null) return;

            var snapshot = CreateSnapshot();
            Task saveTask = _presenter != null
                ? _presenter.ApplySettingsAsync(snapshot)
                : _configService.SaveMusicOverlayAsync(snapshot);

            ExecuteSynchronously(saveTask);
            IsDirty = ComputeIsDirty();
            StatusMessage = "谱面设置已自动保存并生效";
        }
        catch
        {
            // Suppress flush error on dispose to prevent crash during shutdown
        }
    }

    public async Task FlushAutoSaveAsync()
    {
        if (_disposed) return;
        StopAutoSaveTimer();
        if (!IsDirty || HasValidationError) return;

        string? error = ValidateDraft();
        if (error != null) return;

        var snapshot = CreateSnapshot();
        if (_presenter != null)
            await _presenter.ApplySettingsAsync(snapshot);
        else
            await _configService.SaveMusicOverlayAsync(snapshot);

        if (_disposed) return;
        IsDirty = ComputeIsDirty();
        StatusMessage = "谱面设置已自动保存并生效";
    }

    private void ExecuteSynchronously(Task task)
    {
        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult();
            return;
        }

        if (_dispatcher != null && _dispatcher.CheckAccess() && !_dispatcher.HasShutdownStarted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
        }
        else
        {
            task.GetAwaiter().GetResult();
        }
    }
}

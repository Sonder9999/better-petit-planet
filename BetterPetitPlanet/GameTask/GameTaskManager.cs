using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameCapture;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterPetitPlanet.GameTask;

public sealed class GameTaskManager : IDisposable
{
    private readonly ILogger<GameTaskManager>? _logger;
    private readonly IConfigService _configService;
    private readonly GameProcessDetector _processDetector;
    private readonly TaskTriggerDispatcher _dispatcher;

    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private IGameCapture? _gameCapture;
    private bool _isRunning;

    public bool IsRunning => _isRunning;

    public event Action<bool>? StateChanged;

    public GameTaskManager(
        IConfigService configService,
        GameProcessDetector processDetector,
        TaskTriggerDispatcher dispatcher,
        ILogger<GameTaskManager>? logger = null)
    {
        _configService = configService;
        _processDetector = processDetector;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public bool Start()
    {
        if (_isRunning)
        {
            return true;
        }

        var hwnd = _processDetector.FindMainWindowHandle();
        if (hwnd == IntPtr.Zero)
        {
            _logger?.LogWarning("Target game window not found. Process: {ProcessName}", _processDetector.TargetProcessName);
            return false;
        }

        try
        {
            var captureMode = _configService.Config.General.CaptureMode.ToCaptureMode();

            _gameCapture = GameCaptureFactory.Create(captureMode);
            _gameCapture.Start(hwnd);

            _cts = new CancellationTokenSource();
            _isRunning = true;
            _captureTask = Task.Run(() => CaptureLoopAsync(hwnd, _cts.Token));

            _logger?.LogInformation("GameTaskManager started successfully. CaptureMode: {Mode}", captureMode);
            StateChanged?.Invoke(true);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start GameTaskManager");
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;
        _cts?.Cancel();

        try
        {
            _gameCapture?.Stop();
            _gameCapture?.Dispose();
            _gameCapture = null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Exception while stopping game capture");
        }

        _logger?.LogInformation("GameTaskManager stopped");
        StateChanged?.Invoke(false);
    }

    private async Task CaptureLoopAsync(IntPtr hwnd, CancellationToken token)
    {
        GameProcessDetector.EnsureDefaultDesktop();
        long frameCounter = 0;

        while (!token.IsCancellationRequested && _isRunning)
        {
            try
            {
                if (!_processDetector.IsGameRunning())
                {
                    _logger?.LogInformation("Game process terminated. Stopping task manager.");
                    Stop();
                    break;
                }

                if (_gameCapture != null)
                {
                    using var frame = _gameCapture.Capture();
                    if (frame != null && !frame.Frame.Empty())
                    {
                        frameCounter++;
                        if (frameCounter == 1 || frameCounter % 300 == 0)
                        {
                            _logger?.LogDebug("Capture active, processed frame {FrameCount} ({Width}x{Height})", frameCounter, frame.Frame.Width, frame.Frame.Height);
                        }

                        var clientSize = _processDetector.GetClientSize(hwnd);
                        using var content = new CaptureContent(frame.Frame.Clone(), hwnd, clientSize);
                        _dispatcher.Dispatch(content);
                    }
                }

                await Task.Delay(40, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in capture loop cycle");
                await Task.Delay(500, token);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}

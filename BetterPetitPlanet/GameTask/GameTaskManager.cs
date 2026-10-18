using System;
using System.Collections.Generic;
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

    private readonly HashSet<string> _activeConsumers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _consumerLock = new();

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

    /// <summary>
    /// 多消费者会话声明：指定消费者（如 "AutoPick" 或 "Music"）请求保持画面捕获。
    /// 只要有任一消费者持有活跃会话，底层截图通道即持续运转；各取所需，互不干扰。
    /// </summary>
    public bool AcquireSession(string consumerId)
    {
        lock (_consumerLock)
        {
            _activeConsumers.Add(consumerId);
            _logger?.LogInformation("Consumer {Consumer} acquired capture session. Active count: {Count}", consumerId, _activeConsumers.Count);
            if (!_isRunning)
            {
                return Start();
            }
            return true;
        }
    }

    /// <summary>
    /// 释放指定消费者的截图会话。
    /// 仅当所有消费者均释放（计数归零）时，底层截图会话才会真正关闭，绝不破坏其他任务（如停止音乐绝不破坏自动拾取）。
    /// </summary>
    public void ReleaseSession(string consumerId)
    {
        lock (_consumerLock)
        {
            _activeConsumers.Remove(consumerId);
            _logger?.LogInformation("Consumer {Consumer} released capture session. Remaining count: {Count}", consumerId, _activeConsumers.Count);
            if (_activeConsumers.Count == 0 && _isRunning)
            {
                StopInternal();
            }
        }
    }

    public bool IsConsumerActive(string consumerId)
    {
        lock (_consumerLock)
        {
            return _activeConsumers.Contains(consumerId);
        }
    }

    public bool Start()
    {
        lock (_consumerLock)
        {
            _activeConsumers.Add("Default");
        }

        if (_isRunning)
        {
            return true;
        }

        var hwnd = _processDetector.FindMainWindowHandle();
        return Start(hwnd);
    }

    /// <summary>Starts capture using the latest non-blocking cached game-window snapshot.</summary>
    public bool StartWithCachedTarget()
    {
        lock (_consumerLock)
        {
            _activeConsumers.Add("Default");
        }

        if (_isRunning) return true;
        return Start(_processDetector.FindMainWindowHandleCached());
    }

    private bool Start(IntPtr hwnd)
    {
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

    /// <summary>
    /// 全局显式停止所有捕获（例如用户在主界面手动关闭或应用退出）。
    /// </summary>
    public void Stop()
    {
        lock (_consumerLock)
        {
            _activeConsumers.Clear();
        }
        StopInternal();
    }

    private void StopInternal()
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
                if (!_processDetector.IsGameRunningCached())
                {
                    _logger?.LogInformation("Game process terminated. Stopping task manager.");
                    Stop();
                    break;
                }

                if (_gameCapture != null && _gameCapture.IsCapturing)
                {
                    var clientSize = _processDetector.GetClientSize(hwnd);
                    var windowSize = new OpenCvSharp.Size(clientSize.Width, clientSize.Height);

                    // 使用按需 GPU ROI 直读的 CaptureContent，杜绝每帧 14.7MB 的全屏无谓映射与复制
                    using var content = new CaptureContent(
                        frameFactory: () => _gameCapture?.Capture(),
                        roiCaptureFunc: roi => _gameCapture?.CaptureRoi(roi),
                        hwnd: hwnd,
                        clientRect: clientSize,
                        windowSize: windowSize);

                    if (content.IsValid)
                    {
                        frameCounter++;
                        if (frameCounter == 1 || frameCounter % 300 == 0)
                        {
                            _logger?.LogDebug("Capture active, processed frame {FrameCount} ({Width}x{Height})", frameCounter, clientSize.Width, clientSize.Height);
                        }

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

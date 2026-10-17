using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Rendering.Gpu;
using BetterPetitPlanet.GameTask.Music.Service;
using BetterPetitPlanet.GameTask.Music.Skins;
using BetterPetitPlanet.View.Windows;
using Microsoft.Extensions.Logging;

namespace BetterPetitPlanet.GameTask.Music.Overlay;

public sealed partial class MusicOverlayPresenter : IDisposable
{
    private readonly IConfigService _config;
    private readonly MusicSessionCoordinator _session;
    private readonly GameProcessDetector _process;
    private readonly GameWindowTracker _tracker;
    private readonly MusicSkinRepository _skinRepository;
    private readonly Func<string, MusicSkinDecodeSize, CancellationToken, Task<MusicSkinLoadResult>> _loadSkin;
    private readonly ILogger<MusicOverlayPresenter>? _logger;
    private readonly Func<MusicOverlayWindow> _windowFactory;
    private readonly Dispatcher? _dispatcher;
    private readonly DispatcherTimer? _timer;
    private readonly MusicOverlayWindowCreation _creation;
    private readonly MusicLayoutAdjustmentControl _adjustment;

    private MusicWindowEvents? _windowEvents;
    private MusicOverlayWindow? _window;
    private bool _failed;
    private bool _disposed;
    private bool _started;
    private string? _renderError;
    private PerformanceTimeline? _lastTimeline;
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private string? _pendingSkinId;
    private string? _pendingSkinDiagnostic;
    private string? _activeSkinDiagnostic;
    private MusicSkin? _activeSkin;
    private string _activeSkinId;
    private long _skinRevision;
    private long? _loadingRevision;
    private MusicSkinDecodeSize? _activeDecodeSize;
    private MusicSkinDecodeSize? _loadingDecodeSize;
    private CancellationTokenSource? _skinLoadCancellation;
    private bool _skinResourcesVisible;

    public bool Adjusting => _adjustment.Adjusting;
    public bool IsFailed => _failed;
    public string? RenderError => _renderError;
    public string? SkinDiagnostic => _pendingSkinDiagnostic == null ? _activeSkinDiagnostic :
        (_activeSkinDiagnostic == null ? "" : _activeSkinDiagnostic + Environment.NewLine) + "待切换：" + _pendingSkinDiagnostic;
    public MusicOverlayWindow? Window => _window;
    public MusicLayoutAdjustmentControl AdjustmentControl => _adjustment;

    public event Action? Changed;

    public MusicOverlayPresenter(
        IConfigService config,
        MusicSessionCoordinator session,
        GameProcessDetector process,
        ILogger<MusicOverlayPresenter>? logger = null,
        Func<MusicOverlayWindow>? windowFactory = null,
        GameWindowTracker? tracker = null,
        Dispatcher? dispatcher = null,
        MusicSkinRepository? skinRepository = null,
        Func<string, Task<MusicSkin>>? skinLoader = null,
        Func<IMusicOverlayGpuBackend>? gpuBackendFactory = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _logger = logger;
        _gpuBackendFactory = gpuBackendFactory ?? (() => new MusicOverlayGpuBackend());
        _windowFactory = windowFactory ?? (() => new MusicOverlayWindow());
        _tracker = tracker ?? new GameWindowTracker(process);
        _skinRepository = skinRepository ?? new MusicSkinRepository();
        _loadSkin = skinLoader == null
            ? (id, size, token) => _skinRepository.LoadSkinResultAsync(id, size, token)
            : async (id, _, _) => new MusicSkinLoadResult(id, await skinLoader(id));
        _activeSkinId = _config.Config.MusicOverlay.SkinId;

        _creation = new MusicOverlayWindowCreation(CreateWindowCoreAsync);
        _adjustment = new MusicLayoutAdjustmentControl(
            pauseAndRelease: () =>
            {
                _session.CancelTimelineStart();
                _session.Suspend("调整布局");
                _session.Playback.Pause();
            },
            activate: () =>
            {
                _config.Config.MusicOverlay.Enabled = true;
                if (!_process.IsGameForegroundCached())
                {
                    _process.ActivateGameWindow();
                }
            },
            ensureWindow: CreateWindowAsync,
            setInteractive: adjusting =>
            {
                _session.SetAdjusting(adjusting);
                _window?.SetAdjusting(adjusting);
            },
            save: async () =>
            {
                _config.Config.MusicOverlay.Validate();
                await _config.SaveAsync();
                Tick();
            });

        _dispatcher = dispatcher ?? Dispatcher.FromThread(Thread.CurrentThread) ?? Dispatcher.CurrentDispatcher;
        if (_dispatcher != null && !_dispatcher.HasShutdownStarted)
        {
            int interval = Math.Min(_config.Config.MusicOverlay.WindowPollIntervalMs, _config.Config.MusicOverlay.SnapshotIntervalMs);
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(interval), DispatcherPriority.Normal, (_, _) => Tick(), _dispatcher);
        }
    }

    public void Start()
    {
        if (_disposed || _started) return;
        _started = true;

        _windowEvents = new MusicWindowEvents();
        _windowEvents.Changed += OnWindowEventsChanged;

        _session.SessionChanged += OnSession;
        _session.InputObserved += OnInputObserved;
        _session.Changed += OnSessionStateChanged;

        _timer?.Start();
        Tick();
    }

    public Task SetAdjustingAsync(bool adjusting) =>
        _disposed ? Task.CompletedTask : _adjustment.SetAsync(adjusting);

    public async Task ToggleEnabledAsync()
    {
        if (_disposed) return;
        _config.Config.MusicOverlay.Enabled = !_config.Config.MusicOverlay.Enabled;
        ResetRendererFailure();
        await _config.SaveAsync();
        Tick();
        Changed?.Invoke();
    }

    public async Task<string?> ApplySettingsAsync(MusicOverlayConfig settings)
    {
        var snapshot = settings.Clone();
        snapshot.Validate();
        await _settingsGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _config.SaveMusicOverlayAsync(snapshot);
            // Persistence is committed. Rendering failures must never be reported as failed saves.
            if (_disposed) return "谱面设置已保存；谱面窗口已关闭";
            try
            {
                ResetRendererFailure();
                var size = DecodeSize(_tracker.Sample());
                var result = await LoadSkinResultAsync(snapshot.SkinId, size, CancellationToken.None);
                if (_disposed) return "谱面设置已保存；谱面窗口已关闭";
                // Preparation uses PlayerState.Playing with a negative timeline position.
                if (_session.Playback.State == PlayerState.Playing)
                {
                    _pendingSkinId = snapshot.SkinId;
                    _pendingSkinDiagnostic = result.Diagnostic;
                }
                else
                    ApplySkin(snapshot.SkinId, result.Skin, size, result.Diagnostic);
                if (_window != null)
                {
                    var committed = _config.Config.MusicOverlay;
                    _window.PlayfieldControl.LayoutConfig = committed.Layout;
                    _window.PlayfieldControl.EffectsConfig = committed.Effects;
                    _window.PlayfieldControl.FallDurationMs = committed.FallDurationMs;
                    _window.PlayfieldControl.VisualOffsetMs = committed.VisualOffsetMs;
                    _window.PlayfieldControl.InvalidateTracks();
                    _window.PlayfieldControl.UpdateSnapshot(_window.PlayfieldControl.CurrentSnapshot);
                }
                Tick();
                Changed?.Invoke();
                if (_failed) return "谱面设置已保存；视图应用失败：" + _renderError;
                string? status = _pendingSkinId != null ? "谱面设置已保存；皮肤将在暂停或停止后生效" : null;
                return result.Diagnostic == null ? status : (status ?? "谱面设置已保存") + "；" + result.Diagnostic;
            }
            catch (Exception ex)
            {
                _failed = true;
                _renderError = ex.Message;
                try { _window?.Hide(); } catch { }
                ReleaseSkinResources();
                return "谱面设置已保存；视图应用失败：" + ex.Message;
            }
        }
        finally { _settingsGate.Release(); }
    }

    private void ApplySkin(string requestedId, MusicSkin? skin = null, MusicSkinDecodeSize? size = null, string? diagnostic = null)
    {
        _activeSkin = skin;
        _activeSkinId = requestedId;
        _activeDecodeSize = size;
        _activeSkinDiagnostic = diagnostic;
        InvalidateSkinLoad();
        _pendingSkinId = null;
        _pendingSkinDiagnostic = null;
        if (skin == null) _window?.PlayfieldControl.ReleaseSkinResources();
        if (_window?.IsVisible == true && skin != null) _window.PlayfieldControl.SetSkin(skin);
    }

    private void InvalidateSkinLoad()
    {
        _skinRevision++;
        _loadingRevision = null;
        _skinLoadCancellation?.Cancel();
        _skinLoadCancellation?.Dispose();
        _skinLoadCancellation = null;
    }

    private void ReleaseSkinResources()
    {
        if (_skinResourcesVisible || _activeSkin != null || _loadingRevision != null) InvalidateSkinLoad();
        _skinResourcesVisible = false;
        _activeSkin = null;
        _activeDecodeSize = null;
        _window?.PlayfieldControl.ReleaseSkinResources();
    }

    private static MusicSkinDecodeSize DecodeSize(GameWindowState game)
    {
        // Tracker bounds are already physical pixels (DIP × monitor DPI / 96).
        var maximum = MusicSkinDecodeSize.Overlay;
        if (!game.Exists) return maximum;
        return new((int)Math.Clamp(Math.Ceiling(game.PhysicalBounds.Width), 1, maximum.Width),
            (int)Math.Clamp(Math.Ceiling(game.PhysicalBounds.Height), 1, maximum.Height));
    }

    private async Task<MusicSkinLoadResult> LoadSkinResultAsync(string id, MusicSkinDecodeSize size, CancellationToken token)
    {
        try { return await _loadSkin(id, size, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(id, MusicSkin.Default, $"皮肤 '{id}' 无法加载，已使用原生默认：{ex.Message}"); }
    }

    private async Task RestoreSkinAsync(MusicSkinDecodeSize size)
    {
        var window = _window;
        bool isGpu = _gpuVisible || _config.Config.MusicOverlay.Backend == MusicOverlayBackend.Gpu;
        if (_disposed) return;
        if (!isGpu && (!_skinResourcesVisible || window == null)) return;
        if (!isGpu)
        {
            var control = window!.PlayfieldControl;
            if (_activeSkin != null && ReferenceEquals(control.Skin, MusicSkin.Default) &&
                control.SkinRenderDiagnostic is { } diagnostic)
            {
                // A lazy frame decode can fail after loading. Keep the fallback until an
                // explicit selection or hide/show reload, including across size changes.
                bool changed = !ReferenceEquals(_activeSkin, MusicSkin.Default) || _activeSkinDiagnostic != diagnostic;
                if (changed) InvalidateSkinLoad();
                _activeSkin = MusicSkin.Default;
                _activeDecodeSize = size;
                _activeSkinDiagnostic = diagnostic;
                if (changed) Changed?.Invoke();
                return;
            }
            if (_activeSkin != null && _activeDecodeSize == size)
            {
                if (!ReferenceEquals(window!.PlayfieldControl.Skin, _activeSkin)) window.PlayfieldControl.SetSkin(_activeSkin);
                return;
            }
        }
        else
        {
            if (_activeSkin != null && _activeDecodeSize == size &&
                string.Equals(_activeSkin.Id, _activeSkinId, StringComparison.OrdinalIgnoreCase))
                return;
        }

        if (_loadingRevision == _skinRevision && _loadingDecodeSize == size) return;
        InvalidateSkinLoad();
        long revision = _skinRevision;
        _loadingRevision = revision;
        _loadingDecodeSize = size;
        var cancellation = _skinLoadCancellation = new CancellationTokenSource();
        try
        {
            var result = await LoadSkinResultAsync(_activeSkinId, size, cancellation.Token);
            if (_disposed || revision != _skinRevision) return;
            if (!isGpu && (!_skinResourcesVisible || _window != window)) return;
            _activeSkin = result.Skin;
            _activeDecodeSize = size;
            _activeSkinDiagnostic = result.Diagnostic;
            if (_window != null && _skinResourcesVisible) window?.PlayfieldControl.SetSkin(result.Skin);
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_disposed || revision != _skinRevision) return;
            _failed = true;
            _renderError = ex.Message;
            _logger?.LogError(ex, "Music skin presentation failed; playback remains independent");
            try { window?.Hide(); } catch { }
            ReleaseSkinResources();
        }
        finally
        {
            if (revision == _skinRevision) _loadingRevision = null;
        }
    }

    public void ResetRendererFailure()
    {
        _gpuFailure = null;
        if (!_failed) return;
        ReleaseSkinResources();
        try
        {
            _window?.Close();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Exception closing window during renderer failure reset");
        }
        _window = null;
        _failed = false;
        _renderError = null;
    }

    public void Tick()
    {
        using var diagnostic = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.PresenterExecuteMs);
        if (_disposed) return;
        try
        {
            if (_pendingSkinId != null && _session.Playback.State != PlayerState.Playing)
                ApplySkin(_pendingSkinId, diagnostic: _pendingSkinDiagnostic);
            var game = _tracker.Sample();
            var config = _config.Config.MusicOverlay;

            bool visible = !_failed && config.Enabled &&
                ((_session.Ready && !_session.Adjusting && game.Exists && game.Foreground) ||
                 (_session.Adjusting && game.Exists && game.Foreground));

            if (TryUseGpu(game, config, visible))
            {
                return;
            }

            if (visible && _window == null && !_creation.IsPending && !_failed)
            {
                _ = CreateWindowAsync();
            }

            if (_window != null)
            {
                if (visible)
                {
                    GameWindowTracker.Position(_window, game);
                    _window.EditableLayout = config.Layout;
                    _window.PlayfieldControl.LayoutConfig = config.Layout;
                    _window.PlayfieldControl.EffectsConfig = config.Effects;
                    _window.PlayfieldControl.FallDurationMs = config.FallDurationMs;
                    _window.PlayfieldControl.VisualOffsetMs = config.VisualOffsetMs;

                    if (!_window.IsVisible)
                    {
                        _window.Show();
                        var timeline = _session.Playback.CurrentTimeline;
                        _lastTimeline = timeline;
                        _window.PlayfieldControl.SetChart(timeline);
                        var snapshot = _session.Playback.CaptureSnapshot();
                        _window.PlayfieldControl.UpdateSnapshot(CreateRenderSnapshot(snapshot));
                    }
                    _skinResourcesVisible = true;
                    _ = RestoreSkinAsync(DecodeSize(game));
                }
                else
                {
                    if (_window.IsVisible) _window.Hide();
                    ReleaseSkinResources();
                }
            }
            else if (!visible) ReleaseSkinResources();
        }
        catch (Exception ex)
        {
            _failed = true;
            _renderError = ex.Message;
            _logger?.LogError(ex, "Music overlay update failed; playback remains independent");
            try
            {
                _window?.Hide();
            }
            catch { }
            ReleaseSkinResources();
        }
    }

    private Task CreateWindowAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (_creation.IsPending) return _creation.EnsureAsync();
        return _window != null ? Task.CompletedTask : _creation.EnsureAsync();
    }

    private Task CreateWindowCoreAsync()
    {
        try
        {
            if (_disposed) return Task.CompletedTask;
            var window = _windowFactory();
            _window = window;
            var overlayConfig = _config.Config.MusicOverlay;
            _window.EditableLayout = overlayConfig.Layout;
            _window.PlayfieldControl.LayoutConfig = overlayConfig.Layout;
            _window.PlayfieldControl.EffectsConfig = overlayConfig.Effects;
            _window.PlayfieldControl.FallDurationMs = overlayConfig.FallDurationMs;
            _window.PlayfieldControl.VisualOffsetMs = overlayConfig.VisualOffsetMs;

            _window.LayoutMoved += () =>
            {
                if (_window?.EditableLayout != null)
                {
                    _config.Config.MusicOverlay.Layout = _window.EditableLayout;
                    _window.PlayfieldControl.LayoutConfig = _window.EditableLayout;
                    _window.PlayfieldControl.InvalidateTracks();
                    _window.PlayfieldControl.UpdateSnapshot(_window.PlayfieldControl.CurrentSnapshot);
                }
            };

            if (_disposed)
            {
                _window?.Close();
                _window = null;
            }
        }
        catch (Exception ex)
        {
            _failed = true;
            _renderError = ex.Message;
            try
            {
                _window?.Close();
            }
            catch { }
            _window = null;
            _logger?.LogError(ex, "Music overlay window creation failed; playback remains independent");
        }
        return Task.CompletedTask;
    }

    public MusicRenderSnapshot CreateRenderSnapshot(MusicSessionSnapshot sessionSnapshot)
    {
        var autoKeys = sessionSnapshot.ActiveKeys ?? Array.Empty<string>();
        var manualKeys = _session.InputObserver.ActiveKeys ?? Array.Empty<string>();
        var allKeys = autoKeys.Union(manualKeys).ToArray();

        bool isPlaying = string.Equals(sessionSnapshot.State, "playing", StringComparison.OrdinalIgnoreCase);
        bool isPreparing = string.Equals(sessionSnapshot.State, "preparing", StringComparison.OrdinalIgnoreCase);
        int noteCount = _session.Playback.CurrentTimeline?.Events.Count ?? 0;

        long currentMonotonic = (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

        return new MusicRenderSnapshot(
            SessionId: sessionSnapshot.SessionId,
            Revision: sessionSnapshot.Revision,
            PositionMs: sessionSnapshot.PositionMs,
            HostMonotonicMs: sessionSnapshot.HostMonotonicMs,
            MonotonicTimestamp: currentMonotonic,
            PlaybackSpeed: sessionSnapshot.PlaybackSpeed,
            IsPlaying: isPlaying,
            IsPreparing: isPreparing,
            NoteCount: noteCount,
            ActiveAutomaticKeys: autoKeys,
            ActiveManualKeys: manualKeys,
            ActiveKeys: allKeys);
    }

    private void Post(Action action, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        if (_disposed) return;
        if (_dispatcher == null || _dispatcher.CheckAccess())
        {
            using var operation = new MusicOverlayDiagnostics.UiOperation(MusicOverlayDiagnostics.Metric.PresenterActionMs, "Presenter", caller, 0);
            action();
            return;
        }

        if (!_dispatcher.HasShutdownStarted)
        {
            long diagnosticPosted = MusicOverlayDiagnostics.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            _dispatcher.BeginInvoke(() =>
            {
                using var operation = new MusicOverlayDiagnostics.UiOperation(MusicOverlayDiagnostics.Metric.PresenterActionMs, "Presenter", caller, diagnosticPosted);
                if (diagnosticPosted != 0) MusicOverlayDiagnostics.Record(MusicOverlayDiagnostics.Metric.PresenterQueueMs,
                    (System.Diagnostics.Stopwatch.GetTimestamp() - diagnosticPosted) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                if (!_disposed) action();
            });
        }
    }

    private void OnWindowEventsChanged() => Post(Tick);

    private void OnSessionStateChanged() => Post(Tick);

    private void OnSession(MusicSessionSnapshot snapshot) => Post(() =>
    {
        if (!_disposed && !_failed && TryPublishGpu(snapshot)) return;
        if (_disposed || _failed || _window == null || !_window.IsVisible) return;
        try
        {
            var timeline = _session.Playback.CurrentTimeline;
            if (_lastTimeline != timeline)
            {
                _lastTimeline = timeline;
                _window.PlayfieldControl.SetChart(timeline);
            }

            var renderSnapshot = CreateRenderSnapshot(snapshot);
            _window.PlayfieldControl.UpdateSnapshot(renderSnapshot);
        }
        catch (Exception ex)
        {
            _failed = true;
            _renderError = ex.Message;
            _logger?.LogError(ex, "Failed to render music snapshot; playback remains independent");
            try
            {
                _window?.Hide();
            }
            catch { }
            ReleaseSkinResources();
        }
    });

    private void OnInputObserved(MusicInputEvent? input)
    {
        if (input != null && string.Equals(input.Action, "down", StringComparison.OrdinalIgnoreCase))
        {
            int lane = !string.IsNullOrEmpty(input.LaneId) ? MusicPlayfieldGeometry.GetLaneIndex(input.LaneId) : -1;
            if (lane >= 0)
            {
                var hit = new MusicGpuHitEvent(
                    LaneKey: input.LaneId ?? MusicPlayfieldGeometry.LaneKeys[lane],
                    LaneIndex: lane,
                    ChartTimeMs: input.ChartTimeMs,
                    HostMonotonicMs: input.HostMonotonicMs,
                    NoteId: input.NoteId,
                    Source: input.Source ?? "automatic");
                _gpuBackend?.PostHitEvent(hit);
            }
        }

        Post(() =>
        {
            if (!_disposed && !_failed && TryPublishGpu()) return;
            if (_disposed || _failed || _window == null || !_window.IsVisible) return;
            try
            {
                var snapshot = _session.Playback.CaptureSnapshot();
                var renderSnapshot = CreateRenderSnapshot(snapshot);
                _window.PlayfieldControl.UpdateSnapshot(renderSnapshot);
            }
            catch (Exception ex)
            {
                _failed = true;
                _renderError = ex.Message;
                _logger?.LogError(ex, "Failed to update input visual; playback remains independent");
                try
                {
                    _window?.Hide();
                }
                catch { }
                ReleaseSkinResources();
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseGpuBackend();
        _pendingSkinId = null;
        ReleaseSkinResources();
        InvalidateSkinLoad();

        _adjustment.Dispose();
        _windowEvents?.Dispose();
        _windowEvents = null;
        _timer?.Stop();

        _session.SessionChanged -= OnSession;
        _session.InputObserved -= OnInputObserved;
        _session.Changed -= OnSessionStateChanged;

        if (_window != null)
        {
            try
            {
                _window.Close();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error closing music overlay window on dispose");
            }
            _window = null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.View.Controls.Music;

/// <summary>
/// High-performance native WPF FrameworkElement rendering 7-track rhythm game playfield
/// using layered DrawingVisuals managed by a VisualCollection.
/// Dispatches frame updates via CompositionTarget.Rendering ONLY when visible and actively playing/animating,
/// eliminating all idle CPU spin when paused or hidden. Supports skin packages and smooth hit effect fadeout.
/// </summary>
public class MusicPlayfieldControl : FrameworkElement
{
    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _tracksVisual = new();
    private readonly DrawingVisual _notesVisual = new();
    private readonly DrawingVisual _feedbackVisual = new();
    private readonly Func<double> _monotonicClock;
    private readonly Action<EventHandler> _subscribeRendering;
    private readonly Action<EventHandler> _unsubscribeRendering;
    private readonly Func<string, bool> _diagnosticSimpleScene;
    private static readonly Brush DiagnosticBackground = CreateDiagnosticBackground();
    private static Brush CreateDiagnosticBackground()
    {
        var brush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        brush.Freeze();
        return brush;
    }

    private readonly MusicOverlayDiagnostics.ControlProbe? _diagnostic = MusicOverlayDiagnostics.RegisterControl();
    private bool _tracksDirty = true;
    private bool _isRenderingHooked;
    private bool _isLoaded;
    private bool _wasLoaded;
    private bool _resourcesReleased;
    private double _lastWidth;
    private double _lastHeight;
    private double _lastPixelsPerDip;

    private readonly MusicEffectTimeline _effectTimeline = new();
    private readonly Dictionary<PerformanceEvent, HoldEffectRenderer> _holdRenderers = new();
    private readonly Dictionary<PerformanceEvent, ReferenceCompiledHoldRenderer> _compiledHoldRenderers = new();
    private readonly Dictionary<MusicEffectTimeline.Hit, ReferenceHitRenderer> _referenceHitRenderers = new();

    public MusicOverlayLayoutConfig LayoutConfig { get; set; } = new();
    public MusicOverlayEffectsConfig EffectsConfig { get; set; } = new();
    public MusicChartIndex? ChartIndex { get; set; }
    public int FallDurationMs { get; set; } = 3000;
    public int VisualOffsetMs { get; set; }
    public MusicSkin Skin { get; set; } = MusicSkin.Default;
    public string? SkinRenderDiagnostic { get; private set; }
    public MusicRenderSnapshot CurrentSnapshot { get; private set; } = MusicRenderSnapshot.Empty;

    public MusicPlayfieldControl() : this(
        () => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency,
        handler => CompositionTarget.Rendering += handler,
        handler => CompositionTarget.Rendering -= handler)
    {
    }

    internal MusicPlayfieldControl(Func<double> monotonicClock, Action<EventHandler> subscribeRendering,
        Action<EventHandler> unsubscribeRendering, Func<string, bool>? diagnosticSimpleScene = null)
    {
        _monotonicClock = monotonicClock;
        _subscribeRendering = subscribeRendering;
        _unsubscribeRendering = unsubscribeRendering;
        _diagnosticSimpleScene = diagnosticSimpleScene ?? (_ => false);
        _visuals = new VisualCollection(this)
        {
            _tracksVisual,
            _notesVisual,
            _feedbackVisual
        };
        ClipToBounds = true;

        Loaded += (_, _) =>
        {
            _wasLoaded = true;
            _isLoaded = true;
            _resourcesReleased = false;
            UpdateSnapshot(CurrentSnapshot);
        };
        Unloaded += (_, _) =>
        {
            _isLoaded = false;
            ReleaseSkinResources();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) ReleaseSkinResources();
            else
            {
                _resourcesReleased = false;
                UpdateSnapshot(CurrentSnapshot);
            }
        };
    }

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _tracksDirty = true;
        UpdateRenderingHook();

        if (ActualWidth > 0 && ActualHeight > 0)
        {
            RenderSnapshot(CurrentSnapshot, ActualWidth, ActualHeight);
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _tracksDirty = true;
        UpdateSnapshot(CurrentSnapshot);
    }

    public void InvalidateTracks()
    {
        _tracksDirty = true;
    }

    public void SetSkin(MusicSkin? skin)
    {
        if (_wasLoaded && (!_isLoaded || !IsVisible)) return;
        _resourcesReleased = false;
        _holdRenderers.Clear();
        _compiledHoldRenderers.Clear();
        _referenceHitRenderers.Clear();
        Skin = skin ?? MusicSkin.Default;
        SkinRenderDiagnostic = null;
        _tracksDirty = true;

        double w = ActualWidth > 0 ? ActualWidth : (_lastWidth > 0 ? _lastWidth : Width);
        double h = ActualHeight > 0 ? ActualHeight : (_lastHeight > 0 ? _lastHeight : Height);
        if (w > 0 && h > 0)
        {
            RenderSnapshot(CurrentSnapshot, w, h);
        }
    }

    /// <summary>Release both the model and retained WPF drawing commands; the owner retains only the requested ID.</summary>
    public void ReleaseSkinResources()
    {
        UnhookRendering();
        _resourcesReleased = true;
        if (_diagnostic != null) { _diagnostic.Released = true; _diagnostic.Loaded = _isLoaded; _diagnostic.Visible = IsVisible; }
        Skin = MusicSkin.Default;
        SkinRenderDiagnostic = null;
        _tracksDirty = true;
        _effectTimeline.Clear();
        _holdRenderers.Clear();
        _compiledHoldRenderers.Clear();
        _referenceHitRenderers.Clear();
        using (_tracksVisual.RenderOpen()) { }
        using (_notesVisual.RenderOpen()) { }
        using (_feedbackVisual.RenderOpen()) { }
    }

    public void SetChart(PerformanceTimeline? timeline)
    {
        if (_diagnostic != null) _diagnostic.ChartFile = timeline?.FilePath ?? "";
        SetChartIndex(timeline != null ? new MusicChartIndex(timeline) : null);
    }

    public void SetChartIndex(MusicChartIndex? chartIndex)
    {
        ChartIndex = chartIndex;
        _tracksDirty = true;
        _effectTimeline.Clear();
        _holdRenderers.Clear();
        _compiledHoldRenderers.Clear();
        _referenceHitRenderers.Clear();
    }

    public void UpdateSnapshot(MusicRenderSnapshot snapshot)
    {
        using var diagnostic = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.SnapshotDrawMs);
        if (_diagnostic != null) System.Threading.Interlocked.Increment(ref _diagnostic.SnapshotDraws);
        AcceptSnapshot(snapshot);

        double w = ActualWidth > 0 ? ActualWidth : (_lastWidth > 0 ? _lastWidth : Width);
        double h = ActualHeight > 0 ? ActualHeight : (_lastHeight > 0 ? _lastHeight : Height);

        // State changes must be drawn even when playback is paused or a frame is pending.
        if (w > 0 && h > 0)
        {
            RenderSnapshot(CurrentSnapshot, w, h);
        }
        else
        {
            UpdateRenderingHook();
        }
    }

    /// <summary>
    /// Explicitly renders a single frame snapshot with custom dimensions.
    /// </summary>
    public void RenderSnapshot(MusicRenderSnapshot snapshot, double width, double height)
    {
        try { RenderSnapshotCore(snapshot, width, height); }
        catch (Exception ex) when (Skin.Reference != null && ex is IOException or FormatException or NotSupportedException)
        {
            // All DrawingContexts from the failed frame have exited before re-rendering.
            SkinRenderDiagnostic = $"皮肤动画资源无法读取，已使用原生默认：{ex.Message}";
            Trace.TraceWarning(SkinRenderDiagnostic);
            Skin = MusicSkin.Default;
            _tracksDirty = true;
            _holdRenderers.Clear(); _compiledHoldRenderers.Clear(); _referenceHitRenderers.Clear();
            RenderSnapshotCore(snapshot, width, height);
        }
    }

    private void RenderSnapshotCore(MusicRenderSnapshot snapshot, double width, double height)
    {
        using var diagnostic = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.RenderCoreMs);
        if (_diagnostic != null) System.Threading.Interlocked.Increment(ref _diagnostic.CoreCalls);
        AcceptSnapshot(snapshot);
        if (_resourcesReleased || width <= 0 || height <= 0)
        {
            UpdateRenderingHook();
            return;
        }

        double now = _monotonicClock();
        double displayTime = snapshot.ComputeCurrentDisplayTimeMs(now, VisualOffsetMs);
        _diagnostic?.Display(snapshot, now, displayTime);
        if (_diagnosticSimpleScene(snapshot.SessionId))
        {
            // Same HWND, snapshot clock and Rendering/UpdateSnapshot entry points.
            // Only drawing content is substituted; no second clock or frame timer.
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (_diagnostic != null) { _diagnostic.Width = width; _diagnostic.Height = height; _diagnostic.Dpi = dpi; _diagnostic.Simple = true; }
            _lastWidth = width; _lastHeight = height; _lastPixelsPerDip = dpi;
            _tracksDirty = true;
            _tracksVisual.Opacity = _notesVisual.Opacity = 1;
            using (var dc = _tracksVisual.RenderOpen())
                dc.DrawRectangle(DiagnosticBackground, null, new Rect(0, 0, width, height));
            using (var dc = _notesVisual.RenderOpen())
            {
                double phase = ((displayTime % 4000) + 4000) % 4000 / 4000;
                dc.DrawRectangle(Brushes.Cyan, null, new Rect(phase * (width - 12), 0, 12, height));
            }
            using (var dc = _feedbackVisual.RenderOpen()) { }
            UpdateRenderingHook();
            return;
        }
        if (_diagnostic != null) _diagnostic.Simple = false;
        bool hasChart = ChartIndex is { NoteCount: > 0 };
        double duration = HitBurstEffectRenderer.Duration(Skin);
        var visibleNotes = hasChart ? ChartIndex!.GetVisibleNotes(displayTime - duration, displayTime + FallDurationMs)
            : Array.Empty<PerformanceEvent>();
        double effectTime = hasChart ? displayTime : now;
        _effectTimeline.Update(snapshot, visibleNotes, hasChart, effectTime, duration);
        var activeKeys = new HashSet<string>(snapshot.ActiveKeys, StringComparer.OrdinalIgnoreCase);
        var holdingNotes = visibleNotes.Where(note => MusicChartIndex.GetNoteDuration(note) > 0 &&
            activeKeys.Contains(note.Key) && displayTime >= note.TimeMs &&
            displayTime < note.TimeMs + MusicChartIndex.GetNoteDuration(note)).ToArray();
        foreach (var note in _holdRenderers.Keys.Except(holdingNotes).ToArray()) _holdRenderers.Remove(note);
        foreach (var note in _compiledHoldRenderers.Keys.Except(holdingNotes).ToArray()) _compiledHoldRenderers.Remove(note);
        foreach (var hit in _referenceHitRenderers.Keys.Except(_effectTimeline.Hits).ToArray()) _referenceHitRenderers.Remove(hit);

        MusicOverlayDiagnostics.Record(MusicOverlayDiagnostics.Metric.VisibleNotes, visibleNotes.Count);
        MusicOverlayDiagnostics.Record(MusicOverlayDiagnostics.Metric.ActiveHits, _effectTimeline.Hits.Count);
        MusicOverlayDiagnostics.Record(MusicOverlayDiagnostics.Metric.ActiveHolds, holdingNotes.Length);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_diagnostic != null) { _diagnostic.Width = width; _diagnostic.Height = height; _diagnostic.Dpi = pixelsPerDip; }
        var boundsClip = new RectangleGeometry(new Rect(0, 0, width, height));
        boundsClip.Freeze();
        if (_tracksDirty || Math.Abs(_lastWidth - width) > .001 || Math.Abs(_lastHeight - height) > .001 ||
            Math.Abs(_lastPixelsPerDip - pixelsPerDip) > .001)
        {
            using var dc = _tracksVisual.RenderOpen();
            dc.PushClip(boundsClip);
            PlayfieldTrackRenderer.DrawTracks(dc, width, height, LayoutConfig, EffectsConfig, pixelsPerDip, Skin, !hasChart, !hasChart);
            dc.Pop();
            _tracksDirty = false;
        }
        _lastWidth = width; _lastHeight = height; _lastPixelsPerDip = pixelsPerDip;

        // Beams and ribbons are behind the source hold layer; the source flowers sit above it.
        var beams = new DrawingGroup();
        using (var dc = beams.Open())
        {
            MusicPlayfieldDrawing.DrawNotes(dc, visibleNotes, displayTime, FallDurationMs, width, height,
                LayoutConfig, EffectsConfig, snapshot.ActiveKeys, Skin, drawSprites: false);
            if (hasChart) PlayfieldTrackRenderer.DrawJudgePoints(dc, width, height, LayoutConfig, Skin);
        }
        DrawingGroup scene = beams;
        bool sourceHolds = !Skin.IsCustom && EffectsConfig.HoldGlow && EffectsConfig.Intensity > 0 &&
            holdingNotes.Length > 0 && HoldEffectRenderer.HasSource;
        _tracksVisual.Opacity = sourceHolds ? 0 : 1;
        if (sourceHolds)
        {
            scene = new DrawingGroup();
            if (_tracksVisual.Drawing != null) scene.Children.Add(_tracksVisual.Drawing);
            scene.Children.Add(beams);
            double judgeY = MusicPlayfieldGeometry.GetJudgementY(height, LayoutConfig);
            // Simultaneous chord notes share sprite generation, but each still
            // composites against its own position and the preceding scene.
            var preparedHolds = new Dictionary<double, ReferenceEffectChannels>();
            foreach (var note in holdingNotes)
            {
                using var holdDiagnostic = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.HoldMs);
                double laneX = MusicPlayfieldGeometry.GetLaneX(note.Key, width, LayoutConfig);
                Rect rect;
                System.Windows.Media.Imaging.BitmapSource? bitmap;
                if (Skin.Reference?.Definition.HoldProfile is { } profile)
                {
                    if (!_compiledHoldRenderers.TryGetValue(note, out var compiled))
                        _compiledHoldRenderers[note] = compiled = new ReferenceCompiledHoldRenderer(profile);
                    double size = compiled.DisplaySize * width / 1920 * LayoutConfig.Scale * Skin.HitEffectScale;
                    rect = new Rect(laneX - size / 2, judgeY - size / 2, size, size);
                    double age = displayTime - note.TimeMs;
                    bool reused = preparedHolds.TryGetValue(age, out var channels);
                    bitmap = compiled.Render(scene, rect, age, EffectsConfig.Intensity, pixelsPerDip, reused ? channels : null);
                    if (!reused) preparedHolds.Add(age, compiled.Channels);
                }
                else
                {
                    if (!_holdRenderers.TryGetValue(note, out var renderer))
                        _holdRenderers[note] = renderer = new HoldEffectRenderer(FindNoteIndex(note),
                            Skin.Reference?.Definition.HoldStyles.Select(style => new HoldParticleStyle(style.Texture,
                                style.Size, style.LifeMs, style.IntervalMs, style.Opacity, style.Color, style.Motion, style.Blend == "add")).ToArray());
                    rect = HoldEffectRenderer.Bounds(laneX, judgeY, width, LayoutConfig, Skin.HitEffectScale);
                    bitmap = renderer.Render(scene, rect, displayTime - note.TimeMs, EffectsConfig.Intensity, pixelsPerDip);
                }
                if (bitmap == null) continue;
                // The sprite compositor returns the already-composited backdrop. Exclude
                // the replaced rectangle to preserve alpha on transparent overlays too.
                var replacement = new DrawingGroup();
                using (var dc = replacement.Open())
                {
                    var innerRect = new Rect(rect.X + 0.5, rect.Y + 0.5, Math.Max(0, rect.Width - 1), Math.Max(0, rect.Height - 1));
                    dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, boundsClip, new RectangleGeometry(innerRect)));
                    dc.DrawDrawing(scene); dc.Pop();
                    dc.DrawImage(bitmap, rect);
                }
                scene = replacement;
            }
        }
        _notesVisual.Opacity = 1;
        using (var dc = _notesVisual.RenderOpen())
        {
            dc.PushClip(boundsClip);
            dc.DrawDrawing(scene);
            if (Skin.IsCustom && EffectsConfig.HoldGlow)
                foreach (var note in holdingNotes)
                    HoldEffectRenderer.DrawHoldingEffect(dc, MusicPlayfieldGeometry.GetLaneX(note.Key, width, LayoutConfig),
                        MusicPlayfieldGeometry.GetJudgementY(height, LayoutConfig), displayTime - note.TimeMs,
                        width, LayoutConfig, Skin, EffectsConfig.Intensity, MusicPlayfieldGeometry.GetLaneIndex(note.Key));
            MusicPlayfieldDrawing.DrawNotes(dc, visibleNotes, displayTime, FallDurationMs, width, height,
                LayoutConfig, EffectsConfig, snapshot.ActiveKeys, Skin, drawBeams: false, drawSync: false);
            dc.Pop();
        }
        using (var dc = _feedbackVisual.RenderOpen())
        {
            dc.PushClip(boundsClip);
            if (Skin.Reference is { } reference)
            {
                var complete = new DrawingGroup();
                if (_tracksVisual.Opacity > 0 && _tracksVisual.Drawing != null) complete.Children.Add(_tracksVisual.Drawing);
                complete.Children.Add(_notesVisual.Drawing);
                DrawingGroup composite = complete;
                var preparedHits = new Dictionary<int, ReferenceEffectChannels>();
                if (EffectsConfig.HitBursts)
                    foreach (var hit in _effectTimeline.Hits)
                    {
                        using var hitDiagnostic = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.HitMs);
                        if (!_referenceHitRenderers.TryGetValue(hit, out var renderer))
                            _referenceHitRenderers[hit] = renderer = new ReferenceHitRenderer();
                        double size = reference.Definition.Hit.DisplaySize * width / 1920 * LayoutConfig.Scale * Skin.HitEffectScale;
                        double x = MusicPlayfieldGeometry.GetLaneX(hit.Key, width, LayoutConfig);
                        double y = MusicPlayfieldGeometry.GetJudgementY(height, LayoutConfig);
                        var rect = new Rect(x - size / 2, y - size / 2, size, size);
                        double age = effectTime - hit.StartMs;
                        int frame = reference.Definition.Hit.FrameAt(age);
                        bool reused = preparedHits.TryGetValue(frame, out var channels);
                        var bitmap = renderer.Render(composite, rect, reference.Definition.Hit, age,
                            EffectsConfig.Intensity, pixelsPerDip, reused ? channels : null);
                        if (!reused && bitmap != null) preparedHits.Add(frame, renderer.Channels);
                        if (bitmap != null) composite = ReferenceEffectSurface.Replace(composite, bitmap, rect, boundsClip);
                    }
                _tracksVisual.Opacity = 0; _notesVisual.Opacity = 0;
                dc.DrawDrawing(composite);
                if (hasChart) PlayfieldTrackRenderer.DrawLabels(dc, width, height, LayoutConfig, EffectsConfig, pixelsPerDip, Skin);
                else
                    foreach (var hit in _effectTimeline.Hits)
                        MusicPlayfieldDrawing.DrawFeedback(dc, new[] { hit.Key }, snapshot.ActiveManualKeys,
                            snapshot.ActiveAutomaticKeys, width, height, LayoutConfig, EffectsConfig, Skin,
                            pixelsPerDip: pixelsPerDip, drawEffects: false);
            }
            else if (hasChart)
            {
                if (EffectsConfig.HitBursts)
                    foreach (var hit in _effectTimeline.Hits)
                        HitBurstEffectRenderer.DrawHitBurst(dc, MusicPlayfieldGeometry.GetLaneX(hit.Key, width, LayoutConfig),
                            MusicPlayfieldGeometry.GetJudgementY(height, LayoutConfig), effectTime - hit.StartMs,
                            width, LayoutConfig, Skin, EffectsConfig.Intensity);
                PlayfieldTrackRenderer.DrawLabels(dc, width, height, LayoutConfig, EffectsConfig, pixelsPerDip, Skin);
            }
            else
            {
                // Uncharted key feedback retains the existing ownership indication.
                foreach (var hit in _effectTimeline.Hits)
                    MusicPlayfieldDrawing.DrawFeedback(dc, new[] { hit.Key }, snapshot.ActiveManualKeys,
                        snapshot.ActiveAutomaticKeys, width, height, LayoutConfig, EffectsConfig, Skin,
                        keyElapsedMs: new Dictionary<string, double> { [hit.Key] = effectTime - hit.StartMs }, pixelsPerDip: pixelsPerDip);
            }
            dc.Pop();
        }
        UpdateRenderingHook();
    }

    private int FindNoteIndex(PerformanceEvent note)
    {
        if (ChartIndex == null) return 0;
        for (int i = 0; i < ChartIndex.AllNotes.Count; i++)
            if (ReferenceEquals(ChartIndex.AllNotes[i], note)) return i;
        return 0;
    }

    private void AcceptSnapshot(MusicRenderSnapshot snapshot)
    {
        if (CurrentSnapshot.SessionId != snapshot.SessionId || CurrentSnapshot.Revision != snapshot.Revision)
        {
            _effectTimeline.Clear();
            _holdRenderers.Clear();
            _compiledHoldRenderers.Clear();
            _referenceHitRenderers.Clear();
        }
        if (MusicOverlayDiagnostics.Enabled && CurrentSnapshot.SessionId == snapshot.SessionId &&
            CurrentSnapshot.Revision == snapshot.Revision &&
            CurrentSnapshot.MonotonicTimestamp != snapshot.MonotonicTimestamp)
        {
            double time = _monotonicClock();
            MusicOverlayDiagnostics.Record(MusicOverlayDiagnostics.Metric.DisplayCorrectionMs,
                snapshot.ComputeCurrentDisplayTimeMs(time, VisualOffsetMs) - CurrentSnapshot.ComputeCurrentDisplayTimeMs(time, VisualOffsetMs));
        }
        CurrentSnapshot = snapshot;
    }

    private void UpdateRenderingHook()
    {
        if (_diagnostic != null)
        {
            _diagnostic.Loaded = _isLoaded; _diagnostic.Visible = IsVisible; _diagnostic.Released = _resourcesReleased;
            _diagnostic.Playing = CurrentSnapshot.IsPlaying; _diagnostic.Preparing = CurrentSnapshot.IsPreparing;
            _diagnostic.SkinId = Skin.Id; _diagnostic.SessionId = CurrentSnapshot.SessionId;
            if (_diagnostic.Hwnd == 0 && _isLoaded && System.Windows.PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
            {
                _diagnostic.Hwnd = source.Handle.ToInt64();
                var window = Window.GetWindow(this);
                MusicOverlayDiagnostics.Event("control-window", new { controlId = _diagnostic.Id, hwnd = _diagnostic.Hwnd,
                    windowType = window?.GetType().FullName, window?.AllowsTransparency, window?.Topmost, window?.ShowActivated, window?.WindowStyle });
            }
            _diagnostic.Branch = Skin.Reference != null ? "reference" : Skin.IsCustom ? "custom" : "native-default";
            _diagnostic.HitBursts = EffectsConfig.HitBursts; _diagnostic.HoldGlow = EffectsConfig.HoldGlow;
            _diagnostic.Speed = CurrentSnapshot.PlaybackSpeed; _diagnostic.Intensity = EffectsConfig.Intensity;
            _diagnostic.FallDurationMs = FallDurationMs; _diagnostic.VisualOffsetMs = VisualOffsetMs;
        }
        // A held key is static. Only released uncharted feedback needs frames while the song is paused.
        bool hasFadingFeedback = ChartIndex is not { NoteCount: > 0 } && _effectTimeline.HasReleasedFeedback;
        bool shouldHook = !_resourcesReleased && _isLoaded && IsVisible && ActualWidth > 0 && ActualHeight > 0 &&
                          (CurrentSnapshot.IsPlaying || CurrentSnapshot.IsPreparing || hasFadingFeedback);

        if (shouldHook && !_isRenderingHooked)
        {
            _subscribeRendering(OnRendering);
            _isRenderingHooked = true;
            if (_diagnostic != null) { _diagnostic.Hooked = true; _diagnostic.Hooks++; _diagnostic.LastCallback = 0; }
        }
        else if (!shouldHook && _isRenderingHooked)
        {
            UnhookRendering();
        }
    }

    private void UnhookRendering()
    {
        if (_isRenderingHooked)
        {
            _unsubscribeRendering(OnRendering);
            _isRenderingHooked = false;
            if (_diagnostic != null) { _diagnostic.Hooked = false; _diagnostic.Unhooks++; _diagnostic.LastCallback = 0; _diagnostic.NextDisplayIsCallback = false; }
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        _diagnostic?.Rendering(e is RenderingEventArgs rendering ? rendering.RenderingTime.TotalMilliseconds : double.NaN);
        if (!_isLoaded || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0)
        {
            UnhookRendering();
            return;
        }

        RenderSnapshot(CurrentSnapshot, ActualWidth, ActualHeight);
    }
}

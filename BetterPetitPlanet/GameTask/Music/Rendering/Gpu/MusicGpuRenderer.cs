using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Skins;
using BetterPetitPlanet.MusicOverlay.Gpu;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace BetterPetitPlanet.GameTask.Music.Rendering.Gpu;

/// <summary>
/// GPU playfield renderer supporting native-default and custom skin packages.
/// The host owns drawing/presentation and DPI; this content owns only reusable Direct2D/DirectWrite resources on the render thread.
/// </summary>
public sealed class MusicGpuRenderer : IGpuOverlayContent
{
    public const string CapabilityDescription = "GPU 试验：原生默认纹理、基础长音、七轨、同步连线、装饰背景、命中爆发动画、长按粒子及按键反馈。";
    public const string CustomCapabilityDescription = "GPU 试验：自定义皮肤（数据驱动配色、独立贴图、回退命中爆发、长按粒子及按键反馈）。";
    public const string ReferenceCapabilityDescription = "GPU 试验：内置原版皮肤（多层背景装配、主题配色与音符、多通道命中爆发动画及长按粒子）。";

    private sealed record State(MusicRenderSnapshot Snapshot, MusicChartIndex Chart, MusicOverlayConfig Config, int ActiveLaneMask, int ManualLaneMask, MusicSkin? Skin);
    private State _state = new(MusicRenderSnapshot.Empty, MusicChartIndex.Empty, new(), 0, 0, null);
    private readonly ConcurrentQueue<MusicGpuHitEvent> _hitQueue = new();
    private readonly MusicGpuFrameBuilder _builder = new();
    private readonly MusicGpuEffectTimeline _timeline = new();
    internal MusicGpuEffectTimeline Timeline => _timeline;
    internal MusicGpuDefaultSprites? Sprites => _sprites;
    internal MusicGpuCustomSprites? CustomSprites => _customSprites;
    internal MusicGpuReferenceSprites? ReferenceSprites => _referenceSprites;
    private ID2D1SolidColorBrush[] _brushes = [];
    private MusicGpuDefaultSprites? _sprites;
    private MusicGpuCustomSprites? _customSprites;
    private string? _activeCustomSkinId;
    private MusicGpuReferenceSprites? _referenceSprites;
    private string? _activeReferenceSkinId;
    private MusicGpuSyncRibbon? _ribbon;
    private (double Width, double LayoutWidth, double Scale) _ribbonKey;
    private IDWriteFactory? _textFactory;
    private IDWriteTextFormat? _labelFormat;
    private readonly IDWriteTextLayout?[] _labelLayouts = new IDWriteTextLayout?[7];
    public bool OpaqueBackdrop { get; set; }
    private ID2D1SolidColorBrush? _cardBackdropBrush;
    private float _labelScale;

    internal enum Ink {
        Guide, CenterGuide, White, Judge, Ring, Tap, Tail, HoldEdge, Sync,
        Label, Keycap, KeycapActive, KeyManualGlow, KeyAutoGlow
    }

    /// <summary>
    /// Enqueues a hit event for event-driven hit burst playback.
    /// Thread-safe and bounded to prevent excessive backlog.
    /// </summary>
    public void EnqueueHit(in MusicGpuHitEvent hit)
    {
        while (_hitQueue.Count >= 64 && _hitQueue.TryDequeue(out _)) { }
        _hitQueue.Enqueue(hit);
    }

    /// <summary>
    /// Clears any pending unconsumed hit events.
    /// </summary>
    public void ClearHits()
    {
        while (_hitQueue.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Atomically publishes a complete frame source with an optional active skin.
    /// Pass MusicChartIndex.Empty to clear the chart. Settings are copied so later UI edits cannot mutate the render thread.
    /// </summary>
    public void Publish(MusicRenderSnapshot snapshot, MusicChartIndex chart, MusicOverlayConfig config, MusicSkin? skin = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(config);
        if (!string.Equals(_state.Snapshot.SessionId, snapshot.SessionId, StringComparison.Ordinal) ||
            snapshot.PositionMs < _state.Snapshot.PositionMs)
        {
            ClearHits();
        }
        int mask = 0;
        int manualMask = 0;
        for (int lane = 0; lane < MusicPlayfieldGeometry.LaneKeys.Length; lane++)
        {
            string laneKey = MusicPlayfieldGeometry.LaneKeys[lane];
            for (int i = 0; i < snapshot.ActiveKeys.Count; i++)
            {
                if (string.Equals(snapshot.ActiveKeys[i], laneKey, StringComparison.OrdinalIgnoreCase))
                {
                    mask |= 1 << lane;
                    break;
                }
            }
            for (int i = 0; i < snapshot.ActiveManualKeys.Count; i++)
            {
                if (string.Equals(snapshot.ActiveManualKeys[i], laneKey, StringComparison.OrdinalIgnoreCase))
                {
                    manualMask |= 1 << lane;
                    break;
                }
            }
        }
        Volatile.Write(ref _state, new State(snapshot, chart, config.Clone(), mask, manualMask, skin));
    }

    public void Publish(MusicRenderSnapshot snapshot, MusicChartIndex chart, MusicOverlayConfig config)
        => Publish(snapshot, chart, config, null);

    public void OnDeviceCreated(ID2D1DeviceContext context)
    {
        OnDeviceDestroyed();
        // D2D takes straight colors; the composition target supplies premultiplied output.
        _brushes = new ID2D1SolidColorBrush[Enum.GetValues<Ink>().Length];
        try
        {
            Create(Ink.Guide, 255, 255, 255, 50);
            Create(Ink.CenterGuide, 126, 227, 196, 100);
            Create(Ink.White, 255, 255, 255, 255);
            Create(Ink.Judge, 255, 255, 255, 230);
            Create(Ink.Ring, 255, 255, 255, 115);
            Create(Ink.Tap, 127, 227, 196, 240);
            Create(Ink.Tail, 198, 232, 107, 240);
            Create(Ink.HoldEdge, 220, 255, 240, 220);
            Create(Ink.Sync, 172, 255, 241, 130);
            Create(Ink.Label, 43, 61, 85, 255);
            Create(Ink.Keycap, 255, 255, 255, 209);
            Create(Ink.KeycapActive, 255, 255, 255, 245);
            Create(Ink.KeyManualGlow, 120, 210, 255, 160);
            Create(Ink.KeyAutoGlow, 126, 227, 196, 180);
            _sprites = new MusicGpuDefaultSprites(context);
            _ribbon = new MusicGpuSyncRibbon(context);
            _cardBackdropBrush = context.CreateSolidColorBrush(new Color4(32f / 255f, 32f / 255f, 32f / 255f, 1f));
            _textFactory = DWrite.DWriteCreateFactory<IDWriteFactory>();
        }
        catch
        {
            OnDeviceDestroyed();
            throw;
        }

        void Create(Ink ink, byte r, byte g, byte b, byte a)
            => _brushes[(int)ink] = context.CreateSolidColorBrush(Color(r, g, b, a));
    }

    public void Render(in GpuOverlayFrame frame)
    {
        if (_brushes.Length == 0 || frame.WidthDip <= 0 || frame.HeightDip <= 0) return;
        State state = Volatile.Read(ref _state);
        _builder.Build(state.Snapshot, state.Chart, state.Config, frame.MonotonicMs,
            frame.WidthDip, frame.HeightDip, state.ActiveLaneMask);
        var dc = frame.Context;
        var layout = state.Config.Layout;
        double width = frame.WidthDip, height = frame.HeightDip;
        float scale = (float)(width / 1920 * layout.Scale);
        float judgeY = (float)MusicPlayfieldGeometry.GetJudgementY(height, layout);
        float spawnY = (float)MusicPlayfieldGeometry.GetSpawnY(height, layout);
        float X(int lane) => (float)MusicPlayfieldGeometry.GetLaneX(lane, width, layout);

        bool isReference = state.Skin?.Reference != null;
        bool isCustom = !isReference && state.Skin != null && state.Skin.IsCustom;

        if (isReference)
        {
            if (_referenceSprites == null || !string.Equals(_activeReferenceSkinId, state.Skin!.Id, StringComparison.Ordinal))
            {
                _referenceSprites?.Dispose();
                _referenceSprites = new MusicGpuReferenceSprites(dc, state.Skin!);
                _activeReferenceSkinId = state.Skin!.Id;
            }
            if (_customSprites != null)
            {
                _customSprites.Dispose();
                _customSprites = null;
                _activeCustomSkinId = null;
            }
        }
        else if (isCustom)
        {
            if (_customSprites == null || !string.Equals(_activeCustomSkinId, state.Skin!.Id, StringComparison.Ordinal))
            {
                _customSprites?.Dispose();
                _customSprites = new MusicGpuCustomSprites(dc, state.Skin!);
                _activeCustomSkinId = state.Skin!.Id;
            }
            if (_referenceSprites != null)
            {
                _referenceSprites.Dispose();
                _referenceSprites = null;
                _activeReferenceSkinId = null;
            }
        }
        else
        {
            if (_referenceSprites != null)
            {
                _referenceSprites.Dispose();
                _referenceSprites = null;
                _activeReferenceSkinId = null;
            }
            if (_customSprites != null)
            {
                _customSprites.Dispose();
                _customSprites = null;
                _activeCustomSkinId = null;
            }
        }

        List<MusicGpuHitEvent>? hitBatch = null;
        while (_hitQueue.TryDequeue(out var hitEvent))
        {
            hitBatch ??= new List<MusicGpuHitEvent>(4);
            hitBatch.Add(hitEvent);
        }

        bool hasChart = state.Chart.NoteCount > 0;
        double displayTime = _builder.DisplayTimeMs;
        double burstDuration = state.Skin != null
            ? HitBurstEffectRenderer.Duration(state.Skin)
            : (_sprites?.HitAtlas != null
                ? HitBurstEffectRenderer.TotalDurationMs
                : HitBurstEffectRenderer.FallbackDurationMs);
        double effectTime = hasChart ? displayTime : frame.MonotonicMs;
        _timeline.Update(state.Snapshot, state.Chart, effectTime, burstDuration, state.Config.FallDurationMs, hitBatch);

        if (!isCustom && !isReference) _sprites!.Prepare(dc);
        if (OpaqueBackdrop && _cardBackdropBrush != null)
        {
            dc.FillRectangle(new Rect(0, 0, (float)width, (float)height), _cardBackdropBrush);
        }
        if (state.Config.Effects.ShowBackgroundDeco)
        {
            float bgOpacity = (float)Math.Clamp(state.Config.Effects.BackgroundOpacity, 0, 1);
            if (isReference)
                _referenceSprites!.DrawBackground(dc, (float)width, (float)height, frame.Dpi, layout.Scale, bgOpacity);
            else if (isCustom)
                _customSprites!.DrawBackground(dc, (float)width, (float)height, frame.Dpi, layout.Scale, bgOpacity);
            else
                _sprites!.DrawBackground(dc, frame.WidthDip, frame.HeightDip, frame.Dpi, layout.Scale, bgOpacity);
        }
        var ribbonKey = (width, layout.Width, layout.Scale);
        if (_ribbonKey != ribbonKey) { _ribbon!.ClearGeometry(); _ribbonKey = ribbonKey; }

        if (state.Config.Effects.LaneGuides)
            for (int lane = 0; lane < 7; lane++)
                dc.DrawLine(new Vector2(X(lane), spawnY), new Vector2(X(lane), judgeY),
                    Brush(lane == 3 ? Ink.CenterGuide : Ink.Guide), lane == 3 ? 1.8f : 1.2f);

        dc.DrawLine(new Vector2(X(0) - 90 * scale, judgeY), new Vector2(X(6) + 90 * scale, judgeY),
            Brush(Ink.Judge), 3 * scale);

        // Sustain bodies precede chord connectors and caps, matching the WPF layer order.
        var notes = _builder.Notes;
        var beamTexture = isReference ? _referenceSprites!.Beam : (isCustom ? _customSprites!.Beam : _sprites!.Beam);
        for (int i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            if (!note.IsHold || note.Beam.Height <= 0) continue;
            double fullWidth = MusicPlayfieldGeometry.GetHoldBeamWidth(width, layout);
            double center = note.Beam.X + note.Beam.Width / 2;
            MusicGpuDefaultSprites.Draw(dc, beamTexture, center - fullWidth / 2, note.Beam.Y,
                fullWidth, note.Beam.Height, note.IsHolding ? 1 : .82f);
        }

        if (state.Config.Effects.SyncLines) DrawSync(dc, width, height, state.Config, scale);

        for (int lane = 0; lane < 7; lane++)
        {
            var center = new Vector2(X(lane), judgeY);
            dc.DrawEllipse(new Ellipse(center, 18 * scale, 18 * scale), Brush(Ink.Ring), 1.5f * scale);
            dc.FillEllipse(new Ellipse(center, 13 * scale, 13 * scale), Brush(Ink.White));
        }

        if (state.Config.Effects.HoldGlow && state.Config.Effects.Intensity > 0.001)
        {
            if (isReference)
                _referenceSprites!.DrawHoldGlow(dc, notes, width, height, state.Config, scale, judgeY, displayTime);
            else if (isCustom)
                _customSprites!.DrawHoldGlow(dc, state.Config, notes, width, scale, judgeY, displayTime);
            else
                DrawHoldGlow(dc, state.Config, width, height, scale, judgeY, displayTime);
        }

        float corner = Math.Max(4, 8 * scale);
        for (int i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            var capTexture = isReference
                ? (note.IsHold ? (_referenceSprites!.Hold ?? _referenceSprites.Tap) : _referenceSprites!.Tap)
                : (isCustom
                    ? (note.IsHold ? (_customSprites!.Hold ?? _customSprites.Tap) : _customSprites!.Tap)
                    : (note.IsHold ? _sprites!.Hold : _sprites!.Tap));
            if (capTexture != null)
            {
                MusicGpuDefaultSprites.Draw(dc, capTexture, note.Head.X, note.Head.Y, note.Head.Width, note.Head.Height);
                if (note.IsHold)
                {
                    double tailCenter = note.Tail.Y + note.Tail.Height / 2;
                    MusicGpuDefaultSprites.Draw(dc, capTexture, note.Head.X, tailCenter - note.Head.Height / 2, note.Head.Width, note.Head.Height);
                }
                continue;
            }
            var head = Rounded(note.Head, corner);
            dc.FillRoundedRectangle(head, Brush(Ink.Tap));
            dc.DrawRoundedRectangle(head, Brush(Ink.White), 1.5f);
            if (!note.IsHold) continue;
            var tail = Rounded(note.Tail, corner / 2);
            dc.FillRoundedRectangle(tail, Brush(Ink.Tail));
            dc.DrawRoundedRectangle(tail, Brush(Ink.HoldEdge), 1.5f);
        }

        if (state.Config.Effects.HitBursts && state.Config.Effects.Intensity > 0.001)
        {
            if (isReference)
                _referenceSprites!.DrawHitBursts(dc, state.Config, width, height, scale, judgeY, hasChart, displayTime, frame.MonotonicMs, _timeline.Hits);
            else
                DrawHitBursts(dc, state.Config, width, height, scale, judgeY, hasChart, displayTime, frame.MonotonicMs, isCustom);
        }

        DrawKeyFeedback(dc, state, width, scale, judgeY);

        if (state.Config.Effects.ShowKeyLabels)
            DrawKeyLabels(dc, state, width, scale, judgeY);
    }

    private void DrawSync(ID2D1DeviceContext dc, double width, double height, MusicOverlayConfig config, float scale)
    {
        var notes = _builder.Notes;
        int start = 0;
        while (start < notes.Count)
        {
            int end = start + 1;
            while (end < notes.Count && notes[end].TimeMs == notes[start].TimeMs) end++;
            if (end - start > 1 && _builder.DisplayTimeMs < notes[start].TimeMs)
            {
                float y = (float)MusicPlayfieldGeometry.TimeToY(notes[start].TimeMs, _builder.DisplayTimeMs,
                    config.FallDurationMs, height, config.Layout);
                for (int i = start; i < end - 1; i++)
                {
                    float x1 = (float)MusicPlayfieldGeometry.GetLaneX(notes[i].Lane, width, config.Layout);
                    float x2 = (float)MusicPlayfieldGeometry.GetLaneX(notes[i + 1].Lane, width, config.Layout);
                    _ribbon!.Draw(dc, x1, x2, y, scale);
                }
            }
            start = end;
        }
    }

    private void EnsureLabelFormat(float scale)
    {
        if (_labelFormat != null && _labelScale == scale) return;
        for (int i = 0; i < _labelLayouts.Length; i++) { _labelLayouts[i]?.Dispose(); _labelLayouts[i] = null; }
        _labelFormat?.Dispose();
        _labelFormat = _textFactory!.CreateTextFormat("Segoe UI", null, FontWeight.SemiBold,
            Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, Math.Max(1, 30 * scale), "en-us");
        for (int i = 0; i < _labelLayouts.Length; i++)
            _labelLayouts[i] = _textFactory.CreateTextLayout(MusicPlayfieldGeometry.LaneKeys[i], _labelFormat, 1000, 1000);
        _labelScale = scale;
    }

    private ID2D1SolidColorBrush Brush(Ink ink) =>
        _referenceSprites != null ? _referenceSprites.Brush(ink)
        : _customSprites != null ? _customSprites.Brush(ink)
        : _brushes[(int)ink];
    private static Color4 Color(byte r, byte g, byte b, byte a) => new(r / 255f, g / 255f, b / 255f, a / 255f);
    private static Rect Rect(NoteRenderBounds value) => new((float)value.X, (float)value.Y, (float)value.Width, (float)value.Height);
    private static RoundedRectangle Rounded(NoteRenderBounds value, float radius) => new(
        new System.Drawing.RectangleF((float)value.X, (float)value.Y, (float)value.Width, (float)value.Height), radius, radius);

    private void DrawHitBursts(ID2D1DeviceContext dc, MusicOverlayConfig config,
        double width, double height, float scale, float judgeY,
        bool hasChart, double displayTime, double monotonicMs, bool isCustom = false)
    {
        float intensity = (float)Math.Clamp(config.Effects.Intensity, 0.0, 1.0);
        float burstScale = isCustom ? (float)_customSprites!.HitEffectScale : 1.0f;
        float burstSize = (float)(576.0 * (width / 1920.0) * config.Layout.Scale * intensity * burstScale);
        if (burstSize <= 0) return;
        const float alpha = 1.0f;
        float opacity = alpha * intensity;
        double currentTime = hasChart ? displayTime : monotonicMs;
        double durationMs = isCustom
            ? _customSprites!.HitEffectDurationMs
            : (_sprites?.HitAtlas != null ? HitBurstEffectRenderer.TotalDurationMs : HitBurstEffectRenderer.FallbackDurationMs);

        for (int i = 0; i < _timeline.Hits.Count; i++)
        {
            var hit = _timeline.Hits[i];
            if (hit.Lane < 0 || hit.Lane >= 7) continue;
            float laneX = (float)MusicPlayfieldGeometry.GetLaneX(hit.Lane, width, config.Layout);
            double elapsedMs = currentTime - hit.StartMs;
            if (elapsedMs < 0 || elapsedMs > durationMs) continue;

            if (!isCustom && _sprites?.HitAtlas != null)
            {
                int frameIndex = (int)(elapsedMs * 60.0 / 1000.0);
                if (frameIndex >= 0 && frameIndex < 64)
                {
                    int col = frameIndex % 8;
                    int row = frameIndex / 8;
                    var sourceRect = new Rect(col * 512, row * 512, 512, 512);
                    var destRect = new Rect(laneX - burstSize / 2f, judgeY - burstSize / 2f, burstSize, burstSize);
                    dc.DrawBitmap(_sprites.HitAtlas, destRect, opacity, InterpolationMode.Linear, sourceRect, null);
                }
            }
            else
            {
                var shockwave = isCustom ? _customSprites!.HitShockwave : _sprites?.HitShockwave;
                var flower = isCustom ? (_customSprites!.HitFlower ?? _customSprites.Tap) : (_sprites?.HitFlower ?? _sprites?.Tap);
                DrawFallbackBurst(dc, laneX, judgeY, elapsedMs, durationMs, width, config.Layout, opacity, burstScale, shockwave, flower);
            }
        }
    }

    private void DrawFallbackBurst(ID2D1DeviceContext dc, float laneX, float judgeY,
        double elapsedMs, double durationMs, double width, MusicOverlayLayoutConfig layout, float opacity,
        float burstScale = 1.0f, ID2D1Bitmap1? shockwaveBitmap = null, ID2D1Bitmap1? flowerBitmap = null)
    {
        if (elapsedMs < 0 || elapsedMs > durationMs || durationMs <= 0) return;

        double progress = Math.Clamp(elapsedMs / durationMs, 0.0, 1.0);
        double inv = 1.0 - progress;
        double easeOut = 1.0 - (inv * inv * inv);

        float scaleFactor = (float)((width / 1920.0) * layout.Scale * burstScale);

        // 1. Expanding shockwave ring (Cubic ease-out expanding from 25px to 80px)
        float ringRadius = (float)((25.0 + 55.0 * easeOut) * scaleFactor);
        float ringAlpha = (float)Math.Clamp(opacity * (1.0 - easeOut) * 0.85, 0.0, 1.0);

        if (ringAlpha > 0.001f)
        {
            if (shockwaveBitmap != null)
            {
                float swSize = ringRadius * 2.0f;
                var swRect = new Rect(laneX - swSize / 2f, judgeY - swSize / 2f, swSize, swSize);
                dc.DrawBitmap(shockwaveBitmap, swRect, ringAlpha, InterpolationMode.Linear, null, null);
            }
            else
            {
                var brush = Brush(Ink.CenterGuide);
                float oldOpacity = brush.Opacity;
                brush.Opacity = ringAlpha;
                dc.DrawEllipse(new Ellipse(new Vector2(laneX, judgeY), ringRadius, ringRadius),
                    brush, 2.5f * scaleFactor);
                brush.Opacity = oldOpacity;
            }
        }

        // 2. Central blooming flower (Expanding scale 0.5 -> 1.1 with smooth linear fade)
        double cloverScale = 0.5 + 0.6 * easeOut;
        float cloverSize = (float)(110.0 * scaleFactor * cloverScale);
        float cloverAlpha = (float)Math.Clamp(opacity * (1.0 - progress) * 0.95, 0.0, 1.0);

        if (cloverAlpha > 0.001f)
        {
            if (flowerBitmap != null)
            {
                var flRect = new Rect(laneX - cloverSize / 2f, judgeY - cloverSize / 2f, cloverSize, cloverSize);
                dc.DrawBitmap(flowerBitmap, flRect, cloverAlpha, InterpolationMode.Linear, null, null);
            }
            else
            {
                var tapBrush = Brush(Ink.Tap);
                var whiteBrush = Brush(Ink.White);
                float oldTapOpacity = tapBrush.Opacity;
                float oldWhiteOpacity = whiteBrush.Opacity;
                tapBrush.Opacity = cloverAlpha;
                whiteBrush.Opacity = cloverAlpha;
                var center = new Vector2(laneX, judgeY);
                float radius = cloverSize / 2.0f;
                dc.FillEllipse(new Ellipse(center, radius, radius), tapBrush);
                dc.DrawEllipse(new Ellipse(center, radius, radius), whiteBrush, 1.5f * scaleFactor);
                tapBrush.Opacity = oldTapOpacity;
                whiteBrush.Opacity = oldWhiteOpacity;
            }
        }
    }

    private void DrawHoldGlow(ID2D1DeviceContext dc, MusicOverlayConfig config,
        double width, double height, float scale, float judgeY, double displayTime)
    {
        float intensity = (float)Math.Clamp(config.Effects.Intensity, 0.0, 1.0);
        var notes = _builder.Notes;
        if (notes.Count == 0 || _sprites == null) return;

        var originalBlend = dc.PrimitiveBlend;
        var originalTransform = dc.Transform;

        try
        {
            for (int i = 0; i < notes.Count; i++)
            {
                var note = notes[i];
                if (!note.IsHold || !note.IsHolding) continue;

                double ageMs = displayTime - note.TimeMs;
                if (ageMs < 0 || displayTime >= note.TimeMs + note.DurationMs) continue;

                float laneX = (float)MusicPlayfieldGeometry.GetLaneX(note.Lane, width, config.Layout);
                int noteIndex = note.Lane;

                for (int layer = 0; layer < NahidaHoldParticles.Styles.Count; layer++)
                {
                    var style = NahidaHoldParticles.Styles[layer];
                    var bitmap = _sprites.HoldLayers[layer];
                    if (bitmap == null) continue;

                    var targetBlend = style.Additive ? PrimitiveBlend.Add : PrimitiveBlend.SourceOver;

                    for (int slot = 0; slot < style.Slots; slot++)
                    {
                        var particle = NahidaHoldParticles.Evaluate(style, noteIndex, layer, slot, ageMs);
                        if (particle == null) continue;

                        float alpha = (float)Math.Clamp(particle.Value.Alpha * intensity, 0.0, 1.0);
                        if (alpha <= 0.001f) continue;

                        float posX = laneX + (float)(particle.Value.X * scale);
                        float posY = judgeY + (float)(particle.Value.Y * scale);
                        float pSize = (float)(particle.Value.Size * scale);
                        if (pSize <= 0) continue;

                        if (dc.PrimitiveBlend != targetBlend)
                            dc.PrimitiveBlend = targetBlend;

                        var destRect = new Rect(posX - pSize / 2f, posY - pSize / 2f, pSize, pSize);

                        if (Math.Abs(particle.Value.Rotation) > 1e-4)
                        {
                            dc.Transform = Matrix3x2.CreateRotation((float)particle.Value.Rotation, new Vector2(posX, posY)) * originalTransform;
                            dc.DrawBitmap(bitmap, destRect, alpha, InterpolationMode.Linear, null, null);
                            dc.Transform = originalTransform;
                        }
                        else
                        {
                            dc.DrawBitmap(bitmap, destRect, alpha, InterpolationMode.Linear, null, null);
                        }
                    }
                }
            }
        }
        finally
        {
            dc.PrimitiveBlend = originalBlend;
            dc.Transform = originalTransform;
        }
    }

    private void DrawKeyFeedback(ID2D1DeviceContext dc, State state, double width, float scale, float judgeY)
    {
        if (state.ActiveLaneMask == 0) return;
        float intensity = (float)Math.Clamp(state.Config.Effects.Intensity, 0.0, 1.0);
        if (intensity <= 0.001f) return;

        float radius = 48.0f * scale;
        float haloAlpha = (float)Math.Clamp(0.45 * intensity, 0.0, 1.0);

        for (int lane = 0; lane < 7; lane++)
        {
            if ((state.ActiveLaneMask & (1 << lane)) == 0) continue;
            bool isManual = (state.ManualLaneMask & (1 << lane)) != 0;
            var glowBrush = Brush(isManual ? Ink.KeyManualGlow : Ink.KeyAutoGlow);
            float oldOpacity = glowBrush.Opacity;
            glowBrush.Opacity = haloAlpha;
            float laneX = (float)MusicPlayfieldGeometry.GetLaneX(lane, width, state.Config.Layout);
            dc.FillEllipse(new Ellipse(new Vector2(laneX, judgeY), radius, radius), glowBrush);
            glowBrush.Opacity = oldOpacity;
        }
    }

    private void DrawKeyLabels(ID2D1DeviceContext dc, State state, double width, float scale, float judgeY)
    {
        EnsureLabelFormat(scale);

        for (int lane = 0; lane < 7; lane++)
        {
            var text = _labelLayouts[lane]!;
            var metrics = text.Metrics;
            float centerY = judgeY + 56 * scale, pad = 14 * scale;
            float laneX = (float)MusicPlayfieldGeometry.GetLaneX(lane, width, state.Config.Layout);
            var label = new Rect(laneX - metrics.Width / 2 - pad, centerY - metrics.Height / 2 - pad * .3f,
                metrics.Width + 2 * pad, metrics.Height + pad * .6f);
            float cornerRadius = 8 * scale;

            bool isActive = (state.ActiveLaneMask & (1 << lane)) != 0;

            if (isActive)
            {
                bool isManual = (state.ManualLaneMask & (1 << lane)) != 0;
                var glowBrush = Brush(isManual ? Ink.KeyManualGlow : Ink.KeyAutoGlow);

                // 1. Glowing halo around pillRect: haloRect.Inflate(3 * scale, 3 * scale) with glowBrush at alpha 0.55
                float haloInflate = 3 * scale;
                var haloRect = new System.Drawing.RectangleF(
                    label.X - haloInflate,
                    label.Y - haloInflate,
                    label.Width + 2 * haloInflate,
                    label.Height + 2 * haloInflate);

                float oldOpacity = glowBrush.Opacity;
                glowBrush.Opacity = 0.55f;
                dc.FillRoundedRectangle(new RoundedRectangle(haloRect, cornerRadius + 2 * scale, cornerRadius + 2 * scale), glowBrush);

                // 2. Fill pillRect with white/active pill brush, stroke with glowBrush
                var pillRectF = new System.Drawing.RectangleF(label.X, label.Y, label.Width, label.Height);
                dc.FillRoundedRectangle(new RoundedRectangle(pillRectF, cornerRadius, cornerRadius), Brush(Ink.KeycapActive));

                glowBrush.Opacity = 1.0f;
                dc.DrawRoundedRectangle(new RoundedRectangle(pillRectF, cornerRadius, cornerRadius), glowBrush, 1.5f * scale);
                glowBrush.Opacity = oldOpacity;

                // 3. Text drawn in deep blue-grey or active label color
                dc.DrawTextLayout(new Vector2(laneX - metrics.Width / 2, centerY - metrics.Height / 2), text, Brush(Ink.Label));
            }
            else
            {
                var pillRectF = new System.Drawing.RectangleF(label.X, label.Y, label.Width, label.Height);
                dc.FillRoundedRectangle(new RoundedRectangle(pillRectF, cornerRadius, cornerRadius), Brush(Ink.Keycap));
                dc.DrawTextLayout(new Vector2(laneX - metrics.Width / 2, centerY - metrics.Height / 2), text, Brush(Ink.Label));
            }
        }
    }

    public void OnDeviceDestroyed()
    {
        _timeline.Clear();
        ClearHits();
        for (int i = 0; i < _labelLayouts.Length; i++) { _labelLayouts[i]?.Dispose(); _labelLayouts[i] = null; }
        _labelFormat?.Dispose(); _labelFormat = null;
        _textFactory?.Dispose(); _textFactory = null;
        _sprites?.Dispose(); _sprites = null;
        _customSprites?.Dispose(); _customSprites = null;
        _activeCustomSkinId = null;
        _referenceSprites?.Dispose(); _referenceSprites = null;
        _activeReferenceSkinId = null;
        _ribbon?.Dispose(); _ribbon = null;
        _cardBackdropBrush?.Dispose(); _cardBackdropBrush = null;
        foreach (var brush in _brushes) brush?.Dispose();
        _brushes = [];
    }

    public void Dispose() => OnDeviceDestroyed();
}

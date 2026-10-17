using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Skins;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace BetterPetitPlanet.GameTask.Music.Rendering.Gpu;

/// <summary>
/// Device-owned resources for a Reference skin package (C1 / shougun, etc.).
/// Handles multi-layer assembled background with blend/tint, hold beam, 14 theme brushes,
/// 10-layer hold particle glow, and hit burst frame decoding with fallback.
/// Disposed when switching skins or when the D2D device is destroyed.
/// </summary>
internal sealed class MusicGpuReferenceSprites : IDisposable
{
    private readonly List<ID2D1Bitmap1> _textures = new();
    private readonly ID2D1SolidColorBrush[] _brushes;
    private readonly ID2D1SolidColorBrush _trackBackgroundBrush;
    private readonly Dictionary<(string Url, uint Tint), ID2D1Bitmap1> _layerBitmaps = new();
    private readonly HoldParticleStyle[] _styles;
    private readonly ID2D1Bitmap1?[] _holdLayers;
    private readonly ID2D1Bitmap1?[] _normalFrames;
    private readonly ID2D1Bitmap1?[] _additiveFrames;
    private readonly int _frameCount;
    private readonly double _hitFps;
    private readonly double _hitDisplaySize;
    private readonly double _hitDurationMs;
    private readonly double _hitEffectScale;
    private readonly ReferenceHoldProfile? _holdProfile;
    private readonly ID2D1Bitmap1?[] _holdNormalFrames;
    private readonly ID2D1Bitmap1?[] _holdAdditiveFrames;
    private readonly double _holdDisplaySize;
    private readonly Dictionary<(int Lane, int TimeMs), ReferenceHoldEmission[]> _holdEmitters = new();
    private ID2D1Bitmap1? _assembledBackground;
    private (float Width, float Height, float Dpi, double Scale) _backgroundKey;
    private readonly ReferenceSkinResources? _resources;

    public ID2D1Bitmap1? Tap { get; }
    public ID2D1Bitmap1? Hold { get; }
    public ID2D1Bitmap1? Point { get; }
    public ID2D1Bitmap1 Beam { get; }
    public ReferenceSkinDefinition Definition { get; }
    public IReadOnlyList<HoldParticleStyle> HoldStyles => _styles;

    public MusicGpuReferenceSprites(ID2D1DeviceContext dc, MusicSkin skin)
    {
        ArgumentNullException.ThrowIfNull(dc);
        ArgumentNullException.ThrowIfNull(skin);

        _resources = skin.Reference;
        var definition = skin.Reference?.Definition ?? new();
        Definition = definition;

        try
        {
            Tap = Upload(dc, skin.NoteTapBitmap);
            Hold = Upload(dc, skin.NoteHoldBitmap);
            Point = Upload(dc, skin.NotePointBitmap);
            Beam = (skin.HoldBeamBitmap != null ? Upload(dc, skin.HoldBeamBitmap) : null)
                ?? CreateFallbackBeam(dc, definition.Accent.Primary);

            // 14 theme solid color brushes matching MusicGpuRenderer.Ink
            _brushes = new ID2D1SolidColorBrush[14];
            _brushes[(int)MusicGpuRenderer.Ink.Guide] = dc.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 50f / 255f));
            _brushes[(int)MusicGpuRenderer.Ink.CenterGuide] = dc.CreateSolidColorBrush(HexToColor4(definition.Accent.Secondary, 100f / 255f));
            _brushes[(int)MusicGpuRenderer.Ink.White] = dc.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
            _brushes[(int)MusicGpuRenderer.Ink.Judge] = dc.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 230f / 255f));
            _brushes[(int)MusicGpuRenderer.Ink.Ring] = dc.CreateSolidColorBrush(HexToColor4(definition.Accent.Primary, 115f / 255f));
            _brushes[(int)MusicGpuRenderer.Ink.Tap] = dc.CreateSolidColorBrush(HexToColor4(definition.Accent.Primary, 240f / 255f));
            _brushes[(int)MusicGpuRenderer.Ink.Tail] = dc.CreateSolidColorBrush(HexToColor4(definition.Accent.Secondary, 240f / 255f));
            _brushes[(int)MusicGpuRenderer.Ink.HoldEdge] = dc.CreateSolidColorBrush(HexToColor4(definition.Accent.Primary, 220f / 255f));
            _brushes[(int)MusicGpuRenderer.Ink.Sync] = dc.CreateSolidColorBrush(HexToColor4(0xffffd1, 130f / 255f));
            _brushes[(int)MusicGpuRenderer.Ink.Label] = dc.CreateSolidColorBrush(new Color4(43f / 255f, 61f / 255f, 85f / 255f, 1f));
            _brushes[(int)MusicGpuRenderer.Ink.Keycap] = dc.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 209f / 255f));
            _brushes[(int)MusicGpuRenderer.Ink.KeycapActive] = dc.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 245f / 255f));
            _brushes[(int)MusicGpuRenderer.Ink.KeyManualGlow] = dc.CreateSolidColorBrush(HexToColor4(definition.Accent.Secondary, 160f / 255f));
            _brushes[(int)MusicGpuRenderer.Ink.KeyAutoGlow] = dc.CreateSolidColorBrush(HexToColor4(definition.Accent.Primary, 180f / 255f));

            _trackBackgroundBrush = dc.CreateSolidColorBrush(HexToColor4(definition.Backdrop, 1f));

            // Hold particle styles
            if (definition.HoldStyles != null && definition.HoldStyles.Length > 0)
            {
                _styles = new HoldParticleStyle[definition.HoldStyles.Length];
                for (int i = 0; i < definition.HoldStyles.Length; i++)
                {
                    var s = definition.HoldStyles[i];
                    _styles[i] = new HoldParticleStyle(s.Texture, s.Size, s.LifeMs, s.IntervalMs, s.Opacity, s.Color, s.Motion, string.Equals(s.Blend, "add", StringComparison.OrdinalIgnoreCase));
                }
            }
            else
            {
                _styles = NahidaHoldParticles.Styles.ToArray();
            }

            _holdLayers = new ID2D1Bitmap1?[_styles.Length];
            var rawSprites = MusicGpuDefaultSprites.RawHoldSprites.Value;
            var uniqueHoldBitmaps = new Dictionary<(string Texture, uint Color), ID2D1Bitmap1>();
            for (int i = 0; i < _styles.Length; i++)
            {
                var style = _styles[i];
                var key = (style.Texture, style.Color);
                if (!uniqueHoldBitmaps.TryGetValue(key, out var bitmap))
                {
                    if (rawSprites.TryGetValue(style.Texture, out var sprite))
                    {
                        bitmap = UploadTinted(dc, sprite.Pixels, sprite.Width, sprite.Height, style.Color);
                        if (bitmap != null)
                            uniqueHoldBitmaps[key] = bitmap;
                    }
                }
                _holdLayers[i] = bitmap;
            }

            // Hit animation parameters
            _frameCount = definition.Hit.FrameCount > 0 ? definition.Hit.FrameCount : 40;
            _hitFps = definition.Hit.Fps > 0 ? definition.Hit.Fps : 60.0;
            _hitDisplaySize = definition.Hit.DisplaySize > 0 ? definition.Hit.DisplaySize : 576.0;
            _hitDurationMs = definition.Hit.DurationMs > 0 ? definition.Hit.DurationMs : (_frameCount * 1000.0 / _hitFps);
            _hitEffectScale = skin.HitEffectScale > 0 ? skin.HitEffectScale : 1.0;
            _normalFrames = new ID2D1Bitmap1?[_frameCount];
            _additiveFrames = new ID2D1Bitmap1?[_frameCount];

            if (definition.HoldProfile is { } holdProfile)
            {
                _holdProfile = holdProfile;
                int maxHoldFrames = 0;
                foreach (var layer in holdProfile.Layers)
                    maxHoldFrames = Math.Max(maxHoldFrames, layer.FrameStart + layer.FrameCount);
                _holdNormalFrames = new ID2D1Bitmap1?[maxHoldFrames];
                _holdAdditiveFrames = new ID2D1Bitmap1?[maxHoldFrames];
                _holdDisplaySize = holdProfile.Layers.Length > 0 ? holdProfile.Layers.Max(l => l.DisplaySize) : 640.0;
            }
            else
            {
                _holdNormalFrames = [];
                _holdAdditiveFrames = [];
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void DrawBackground(ID2D1DeviceContext dc, float width, float height, float dpi, double layoutScale, float opacity)
    {
        if (opacity <= 0 || width <= 0 || height <= 0) return;

        var key = (width, height, dpi, layoutScale);
        if (_assembledBackground == null || _backgroundKey != key)
        {
            _assembledBackground?.Dispose();
            _assembledBackground = MusicGpuDefaultSprites.Target(dc,
                Math.Max(1, (int)Math.Ceiling(width * dpi / 96)),
                Math.Max(1, (int)Math.Ceiling(height * dpi / 96)), dpi);

            using var previous = dc.Target;
            var transform = dc.Transform;
            var blend = dc.PrimitiveBlend;

            try
            {
                dc.Target = _assembledBackground;
                dc.Transform = Matrix3x2.Identity;
                dc.Clear(new Color4(0, 0, 0, 0));
                dc.FillRectangle(new Rect(0, 0, width, height), _trackBackgroundBrush);

                float sx = (float)(width / 1920.0);
                float sy = (float)(height / 1080.0);

                foreach (var layer in Definition.Background)
                {
                    if (layer.Alpha <= 0) continue;
                    var layerBlend = string.Equals(layer.Blend, "add", StringComparison.OrdinalIgnoreCase)
                        ? PrimitiveBlend.Add
                        : PrimitiveBlend.SourceOver;
                    dc.PrimitiveBlend = layerBlend;
                    float alpha = (float)Math.Clamp(layer.Alpha, 0, 1);

                    foreach (var sprite in GenerateSprites(Definition, layer, layoutScale, _resources?.Images))
                    {
                        if (!sprite.Bounds.IntersectsWith(new System.Windows.Rect(0, 0, 1920, 1080))) continue;
                        var bitmap = GetLayerBitmap(dc, sprite.Url, layer.Tint);
                        if (bitmap == null) continue;

                        float x = (float)(sprite.Bounds.X * sx);
                        float y = (float)(sprite.Bounds.Y * sy);
                        float w = (float)(sprite.Bounds.Width * sx);
                        float h = (float)(sprite.Bounds.Height * sy);

                        if (sprite.MirrorX)
                        {
                            float centerX = x + w / 2f;
                            dc.Transform = Matrix3x2.CreateScale(-1, 1, new Vector2(centerX, 0));
                            dc.DrawBitmap(bitmap, new Rect(x, y, w, h), alpha, InterpolationMode.Linear, null, null);
                            dc.Transform = Matrix3x2.Identity;
                        }
                        else
                        {
                            dc.DrawBitmap(bitmap, new Rect(x, y, w, h), alpha, InterpolationMode.Linear, null, null);
                        }
                    }
                }

                _backgroundKey = key;
            }
            finally
            {
                dc.Target = previous;
                dc.Transform = transform;
                dc.PrimitiveBlend = blend;
            }
        }

        dc.DrawBitmap(_assembledBackground, new Rect(0, 0, width, height), opacity, InterpolationMode.Linear, null, null);
    }

    public void DrawHoldGlow(ID2D1DeviceContext dc, IReadOnlyList<MusicGpuNote> notes,
        double width, double height, MusicOverlayConfig config, float scale, float judgeY,
        double displayTime)
    {
        float intensity = (float)Math.Clamp(config.Effects.Intensity, 0.0, 1.0);
        if (notes.Count == 0 || intensity <= 0.001f) return;

        var originalBlend = dc.PrimitiveBlend;
        var originalTransform = dc.Transform;

        try
        {
            if (_holdProfile != null)
            {
                DrawHoldProfileAnimation(dc, notes, width, height, config, scale, judgeY, displayTime, intensity);
                return;
            }

            for (int i = 0; i < notes.Count; i++)
            {
                var note = notes[i];
                if (!note.IsHold || !note.IsHolding) continue;

                double ageMs = displayTime - note.TimeMs;
                if (ageMs < 0 || displayTime >= note.TimeMs + note.DurationMs) continue;

                float laneX = (float)MusicPlayfieldGeometry.GetLaneX(note.Lane, width, config.Layout);
                int noteIndex = note.Lane;

                float effectScale = (float)(_hitEffectScale > 0 ? _hitEffectScale : 1.0);
                float particleScale = scale * effectScale;

                for (int layer = 0; layer < _styles.Length; layer++)
                {
                    var style = _styles[layer];
                    var bitmap = _holdLayers[layer];
                    if (bitmap == null) continue;

                    var targetBlend = style.Additive ? PrimitiveBlend.Add : PrimitiveBlend.SourceOver;

                    for (int slot = 0; slot < style.Slots; slot++)
                    {
                        var particle = NahidaHoldParticles.Evaluate(style, noteIndex, layer, slot, ageMs);
                        if (particle == null) continue;

                        float alpha = (float)Math.Clamp(particle.Value.Alpha * intensity, 0.0, 1.0);
                        if (alpha <= 0.001f) continue;

                        float posX = laneX + (float)(particle.Value.X * particleScale);
                        float posY = judgeY + (float)(particle.Value.Y * particleScale);
                        float pSize = (float)(particle.Value.Size * particleScale);
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

    private void DrawHoldProfileAnimation(ID2D1DeviceContext dc, IReadOnlyList<MusicGpuNote> notes,
        double width, double height, MusicOverlayConfig config, float scale, float judgeY,
        double displayTime, float intensity)
    {
        if (_holdProfile == null) return;

        float effectScale = (float)(_hitEffectScale > 0 ? _hitEffectScale : 1.0);

        if (_holdEmitters.Count > 16)
        {
            var activeKeys = new HashSet<(int Lane, int TimeMs)>();
            for (int i = 0; i < notes.Count; i++)
            {
                if (notes[i].IsHold && notes[i].IsHolding)
                    activeKeys.Add((notes[i].Lane, notes[i].TimeMs));
            }
            var toRemove = new List<(int Lane, int TimeMs)>();
            foreach (var key in _holdEmitters.Keys)
            {
                if (!activeKeys.Contains(key)) toRemove.Add(key);
            }
            foreach (var key in toRemove) _holdEmitters.Remove(key);
        }

        for (int i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            if (!note.IsHold || !note.IsHolding) continue;

            double ageMs = displayTime - note.TimeMs;
            if (ageMs < 0 || displayTime >= note.TimeMs + note.DurationMs) continue;

            float laneX = (float)MusicPlayfieldGeometry.GetLaneX(note.Lane, width, config.Layout);
            var emitterKey = (note.Lane, note.TimeMs);
            if (!_holdEmitters.TryGetValue(emitterKey, out var schedules))
            {
                schedules = new ReferenceHoldEmission[_holdProfile.Layers.Length];
                for (int l = 0; l < schedules.Length; l++)
                    schedules[l] = new ReferenceHoldEmission(_holdProfile.Layers[l]);
                _holdEmitters[emitterKey] = schedules;
            }

            for (int layerIndex = 0; layerIndex < _holdProfile.Layers.Length; layerIndex++)
            {
                var layer = _holdProfile.Layers[layerIndex];
                var schedule = schedules[layerIndex];
                schedule.Update(ageMs);

                float layerSize = (float)(layer.DisplaySize * (width / 1920.0) * config.Layout.Scale * effectScale);
                if (layerSize <= 0) continue;
                var destRect = new Rect(laneX - layerSize / 2f, judgeY - layerSize / 2f, layerSize, layerSize);

                for (int slot = 0; slot < layer.Slots; slot++)
                {
                    int? frame = schedule.Frame(slot);
                    if (frame == null) continue;

                    int frameIdx = frame.Value;
                    try
                    {
                        var normalBitmap = GetOrLoadFrame(dc, _holdProfile.NormalUrl, frameIdx, _holdNormalFrames);
                        if (normalBitmap != null)
                        {
                            if (dc.PrimitiveBlend != PrimitiveBlend.SourceOver)
                                dc.PrimitiveBlend = PrimitiveBlend.SourceOver;
                            dc.DrawBitmap(normalBitmap, destRect, intensity, InterpolationMode.Linear, null, null);
                        }

                        var additiveBitmap = GetOrLoadFrame(dc, _holdProfile.AdditiveUrl, frameIdx, _holdAdditiveFrames);
                        if (additiveBitmap != null)
                        {
                            if (dc.PrimitiveBlend != PrimitiveBlend.Add)
                                dc.PrimitiveBlend = PrimitiveBlend.Add;
                            dc.DrawBitmap(additiveBitmap, destRect, intensity, InterpolationMode.Linear, null, null);
                        }
                    }
                    catch (Exception)
                    {
                        // Safely handle missing frame entries or decode errors without crashing
                    }
                }
            }
        }
    }

    public void DrawHitBursts(ID2D1DeviceContext dc, MusicOverlayConfig config,
        double width, double height, float scale, float judgeY,
        bool hasChart, double displayTime, double monotonicMs,
        IReadOnlyList<MusicGpuEffectTimeline.Hit> hits)
    {
        float intensity = (float)Math.Clamp(config.Effects.Intensity, 0.0, 1.0);
        if (hits.Count == 0 || intensity <= 0.001f) return;

        float burstSize = (float)(_hitDisplaySize * (width / 1920.0) * config.Layout.Scale * _hitEffectScale * intensity);
        if (burstSize <= 0) return;

        float opacity = (float)Math.Clamp(intensity, 0, 1);
        double currentTime = hasChart ? displayTime : monotonicMs;

        var originalBlend = dc.PrimitiveBlend;
        bool blendModified = false;

        try
        {
            for (int i = 0; i < hits.Count; i++)
            {
                var hit = hits[i];
                if (hit.Lane < 0 || hit.Lane >= 7) continue;

                float laneX = (float)MusicPlayfieldGeometry.GetLaneX(hit.Lane, width, config.Layout);
                double elapsedMs = currentTime - hit.StartMs;
                if (elapsedMs < 0) continue;

                int frameIndex = (int)(elapsedMs * _hitFps / 1000.0);
                if (frameIndex >= 0 && frameIndex < _frameCount)
                {
                    bool drewFrame = false;
                    try
                    {
                        var normalBitmap = GetOrLoadFrame(dc, Definition.Hit.Url, frameIndex, _normalFrames);
                        ID2D1Bitmap1? additiveBitmap = null;
                        if (!string.IsNullOrEmpty(Definition.Hit.AdditiveUrl))
                        {
                            additiveBitmap = GetOrLoadFrame(dc, Definition.Hit.AdditiveUrl, frameIndex, _additiveFrames);
                        }

                        if (normalBitmap != null || additiveBitmap != null)
                        {
                            var destRect = new Rect(laneX - burstSize / 2f, judgeY - burstSize / 2f, burstSize, burstSize);

                            if (normalBitmap != null)
                            {
                                if (dc.PrimitiveBlend != PrimitiveBlend.SourceOver)
                                {
                                    dc.PrimitiveBlend = PrimitiveBlend.SourceOver;
                                    blendModified = true;
                                }
                                dc.DrawBitmap(normalBitmap, destRect, opacity, InterpolationMode.Linear, null, null);
                            }

                            if (additiveBitmap != null)
                            {
                                dc.PrimitiveBlend = PrimitiveBlend.Add;
                                blendModified = true;
                                dc.DrawBitmap(additiveBitmap, destRect, opacity, InterpolationMode.Linear, null, null);
                            }

                            drewFrame = true;
                        }
                    }
                    catch (Exception)
                    {
                        // Fall back to vector / default fallback if frame archive or frame entry is missing
                        drewFrame = false;
                    }

                    if (!drewFrame)
                    {
                        DrawFallbackBurst(dc, laneX, judgeY, elapsedMs, width, config.Layout, opacity);
                    }
                }
            }
        }
        finally
        {
            if (blendModified)
                dc.PrimitiveBlend = originalBlend;
        }
    }

    private void DrawFallbackBurst(ID2D1DeviceContext dc, float laneX, float judgeY,
        double elapsedMs, double width, MusicOverlayLayoutConfig layout, float opacity)
    {
        double durationMs = _hitDurationMs > 0 ? _hitDurationMs : HitBurstEffectRenderer.FallbackDurationMs;
        if (elapsedMs < 0 || elapsedMs > durationMs) return;

        double progress = Math.Clamp(elapsedMs / durationMs, 0.0, 1.0);
        double inv = 1.0 - progress;
        double easeOut = 1.0 - (inv * inv * inv);

        float scaleFactor = (float)((width / 1920.0) * layout.Scale * _hitEffectScale);

        // 1. Expanding shockwave ring
        float ringRadius = (float)((25.0 + 55.0 * easeOut) * scaleFactor);
        float ringAlpha = (float)Math.Clamp(opacity * (1.0 - easeOut) * 0.85, 0.0, 1.0);

        if (ringAlpha > 0.001f)
        {
            var brush = Brush(MusicGpuRenderer.Ink.CenterGuide);
            float oldOpacity = brush.Opacity;
            brush.Opacity = ringAlpha;
            dc.DrawEllipse(new Ellipse(new Vector2(laneX, judgeY), ringRadius, ringRadius),
                brush, 2.5f * scaleFactor);
            brush.Opacity = oldOpacity;
        }

        // 2. Central blooming
        double cloverScale = 0.5 + 0.6 * easeOut;
        float cloverSize = (float)(110.0 * scaleFactor * cloverScale);
        float cloverAlpha = (float)Math.Clamp(opacity * (1.0 - progress) * 0.95, 0.0, 1.0);

        if (cloverAlpha > 0.001f)
        {
            if (Tap != null)
            {
                var flRect = new Rect(laneX - cloverSize / 2f, judgeY - cloverSize / 2f, cloverSize, cloverSize);
                dc.DrawBitmap(Tap, flRect, cloverAlpha, InterpolationMode.Linear, null, null);
            }
            else
            {
                var tapBrush = Brush(MusicGpuRenderer.Ink.Tap);
                var whiteBrush = Brush(MusicGpuRenderer.Ink.White);
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

    private ID2D1Bitmap1? GetOrLoadFrame(ID2D1DeviceContext dc, string url, int frameIndex, ID2D1Bitmap1?[] cache)
    {
        if (frameIndex < 0 || frameIndex >= cache.Length) return null;
        if (cache[frameIndex] != null) return cache[frameIndex];

        var pixels = ReferenceFrameStore.Get(url, frameIndex);
        if (pixels == null || pixels.Bytes == null || pixels.Width <= 0 || pixels.Height <= 0) return null;

        var pinned = GCHandle.Alloc(pixels.Bytes, GCHandleType.Pinned);
        try
        {
            uint stride = (uint)(pixels.Width * 4);
            var bitmap = dc.CreateBitmap(new SizeI(pixels.Width, pixels.Height), pinned.AddrOfPinnedObject(), stride,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96));
            _textures.Add(bitmap);
            cache[frameIndex] = bitmap;
            return bitmap;
        }
        finally
        {
            pinned.Free();
        }
    }

    private readonly record struct Sprite(string Url, System.Windows.Rect Bounds, bool MirrorX = false);

    private static IEnumerable<Sprite> GenerateSprites(ReferenceSkinDefinition definition, ReferenceBackgroundLayer layer, double layoutScale, IReadOnlyDictionary<string, ReferenceSkinImage>? images)
    {
        if (images == null || !images.TryGetValue(layer.Url, out var image)) yield break;
        var fit = layer.Fit;
        double scale = fit.Scale * layoutScale;
        double width = image.SourceWidth * scale, height = image.SourceHeight * scale;
        double bottomOffset = fit.BottomOffsetPx * layoutScale;
        double bottom = 1080 + bottomOffset;
        switch (fit.Kind)
        {
            case "stretch":
                yield return new(layer.Url, new System.Windows.Rect(0, 0, 1920, 1080));
                break;
            case "bottomBand":
                yield return new(layer.Url, new System.Windows.Rect(0, 1080 * fit.TopRatio, 1920, 1080 * (1 - fit.TopRatio)));
                break;
            case "center":
                yield return new(layer.Url, new System.Windows.Rect((1920 - width) / 2,
                    fit.YAnchor == "center" ? (1080 - height) / 2 + bottomOffset : bottom - height, width, height));
                break;
            case "tile":
                if (width <= 0 || height <= 0) yield break;
                double phase = ((1920 - width) / 2) % width;
                for (double x = phase - width; x < 1920; x += width)
                    yield return new(layer.Url, new System.Windows.Rect(x, bottom - height, width, height));
                break;
            case "mirroredArch":
                if (fit.WingUrl == null || !images.TryGetValue(fit.WingUrl, out var wing)) yield break;
                double wingWidth = wing.SourceWidth * scale, wingHeight = wing.SourceHeight * scale;
                if (wingWidth <= 0 || wingHeight <= 0) yield break;
                yield return new(layer.Url, new System.Windows.Rect(1920 / 2.0 - width, bottom - height, width, height));
                yield return new(layer.Url, new System.Windows.Rect(1920 / 2.0, bottom - height, width, height), true);
                for (double x = 1920 / 2.0 - width; x > 0; x -= wingWidth)
                    yield return new(fit.WingUrl, new System.Windows.Rect(x - wingWidth, bottom - wingHeight, wingWidth, wingHeight));
                for (double x = 1920 / 2.0 + width; x < 1920; x += wingWidth)
                    yield return new(fit.WingUrl, new System.Windows.Rect(x, bottom - wingHeight, wingWidth, wingHeight), true);
                break;
            case "symmetricPair":
                double edge = fit.EdgeOffsetPx * layoutScale;
                double? centerOffset = fit.CenterOffsetPx * layoutScale;
                double top = fit.TopOffsetPx * layoutScale;
                double y = fit.YAnchor == "top" ? top : bottom - height;
                double leftX = centerOffset.HasValue ? 1920 / 2.0 - centerOffset.Value - width : edge;
                yield return new(layer.Url, new System.Windows.Rect(leftX, y, width, height));
                bool mirror = string.IsNullOrEmpty(fit.RightUrl);
                string rightUrl = !mirror && images.ContainsKey(fit.RightUrl!) ? fit.RightUrl! : layer.Url;
                var right = !mirror && images.TryGetValue(fit.RightUrl!, out var explicitRight) ? explicitRight : image;
                double rightWidth = right.SourceWidth * scale, rightHeight = right.SourceHeight * scale;
                double rightX = centerOffset.HasValue ? 1920 / 2.0 + centerOffset.Value : 1920 - edge - rightWidth;
                double rightY = mirror ? y : fit.YAnchor == "top" ? top : bottom - rightHeight;
                yield return new(rightUrl, new System.Windows.Rect(rightX, rightY, rightWidth, rightHeight), mirror);
                break;
        }
    }

    private ID2D1Bitmap1? GetLayerBitmap(ID2D1DeviceContext dc, string url, uint tint)
    {
        var key = (url, tint);
        if (_layerBitmaps.TryGetValue(key, out var cached)) return cached;

        if (_resources == null || !_resources.Images.TryGetValue(url, out var image))
            return null;

        ID2D1Bitmap1? bitmap;
        if (tint == 0 || tint == 0xffffff)
        {
            bitmap = MusicGpuDefaultSprites.CreateBitmap(dc, image.Bitmap);
        }
        else
        {
            var converted = new FormatConvertedBitmap(image.Bitmap, PixelFormats.Pbgra32, null, 0);
            int width = converted.PixelWidth, height = converted.PixelHeight, stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            converted.CopyPixels(pixels, stride, 0);

            double tintB = (tint & 255) / 255.0;
            double tintG = ((tint >> 8) & 255) / 255.0;
            double tintR = ((tint >> 16) & 255) / 255.0;

            for (int p = 0; p < pixels.Length; p += 4)
            {
                pixels[p] = (byte)Math.Clamp(Math.Round(pixels[p] * tintB), 0, 255);
                pixels[p + 1] = (byte)Math.Clamp(Math.Round(pixels[p + 1] * tintG), 0, 255);
                pixels[p + 2] = (byte)Math.Clamp(Math.Round(pixels[p + 2] * tintR), 0, 255);
            }

            var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                bitmap = dc.CreateBitmap(new SizeI(width, height), pinned.AddrOfPinnedObject(), (uint)stride,
                    new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96));
            }
            finally
            {
                pinned.Free();
            }
        }

        if (bitmap != null)
        {
            _textures.Add(bitmap);
            _layerBitmaps[key] = bitmap;
        }
        return bitmap;
    }

    private ID2D1Bitmap1? UploadTinted(ID2D1DeviceContext dc, byte[] basePixels, int width, int height, uint color)
    {
        int stride = checked(width * 4);
        var tinted = new byte[checked(stride * height)];
        double tintB = (color & 255) / 255.0;
        double tintG = ((color >> 8) & 255) / 255.0;
        double tintR = ((color >> 16) & 255) / 255.0;

        for (int p = 0; p < basePixels.Length; p += 4)
        {
            tinted[p] = (byte)Math.Clamp(Math.Round(basePixels[p] * tintB), 0, 255);
            tinted[p + 1] = (byte)Math.Clamp(Math.Round(basePixels[p + 1] * tintG), 0, 255);
            tinted[p + 2] = (byte)Math.Clamp(Math.Round(basePixels[p + 2] * tintR), 0, 255);
            tinted[p + 3] = basePixels[p + 3];
        }

        var pinned = GCHandle.Alloc(tinted, GCHandleType.Pinned);
        try
        {
            var bitmap = dc.CreateBitmap(new SizeI(width, height), pinned.AddrOfPinnedObject(), (uint)stride,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96));
            _textures.Add(bitmap);
            return bitmap;
        }
        finally
        {
            pinned.Free();
        }
    }

    private ID2D1Bitmap1 CreateFallbackBeam(ID2D1DeviceContext dc, uint color)
    {
        byte r = (byte)((color >> 16) & 255), g = (byte)((color >> 8) & 255), b = (byte)(color & 255);
        byte rb = (byte)Math.Min(255, r + 55), gb = (byte)Math.Min(255, g + 55), bb = (byte)Math.Min(255, b + 55);

        using var beamBody = MusicGpuDefaultSprites.Gradient(dc,
            (0, 0, r, g, b), (.12, .12, r, g, b), (.4, .24, r, g, b),
            (.75, .34, r, g, b), (.95, .4, rb, gb, bb), (1, .44, rb, gb, bb));
        using var beamGlow = MusicGpuDefaultSprites.Gradient(dc,
            (0, 0, r, g, b), (.3, .18, r, g, b), (.8, .5, rb, gb, bb), (1, .7, rb, gb, bb));
        using var beamEdge = MusicGpuDefaultSprites.Gradient(dc,
            (0, .02, 255, 255, 255), (.2, .45, rb, gb, bb),
            (.65, .85, 235, 248, 255), (1, .98, 255, 255, 255));

        var beam = MusicGpuDefaultSprites.Target(dc, 144, 512, 96);
        using var previous = dc.Target;
        dc.GetDpi(out float dpiX, out float dpiY);
        try
        {
            dc.Target = beam;
            dc.SetDpi(96, 96);
            dc.Clear(new Color4(0, 0, 0, 0));
            dc.FillRectangle(new Rect(10, 0, 124, 512), beamBody);
            foreach (float x in new[] { 10f, 134f })
                dc.DrawLine(new Vector2(x, 0), new Vector2(x, 512), beamGlow, 8);
            foreach (float x in new[] { 10f, 134f })
                dc.DrawLine(new Vector2(x, 0), new Vector2(x, 512), beamEdge, 2.4f);
        }
        finally
        {
            dc.Target = previous;
            dc.SetDpi(dpiX, dpiY);
        }
        _textures.Add(beam);
        return beam;
    }

    private ID2D1Bitmap1? Upload(ID2D1DeviceContext dc, BitmapSource? source)
    {
        var bitmap = MusicGpuDefaultSprites.CreateBitmap(dc, source);
        if (bitmap != null) _textures.Add(bitmap);
        return bitmap;
    }

    internal ID2D1SolidColorBrush Brush(MusicGpuRenderer.Ink ink) => _brushes[(int)ink];

    private static Color4 HexToColor4(uint rgb, float alpha) =>
        new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, alpha);

    private static Color4 ExtractColor(Brush? brush, Color4 fallback)
    {
        if (brush is SolidColorBrush scb)
            return new Color4(scb.Color.R / 255f, scb.Color.G / 255f, scb.Color.B / 255f, scb.Color.A / 255f);
        return fallback;
    }

    public void Dispose()
    {
        _assembledBackground?.Dispose();
        _assembledBackground = null;
        _trackBackgroundBrush?.Dispose();
        foreach (var brush in _brushes) brush?.Dispose();
        Array.Clear(_brushes);
        foreach (var tex in _textures) tex.Dispose();
        _textures.Clear();
        _layerBitmaps.Clear();
        Array.Clear(_holdLayers);
        Array.Clear(_normalFrames);
        Array.Clear(_additiveFrames);
        Array.Clear(_holdNormalFrames);
        Array.Clear(_holdAdditiveFrames);
        _holdEmitters.Clear();
    }
}

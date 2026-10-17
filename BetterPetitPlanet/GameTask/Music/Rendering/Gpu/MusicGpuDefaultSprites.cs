using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using GradientStop = Vortice.Direct2D1.GradientStop;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace BetterPetitPlanet.GameTask.Music.Rendering.Gpu;

/// <summary>
/// Device-owned static default-skin sprites and a GPU-only assembled background.
/// PNG pixels upload once per device. Targets are never mapped or read back to the CPU.
/// </summary>
internal sealed class MusicGpuDefaultSprites : IDisposable
{
    internal static readonly Lazy<Dictionary<string, (byte[] Pixels, int Width, int Height)>> RawHoldSprites = new(LoadAllHoldSprites);

    private readonly List<ID2D1Bitmap1> _textures = new();
    private readonly ID2D1Bitmap1?[] _holdLayers = new ID2D1Bitmap1?[NahidaHoldParticles.Styles.Count];
    private readonly ID2D1Bitmap1? _backgroundImage, _gradient, _arch, _wing, _warmBand;
    private readonly ID2D1SolidColorBrush _backdrop;
    private readonly ID2D1LinearGradientBrush _beamBody, _beamGlow, _beamEdge;
    private ID2D1Bitmap1? _background;
    private bool _beamReady;
    private (float Width, float Height, float Dpi, double Scale) _backgroundKey;
    public ID2D1Bitmap1? Tap { get; }
    public ID2D1Bitmap1? Hold { get; }
    public ID2D1Bitmap1 Beam { get; }
    public ID2D1Bitmap1? HitAtlas { get; internal set; }
    public ID2D1Bitmap1? HitShockwave { get; internal set; }
    public ID2D1Bitmap1? HitFlower { get; internal set; }
    public IReadOnlyList<ID2D1Bitmap1?> HoldLayers => _holdLayers;

    public MusicGpuDefaultSprites(ID2D1DeviceContext dc)
    {
        try
        {
            Tap = Upload(dc, MusicDefaultSkin.NoteTapBitmap);
            Hold = Upload(dc, MusicDefaultSkin.NoteHoldBitmap);
            HitAtlas = Upload(dc, MusicDefaultSkin.HitAtlasBitmap);
            HitShockwave = Upload(dc, MusicDefaultSkin.HitShockwaveBitmap);
            HitFlower = Upload(dc, MusicDefaultSkin.HitFlowerBitmap);
            _backgroundImage = Upload(dc, MusicDefaultSkin.BackgroundBitmap);
            _gradient = Upload(dc, MusicDefaultSkin.BackgroundGradientBitmap);
            _arch = Upload(dc, MusicDefaultSkin.BackgroundArchBitmap);
            _wing = Upload(dc, MusicDefaultSkin.BackgroundWingBitmap);
            _warmBand = Upload(dc, MusicDefaultSkin.BackgroundBottomBitmap, warmBand: true);
            _backdrop = dc.CreateSolidColorBrush(new Color4(18 / 255f, 49 / 255f, 74 / 255f, 180 / 255f));
            Beam = Target(dc, 144, 512, 96);
            const uint color = 0x78e3c5;
            byte r = (byte)((color >> 16) & 255), g = (byte)((color >> 8) & 255), b = (byte)(color & 255);
            byte rb = (byte)Math.Min(255, r + 55), gb = (byte)Math.Min(255, g + 55), bb = (byte)Math.Min(255, b + 55);
            _beamBody = Gradient(dc,
                (0, 0, r, g, b), (.12, .12, r, g, b), (.4, .24, r, g, b),
                (.75, .34, r, g, b), (.95, .4, rb, gb, bb), (1, .44, rb, gb, bb));
            _beamGlow = Gradient(dc, (0, 0, r, g, b), (.3, .18, r, g, b), (.8, .5, rb, gb, bb), (1, .7, rb, gb, bb));
            _beamEdge = Gradient(dc, (0, .02, 255, 255, 255), (.2, .45, rb, gb, bb),
                (.65, .85, 235, 248, 255), (1, .98, 255, 255, 255));

            // Upload pre-tinted hold particle bitmaps (deduplicating identical texture & color combinations)
            var rawSprites = RawHoldSprites.Value;
            var uniqueHoldBitmaps = new Dictionary<(string Texture, uint Color), ID2D1Bitmap1>();
            for (int i = 0; i < NahidaHoldParticles.Styles.Count; i++)
            {
                var style = NahidaHoldParticles.Styles[i];
                var key = (style.Texture, style.Color);
                if (!uniqueHoldBitmaps.TryGetValue(key, out var bitmap))
                {
                    if (rawSprites.TryGetValue(style.Texture, out var sprite))
                    {
                        bitmap = UploadTinted(dc, sprite.Pixels, sprite.Width, sprite.Height, style.Color);
                        if (bitmap != null)
                        {
                            uniqueHoldBitmaps[key] = bitmap;
                        }
                    }
                }
                _holdLayers[i] = bitmap;
            }
        }
        catch { Dispose(); throw; }
    }

    public void Prepare(ID2D1DeviceContext dc)
    {
        if (_beamReady) return;
        using var previous = dc.Target;
        dc.GetDpi(out float dpiX, out float dpiY);
        try
        {
            dc.Target = Beam; dc.SetDpi(96, 96); dc.Clear(new Color4(0, 0, 0, 0));
            dc.FillRectangle(new Rect(10, 0, 124, 512), _beamBody);
            foreach (float x in new[] { 10f, 134f })
                dc.DrawLine(new Vector2(x, 0), new Vector2(x, 512), _beamGlow, 8);
            foreach (float x in new[] { 10f, 134f })
                dc.DrawLine(new Vector2(x, 0), new Vector2(x, 512), _beamEdge, 2.4f);
            _beamReady = true;
        }
        finally { dc.Target = previous; dc.SetDpi(dpiX, dpiY); }
    }

    public void DrawBackground(ID2D1DeviceContext dc, float width, float height, float dpi, double layoutScale, float opacity)
    {
        if (opacity <= 0) return;
        var key = (width, height, dpi, layoutScale);
        if (_background == null || _backgroundKey != key)
        {
            _background?.Dispose();
            _background = Target(dc, Math.Max(1, (int)Math.Ceiling(width * dpi / 96)),
                Math.Max(1, (int)Math.Ceiling(height * dpi / 96)), dpi);
            using var previous = dc.Target;
            var transform = dc.Transform;
            var blend = dc.PrimitiveBlend;
            try
            {
                dc.Target = _background; dc.Transform = Matrix3x2.Identity;
                dc.Clear(new Color4(0, 0, 0, 0));
                dc.FillRectangle(new Rect(0, 0, width, height), _backdrop);
                Draw(dc, _backgroundImage, 0, 0, width, height);
                Draw(dc, _gradient, 0, 0, width, height, .85f);
                if (_arch != null && _wing != null)
                {
                    float scale = (float)(.78 * width / 1920 * layoutScale);
                    float archW = _arch.Size.Width * scale, archH = _arch.Size.Height * scale;
                    float wingW = _wing.Size.Width * scale, wingH = _wing.Size.Height * scale;
                    float center = width / 2, bottom = (float)(height + 60 * height / 1080 * layoutScale);
                    for (int side = 0; side < 2; side++)
                    {
                        dc.Transform = side == 0 ? Matrix3x2.Identity : Matrix3x2.CreateScale(-1, 1, new Vector2(center, 0));
                        Draw(dc, _arch, center - archW, bottom - archH, archW, archH);
                        if (wingW > 0)
                            for (float x = center - archW; x > 0; x -= wingW)
                                Draw(dc, _wing, x - wingW, bottom - wingH, wingW, wingH);
                    }
                    dc.Transform = Matrix3x2.Identity;
                }
                // Match MusicBackgroundRenderer's sRGB byte-channel add before group opacity.
                dc.PrimitiveBlend = PrimitiveBlend.Add;
                Draw(dc, _warmBand, 0, height * .45f, width, height * .55f);
                _backgroundKey = key;
            }
            finally { dc.Target = previous; dc.Transform = transform; dc.PrimitiveBlend = blend; }
        }
        Draw(dc, _background, 0, 0, width, height, opacity);
    }

    internal static void Draw(ID2D1DeviceContext dc, ID2D1Bitmap1? bitmap,
        double x, double y, double width, double height, float opacity = 1)
    {
        if (bitmap == null || width <= 0 || height <= 0) return;
        dc.DrawBitmap(bitmap, new Vortice.RawRectF((float)x, (float)y, (float)(x + width), (float)(y + height)),
            opacity, InterpolationMode.Linear, null, null);
    }

    internal static ID2D1Bitmap1? CreateBitmap(ID2D1DeviceContext dc, BitmapSource? source, bool warmBand = false)
    {
        if (source == null) return null;
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int width = converted.PixelWidth, height = converted.PixelHeight, stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        converted.CopyPixels(pixels, stride, 0);
        if (warmBand)
            for (int p = 0; p < pixels.Length; p += 4)
            {
                pixels[p] = (byte)Math.Round(pixels[p] * .45 * 40 / 255);
                pixels[p + 1] = (byte)Math.Round(pixels[p + 1] * .45 * 200 / 255);
                pixels[p + 2] = (byte)Math.Round(pixels[p + 2] * .45);
                pixels[p + 3] = (byte)Math.Round(pixels[p + 3] * .45);
            }
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return dc.CreateBitmap(new SizeI(width, height), pinned.AddrOfPinnedObject(), (uint)stride,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96));
        }
        finally { pinned.Free(); }
    }

    private ID2D1Bitmap1? Upload(ID2D1DeviceContext dc, BitmapSource? source, bool warmBand = false)
    {
        var bitmap = CreateBitmap(dc, source, warmBand);
        if (bitmap != null) _textures.Add(bitmap);
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

    private static Dictionary<string, (byte[] Pixels, int Width, int Height)> LoadAllHoldSprites()
    {
        var dict = new Dictionary<string, (byte[] Pixels, int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "glow", "clover", "ring", "dot", "star" })
        {
            string? path = MusicDefaultSkin.ResolveAssetPath("hold/" + name + ".png")
                ?? MusicDefaultSkin.ResolveAssetPath(Path.Combine("Reference", "assets", "hold", name + ".png"))
                ?? MusicDefaultSkin.ResolveAssetPath(name + ".png")
                ?? MusicDefaultSkin.ResolveAssetPath("hold-" + name + ".png");

            if (path == null || !File.Exists(path)) continue;

            try
            {
                using var stream = File.OpenRead(path);
                var source = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
                int width = converted.PixelWidth;
                int height = converted.PixelHeight;
                int stride = checked(width * 4);
                var pixels = new byte[checked(stride * height)];
                converted.CopyPixels(pixels, stride, 0);
                dict[name] = (pixels, width, height);
            }
            catch
            {
                // Non-fatal fallback for missing or corrupted optional asset
            }
        }
        return dict;
    }

    internal static ID2D1Bitmap1 Target(ID2D1DeviceContext dc, int width, int height, float dpi)
        => dc.CreateBitmap(new SizeI(width, height), IntPtr.Zero, 0,
            new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), dpi, dpi, BitmapOptions.Target));

    internal static ID2D1LinearGradientBrush Gradient(ID2D1DeviceContext dc,
        params (double Position, double Alpha, byte R, byte G, byte B)[] values)
    {
        var stops = new GradientStop[values.Length];
        for (int i = 0; i < stops.Length; i++)
        {
            var value = values[i];
            stops[i] = new GradientStop { Position = (float)value.Position,
                Color = new Color4(value.R / 255f, value.G / 255f, value.B / 255f, (float)((byte)(value.Alpha * 255) / 255.0)) };
        }
        using var collection = dc.CreateGradientStopCollection(stops);
        return dc.CreateLinearGradientBrush(new LinearGradientBrushProperties
            { StartPoint = Vector2.Zero, EndPoint = new Vector2(0, 512) }, collection);
    }

    public void Dispose()
    {
        _background?.Dispose(); _background = null;
        Beam?.Dispose(); _beamBody?.Dispose(); _beamGlow?.Dispose(); _beamEdge?.Dispose(); _backdrop?.Dispose();
        foreach (var bitmap in _textures) bitmap.Dispose();
        _textures.Clear();
        Array.Clear(_holdLayers);
    }
}

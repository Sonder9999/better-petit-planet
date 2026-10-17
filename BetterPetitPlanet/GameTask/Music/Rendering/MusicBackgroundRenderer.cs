using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

internal static class MusicBackgroundRenderer
{
    public static void Draw(DrawingContext target, double width, double height, MusicOverlayLayoutConfig layout,
        MusicOverlayEffectsConfig effects, MusicSkin skin)
    {
        if (!effects.ShowBackgroundDeco || effects.BackgroundOpacity <= 0) return;
        BitmapSource? Resolve(BitmapSource? image, BitmapSource? fallback) => image ?? (skin.IsCustom ? null : fallback);
        var background = Resolve(skin.BackgroundBitmap, MusicDefaultSkin.BackgroundBitmap);
        var gradient = Resolve(skin.BackgroundGradientBitmap, MusicDefaultSkin.BackgroundGradientBitmap);
        var arch = Resolve(skin.BackgroundArchBitmap, MusicDefaultSkin.BackgroundArchBitmap);
        var wing = Resolve(skin.BackgroundWingBitmap, MusicDefaultSkin.BackgroundWingBitmap);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(skin.TrackBackgroundBrush, null, new Rect(0, 0, width, height));
            if (background != null) dc.DrawImage(background, new Rect(0, 0, width, height));
            if (gradient != null)
            {
                dc.PushOpacity(.85); dc.DrawImage(gradient, new Rect(0, 0, width, height)); dc.Pop();
            }
            if (arch != null && (wing != null || skin.IsCustom))
            {
                double scale = .78 * width / 1920 * layout.Scale;
                double midW = arch.PixelWidth * scale, midH = arch.PixelHeight * scale;
                double wingW = (wing?.PixelWidth ?? 1) * scale, wingH = (wing?.PixelHeight ?? 1) * scale;
                double bottom = height + 60 * height / 1080 * layout.Scale;
                double center = width / 2;
                dc.DrawImage(arch, new Rect(center - midW, bottom - midH, midW, midH));
                for (double x = center - midW; wing != null && x > 0; x -= wingW)
                    dc.DrawImage(wing, new Rect(x - wingW, bottom - wingH, wingW, wingH));
                dc.PushTransform(new ScaleTransform(-1, 1, center, 0));
                dc.DrawImage(arch, new Rect(center - midW, bottom - midH, midW, midH));
                for (double x = center - midW; wing != null && x > 0; x -= wingW)
                    dc.DrawImage(wing, new Rect(x - wingW, bottom - wingH, wingW, wingH));
                dc.Pop();
            }
        }
        // Static background is cached by the playfield. Add the original warm bottom
        // band in sRGB, matching Background's "add" blend, once per layout change.
        target.PushOpacity(Math.Clamp(effects.BackgroundOpacity, 0, 1));
        if (!skin.IsCustom && MusicDefaultSkin.BackgroundBottomBitmap is { } band)
        {
            int w = Math.Max(1, (int)Math.Ceiling(width)), h = Math.Max(1, (int)Math.Ceiling(height));
            var baseBitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            baseBitmap.Render(visual);
            var pixels = new byte[w * h * 4]; baseBitmap.CopyPixels(pixels, w * 4, 0);
            var lightVisual = new DrawingVisual();
            using (var dc = lightVisual.RenderOpen()) dc.DrawImage(band, new Rect(0, height * .45, width, height * .55));
            var lightBitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); lightBitmap.Render(lightVisual);
            var light = new byte[pixels.Length]; lightBitmap.CopyPixels(light, w * 4, 0);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = Add(pixels[i], light[i], .45 * 40 / 255);
                pixels[i + 1] = Add(pixels[i + 1], light[i + 1], .45 * 200 / 255);
                pixels[i + 2] = Add(pixels[i + 2], light[i + 2], .45);
                pixels[i + 3] = Add(pixels[i + 3], light[i + 3], .45);
            }
            var combined = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
            combined.Freeze(); target.DrawImage(combined, new Rect(0, 0, width, height));
        }
        else target.DrawDrawing(visual.Drawing);
        target.Pop();
    }

    private static byte Add(byte background, byte light, double gain) => (byte)Math.Min(255, Math.Round(background + light * gain));
}

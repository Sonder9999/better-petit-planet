using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

/// <summary>Static background.ts layers in the reference's 1920 by 1080 design space.</summary>
public static class ReferenceBackgroundRenderer
{
    private const double DesignWidth = 1920, DesignHeight = 1080;

    public static void Draw(DrawingContext target, double width, double height, MusicOverlayLayoutConfig layout,
        MusicOverlayEffectsConfig effects, ReferenceSkinResources resources, double pixelsPerDip = 1)
    {
        if (!effects.ShowBackgroundDeco || effects.BackgroundOpacity <= 0 || width <= 0 || height <= 0) return;
        int w = Math.Max(1, (int)Math.Ceiling(width * pixelsPerDip));
        int h = Math.Max(1, (int)Math.Ceiling(height * pixelsPerDip));
        var back = new byte[checked(w * h * 4)];
        uint backdrop = resources.Definition.Backdrop;
        for (int p = 0; p < back.Length; p += 4)
        {
            back[p] = (byte)backdrop; back[p + 1] = (byte)(backdrop >> 8);
            back[p + 2] = (byte)(backdrop >> 16); back[p + 3] = 255;
        }

        var front = new byte[back.Length];
        var capture = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(capture, BitmapScalingMode.Linear);
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        foreach (var layer in resources.Definition.Background)
        {
            if (layer.Alpha <= 0) continue;
            foreach (var sprite in Sprites(resources, layer, layout.Scale))
            {
                if (!sprite.Bounds.IntersectsWith(new Rect(0, 0, DesignWidth, DesignHeight))) continue;
                using (var dc = capture.RenderOpen())
                {
                    dc.PushTransform(new ScaleTransform(w / DesignWidth, h / DesignHeight));
                    if (sprite.MirrorX)
                        dc.PushTransform(new ScaleTransform(-1, 1, sprite.Bounds.X + sprite.Bounds.Width / 2, 0));
                    dc.DrawImage(sprite.Image.Bitmap, sprite.Bounds);
                    if (sprite.MirrorX) dc.Pop();
                    dc.Pop();
                }
                bitmap.Clear(); bitmap.Render(capture); bitmap.CopyPixels(front, w * 4, 0);
                // Pixi inherits container alpha, tint and blend onto each child sprite.
                // Flattening the raw container before blending loses overlap contributions.
                ReferencePixels.BlendSrgb(back, front, layer.Blend == "add", Math.Clamp(layer.Alpha, 0, 1), layer.Tint);
            }
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, back, w * 4);
        result.Freeze();
        target.PushOpacity(Math.Clamp(effects.BackgroundOpacity, 0, 1));
        target.DrawImage(result, new Rect(0, 0, width, height));
        target.Pop();
    }

    private readonly record struct Sprite(ReferenceSkinImage Image, Rect Bounds, bool MirrorX = false);

    private static IEnumerable<Sprite> Sprites(ReferenceSkinResources resources, ReferenceBackgroundLayer layer, double layoutScale)
    {
        if (!resources.Images.TryGetValue(layer.Url, out var image)) yield break;
        var fit = layer.Fit;
        double scale = fit.Scale * layoutScale;
        double width = image.SourceWidth * scale, height = image.SourceHeight * scale;
        double bottomOffset = fit.BottomOffsetPx * layoutScale;
        double bottom = DesignHeight + bottomOffset;
        switch (fit.Kind)
        {
            case "stretch":
                yield return new(image, new Rect(0, 0, DesignWidth, DesignHeight));
                break;
            case "bottomBand":
                yield return new(image, new Rect(0, DesignHeight * fit.TopRatio, DesignWidth, DesignHeight * (1 - fit.TopRatio)));
                break;
            case "center":
                yield return new(image, new Rect((DesignWidth - width) / 2,
                    fit.YAnchor == "center" ? (DesignHeight - height) / 2 + bottomOffset : bottom - height, width, height));
                break;
            case "tile":
                if (width <= 0 || height <= 0) yield break;
                double phase = ((DesignWidth - width) / 2) % width;
                for (double x = phase - width; x < DesignWidth; x += width)
                    yield return new(image, new Rect(x, bottom - height, width, height));
                break;
            case "mirroredArch":
                if (fit.WingUrl == null || !resources.Images.TryGetValue(fit.WingUrl, out var wing)) yield break;
                double wingWidth = wing.SourceWidth * scale, wingHeight = wing.SourceHeight * scale;
                if (wingWidth <= 0 || wingHeight <= 0) yield break;
                yield return new(image, new Rect(DesignWidth / 2 - width, bottom - height, width, height));
                yield return new(image, new Rect(DesignWidth / 2, bottom - height, width, height), true);
                for (double x = DesignWidth / 2 - width; x > 0; x -= wingWidth)
                    yield return new(wing, new Rect(x - wingWidth, bottom - wingHeight, wingWidth, wingHeight));
                for (double x = DesignWidth / 2 + width; x < DesignWidth; x += wingWidth)
                    yield return new(wing, new Rect(x, bottom - wingHeight, wingWidth, wingHeight), true);
                break;
            case "symmetricPair":
                double edge = fit.EdgeOffsetPx * layoutScale;
                double? centerOffset = fit.CenterOffsetPx * layoutScale;
                double top = fit.TopOffsetPx * layoutScale;
                double y = fit.YAnchor == "top" ? top : bottom - height;
                double leftX = centerOffset.HasValue ? DesignWidth / 2 - centerOffset.Value - width : edge;
                yield return new(image, new Rect(leftX, y, width, height));
                bool mirror = string.IsNullOrEmpty(fit.RightUrl);
                var right = !mirror && resources.Images.TryGetValue(fit.RightUrl!, out var explicitRight) ? explicitRight : image;
                double rightWidth = right.SourceWidth * scale, rightHeight = right.SourceHeight * scale;
                double rightX = centerOffset.HasValue ? DesignWidth / 2 + centerOffset.Value : DesignWidth - edge - rightWidth;
                double rightY = mirror ? y : fit.YAnchor == "top" ? top : bottom - rightHeight;
                yield return new(right, new Rect(rightX, rightY, rightWidth, rightHeight), mirror);
                break;
        }
    }
}

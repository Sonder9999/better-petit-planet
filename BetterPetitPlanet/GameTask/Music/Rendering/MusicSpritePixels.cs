using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

/// <summary>Bilinear premultiplied sRGB sprite blending, matching Pixi's normal/add
/// blend modes, including per-sprite tint, rotation and alpha.</summary>
internal sealed class MusicSpritePixels
{
    private readonly byte[] _pixels;
    private readonly int _width, _height;

    public MusicSpritePixels(string path)
    {
        using var diagnostic = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.HoldTextureDecodeMs);
        using var stream = File.OpenRead(path);
        var source = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        _width = bitmap.PixelWidth; _height = bitmap.PixelHeight;
        _pixels = new byte[_width * _height * 4];
        bitmap.CopyPixels(_pixels, _width * 4, 0);
    }

    public void Blend(byte[] target, int side, HoldParticle particle, HoldParticleStyle style, double scale, double intensity)
    {
        double alpha = Math.Clamp(particle.Alpha * intensity, 0, 1);
        if (alpha <= 0 || particle.Size <= 0) return;
        double size = particle.Size * scale, cx = side / 2.0 + particle.X * scale, cy = side / 2.0 + particle.Y * scale;
        double cos = Math.Cos(particle.Rotation), sin = Math.Sin(particle.Rotation);
        double half = size * (Math.Abs(cos) + Math.Abs(sin)) / 2;
        int left = Math.Max(0, (int)Math.Floor(cx - half)), right = Math.Min(side, (int)Math.Ceiling(cx + half));
        int top = Math.Max(0, (int)Math.Floor(cy - half)), bottom = Math.Min(side, (int)Math.Ceiling(cy + half));
        Span<double> tint = stackalloc double[] { (style.Color & 255) / 255.0, ((style.Color >> 8) & 255) / 255.0, ((style.Color >> 16) & 255) / 255.0, 1 };
        Span<double> sample = stackalloc double[4];
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                double dx = x + .5 - cx, dy = y + .5 - cy;
                double u = (cos * dx + sin * dy) / size + .5, v = (-sin * dx + cos * dy) / size + .5;
                if (u < 0 || u >= 1 || v < 0 || v >= 1) continue;
                double sx = Math.Clamp(u * _width - .5, 0, _width - 1), sy = Math.Clamp(v * _height - .5, 0, _height - 1);
                int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(x0 + 1, _width - 1), y1 = Math.Min(y0 + 1, _height - 1);
                double fx = sx - x0, fy = sy - y0;
                int a = (y0 * _width + x0) * 4, b = (y0 * _width + x1) * 4;
                int c = (y1 * _width + x0) * 4, d = (y1 * _width + x1) * 4;
                for (int channel = 0; channel < 4; channel++)
                    sample[channel] = ((_pixels[a + channel] * (1 - fx) + _pixels[b + channel] * fx) * (1 - fy)
                        + (_pixels[c + channel] * (1 - fx) + _pixels[d + channel] * fx) * fy) * alpha * tint[channel];
                int p = (y * side + x) * 4;
                double retained = style.Additive ? 1 : 1 - sample[3] / 255;
                for (int channel = 0; channel < 4; channel++)
                    target[p + channel] = (byte)Math.Clamp(Math.Round(sample[channel] + target[p + channel] * retained), 0, 255);
            }
    }
}

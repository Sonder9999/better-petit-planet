using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

/// <summary>Premultiplied RGBA arithmetic for the reference's sprite and filter passes.</summary>
internal sealed class ReferencePixels
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Bytes { get; }
    public long ByteSize => Bytes.LongLength + (_rowLeft.LongLength + _rowRight.LongLength) * sizeof(int);

    private readonly int[] _rowLeft, _rowRight;
    private readonly int _left, _right, _top, _bottom;

    // A fixed 512 KiB table preserves the original double-precision decode,
    // including unpremultiplication; encoding still uses the exact equation.
    private static readonly double[] LinearColors = CreateLinearColors();
    // Another fixed 64 KiB table selects the opaque output byte. The 255
    // double thresholds correct its quantization before the byte is returned.
    private const int OpaqueEncodeSteps = 65536;
    private static readonly byte[] OpaqueColors = CreateOpaqueColors();
    private static readonly double[] OpaqueThresholds = CreateOpaqueThresholds();

    private readonly struct SampleColumn(int first, int second, double fraction)
    {
        public readonly int First = first, Second = second;
        public readonly double Fraction = fraction;
    }

    public ReferencePixels(Stream stream)
    {
        using var diagnostic = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.DecodeMs);
        // Zip entry streams cannot seek. WPF otherwise returns an asynchronous
        // 1x1 placeholder instead of a synchronously decoded bitmap.
        using var seekable = new MemoryStream();
        stream.CopyTo(seekable); seekable.Position = 0;
        var frame = BitmapFrame.Create(seekable, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        Width = bitmap.PixelWidth; Height = bitmap.PixelHeight;
        Bytes = new byte[Width * Height * 4]; bitmap.CopyPixels(Bytes, Width * 4, 0);
        _rowLeft = new int[Height]; _rowRight = new int[Height];
        Array.Fill(_rowLeft, Width);
        _left = Width; _top = Height;
        ReadOnlySpan<uint> pixels = MemoryMarshal.Cast<byte, uint>(Bytes);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                // Additive sprites can carry RGB even when their alpha is zero.
                if (pixels[y * Width + x] == 0) continue;
                _rowLeft[y] = Math.Min(_rowLeft[y], x); _rowRight[y] = x + 1;
            }
            if (_rowRight[y] == 0) continue;
            _left = Math.Min(_left, _rowLeft[y]); _right = Math.Max(_right, _rowRight[y]);
            _top = Math.Min(_top, y); _bottom = y + 1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Draw(byte[] target, int side, Rect rect, bool additive = false, double opacity = 1)
    {
        if (_bottom == 0 || opacity == 0) return;
        int left = Math.Max(0, (int)Math.Floor(rect.Left)), right = Math.Min(side, (int)Math.Ceiling(rect.Right));
        int top = Math.Max(0, (int)Math.Floor(rect.Top)), bottom = Math.Min(side, (int)Math.Ceiling(rect.Bottom));
        // Test pixel centers with the same arithmetic as the original sampler.
        // The floor/ceil rectangle can include one center outside either edge.
        while (left < right && (left + .5 - rect.X) / rect.Width < 0) left++;
        while (left < right && (right - .5 - rect.X) / rect.Width >= 1) right--;
        if (left >= right || top >= bottom) return;

        SampleColumn[]? rented = null;
        int count = right - left;
        Span<SampleColumn> columns = count <= 512 ? stackalloc SampleColumn[count]
            : (rented = ArrayPool<SampleColumn>.Shared.Rent(count)).AsSpan(0, count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                double u = (left + i + .5 - rect.X) / rect.Width;
                double sx = Math.Clamp(u * Width - .5, 0, Width - 1);
                int x0 = (int)sx;
                columns[i] = new SampleColumn(x0, Math.Min(x0 + 1, Width - 1), sx - x0);
            }
            int firstVisible = FirstColumnEndingAt(columns, _left), endVisible = FirstColumnStartingAt(columns, _right);
            ReadOnlySpan<uint> pixels = MemoryMarshal.Cast<byte, uint>(Bytes);
            Span<uint> targetPixels = MemoryMarshal.Cast<byte, uint>(target.AsSpan());
            Span<double> sample = stackalloc double[4];
            for (int y = top; y < bottom; y++)
            {
                double v = (y + .5 - rect.Y) / rect.Height;
                if (v < 0 || v >= 1) continue;
                double sy = Math.Clamp(v * Height - .5, 0, Height - 1);
                int y0 = (int)sy, y1 = Math.Min(y0 + 1, Height - 1);
                if (y1 < _top || y0 >= _bottom) continue;
                int rowLeft = Math.Min(_rowLeft[y0], _rowLeft[y1]), rowRight = Math.Max(_rowRight[y0], _rowRight[y1]);
                if (rowLeft >= rowRight) continue;
                // Include both bilinear neighbors, even when a nonzero texel is
                // just outside the pixel center; this also retains clamped edges.
                int first = rowLeft == _left ? firstVisible : FirstColumnEndingAt(columns, rowLeft);
                int end = rowRight == _right ? endVisible : FirstColumnStartingAt(columns, rowRight);
                double fy = sy - y0, wy = 1 - fy;
                int row0 = y0 * Width, row1 = y1 * Width;
                int p = (y * side + left + first) * 4;
                if (Avx2.IsSupported && Sse41.IsSupported)
                {
                    DrawVectorRow(pixels, targetPixels.Slice(p / 4, end - first), columns.Slice(first, end - first),
                        row0, row1, fy, opacity, additive);
                    continue;
                }
                for (int i = first; i < end; i++, p += 4)
                {
                    SampleColumn column = columns[i];
                    int a = row0 + column.First, b = row0 + column.Second;
                    int c = row1 + column.First, d = row1 + column.Second;
                    if ((pixels[a] | pixels[b] | pixels[c] | pixels[d]) == 0) continue;
                    a *= 4; b *= 4; c *= 4; d *= 4;
                    double fx = column.Fraction, wx = 1 - fx;
                    for (int ch = 0; ch < 4; ch++)
                        sample[ch] = ((Bytes[a + ch] * wx + Bytes[b + ch] * fx) * wy +
                            (Bytes[c + ch] * wx + Bytes[d + ch] * fx) * fy) * opacity;
                    double retained = additive ? 1 : 1 - sample[3] / 255;
                    for (int ch = 0; ch < 4; ch++) target[p + ch] = Byte(sample[ch] + target[p + ch] * retained);
                }
            }
        }
        finally
        {
            if (rented != null) ArrayPool<SampleColumn>.Shared.Return(rented);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void DrawVectorRow(ReadOnlySpan<uint> pixels, Span<uint> target, ReadOnlySpan<SampleColumn> columns,
        int row0, int row1, double fy, double opacity, bool additive)
    {
        var bottomWeight = Vector256.Create(fy);
        var topWeight = Vector256.Create(1 - fy);
        var opacityVector = Vector256.Create(opacity);
        var one = Vector256.Create(1.0);
        var fullAlpha = Vector256.Create(255.0);
        for (int i = 0; i < columns.Length; i++)
        {
            SampleColumn column = columns[i];
            uint a = pixels[row0 + column.First], b = pixels[row0 + column.Second];
            uint c = pixels[row1 + column.First], d = pixels[row1 + column.Second];
            if ((a | b | c | d) == 0) continue;
            var rightWeight = Vector256.Create(column.Fraction);
            var leftWeight = Vector256.Create(1 - column.Fraction);
            // Keep every multiply and add separate, in the scalar sampler's
            // order: fused multiply-add would change rounding at byte ties.
            var top = Avx.Add(Avx.Multiply(UnpackPixel(a), leftWeight), Avx.Multiply(UnpackPixel(b), rightWeight));
            var bottom = Avx.Add(Avx.Multiply(UnpackPixel(c), leftWeight), Avx.Multiply(UnpackPixel(d), rightWeight));
            var sample = Avx.Multiply(Avx.Add(Avx.Multiply(top, topWeight), Avx.Multiply(bottom, bottomWeight)), opacityVector);
            var retained = additive ? one : Avx.Subtract(one, Avx.Divide(Avx2.Permute4x64(sample, 0xff), fullAlpha));
            var blended = Avx.Add(sample, Avx.Multiply(UnpackPixel(target[i]), retained));
            target[i] = PackPixel(blended);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> UnpackPixel(uint pixel) =>
        Avx.ConvertToVector256Double(Sse41.ConvertToVector128Int32(Vector128.CreateScalar(pixel).AsByte()));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint PackPixel(Vector256<double> value)
    {
        // Explicit nearest-even rounding matches Math.Round independently of
        // the integer conversion instruction's ambient rounding mode.
        var rounded = Avx.RoundToNearestInteger(value);
        var clamped = Avx.Min(Avx.Max(rounded, Vector256<double>.Zero), Vector256.Create(255.0));
        var integers = Avx.ConvertToVector128Int32WithTruncation(clamped);
        var shorts = Sse2.PackSignedSaturate(integers, integers);
        return Sse2.PackUnsignedSaturate(shorts, shorts).AsUInt32().ToScalar();
    }

    private static int FirstColumnEndingAt(ReadOnlySpan<SampleColumn> columns, int sourceX)
    {
        int left = 0, right = columns.Length;
        while (left < right)
        {
            int middle = left + (right - left) / 2;
            if (columns[middle].Second < sourceX) left = middle + 1; else right = middle;
        }
        return left;
    }

    private static int FirstColumnStartingAt(ReadOnlySpan<SampleColumn> columns, int sourceX)
    {
        int left = 0, right = columns.Length;
        while (left < right)
        {
            int middle = left + (right - left) / 2;
            if (columns[middle].First < sourceX) left = middle + 1; else right = middle;
        }
        return left;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void BlendSrgb(byte[] back, byte[] front, bool additive, double opacity = 1, uint tint = 0xffffff)
    {
        using var diagnostic = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.BlendMs);
        Span<double> colors = stackalloc double[] { (tint & 255) / 255.0, ((tint >> 8) & 255) / 255.0, ((tint >> 16) & 255) / 255.0, 1 };
        for (int p = 0; p < back.Length; p += 4)
        {
            double retained = additive ? 1 : 1 - front[p + 3] / 255.0 * opacity;
            for (int ch = 0; ch < 4; ch++) back[p + ch] = Byte(front[p + ch] * opacity * colors[ch] + back[p + ch] * retained);
        }
    }

    // These are hit-composite.ts's filter equations, including its different
    // alpha handling for the additive channel and its unpremultiplication points.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void BlendLinear(byte[] back, byte[] front, bool additive)
    {
        using var diagnostic = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.BlendMs);
        for (int p = 0; p < back.Length; p += 4)
        {
            int frontAlpha = front[p + 3], backAlpha = back[p + 3];
            double fa = frontAlpha / 255.0, ba = backAlpha / 255.0;
            if (fa == 0 && front[p] == 0 && front[p + 1] == 0 && front[p + 2] == 0) continue;
            double alpha = additive ? Math.Max(fa, ba) : fa + ba * (1 - fa);
            for (int ch = 0; ch < 3; ch++)
            {
                double background = LinearColors[(backAlpha << 8) + back[p + ch]];
                double light = additive ? LinearColors[(255 << 8) + front[p + ch]] + background
                    : LinearColors[(frontAlpha << 8) + front[p + ch]] + background * (1 - fa);
                back[p + ch] = alpha == 1 ? EncodeOpaqueByte(light)
                    : Byte(Encode(light / Math.Max(alpha, .000001)) * alpha * 255);
            }
            back[p + 3] = Byte(alpha * 255);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte EncodeOpaqueByte(double light)
    {
        if (!(light > 0)) return 0;
        if (light >= 1) return 255;
        int color = OpaqueColors[(int)(light * OpaqueEncodeSteps)];
        // Across one table interval the sRGB curve changes by at most 0.051
        // byte, so a candidate can cross only one rounding boundary.
        const double boundaryTolerance = 1e-12;
        if (color > 0)
        {
            double lower = OpaqueThresholds[color - 1];
            if (Math.Abs(light - lower) <= boundaryTolerance) return Byte(Encode(light) * 255);
            if (light < lower) color--;
        }
        if (color < 255)
        {
            double upper = OpaqueThresholds[color];
            if (Math.Abs(light - upper) <= boundaryTolerance) return Byte(Encode(light) * 255);
            if (light > upper) color++;
        }
        return (byte)color;
    }

    private static byte[] CreateOpaqueColors()
    {
        var values = new byte[OpaqueEncodeSteps + 1];
        for (int i = 0; i < values.Length; i++) values[i] = Byte(Encode(i / (double)OpaqueEncodeSteps) * 255);
        return values;
    }

    private static double[] CreateOpaqueThresholds()
    {
        var values = new double[255];
        for (int i = 0; i < values.Length; i++) values[i] = Decode((i + .5) / 255.0);
        return values;
    }

    private static double[] CreateLinearColors()
    {
        var values = new double[256 * 256];
        for (int alpha = 0; alpha < 256; alpha++)
        {
            double a = alpha / 255.0;
            for (int color = 0; color < 256; color++)
                values[(alpha << 8) + color] = Decode(color / 255.0 / Math.Max(a, .000001)) * a;
        }
        return values;
    }

    private static double Decode(double value) => value < .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    private static double Encode(double value) => value < .0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - .055;
    private static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}

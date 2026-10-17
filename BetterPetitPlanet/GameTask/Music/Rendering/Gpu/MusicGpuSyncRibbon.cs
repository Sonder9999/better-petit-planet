using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace BetterPetitPlanet.GameTask.Music.Rendering.Gpu;

/// <summary>Cached native equivalent of MusicSyncRibbon's twelve-point ivory ribbon.</summary>
internal sealed class MusicGpuSyncRibbon : IDisposable
{
    private readonly ID2D1Factory _factory;
    private readonly ID2D1StrokeStyle _round;
    private readonly ID2D1SolidColorBrush _fill, _glow, _outline, _core;
    private readonly Dictionary<(float Span, float Scale), ID2D1PathGeometry> _paths = new();

    public MusicGpuSyncRibbon(ID2D1DeviceContext dc)
    {
        // Geometry and stroke resources must belong to this render context's factory.
        // GetFactory returns an AddRef'd COM wrapper; keep it for the ribbon lifetime
        // and release that owned reference in Dispose, without owning the context.
        _factory = dc.Factory;
        try
        {
            _round = _factory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
            _fill = dc.CreateSolidColorBrush(Color(242, 255, 255, 209));
            _glow = dc.CreateSolidColorBrush(Color(46, 255, 255, 209));
            _outline = dc.CreateSolidColorBrush(Color(102, 200, 196, 148));
            _core = dc.CreateSolidColorBrush(Color(250, 255, 255, 255));
        }
        catch { Dispose(); throw; }
    }

    public void ClearGeometry()
    {
        foreach (var path in _paths.Values) path.Dispose();
        _paths.Clear();
    }

    public void Draw(ID2D1DeviceContext dc, float left, float right, float y, float scale)
    {
        float span = right - left;
        if (span <= 0) return;
        if (!_paths.TryGetValue((span, scale), out var geometry))
        {
            float middle = 18 * scale, end = 36 * scale, flare = Math.Min(42 * scale, span * .35f);
            geometry = _factory.CreatePathGeometry();
            try
            {
                using var path = geometry.Open();
                path.BeginFigure(new Vector2(0, -end / 2), FigureBegin.Filled);
                path.AddLines(new[]
                {
                    new Vector2(flare * .45f, -middle * .58f), new Vector2(flare, -middle / 2),
                    new Vector2(span - flare, -middle / 2), new Vector2(span - flare * .45f, -middle * .58f), new Vector2(span, -end / 2),
                    new Vector2(span, end / 2), new Vector2(span - flare * .45f, middle * .58f), new Vector2(span - flare, middle / 2),
                    new Vector2(flare, middle / 2), new Vector2(flare * .45f, middle * .58f), new Vector2(0, end / 2)
                });
                path.EndFigure(FigureEnd.Closed); path.Close();
                _paths.Add((span, scale), geometry);
            }
            catch { geometry.Dispose(); throw; }
        }
        var previous = dc.Transform;
        try
        {
            dc.Transform = Matrix3x2.CreateTranslation(left, y) * previous;
            dc.DrawLine(Vector2.Zero, new Vector2(span, 0), _glow, 24 * scale, _round);
            dc.FillGeometry(geometry, _fill);
            dc.DrawGeometry(geometry, _outline, 1.5f * scale);
            dc.DrawLine(new Vector2(6 * scale, 0), new Vector2(span - 6 * scale, 0), _core, 2.8f * scale, _round);
        }
        finally { dc.Transform = previous; }
    }

    private static Color4 Color(byte a, byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, a / 255f);
    public void Dispose()
    {
        ClearGeometry(); _round?.Dispose(); _fill?.Dispose(); _glow?.Dispose(); _outline?.Dispose(); _core?.Dispose(); _factory?.Dispose();
    }
}

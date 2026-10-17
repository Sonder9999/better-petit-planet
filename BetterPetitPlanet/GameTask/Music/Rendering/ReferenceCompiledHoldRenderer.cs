using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

internal sealed class ReferenceCompiledHoldRenderer
{
    private readonly ReferenceHoldProfile _profile;
    private readonly ReferenceHoldEmission[] _schedules;
    private readonly ReferenceEffectSurface _surface = new();
    public double DisplaySize { get; }
    public ReferenceEffectChannels Channels => _surface.Channels;

    public ReferenceCompiledHoldRenderer(ReferenceHoldProfile profile)
    {
        _profile = profile;
        _schedules = profile.Layers.Select(layer => new ReferenceHoldEmission(layer)).ToArray();
        DisplaySize = profile.Layers.Max(layer => layer.DisplaySize);
    }

    public BitmapSource Render(Drawing scene, Rect bounds, double ageMs, double intensity, double pixelsPerDip,
        ReferenceEffectChannels? prepared = null)
    {
        _surface.Capture(scene, bounds, pixelsPerDip);
        if (prepared is { } shared && shared.Size == _surface.Size && !ReferenceEquals(shared.Normal, _surface.Normal))
        {
            ReferencePixels.BlendLinear(_surface.Back, shared.Normal, false);
            ReferencePixels.BlendLinear(_surface.Back, shared.Light, true);
            return _surface.Finish();
        }
        for (int layerIndex = 0; layerIndex < _profile.Layers.Length; layerIndex++)
        {
            var layer = _profile.Layers[layerIndex]; var schedule = _schedules[layerIndex];
            schedule.Update(ageMs);
            double size = layer.DisplaySize / DisplaySize * _surface.Size;
            var rect = new Rect((_surface.Size - size) / 2, (_surface.Size - size) / 2, size, size);
            for (int slot = 0; slot < layer.Slots; slot++)
                if (schedule.Frame(slot) is { } frame)
                {
                    ReferenceFrameStore.Get(_profile.NormalUrl, frame).Draw(_surface.Normal, _surface.Size, rect, opacity: intensity);
                    ReferenceFrameStore.Get(_profile.AdditiveUrl, frame).Draw(_surface.Light, _surface.Size, rect, additive: true, opacity: intensity);
                }
        }
        ReferencePixels.BlendLinear(_surface.Back, _surface.Normal, false);
        ReferencePixels.BlendLinear(_surface.Back, _surface.Light, true);
        return _surface.Finish();
    }
}

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

internal sealed class ReferenceHitRenderer
{
    private readonly ReferenceEffectSurface _surface = new();
    public ReferenceEffectChannels Channels => _surface.Channels;

    public BitmapSource? Render(Drawing scene, Rect bounds, ReferenceHitAnimation animation,
        double ageMs, double intensity, double pixelsPerDip, ReferenceEffectChannels? prepared = null)
    {
        int frame = animation.FrameAt(ageMs);
        if (frame < 0 || intensity <= 0) return null;
        _surface.Capture(scene, bounds, pixelsPerDip);
        var rect = new Rect(0, 0, _surface.Size, _surface.Size);
        var channels = prepared is { } shared && shared.Size == _surface.Size ? shared : _surface.Channels;
        bool needsSprites = ReferenceEquals(channels.Normal, _surface.Normal);
        if (needsSprites) ReferenceFrameStore.Get(animation.Url, frame).Draw(channels.Normal, _surface.Size, rect, opacity: intensity);
        if (animation.AdditiveUrl is { } light)
        {
            if (needsSprites) ReferenceFrameStore.Get(light, frame).Draw(channels.Light, _surface.Size, rect, opacity: intensity);
            ReferencePixels.BlendLinear(_surface.Back, channels.Normal, false);
            ReferencePixels.BlendLinear(_surface.Back, channels.Light, true);
        }
        else ReferencePixels.BlendSrgb(_surface.Back, channels.Normal, false);
        return _surface.Finish();
    }
}

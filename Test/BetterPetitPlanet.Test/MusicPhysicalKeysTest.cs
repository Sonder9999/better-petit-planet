using BetterPetitPlanet.GameTask.Music.Service;
using Xunit;

namespace BetterPetitPlanet.Test;

public class MusicPhysicalKeysTest
{
    [Fact]
    public void InjectionMarkerAndInjectedFlagsNeverBecomeManualFeedback()
    {
        Assert.True(MusicInputObserver.IsInjected(0, BetterPetitPlanet.GameTask.HardwareInputSimulator.MusicInputMarker));
        Assert.True(MusicInputObserver.IsInjected(0x10, 0));
        Assert.True(MusicInputObserver.IsInjected(0x2, 0));
        Assert.False(MusicInputObserver.IsInjected(0, 0));
    }

    [Fact]
    public void RepeatAndInjectionAreIgnoredAndKeysReleaseIndependently()
    {
        var keys = new MusicPhysicalKeys();
        Assert.True(keys.Apply("A", true, false));
        Assert.False(keys.Apply("A", true, false));
        Assert.False(keys.Apply("F", true, true));
        Assert.True(keys.Apply("L", true, false));
        Assert.True(keys.Apply("A", false, false));
        Assert.Equal(["L"], keys.ActiveKeys);
        Assert.False(keys.Apply("Z", true, false));
        Assert.Equal(["L"], keys.Clear());
        Assert.Empty(keys.ActiveKeys);
    }
}

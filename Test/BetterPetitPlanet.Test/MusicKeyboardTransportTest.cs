using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Service;

namespace BetterPetitPlanet.Test;

public sealed class MusicKeyboardTransportTest
{
    private sealed class Native : IMusicKeyboardNative
    {
        public bool Foreground { get; set; } = true;
        public int Prefix { get; set; } = 1;
        public List<MusicKeyCommand[]> Sent { get; } = [];
        public bool IsTargetForeground(IntPtr target) => Foreground;
        public MusicNativeSubmission Send(IReadOnlyList<MusicKeyCommand> commands)
        {
            Sent.Add(commands.ToArray());
            return new((uint)Math.Min(Prefix, commands.Count), 5);
        }
    }

    private static MusicKeyCommand Down(string key) => new(new PerformanceEvent { Key = key }, true);

    [Fact]
    public void NativePartialSend_ReportsExactSuccessfulPrefix()
    {
        var native = new Native();
        var transport = new KeyInputTransports(keyboardNative: native);
        var commands = new[] { Down("A"), Down("F"), Down("L") };
        var results = transport.Submit(commands, (IntPtr)1, MusicInputMode.Keyboard);
        Assert.Equal([true, false, false], results.Select(result => result.Submitted));
        Assert.Equal(commands, Assert.Single(native.Sent));
    }

    [Fact]
    public void LostFocus_BlocksDownButSubmitsOwnedRelease()
    {
        var native = new Native { Foreground = false };
        var transport = new KeyInputTransports(keyboardNative: native);
        var release = Down("A") with { IsDown = false };
        var results = transport.Submit([Down("F"), release], (IntPtr)1, MusicInputMode.Keyboard);
        Assert.Equal([false, true], results.Select(result => result.Submitted));
        Assert.Equal(release, Assert.Single(Assert.Single(native.Sent)));
    }
}

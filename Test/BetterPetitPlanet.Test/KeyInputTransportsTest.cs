using BetterPetitPlanet.GameTask;
using Xunit;

namespace BetterPetitPlanet.Test;

public class KeyInputTransportsTest
{
    [Theory]
    [InlineData("A", 0x1E)]
    [InlineData("S", 0x1F)]
    [InlineData("D", 0x20)]
    [InlineData("F", 0x21)]
    [InlineData("J", 0x24)]
    [InlineData("K", 0x25)]
    [InlineData("L", 0x26)]
    [InlineData("a", 0x1E)]
    [InlineData("SPACE", 0x39)]
    [InlineData("空格", 0x39)]
    [InlineData("F8", 0x42)]
    [InlineData("F9", 0x43)]
    public void GetScancode_ValidKeys_ReturnsCorrectScancode(string keyName, ushort expectedScancode)
    {
        var scancode = HardwareInputSimulator.GetScancode(keyName);
        Assert.Equal(expectedScancode, scancode);
    }

    [Theory]
    [InlineData("A", (ushort)'A')]
    [InlineData("F", (ushort)'F')]
    [InlineData("SPACE", (ushort)0x20)]
    [InlineData("空格", (ushort)0x20)]
    [InlineData("ENTER", (ushort)0x0D)]
    [InlineData("回车", (ushort)0x0D)]
    [InlineData("ESC", (ushort)0x1B)]
    [InlineData("TAB", (ushort)0x09)]
    [InlineData("F8", (ushort)0x77)]
    [InlineData("F9", (ushort)0x78)]
    [InlineData("F10", (ushort)0x79)]
    [InlineData("F11", (ushort)0x7A)]
    [InlineData("F12", (ushort)0x7B)]
    public void GetVirtualKey_ValidKeys_ReturnsCorrectVk(string keyName, ushort expectedVk)
    {
        var vk = HardwareInputSimulator.GetVirtualKey(keyName);
        Assert.Equal(expectedVk, vk);
    }

    [Fact]
    public void AutoPickConfig_Defaults_OnlyMatchesPickKeyword()
    {
        var cfg = new BetterPetitPlanet.Core.Config.AutoPickConfig();
        Assert.Contains("拾取", cfg.Keywords);
        Assert.Equal("F", cfg.PickKey);
    }

    [Fact]
    public void MusicConfig_Defaults_AutoPauseOnFocusLostIsFalse()
    {
        var cfg = new BetterPetitPlanet.Core.Config.MusicConfig();
        Assert.False(cfg.AutoPauseOnFocusLost);
    }

    [Fact]
    public void HardwareInputSimulator_HeldKeysTracker_RecordsAndClearsCorrectly()
    {
        HardwareInputSimulator.ClearHeldKeys();
        Assert.Empty(HardwareInputSimulator.GetHeldKeys());

        ushort scancode = HardwareInputSimulator.GetScancode("A");
        ushort vk = HardwareInputSimulator.GetVirtualKey("A");

        HardwareInputSimulator.RecordKeyDown(scancode, vk);
        Assert.True(HardwareInputSimulator.IsKeyHeld(scancode, vk));
        Assert.Single(HardwareInputSimulator.GetHeldKeys());

        HardwareInputSimulator.RecordKeyUp(scancode, vk);
        Assert.False(HardwareInputSimulator.IsKeyHeld(scancode, vk));
        Assert.Empty(HardwareInputSimulator.GetHeldKeys());
    }

    [Fact]
    public void HardwareInputSimulator_NormalizeKeyIdentity_FillsMissingScancodeOrVk()
    {
        var normFromScancode = KeyIdentity.Normalize(0x1E, 0);
        Assert.Equal(0x1E, normFromScancode.Scancode);
        Assert.Equal((ushort)'A', normFromScancode.Vk);

        var normFromVk = KeyIdentity.Normalize(0, (ushort)'A');
        Assert.Equal(0x1E, normFromVk.Scancode);
        Assert.Equal((ushort)'A', normFromVk.Vk);
    }
}

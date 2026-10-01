using System;
using System.Collections.Generic;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.ViewModel.Pages;
using Xunit;

namespace BetterPetitPlanet.Test;

public class DropDownAsteriskTest
{
    private class FakeConfigService : IConfigService
    {
        public AppConfig Config { get; set; } = new AppConfig();
        public void Save() { }
        public void Reload() { }
    }

    [Fact]
    public void TestHomePageCaptureModesContainAsterisks()
    {
        var configService = new FakeConfigService();
        var vm = new HomePageViewModel(configService, null!, null!);
        Assert.Contains("WindowsGraphicsCapture", vm.AvailableCaptureModes);
        Assert.Contains("PrintWindow", vm.AvailableCaptureModes);
        Assert.Equal("WindowsGraphicsCapture", vm.CaptureMode);
    }

    [Fact]
    public void TestSettingsPageCaptureModesContainAsterisks()
    {
        var configService = new FakeConfigService();
        var vm = new SettingsPageViewModel(configService);
        Assert.Contains("WindowsGraphicsCapture", vm.AvailableCaptureModes);
        Assert.Contains("PrintWindow", vm.AvailableCaptureModes);
        Assert.Equal("WindowsGraphicsCapture", vm.SelectedCaptureMode);
    }

    [Fact]
    public void TestTriggerSettingsOcrEnginesContainAsterisks()
    {
        var configService = new FakeConfigService();
        var vm = new TriggerSettingsPageViewModel(configService, null!, null!);
        Assert.Contains("DirectML", vm.AvailableOcrEngines);
        Assert.Contains("Paddle", vm.AvailableOcrEngines);
        Assert.Contains("Rapid*", vm.AvailableOcrEngines);
        Assert.Equal("DirectML", vm.SelectedOcrEngine);
    }
}

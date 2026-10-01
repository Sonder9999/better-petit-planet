using System;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.AutoPick;
using BetterPetitPlanet.ViewModel.Pages;
using Xunit;

namespace BetterPetitPlanet.Test;

public class TriggerStateSyncTest
{
    private class FakeConfigService : IConfigService
    {
        public AppConfig Config { get; set; } = new AppConfig();
        public void Save() { }
        public void Reload() { }
    }

    [Fact]
    public void TestAutoPickTriggerStateSyncsToViewModel()
    {
        var configService = new FakeConfigService();
        configService.Config.AutoPick.Enabled = false;

        var trigger = new AutoPickTrigger(configService, null!, null!);
        var viewModel = new TriggerSettingsPageViewModel(configService, trigger, null);

        Assert.False(viewModel.AutoPickEnabled);
        Assert.False(trigger.IsEnabled);

        // Simulate hotkey toggle -> trigger enabled
        trigger.IsEnabled = true;

        // ViewModel should immediately reflect the new state
        Assert.True(viewModel.AutoPickEnabled);
        Assert.True(trigger.IsEnabled);

        // Simulate hotkey toggle -> trigger disabled
        trigger.IsEnabled = false;

        // ViewModel should immediately reflect the disabled state
        Assert.False(viewModel.AutoPickEnabled);
        Assert.False(trigger.IsEnabled);
    }

    [Fact]
    public void TestViewModelAutoPickEnabledSyncsToTrigger()
    {
        var configService = new FakeConfigService();
        configService.Config.AutoPick.Enabled = false;

        var trigger = new AutoPickTrigger(configService, null!, null!);
        var viewModel = new TriggerSettingsPageViewModel(configService, trigger, null);

        Assert.False(viewModel.AutoPickEnabled);
        Assert.False(trigger.IsEnabled);

        // User toggles switch in UI
        viewModel.AutoPickEnabled = true;

        // Trigger should be enabled
        Assert.True(trigger.IsEnabled);
        Assert.True(configService.Config.AutoPick.Enabled);

        // User toggles switch off in UI
        viewModel.AutoPickEnabled = false;

        // Trigger should be disabled
        Assert.False(trigger.IsEnabled);
        Assert.False(configService.Config.AutoPick.Enabled);
    }
}

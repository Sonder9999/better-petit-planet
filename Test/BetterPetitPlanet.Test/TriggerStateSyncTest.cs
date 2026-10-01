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

    [Fact]
    public void TestViewModelBlacklistAndWhitelistManagement()
    {
        var configService = new FakeConfigService();
        var trigger = new AutoPickTrigger(configService, null!, null!);
        var viewModel = new TriggerSettingsPageViewModel(configService, trigger, null);

        // Verify default collections
        Assert.Contains("拾取", viewModel.WhitelistKeywords);
        Assert.Contains("拾取雪球", viewModel.BlacklistKeywords);

        // Add to Blacklist
        viewModel.NewBlacklistKeyword = "烹饪";
        viewModel.AddBlacklistKeywordCommand.Execute(null);
        Assert.Contains("烹饪", viewModel.BlacklistKeywords);
        Assert.Contains("烹饪", configService.Config.AutoPick.Blacklist);
        Assert.Equal(string.Empty, viewModel.NewBlacklistKeyword);

        // Remove from Blacklist
        viewModel.RemoveBlacklistKeywordCommand.Execute("烹饪");
        Assert.DoesNotContain("烹饪", viewModel.BlacklistKeywords);
        Assert.DoesNotContain("烹饪", configService.Config.AutoPick.Blacklist);

        // Add to Whitelist
        viewModel.NewWhitelistKeyword = "调查";
        viewModel.AddWhitelistKeywordCommand.Execute(null);
        Assert.Contains("调查", viewModel.WhitelistKeywords);
        Assert.Contains("调查", configService.Config.AutoPick.Whitelist);
        Assert.Equal(string.Empty, viewModel.NewWhitelistKeyword);

        // Remove from Whitelist
        viewModel.RemoveWhitelistKeywordCommand.Execute("调查");
        Assert.DoesNotContain("调查", viewModel.WhitelistKeywords);
        Assert.DoesNotContain("调查", configService.Config.AutoPick.Whitelist);
    }

    [Theory]
    [InlineData("拾取", true)]
    [InlineData("回拾取", true)]
    [InlineData("可拾取", true)]
    [InlineData("拾取雪球", false)]
    [InlineData("白拾取雪球", false)]
    [InlineData("任意其他文本", false)]
    public void TestOcrBlacklistAndWhitelistMatchingLogic(string ocrText, bool shouldTrigger)
    {
        var cfg = new AutoPickConfig
        {
            Whitelist = ["拾取"],
            Blacklist = ["拾取雪球"]
        };

        // 1. Blacklist check
        bool isBlacklisted = cfg.Blacklist.Any(b =>
            !string.IsNullOrWhiteSpace(b) &&
            ocrText.Contains(b.Trim(), StringComparison.OrdinalIgnoreCase));

        if (isBlacklisted)
        {
            Assert.False(shouldTrigger, $"'{ocrText}' should have been blocked by blacklist");
            return;
        }

        // 2. Whitelist check
        bool matched = cfg.Whitelist.Any(w =>
            !string.IsNullOrWhiteSpace(w) &&
            ocrText.Contains(w.Trim(), StringComparison.OrdinalIgnoreCase))
            || (ocrText.Contains("拾") && ocrText.Contains("取") && cfg.Whitelist.Contains("拾取"));

        Assert.Equal(shouldTrigger, matched);
    }

    [Fact]
    public void TestViewModelInitializationWhenConfigCollectionsAreEmptyOrNull()
    {
        var configService = new FakeConfigService();
        configService.Config.AutoPick.Enabled = true; // Would trigger OnAutoPickEnabledChanged during LoadFromConfig
        configService.Config.AutoPick.Whitelist = [];
        configService.Config.AutoPick.Blacklist = [];

        var trigger = new AutoPickTrigger(configService, null!, null!);
        var viewModel = new TriggerSettingsPageViewModel(configService, trigger, null);

        // Even if config collections were empty, viewModel must automatically populate defaults and not wipe them
        Assert.Contains("拾取", viewModel.WhitelistKeywords);
        Assert.Contains("拾取雪球", viewModel.BlacklistKeywords);
    }
}

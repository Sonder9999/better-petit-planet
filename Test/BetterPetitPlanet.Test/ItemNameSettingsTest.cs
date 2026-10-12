using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.ViewModel.Pages;
using Newtonsoft.Json;

namespace BetterPetitPlanet.Test;

public sealed class ItemNameSettingsTest
{
    [Fact]
    public void OlderConfig_DefaultsOff_AndRoundTripsEnabled()
    {
        var config = JsonConvert.DeserializeObject<AppConfig>("{\"AutoPick\":{\"Enabled\":true}}")!;
        Assert.False(config.AutoPick.ItemNameRecognition.Enabled);
        config.AutoPick.ItemNameRecognition.Enabled = true;
        var restored = JsonConvert.DeserializeObject<AppConfig>(JsonConvert.SerializeObject(config))!;
        Assert.True(restored.AutoPick.ItemNameRecognition.Enabled);
    }

    [Fact]
    public void SettingsToggle_PersistsWithoutEnablingAutoPick()
    {
        var config = new ItemNameRecognitionServiceTest.TestConfigService();
        using var vm = new TriggerSettingsPageViewModel(config);
        var property = vm.GetType().GetProperty("ItemNameRecognitionEnabled");
        Assert.NotNull(property);
        property.SetValue(vm, true);
        Assert.True(config.Config.AutoPick.ItemNameRecognition.Enabled);
        Assert.False(config.Config.AutoPick.Enabled);
        Assert.Equal(1, config.SaveCount);
        using var reloaded = new TriggerSettingsPageViewModel(config);
        Assert.Equal(true, property.GetValue(reloaded));
        property.SetValue(vm, false);
        Assert.False(config.Config.AutoPick.ItemNameRecognition.Enabled);
    }

    [Fact]
    public void ExplicitNullConfig_RestoresSafeDefaults()
    {
        var config = JsonConvert.DeserializeObject<AppConfig>("{\"AutoPick\":{\"ItemNameRecognition\":null}}")!;
        Assert.False(config.AutoPick.ItemNameRecognition.Enabled);
        config = JsonConvert.DeserializeObject<AppConfig>("{\"AutoPick\":{\"ItemNameRecognition\":{\"Detection\":null}}}")!;
        Assert.NotNull(config.AutoPick.ItemNameRecognition.Detection);
    }
}

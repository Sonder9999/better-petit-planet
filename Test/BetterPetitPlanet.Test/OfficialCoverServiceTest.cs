using System;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Web;
using Xunit;

namespace BetterPetitPlanet.Test;

public class OfficialCoverServiceTest
{
    [Fact]
    public void Test_DefaultVisuals_AreValidUrls()
    {
        var visuals = new OfficialCoverVisuals();
        Assert.StartsWith("https://", visuals.BackgroundUrl);
        Assert.StartsWith("https://", visuals.ForegroundUrl);
        Assert.StartsWith("https://", visuals.StarrySkyUrl);
    }

    [Fact]
    public async Task Test_FetchLatestCoverVisuals_ResolvesOfficialUrls()
    {
        var service = new OfficialCoverService();
        var visuals = await service.FetchLatestCoverVisualsAsync();

        Assert.NotNull(visuals);
        Assert.StartsWith("https://planet.mihoyo.com/_nuxt/img/bg-", visuals.BackgroundUrl);
        Assert.StartsWith("https://planet.mihoyo.com/_nuxt/img/character-foreground", visuals.ForegroundUrl);
    }
}

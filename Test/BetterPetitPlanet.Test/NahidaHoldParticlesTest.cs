using System.Text.Json;
using BetterPetitPlanet.GameTask.Music.Rendering;

namespace BetterPetitPlanet.Test;

public class NahidaHoldParticlesTest
{
    [Fact]
    public void EveryParticle_MatchesActuallyEnabledReferenceBranch()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Music", "nahida-particle-oracle.json")));
        Assert.Equal(10, NahidaHoldParticles.Styles.Count);
        Assert.Equal(21, NahidaHoldParticles.Styles.Sum(style => style.Slots));
        foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var particle = NahidaHoldParticles.Evaluate(row.GetProperty("noteIndex").GetInt32(),
                row.GetProperty("layer").GetInt32(), row.GetProperty("slot").GetInt32(), row.GetProperty("age").GetDouble());
            Assert.Equal(row.GetProperty("visible").GetBoolean(), particle.HasValue);
            if (particle is not { } value) continue;
            Assert.Equal(row.GetProperty("x").GetDouble(), value.X, 8);
            Assert.Equal(row.GetProperty("y").GetDouble(), value.Y, 8);
            Assert.Equal(row.GetProperty("size").GetDouble(), value.Size, 8);
            Assert.Equal(row.GetProperty("rotation").GetDouble(), value.Rotation, 8);
            Assert.Equal(row.GetProperty("alpha").GetDouble(), value.Alpha, 8);
        }
    }
}

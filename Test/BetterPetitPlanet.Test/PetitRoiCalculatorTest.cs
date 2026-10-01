using BetterPetitPlanet.GameTask.AutoPick;
using Xunit;

namespace BetterPetitPlanet.Test;

public class PetitRoiCalculatorTest
{
    [Fact]
    public void Calculate_2560x1440_ShouldReturnExpectedBoundaries()
    {
        // 2560 * 0.660 = 1690, 1440 * 0.570 = 821
        // 2560 * 0.835 = 2138, 1440 * 0.660 = 950
        var rect = PetitRoiCalculator.Calculate(2560, 1440);

        Assert.True(rect.X >= 1689 && rect.X <= 1691);
        Assert.True(rect.Y >= 820 && rect.Y <= 822);
        Assert.True(rect.Width >= 447 && rect.Width <= 449);
        Assert.True(rect.Height >= 128 && rect.Height <= 130);
    }

    [Fact]
    public void Calculate_1920x1080_ShouldReturnProportionalBoundaries()
    {
        var rect = PetitRoiCalculator.Calculate(1920, 1080);

        Assert.True(rect.X > 0);
        Assert.True(rect.Y > 0);
        Assert.True(rect.X + rect.Width <= 1920);
        Assert.True(rect.Y + rect.Height <= 1080);
    }

    [Fact]
    public void Calculate_ZeroOrNegativeDimensions_ShouldReturnEmptyRect()
    {
        var rect1 = PetitRoiCalculator.Calculate(0, 0);
        Assert.Equal(0, rect1.Width);
        Assert.Equal(0, rect1.Height);

        var rect2 = PetitRoiCalculator.Calculate(-100, -100);
        Assert.Equal(0, rect2.Width);
        Assert.Equal(0, rect2.Height);
    }
}

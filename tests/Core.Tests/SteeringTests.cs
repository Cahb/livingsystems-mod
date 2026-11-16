using LivingSystems.Core;
using Xunit;

public class SteeringTests
{
    [Fact]
    public void Far_away_cruises() => Assert.Equal(30f, Steering.ApproachSpeed(5000, 30, 100, 4), 3);

    [Fact]
    public void Inside_stop_distance_wants_zero()
    {
        Assert.Equal(0f, Steering.ApproachSpeed(100, 30, 100, 4));
        Assert.Equal(0f, Steering.ApproachSpeed(20, 30, 100, 4));
    }

    [Fact]
    public void Slows_on_approach_and_can_stop_in_time()
    {
        // 50 m left, decel 4: v = sqrt(2*4*50) = 20, and v^2/(2a) = 50 m of braking — exactly fits.
        var v = Steering.ApproachSpeed(150, 30, 100, 4);
        Assert.Equal(20f, v, 3);
        Assert.True(v * v / (2 * 4) <= 50.001f);
    }

    [Theory]
    [InlineData(1f, 1f)]
    [InlineData(0f, 0f)]
    [InlineData(-0.5f, 0f)]
    [InlineData(0.5f, 0.25f)]
    public void Alignment(float dot, float expected) => Assert.Equal(expected, Steering.AlignmentFactor(dot), 3);
}

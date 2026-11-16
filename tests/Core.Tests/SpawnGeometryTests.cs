using LivingSystems.Core;
using Xunit;
using static LivingSystems.Core.SpawnGeometry;

public class SpawnGeometryTests
{
    [Fact]
    public void Yaw_zero_is_plus_z_and_ninety_is_plus_x()
    {
        var f0 = FlatForward(0); var f90 = FlatForward(90);
        Assert.Equal(1f, f0.Z, 3); Assert.Equal(0f, f0.X, 3);
        Assert.Equal(1f, f90.X, 3); Assert.Equal(0f, f90.Z, 3);
    }

    [Fact]
    public void Ships_spawn_ahead_spaced_and_crossing()
    {
        var p = new V3(100, 50, 100);
        var pa = Place(p, new V3(30, 0, 0), 300, 0);   // pitch ignored
        var a = pa.Pos; var ra = pa.Rot;
        var b = Place(p, new V3(0, 0, 0), 300, 1).Pos;
        Assert.Equal(400f, a.Z, 1); Assert.Equal(50f, a.Y, 1);
        Assert.Equal(460f, b.Z, 1);
        Assert.Equal(90f, ra.Y, 1);
    }
}

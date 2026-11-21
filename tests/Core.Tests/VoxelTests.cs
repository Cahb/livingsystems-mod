using LivingSystems.Core;
using LivingSystems.Core.Flight;
using Xunit;

public class VoxelTests
{
    static GridPose At(float x, float size = 2f) => new GridPose { Origin = new Vec3(x, 0, 0), StepX = new Vec3(size, 0, 0), StepY = new Vec3(0, size, 0), StepZ = new Vec3(0, 0, size) };

    static VoxelModel Box(int n, bool hollow)
    {
        var m = new VoxelModel();
        for (var x = 0; x < n; x++) for (var y = 0; y < n; y++) for (var z = 0; z < n; z++)
        {
            var edge = x == 0 || y == 0 || z == 0 || x == n - 1 || y == n - 1 || z == n - 1;
            if (!hollow || edge || (x == n / 2 && y == n / 2 && z == n / 2)) m.Add(x, y, z, edge ? "hull" : "core");
        }
        m.BuildShell();
        return m;
    }

    [Fact]
    public void The_shell_is_only_what_you_can_reach_from_outside()
    {
        var solid = Box(5, hollow: false);
        Assert.Equal(125, solid.Filled.Count);
        Assert.Equal(125 - 27, solid.Shell.Count);                 // the 3×3×3 inside is not shell
        var hollow = Box(5, hollow: true);
        Assert.DoesNotContain(new Cell(2, 2, 2), hollow.Shell);     // a block in a sealed room isn't either
        Assert.Equal(98, hollow.Shell.Count);
    }

    [Fact]
    public void The_gap_between_two_placed_hulls_is_surface_to_surface()
    {
        var a = Box(3, hollow: false); var b = Box(3, hollow: false);
        // a spans cell centres 0..4 m (surface at 5 m); b's first centre at 20 m (surface at 19 m): 14 m apart
        Assert.Equal(14f, VoxelModel.Gap(a, At(0), b, At(20), out var ca, out var cb), 3);
        Assert.Equal(2, ca.X); Assert.Equal(0, cb.X);
        Assert.Equal(5f, a.Gap(At(0), new Vec3(10, 2, 2), out var c), 3);   // a point 10 m out: 5 m off the surface
        Assert.Equal(2, c.X);
    }
}

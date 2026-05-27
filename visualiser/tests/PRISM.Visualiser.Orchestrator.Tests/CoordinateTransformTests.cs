using System.Numerics;

using Xunit;

using PRISM.Visualiser.Orchestrator.Staging;

namespace PRISM.Visualiser.Orchestrator.Tests;

/// <summary>
/// Smoke Test 4 — coordinate transform.
///
/// A unit cube at the origin in ORBIT/Speckle coords (right-handed,
/// Z-up, 1 unit = 1 metre) becomes a 100-cm cube in UE coords
/// (left-handed, Z-up, 1 unit = 1 cm) with the Y-axis mirrored.
/// </summary>
public class CoordinateTransformTests
{
    [Fact]
    public void UnitCube_BecomesHundredCmCube_WithYMirrored()
    {
        var halfSpeckle = 0.5f; // unit cube of side 1 m centred at origin
        var corners = new[]
        {
            new Vector3(-halfSpeckle, -halfSpeckle, -halfSpeckle),
            new Vector3( halfSpeckle, -halfSpeckle, -halfSpeckle),
            new Vector3( halfSpeckle,  halfSpeckle, -halfSpeckle),
            new Vector3(-halfSpeckle,  halfSpeckle, -halfSpeckle),
            new Vector3(-halfSpeckle, -halfSpeckle,  halfSpeckle),
            new Vector3( halfSpeckle, -halfSpeckle,  halfSpeckle),
            new Vector3( halfSpeckle,  halfSpeckle,  halfSpeckle),
            new Vector3(-halfSpeckle,  halfSpeckle,  halfSpeckle),
        };

        var transformed = corners.Select(CoordinateTransform.TransformPoint).ToArray();

        var min = new Vector3(
            transformed.Min(p => p.X),
            transformed.Min(p => p.Y),
            transformed.Min(p => p.Z));
        var max = new Vector3(
            transformed.Max(p => p.X),
            transformed.Max(p => p.Y),
            transformed.Max(p => p.Z));

        // 100 cm cube on every axis — 1 m × 100 = 100 UE units.
        Assert.Equal(-50f, min.X, precision: 4);
        Assert.Equal(50f, max.X, precision: 4);
        Assert.Equal(-50f, min.Y, precision: 4);
        Assert.Equal(50f, max.Y, precision: 4);
        Assert.Equal(-50f, min.Z, precision: 4);
        Assert.Equal(50f, max.Z, precision: 4);

        // Side length is 100 cm.
        Assert.Equal(100f, max.X - min.X, precision: 4);
        Assert.Equal(100f, max.Y - min.Y, precision: 4);
        Assert.Equal(100f, max.Z - min.Z, precision: 4);
    }

    [Fact]
    public void Y_IsMirrored()
    {
        var p = new Vector3(1, 1, 1);
        var t = CoordinateTransform.TransformPoint(p);
        // X and Z scale and keep sign; Y scales and negates.
        Assert.Equal(100f, t.X);
        Assert.Equal(-100f, t.Y);
        Assert.Equal(100f, t.Z);
    }

    [Fact]
    public void Matrix_MatchesPerPointForm()
    {
        var p = new Vector3(0.25f, -0.5f, 1.0f);
        var matMethod = Vector3.Transform(p, CoordinateTransform.ToMatrix());
        var directMethod = CoordinateTransform.TransformPoint(p);
        Assert.Equal(directMethod.X, matMethod.X, precision: 4);
        Assert.Equal(directMethod.Y, matMethod.Y, precision: 4);
        Assert.Equal(directMethod.Z, matMethod.Z, precision: 4);
    }
}

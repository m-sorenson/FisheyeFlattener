using System;
using FisheyeFlattener.Core;
using Xunit;

namespace FisheyeFlattener.Tests;

public class DewarpMathTests
{
    private static CircleCalibration MakeCalib() => new()
    {
        CenterX = 500,
        CenterY = 500,
        Radius = 480,
        MaxFovDeg = 180,
    };

    [Fact]
    public void PerspectiveCenterRayHitsOpticalCenter()
    {
        var calib = MakeCalib();
        var p = new PerspectiveParams { YawDeg = 0, PitchDeg = 0, FovDeg = 90, OutWidth = 100, OutHeight = 100 };
        var map = DewarpMath.BuildPerspectiveMap(calib, p);

        // pixel 50 is where x=(50-100/2)/focal == 0 exactly, i.e. the optical-center ray
        double cx = map.MapX[50, 50];
        double cy = map.MapY[50, 50];
        Assert.True(Math.Abs(cx - calib.CenterX) < 1e-3);
        Assert.True(Math.Abs(cy - calib.CenterY) < 1e-3);
    }

    [Fact]
    public void PerspectiveOutputShape()
    {
        var calib = MakeCalib();
        var p = new PerspectiveParams { OutWidth = 200, OutHeight = 100 };
        var map = DewarpMath.BuildPerspectiveMap(calib, p);

        Assert.Equal(100, map.MapX.GetLength(0));
        Assert.Equal(200, map.MapX.GetLength(1));
    }

    [Fact]
    public void PanoramaOutputShapeAndFinite()
    {
        var calib = MakeCalib();
        var p = new PanoramaParams { OutWidth = 360, OutHeight = 90 };
        var map = DewarpMath.BuildPanoramaMap(calib, p);

        Assert.Equal(90, map.MapX.GetLength(0));
        Assert.Equal(360, map.MapX.GetLength(1));

        for (int v = 0; v < 90; v++)
        {
            for (int u = 0; u < 360; u++)
            {
                Assert.False(float.IsNaN(map.MapX[v, u]));
                Assert.False(float.IsNaN(map.MapY[v, u]));
            }
        }
    }

    [Fact]
    public void PanoramaRowRadiusIncreasesWithTheta()
    {
        var calib = MakeCalib();
        var p = new PanoramaParams
        {
            AzimuthStartDeg = 0,
            AzimuthEndDeg = 0,
            ThetaTopDeg = 90,
            ThetaBottomDeg = 0,
            OutWidth = 1,
            OutHeight = 10,
        };
        var map = DewarpMath.BuildPanoramaMap(calib, p);

        // azimuth fixed at 0 deg -> map_x - center_x is the radius (monotonic with theta)
        double r0 = map.MapX[0, 0] - calib.CenterX;
        double r9 = map.MapX[9, 0] - calib.CenterX;
        Assert.True(r0 > r9);
    }
}

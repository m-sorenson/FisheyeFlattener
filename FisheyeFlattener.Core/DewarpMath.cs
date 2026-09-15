using System;

namespace FisheyeFlattener.Core;

/// <summary>Where the circular fisheye image sits inside the source frame.</summary>
public class CircleCalibration
{
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double Radius { get; set; }
    /// <summary>Real-world field of view (degrees) covered by Radius pixels.</summary>
    public double MaxFovDeg { get; set; } = 180.0;
}

/// <summary>A single virtual PTZ view: look at (yaw, pitch) with a given FOV.</summary>
public class PerspectiveParams
{
    public double YawDeg { get; set; }
    public double PitchDeg { get; set; }
    /// <summary>Rotation around the view's own forward axis, applied before yaw/pitch.
    /// Corrects for a camera mount that isn't perfectly level; unlike yaw/pitch, this
    /// stays fixed regardless of where you pan/tilt afterward.</summary>
    public double RollDeg { get; set; }
    public double FovDeg { get; set; } = 90.0;
    public int OutWidth { get; set; } = 1280;
    public int OutHeight { get; set; } = 720;
}

/// <summary>A cylindrical unwrap sweeping azimuth angle across the output width.</summary>
public class PanoramaParams
{
    public double AzimuthStartDeg { get; set; } = -180.0;
    public double AzimuthEndDeg { get; set; } = 180.0;
    public double ThetaTopDeg { get; set; } = 90.0;
    public double ThetaBottomDeg { get; set; } = 10.0;
    /// <summary>Rotates which direction in the fisheye counts as azimuth 0.</summary>
    public double ReferenceDeg { get; set; }
    public int OutWidth { get; set; } = 2048;
    public int OutHeight { get; set; } = 512;
}

public readonly struct DewarpMap
{
    public DewarpMap(float[,] mapX, float[,] mapY, int width, int height)
    {
        MapX = mapX;
        MapY = mapY;
        Width = width;
        Height = height;
    }

    public float[,] MapX { get; }
    public float[,] MapY { get; }
    public int Width { get; }
    public int Height { get; }
}

/// <summary>
/// Fisheye -> flat image math. Assumes an equidistant fisheye projection
/// (r = f * theta), the standard model for consumer/security fisheye lenses
/// including the Ubiquiti Protect G4/G5 Fisheye.
/// </summary>
public static class DewarpMath
{
    public static DewarpMap BuildPerspectiveMap(CircleCalibration calib, PerspectiveParams p)
    {
        int w = p.OutWidth, h = p.OutHeight;
        var mapX = new float[h, w];
        var mapY = new float[h, w];

        double focalOut = (w / 2.0) / Math.Tan(Deg2Rad(p.FovDeg) / 2.0);
        double maxTheta = Deg2Rad(calib.MaxFovDeg / 2.0);
        double radiusScale = calib.Radius / maxTheta;

        double pitch = Deg2Rad(p.PitchDeg);
        double yaw = Deg2Rad(p.YawDeg);
        double roll = Deg2Rad(p.RollDeg);
        double cp = Math.Cos(pitch), sp = Math.Sin(pitch);
        double cy = Math.Cos(yaw), sy = Math.Sin(yaw);
        double cr = Math.Cos(roll), sr = Math.Sin(roll);

        // Parallelized over rows (each row writes disjoint array slices, so this is
        // safe): rebuilding this per-pixel trig map has to happen on every drag/zoom
        // update, and a single-threaded loop is too slow to feel interactive.
        System.Threading.Tasks.Parallel.For(0, h, v =>
        {
            double y = (v - h / 2.0) / focalOut;
            for (int u = 0; u < w; u++)
            {
                double x = (u - w / 2.0) / focalOut;
                const double z = 1.0;

                // roll: rotate the ray around the forward axis first, so it's
                // unaffected by (and unaffects) subsequent yaw/pitch panning
                double xr = x * cr - y * sr;
                double yr = x * sr + y * cr;

                // pitch: rotate around x-axis
                double y1 = yr * cp - z * sp;
                double z1 = yr * sp + z * cp;

                // yaw: rotate around y-axis
                double x2 = xr * cy + z1 * sy;
                double z2 = -xr * sy + z1 * cy;
                double y2 = y1;

                double theta = Math.Atan2(Math.Sqrt(x2 * x2 + y2 * y2), z2);

                if (z2 <= 0 || theta > maxTheta)
                {
                    mapX[v, u] = -1f;
                    mapY[v, u] = -1f;
                    continue;
                }

                double phi = Math.Atan2(y2, x2);
                double r = radiusScale * theta;
                mapX[v, u] = (float)(calib.CenterX + r * Math.Cos(phi));
                mapY[v, u] = (float)(calib.CenterY + r * Math.Sin(phi));
            }
        });

        return new DewarpMap(mapX, mapY, w, h);
    }

    public static DewarpMap BuildPanoramaMap(CircleCalibration calib, PanoramaParams p)
    {
        int w = p.OutWidth, h = p.OutHeight;
        var mapX = new float[h, w];
        var mapY = new float[h, w];

        double azStart = Deg2Rad(p.AzimuthStartDeg);
        double azEnd = Deg2Rad(p.AzimuthEndDeg);
        double reference = Deg2Rad(p.ReferenceDeg);
        double thetaTop = Deg2Rad(p.ThetaTopDeg);
        double thetaBottom = Deg2Rad(p.ThetaBottomDeg);
        double maxTheta = Deg2Rad(calib.MaxFovDeg / 2.0);
        double radiusScale = calib.Radius / maxTheta;

        int wDenom = Math.Max(w - 1, 1);
        int hDenom = Math.Max(h - 1, 1);

        System.Threading.Tasks.Parallel.For(0, h, v =>
        {
            // row 0 (top of output) = thetaTop (far/horizon), last row = thetaBottom (near/center)
            double theta = thetaTop + (v / (double)hDenom) * (thetaBottom - thetaTop);
            bool invalid = theta > maxTheta;
            double r = radiusScale * theta;

            for (int u = 0; u < w; u++)
            {
                if (invalid)
                {
                    mapX[v, u] = -1f;
                    mapY[v, u] = -1f;
                    continue;
                }

                double phi = azStart + (u / (double)wDenom) * (azEnd - azStart) + reference;
                mapX[v, u] = (float)(calib.CenterX + r * Math.Cos(phi));
                mapY[v, u] = (float)(calib.CenterY + r * Math.Sin(phi));
            }
        });

        return new DewarpMap(mapX, mapY, w, h);
    }

    private static double Deg2Rad(double deg) => deg * Math.PI / 180.0;
}

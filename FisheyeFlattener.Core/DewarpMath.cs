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

/// <summary>A single virtual PTZ view: look at (yaw, pitch) with a given FOV.
/// YawDeg is azimuth around the lens's optical axis (0-360). PitchDeg is the angle
/// from the optical axis/nadir (0 = straight down at the lens center, ~90 = at the
/// horizon) - i.e. it's the same "theta" the rest of this file uses, not a tilt
/// offset from some other reference.</summary>
public class PerspectiveParams
{
    public double YawDeg { get; set; }
    public double PitchDeg { get; set; }
    /// <summary>Rotation around the view's own forward axis. Corrects for a camera
    /// mount that isn't perfectly level; unlike yaw/pitch, this stays fixed
    /// regardless of where you pan/tilt afterward.</summary>
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
        double roll = Deg2Rad(p.RollDeg);
        double cr = Math.Cos(roll), sr = Math.Sin(roll);

        // Build a local tangent-plane (gnomonic) basis at the view center instead of
        // composing independent pitch-then-yaw rotations. Composed Euler rotations do
        // NOT stay roll-free for a nadir-referenced (ceiling fisheye) camera as you
        // pan: a real vertical line held at fixed azimuth would land at wildly
        // different output columns as yaw changed (verified numerically - a ~30 deg
        // sweep along one such line drifted 300+ px sideways), which is exactly the
        // "image rotates while panning" bug. This tangent-basis construction is the
        // standard way to build a roll-free rectilinear "look around" view on a
        // sphere (same idea used by panorama viewers), and keeps a level camera level
        // at any yaw/pitch by construction.
        double phi0 = Deg2Rad(p.YawDeg);
        double theta0 = Deg2Rad(p.PitchDeg);
        double sinT0 = Math.Sin(theta0), cosT0 = Math.Cos(theta0);
        double sinP0 = Math.Sin(phi0), cosP0 = Math.Cos(phi0);

        // Forward = the view center's direction in fisheye space.
        double fx = sinT0 * cosP0, fy = sinT0 * sinP0, fz = cosT0;
        // Right = d(Forward)/d(phi), already unit length.
        double rx = -sinP0, ry = cosP0;
        // Up = -d(Forward)/d(theta), i.e. points back toward the pole (nadir).
        double ux = -cosT0 * cosP0, uy = -cosT0 * sinP0, uz = sinT0;

        // Parallelized over rows (each row writes disjoint array slices, so this is
        // safe): rebuilding this per-pixel trig map has to happen on every drag/zoom
        // update, and a single-threaded loop is too slow to feel interactive.
        System.Threading.Tasks.Parallel.For(0, h, v =>
        {
            double yo = (v - h / 2.0) / focalOut;
            for (int u = 0; u < w; u++)
            {
                double xo = (u - w / 2.0) / focalOut;

                // roll: rotate the pixel offset before projecting onto the basis
                double xr = xo * cr - yo * sr;
                double yr = xo * sr + yo * cr;

                double wx = fx + xr * rx + yr * ux;
                double wy = fy + xr * ry + yr * uy;
                double wz = fz + yr * uz;

                double theta = Math.Atan2(Math.Sqrt(wx * wx + wy * wy), wz);

                if (theta > maxTheta)
                {
                    mapX[v, u] = -1f;
                    mapY[v, u] = -1f;
                    continue;
                }

                double phi = Math.Atan2(wy, wx);
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

using OpenCvSharp;

namespace FisheyeFlattener.Core;

/// <summary>Flip/pan options applied around the dewarp step.</summary>
public class TransformOptions
{
    /// <summary>Flip the raw source frame before dewarping (corrects a mis-mounted camera).</summary>
    public bool SourceFlipHorizontal { get; set; }
    public bool SourceFlipVertical { get; set; }

    /// <summary>Flip the flattened result after dewarping.</summary>
    public bool OutputFlipHorizontal { get; set; }
    public bool OutputFlipVertical { get; set; }

    /// <summary>Pan (translate) the flattened result, in output pixels.</summary>
    public double PanX { get; set; }
    public double PanY { get; set; }

    public static TransformOptions None => new();
}

public static class Transform
{
    public static void FlipInPlace(Mat mat, bool flipHorizontal, bool flipVertical)
    {
        if (!flipHorizontal && !flipVertical)
            return;
        FlipMode mode = flipHorizontal && flipVertical ? FlipMode.XY
            : flipHorizontal ? FlipMode.Y
            : FlipMode.X;
        Cv2.Flip(mat, mat, mode);
    }

    public static Mat FlipClone(Mat src, bool flipHorizontal, bool flipVertical)
    {
        var dst = src.Clone();
        FlipInPlace(dst, flipHorizontal, flipVertical);
        return dst;
    }

    public static Mat Translate(Mat src, double dx, double dy)
    {
        using var m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, 1.0);
        m.Set(0, 1, 0.0);
        m.Set(0, 2, dx);
        m.Set(1, 0, 0.0);
        m.Set(1, 1, 1.0);
        m.Set(1, 2, dy);

        var dst = new Mat();
        Cv2.WarpAffine(src, dst, m, src.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.Black);
        return dst;
    }

    /// <summary>Mirrors an X coordinate across a frame of the given width (for keeping
    /// calibration in sync when the user toggles source flip).</summary>
    public static double MirrorX(double x, int frameWidth) => frameWidth - x;

    public static double MirrorY(double y, int frameHeight) => frameHeight - y;
}

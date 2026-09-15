using OpenCvSharp;

namespace FisheyeFlattener.Core;

/// <summary>
/// The full per-frame pipeline: flip the source, dewarp via the given remap, flip the
/// result, then pan. Used identically by the live preview, image export, and video
/// export so all three stay in sync.
/// </summary>
public static class FlattenPipeline
{
    public static Mat Run(Mat sourceFrame, Mat mapX, Mat mapY, TransformOptions transform)
    {
        using var src = Transform.FlipClone(sourceFrame, transform.SourceFlipHorizontal, transform.SourceFlipVertical);

        using var remapped = new Mat();
        Cv2.Remap(src, remapped, mapX, mapY, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.Black);
        Transform.FlipInPlace(remapped, transform.OutputFlipHorizontal, transform.OutputFlipVertical);

        if (transform.PanX == 0 && transform.PanY == 0)
            return remapped.Clone();

        return Transform.Translate(remapped, transform.PanX, transform.PanY);
    }
}

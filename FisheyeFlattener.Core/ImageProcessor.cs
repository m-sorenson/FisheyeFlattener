using System.IO;
using OpenCvSharp;

namespace FisheyeFlattener.Core;

/// <summary>Applies a precomputed remap to a single still image.</summary>
public static class ImageProcessor
{
    public static Mat Load(string path)
    {
        var img = Cv2.ImRead(path, ImreadModes.Color);
        if (img.Empty())
            throw new FileNotFoundException($"Could not read image: {path}");
        return img;
    }

    public static Mat ApplyMap(Mat img, Mat mapX, Mat mapY)
    {
        var dst = new Mat();
        Cv2.Remap(img, dst, mapX, mapY, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.Black);
        return dst;
    }

    public static void Save(string path, Mat img)
    {
        if (!Cv2.ImWrite(path, img))
            throw new IOException($"Could not write image: {path}");
    }
}

using System;
using System.Linq;
using OpenCvSharp;

namespace FisheyeFlattener.Core;

/// <summary>Auto-detects the circular fisheye image within a source frame.</summary>
public static class Calibration
{
    /// <summary>
    /// Finds the bright circular fisheye disc against a (usually black) letterbox.
    /// Works well for Protect exports, which pad the circular fisheye image with
    /// solid black to fill a rectangular frame. Returns null if nothing confident
    /// is found; the caller should let the user set it manually.
    /// </summary>
    public static CircleCalibration? AutoDetectCircle(Mat bgrImage)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgrImage, gray, ColorConversionCodes.BGR2GRAY);
        using var thresh = new Mat();
        Cv2.Threshold(gray, thresh, 12, 255, ThresholdTypes.Binary);

        Cv2.FindContours(thresh, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0)
            return null;

        var largest = contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
        double area = Cv2.ContourArea(largest);
        double frameArea = (double)bgrImage.Rows * bgrImage.Cols;
        if (area < 0.05 * frameArea)
            return null;

        Cv2.MinEnclosingCircle(largest, out Point2f center, out float radius);
        return new CircleCalibration { CenterX = center.X, CenterY = center.Y, Radius = radius };
    }

    /// <summary>Best-effort calibration: auto-detect, else assume a centered circle.</summary>
    public static CircleCalibration DefaultCalibration(Mat bgrImage)
    {
        var detected = AutoDetectCircle(bgrImage);
        if (detected != null)
            return detected;

        double radius = Math.Min(bgrImage.Cols, bgrImage.Rows) / 2.0;
        return new CircleCalibration { CenterX = bgrImage.Cols / 2.0, CenterY = bgrImage.Rows / 2.0, Radius = radius };
    }
}

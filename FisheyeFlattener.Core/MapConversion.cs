using System;
using OpenCvSharp;

namespace FisheyeFlattener.Core;

public static class MapConversion
{
    /// <summary>Converts a DewarpMap's managed float arrays into OpenCV remap matrices.</summary>
    public static (Mat MapX, Mat MapY) ToMats(this DewarpMap map)
    {
        int count = map.Width * map.Height;
        var flatX = new float[count];
        var flatY = new float[count];
        Buffer.BlockCopy(map.MapX, 0, flatX, 0, count * sizeof(float));
        Buffer.BlockCopy(map.MapY, 0, flatY, 0, count * sizeof(float));

        var mapX = Mat.FromPixelData(map.Height, map.Width, MatType.CV_32FC1, flatX);
        var mapY = Mat.FromPixelData(map.Height, map.Width, MatType.CV_32FC1, flatY);
        return (mapX, mapY);
    }
}

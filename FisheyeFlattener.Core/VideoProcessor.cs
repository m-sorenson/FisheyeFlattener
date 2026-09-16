using System;
using System.IO;
using OpenCvSharp;

namespace FisheyeFlattener.Core;

/// <summary>Applies a precomputed remap to every frame of a video file.</summary>
public static class VideoProcessor
{
    public static Mat GetFirstFrame(string path)
    {
        using var cap = new VideoCapture(path);
        if (!cap.IsOpened())
            throw new FileNotFoundException($"Could not open video: {path}");

        var frame = new Mat();
        if (!cap.Read(frame) || frame.Empty())
            throw new IOException($"Could not read a frame from: {path}");
        return frame;
    }

    /// <summary>
    /// Dewarps every frame of <paramref name="inPath"/> using the same map, writes to
    /// <paramref name="outPath"/>. <paramref name="cancelCb"/>, if given, is polled each
    /// frame; returning true stops early.
    /// </summary>
    public static void ProcessVideo(
        string inPath,
        string outPath,
        Mat mapX,
        Mat mapY,
        TransformOptions? transform = null,
        Action<int, int>? progressCb = null,
        Func<bool>? cancelCb = null)
    {
        transform ??= TransformOptions.None;

        using var cap = new VideoCapture(inPath);
        if (!cap.IsOpened())
            throw new FileNotFoundException($"Could not open video: {inPath}");

        double fps = cap.Fps > 0 ? cap.Fps : 30.0;
        int total = cap.FrameCount; // estimate, used for progress reporting only

        // Prefer actualFrameCount / actualDuration over the codec's reported average
        // fps when possible: for real-world footage that reported value can be a
        // rounded or otherwise imprecise approximation (e.g. 17.909), and writing the
        // output at that rate makes its total length not quite match the original's
        // audio track length - which is what showed up as audio and video drifting
        // apart across an exported clip. Deliberately uses a frame-accurate count
        // (forces real decoding) rather than cap.FrameCount, which - like the fps
        // field - is frequently just an estimate for compressed video, not an actual
        // count; using an estimate here would reintroduce the same class of mismatch
        // this exists to eliminate, just with different numbers.
        double? actualDuration = AudioMuxer.GetDurationSeconds(inPath);
        int? actualFrameCount = AudioMuxer.GetActualFrameCount(inPath);
        if (actualDuration is > 0 && actualFrameCount is > 0)
            fps = actualFrameCount.Value / actualDuration.Value;

        int outW = mapX.Cols, outH = mapX.Rows;

        int fourcc = VideoWriter.FourCC('m', 'p', '4', 'v');
        using var writer = new VideoWriter(outPath, fourcc, fps, new Size(outW, outH));
        if (!writer.IsOpened())
            throw new IOException($"Could not open writer for: {outPath}");

        int frameIdx = 0;
        using var frame = new Mat();
        while (true)
        {
            if (cancelCb?.Invoke() == true)
                break;
            if (!cap.Read(frame) || frame.Empty())
                break;

            using var flat = FlattenPipeline.Run(frame, mapX, mapY, transform);
            writer.Write(flat);
            frameIdx++;
            progressCb?.Invoke(frameIdx, total);
        }
    }
}

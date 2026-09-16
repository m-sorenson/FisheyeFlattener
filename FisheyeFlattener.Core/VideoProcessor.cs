using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
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

        // Preferred path: preserve each frame's own original timestamp rather than
        // assuming constant spacing. Motion-triggered security footage in particular
        // can have genuinely irregular frame timing (not just an imprecise *average*
        // fps, which an earlier version of this tried to correct for) - re-encoding
        // at any single constant rate, however accurately averaged, can never
        // reproduce that, and shows up as a real desync at whichever specific moment
        // the original timing was irregular, not a uniform drift across the file.
        // Needs ffmpeg (for the concat-demuxer assembly step); falls back to the
        // simpler constant-fps writer if it isn't available.
        if (AudioMuxer.IsFfmpegAvailable())
            ProcessVideoPreservingTimestamps(inPath, outPath, mapX, mapY, transform, progressCb, cancelCb);
        else
            ProcessVideoConstantFps(inPath, outPath, mapX, mapY, transform, progressCb, cancelCb);
    }

    private static void ProcessVideoPreservingTimestamps(
        string inPath,
        string outPath,
        Mat mapX,
        Mat mapY,
        TransformOptions transform,
        Action<int, int>? progressCb,
        Func<bool>? cancelCb)
    {
        using var cap = new VideoCapture(inPath);
        if (!cap.IsOpened())
            throw new FileNotFoundException($"Could not open video: {inPath}");

        int total = cap.FrameCount;
        string tempDir = Path.Combine(Path.GetTempPath(), $"ff_frames_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var timestampsMs = new List<double>();
            var pngParams = new int[] { (int)ImwriteFlags.PngCompression, 1 };

            int frameIdx = 0;
            using var frame = new Mat();
            while (true)
            {
                if (cancelCb?.Invoke() == true)
                    break;

                // The upcoming frame's timestamp, queried before Read() advances past it.
                double ts = cap.Get(VideoCaptureProperties.PosMsec);
                if (!cap.Read(frame) || frame.Empty())
                    break;

                using var flat = FlattenPipeline.Run(frame, mapX, mapY, transform);
                Cv2.ImWrite(FramePath(tempDir, frameIdx), flat, pngParams);
                timestampsMs.Add(ts);

                frameIdx++;
                progressCb?.Invoke(frameIdx, total);
            }

            if (frameIdx == 0)
                throw new IOException($"No frames were read from: {inPath}");

            string listPath = Path.Combine(tempDir, "concat.txt");
            WriteConcatList(listPath, tempDir, timestampsMs, frameIdx);
            RunFfmpegConcat(listPath, outPath);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    private static string FramePath(string dir, int idx) => Path.Combine(dir, $"f{idx:D8}.png");

    /// <summary>ffmpeg's concat demuxer format: each frame file followed by how long
    /// (seconds) it should be displayed for, taken directly from the gap to the next
    /// frame's real timestamp. The final entry has no duration line (demuxer quirk -
    /// the last listed duration is ignored, so the last file is listed twice).</summary>
    private static void WriteConcatList(string listPath, string tempDir, List<double> timestampsMs, int frameCount)
    {
        double avgIntervalSec = frameCount > 1
            ? (timestampsMs[^1] - timestampsMs[0]) / 1000.0 / (frameCount - 1)
            : 1.0 / 15.0;

        using var writer = new StreamWriter(listPath);
        for (int i = 0; i < frameCount; i++)
        {
            double durationSec = i < frameCount - 1
                ? Math.Max((timestampsMs[i + 1] - timestampsMs[i]) / 1000.0, 0.001)
                : Math.Max(avgIntervalSec, 0.001);
            writer.WriteLine($"file '{Path.GetFileName(FramePath(tempDir, i))}'");
            writer.WriteLine($"duration {durationSec.ToString("0.000000", CultureInfo.InvariantCulture)}");
        }
        // Concat demuxer ignores the last file's duration line, so repeat it once more.
        writer.WriteLine($"file '{Path.GetFileName(FramePath(tempDir, frameCount - 1))}'");
    }

    private static void RunFfmpegConcat(string listPath, string outPath)
    {
        string? ffmpeg = AudioMuxer.FfmpegPath;
        if (ffmpeg == null)
            throw new IOException("ffmpeg is not available to assemble the video.");

        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
                 {
                     "-y", "-f", "concat", "-safe", "0", "-i", listPath,
                     "-fps_mode", "vfr", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-crf", "18",
                     outPath,
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi);
        var stdoutTask = proc!.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);

        if (proc.ExitCode != 0 || !File.Exists(outPath))
        {
            string tail = stderrTask.Result.Length <= 800 ? stderrTask.Result : stderrTask.Result[^800..];
            throw new IOException($"ffmpeg failed to assemble the video (exit {proc.ExitCode}): {tail.Trim()}");
        }
    }

    /// <summary>Fallback used only when ffmpeg isn't available: writes every frame at
    /// a single constant rate (actual frame count / actual duration when derivable,
    /// else the codec's reported average). Can't reproduce genuinely irregular
    /// (variable) frame timing within the clip - see ProcessVideoPreservingTimestamps.</summary>
    private static void ProcessVideoConstantFps(
        string inPath,
        string outPath,
        Mat mapX,
        Mat mapY,
        TransformOptions transform,
        Action<int, int>? progressCb,
        Func<bool>? cancelCb)
    {
        using var cap = new VideoCapture(inPath);
        if (!cap.IsOpened())
            throw new FileNotFoundException($"Could not open video: {inPath}");

        double fps = cap.Fps > 0 ? cap.Fps : 30.0;
        int total = cap.FrameCount;

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

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
    ///
    /// Returns the list of (start, end) time ranges, in the *source* file's own
    /// timeline (seconds), that the audio track should be trimmed down to match -
    /// see <see cref="WriteConcatList"/>/<see cref="GapThresholdSec"/> for why this
    /// can differ from "the whole file": a real recording gap gets collapsed to a
    /// near-invisible on-screen duration, which shortens the video's total length
    /// relative to the source: muxing the *full* original audio back onto that
    /// shorter video would leave everything after the gap out of sync by exactly
    /// however much was trimmed off. Null means no trimming is needed (either the
    /// constant-fps fallback path was used, which can't produce this map, or no gap
    /// in the source was large enough to be collapsed) - the caller should mux the
    /// whole original audio track unchanged.
    /// </summary>
    public static List<(double Start, double End)>? ProcessVideo(
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
            return ProcessVideoPreservingTimestamps(inPath, outPath, mapX, mapY, transform, progressCb, cancelCb);

        ProcessVideoConstantFps(inPath, outPath, mapX, mapY, transform, progressCb, cancelCb);
        return null;
    }

    private static List<(double Start, double End)> ProcessVideoPreservingTimestamps(
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

            int frameIdx = 0;
            int consecutiveReadFailures = 0;
            using var frame = new Mat();
            while (true)
            {
                if (cancelCb?.Invoke() == true)
                    break;

                // The upcoming frame's timestamp, queried before Read() advances past it.
                double ts = cap.Get(VideoCaptureProperties.PosMsec);
                if (!cap.Read(frame) || frame.Empty())
                {
                    // Could be genuine end-of-stream, or a transient decode hiccup on
                    // one damaged/unusual frame in otherwise-fine footage. If we're
                    // well short of the source's own frame-count estimate, try seeking
                    // a little further ahead and continuing rather than immediately
                    // treating this as the end - silently stopping partway through and
                    // still calling the export "successful" is worse than a few wasted
                    // retry attempts.
                    consecutiveReadFailures++;
                    bool wellShortOfEstimate = total > 10 && frameIdx < total * 0.7;
                    if (wellShortOfEstimate && consecutiveReadFailures <= 10)
                    {
                        double skipToMs = ts + consecutiveReadFailures * 200.0; // nudge forward 200ms per attempt
                        cap.Set(VideoCaptureProperties.PosMsec, skipToMs);
                        continue;
                    }
                    break;
                }
                consecutiveReadFailures = 0;

                using var flat = FlattenPipeline.Run(frame, mapX, mapY, transform);
                // BMP, not PNG: near-zero encode overhead (a raw byte dump vs. real
                // compression work) - these are temp files deleted right after ffmpeg
                // reads them, so the larger disk footprint costs nothing that matters,
                // and PNG encoding turned out to be a bigger share of export time than
                // the video encoder itself.
                string framePath = FramePath(tempDir, frameIdx);
                if (!Cv2.ImWrite(framePath, flat))
                    throw new IOException($"Failed to write temp frame {frameIdx} to {framePath} (disk full? permissions?).");
                timestampsMs.Add(ts);

                frameIdx++;
                progressCb?.Invoke(frameIdx, total);
            }

            if (frameIdx == 0)
                throw new IOException($"No frames were read from: {inPath}");

            // A large gap between how many frames we actually got and the source's own
            // estimate is exactly what a silent early-termination bug looks like from
            // the outside (a valid, non-empty export that's nonetheless missing the back
            // half of the clip) - fail loudly with the actual numbers instead of quietly
            // producing a short video, since cap.FrameCount can be somewhat imprecise
            // but not usually off by this much.
            if (total > 10 && frameIdx < total * 0.8)
            {
                throw new IOException(
                    $"Only read {frameIdx} of an expected ~{total} frames from {inPath} " +
                    "before the source stopped providing frames - the export would be missing " +
                    "the rest of the clip. This may indicate a decode issue partway through the " +
                    "source file.");
            }

            double[] frameDurationsSec = ComputeFrameDurationsSec(timestampsMs, frameIdx);

            string listPath = Path.Combine(tempDir, "concat.txt");
            WriteConcatList(listPath, tempDir, frameDurationsSec, frameIdx);
            RunFfmpegConcat(listPath, outPath);

            return BuildAudioKeepSegments(timestampsMs, frameDurationsSec, frameIdx);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    private static string FramePath(string dir, int idx) => Path.Combine(dir, $"f{idx:D8}.bmp");

    /// <summary>Motion-triggered recording can have a real, large gap between two
    /// consecutive frames where the camera simply wasn't recording (confirmed on real
    /// footage via three independent sources: OpenCV's own position, ffprobe's raw
    /// packet PTS, and the camera's own burned-in clock overlay jumping by the same
    /// ~55 seconds between those two frames). Initially this was reproduced as a
    /// literal hold on that one frame (capped at 2s so it wouldn't be the full 55) -
    /// but confirmed against the source file directly: common players evidently don't
    /// honor a gap this size as a real-time wait at all (the flashing police lights in
    /// this footage keep flashing continuously through it, with no visible pause), so
    /// even a 2-second hold in the export was a pause the source itself never actually
    /// shows. Gaps bigger than this threshold are now treated as "camera was idle,
    /// this timing isn't meaningful" and collapsed to a near-invisible duration
    /// instead of being shown at all - matching what viewers actually see when the
    /// source plays normally, not a faithful-but-wrong reproduction of dead air.</summary>
    private const double GapThresholdSec = 2.0;

    /// <summary>On-screen duration substituted for any gap larger than
    /// GapThresholdSec - short enough to be imperceptible as a pause (about one frame
    /// at a typical low frame rate), not zero (ffmpeg's concat demuxer requires a
    /// positive duration).</summary>
    private const double CollapsedGapDurationSec = 0.1;

    /// <summary>Each frame's on-screen duration (seconds): the gap to the next
    /// frame's real timestamp - or CollapsedGapDurationSec if that gap exceeds
    /// GapThresholdSec (see above); the last frame (no "next" to measure against)
    /// uses the file's overall average interval, same rule applied. Shared by
    /// <see cref="WriteConcatList"/> (the video timeline) and
    /// <see cref="BuildAudioKeepSegments"/> (the matching audio timeline) so the two
    /// are guaranteed to sum to the same total - see BuildAudioKeepSegments.</summary>
    private static double[] ComputeFrameDurationsSec(List<double> timestampsMs, int frameCount)
    {
        double avgIntervalSec = frameCount > 1
            ? (timestampsMs[^1] - timestampsMs[0]) / 1000.0 / (frameCount - 1)
            : 1.0 / 15.0;

        var durations = new double[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            double raw = i < frameCount - 1
                ? Math.Max((timestampsMs[i + 1] - timestampsMs[i]) / 1000.0, 0.001)
                : Math.Max(avgIntervalSec, 0.001);
            durations[i] = raw > GapThresholdSec ? CollapsedGapDurationSec : raw;
        }
        return durations;
    }

    /// <summary>ffmpeg's concat demuxer format: each frame file followed by how long
    /// (seconds) it should be displayed for. The final entry has no duration line
    /// (demuxer quirk - the last listed duration is ignored, so the last file is
    /// listed twice).</summary>
    private static void WriteConcatList(string listPath, string tempDir, double[] frameDurationsSec, int frameCount)
    {
        using var writer = new StreamWriter(listPath);
        for (int i = 0; i < frameCount; i++)
        {
            writer.WriteLine($"file '{Path.GetFileName(FramePath(tempDir, i))}'");
            writer.WriteLine($"duration {frameDurationsSec[i].ToString("0.000000", CultureInfo.InvariantCulture)}");
        }
        // Concat demuxer ignores the last file's duration line, so repeat it once more.
        writer.WriteLine($"file '{Path.GetFileName(FramePath(tempDir, frameCount - 1))}'");
    }

    /// <summary>
    /// Mirrors the same collapsing <see cref="WriteConcatList"/> applies to the video
    /// timeline, but expressed as which spans of the *source's* audio timeline to
    /// keep: everything up to a collapsed gap plays normally (1:1 with the source),
    /// the gap itself keeps only its first <see cref="CollapsedGapDurationSec"/> of
    /// audio (matching how long the held frame is actually shown for) with the rest
    /// dropped, then the next span resumes from the real timestamp of the frame
    /// right after the gap. Concatenating these spans back-to-back produces an audio
    /// track whose total length matches the (now-shorter) collapsed video exactly -
    /// walking the *same* per-frame duration values used for the video (rather than
    /// independently re-deriving gap sizes/thresholds) guarantees the two totals
    /// agree exactly rather than approximately, including the edge case where the
    /// very last frame's own duration (an average, not a real gap - there's no "next"
    /// frame to measure against) is itself large enough to be collapsed.
    /// </summary>
    private static List<(double Start, double End)> BuildAudioKeepSegments(
        List<double> timestampsMs, double[] frameDurationsSec, int frameCount)
    {
        var segments = new List<(double Start, double End)>();
        double segStartMs = timestampsMs[0];
        double curEndMs = timestampsMs[0];

        for (int i = 0; i < frameCount; i++)
        {
            curEndMs = timestampsMs[i] + frameDurationsSec[i] * 1000.0;

            bool isLast = i == frameCount - 1;
            if (!isLast)
            {
                double realNextMs = timestampsMs[i + 1];
                bool wasCapped = curEndMs < realNextMs - 0.5; // small epsilon for float noise
                if (wasCapped)
                {
                    segments.Add((segStartMs / 1000.0, curEndMs / 1000.0));
                    segStartMs = realNextMs;
                    curEndMs = realNextMs;
                }
            }
        }

        segments.Add((segStartMs / 1000.0, curEndMs / 1000.0));
        return segments;
    }

    private static void RunFfmpegConcat(string listPath, string outPath)
    {
        string encoder = AudioMuxer.PreferredVideoEncoder;
        try
        {
            RunFfmpegConcatWithEncoder(listPath, outPath, encoder);
        }
        catch (IOException) when (encoder != "libx264")
        {
            // The hardware encoder passed its quick capability test but failed on the
            // real export anyway (an unsupported resolution, a driver edge case,
            // VRAM pressure, etc.) - fall back to software rather than losing the
            // whole export over it.
            RunFfmpegConcatWithEncoder(listPath, outPath, "libx264");
        }
    }

    private static void RunFfmpegConcatWithEncoder(string listPath, string outPath, string encoder)
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
        var args = new System.Collections.Generic.List<string>
        {
            "-y", "-f", "concat", "-safe", "0", "-i", listPath,
            // libx264 with yuv420p requires even width/height (chroma planes are
            // subsampled 2x in both directions); the app lets output width/height be
            // set to any value including odd ones, so force-round down to even
            // rather than fail - "Could not open encoder... Invalid argument" is
            // exactly what libx264 (and the hardware encoders) do for an odd
            // dimension.
            "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2",
            "-fps_mode", "vfr", "-pix_fmt", "yuv420p", "-c:v", encoder,
        };
        args.AddRange(AudioMuxer.EncoderQualityArgs(encoder));
        args.Add(outPath);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi);
        var stdoutTask = proc!.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);

        if (proc.ExitCode != 0 || !File.Exists(outPath))
        {
            string tail = stderrTask.Result.Length <= 800 ? stderrTask.Result : stderrTask.Result[^800..];
            throw new IOException($"ffmpeg ({encoder}) failed to assemble the video (exit {proc.ExitCode}): {tail.Trim()}");
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

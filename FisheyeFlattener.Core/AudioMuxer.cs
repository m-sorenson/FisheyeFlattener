using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FisheyeFlattener.Core;

/// <summary>
/// OpenCV's VideoWriter (what VideoProcessor uses to write flattened frames) has no
/// audio support at all - that's a hard limitation of the library. This shells out to
/// ffmpeg, if available, to copy the original source's audio track onto the flattened
/// (video-only) output as a separate muxing step.
/// </summary>
public static class AudioMuxer
{
    private static string? _ffmpegPath;
    private static string? _ffprobePath;
    private static bool _resolved;

    /// <summary>Set (non-null) after a MuxAudio call that returned false, explaining
    /// why audio wasn't included, instead of failing silently.</summary>
    public static string? LastSkipReason { get; private set; }

    public static bool IsFfmpegAvailable()
    {
        ResolveTools();
        return _ffmpegPath != null;
    }

    /// <summary>The resolved ffmpeg executable (bare name if on PATH, or a full path
    /// found via the winget install folder fallback), or null if unavailable.</summary>
    public static string? FfmpegPath
    {
        get
        {
            ResolveTools();
            return _ffmpegPath;
        }
    }

    private static string? _detectedEncoder;
    private static bool _encoderDetected;

    /// <summary>
    /// The best available H.264 encoder for this machine: tries NVIDIA NVENC, AMD
    /// AMF, then Intel QuickSync (common consumer GPU encoders, all built into the
    /// ffmpeg distribution this app installs), falling back to libx264 (software) if
    /// none of them actually work here. ffmpeg being *compiled* with support for one
    /// of these doesn't mean the GPU/driver on this machine actually accepts it, so
    /// each candidate is verified with a real tiny test encode rather than assumed
    /// from the presence of a matching graphics card.
    /// </summary>
    public static string PreferredVideoEncoder
    {
        get
        {
            if (!_encoderDetected)
            {
                _detectedEncoder = DetectHardwareEncoder();
                _encoderDetected = true;
            }
            return _detectedEncoder ?? "libx264";
        }
    }

    /// <summary>ffmpeg quality/preset arguments appropriate for the given video
    /// encoder - each hardware encoder uses different flag names than libx264's
    /// familiar -crf.</summary>
    public static string[] EncoderQualityArgs(string encoder) => encoder switch
    {
        "h264_nvenc" => new[] { "-preset", "p4", "-cq", "20" },
        "h264_amf" => new[] { "-quality", "balanced", "-rc", "cqp", "-qp_i", "20", "-qp_p", "20" },
        "h264_qsv" => new[] { "-preset", "medium", "-global_quality", "20" },
        _ => new[] { "-preset", "medium", "-crf", "18" },
    };

    private static string? DetectHardwareEncoder()
    {
        ResolveTools();
        if (_ffmpegPath == null)
            return null;

        foreach (var candidate in new[] { "h264_nvenc", "h264_amf", "h264_qsv" })
        {
            if (TestEncoder(candidate))
                return candidate;
        }
        return null;
    }

    private static bool TestEncoder(string encoder)
    {
        try
        {
            var psi = new ProcessStartInfo(_ffmpegPath!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // 256x256: comfortably above NVENC's (and other hardware encoders')
            // minimum supported frame dimension - a too-small test frame fails with
            // "Frame Dimension less than the minimum supported value", which looks
            // exactly like "this encoder doesn't work here" but isn't.
            foreach (var arg in new[]
                     {
                         "-y", "-f", "lavfi", "-i", "color=black:s=256x256:d=0.1",
                         "-c:v", encoder, "-f", "null", "-",
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            using var p = Process.Start(psi);
            var stdoutTask = p!.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000))
            {
                p.Kill(entireProcessTree: true);
                return false;
            }
            Task.WaitAll(stdoutTask, stderrTask);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ResolveTools()
    {
        if (_resolved)
            return;
        _resolved = true;
        _ffmpegPath = ResolveTool("ffmpeg");
        _ffprobePath = ResolveTool("ffprobe");
    }

    private static string? ResolveTool(string name)
    {
        // Prefer PATH - respects whatever the user has installed/upgraded to.
        if (TryRun(name, "-version", out _))
            return name;

        // Fall back to the known winget install location: a PATH change made by an
        // installer isn't visible to a process whose environment block predates the
        // change (e.g. an Explorer/shell session that was already running when
        // ffmpeg was installed) - this makes ffmpeg still resolve without requiring
        // the user to sign out or reboot first.
        try
        {
            string wingetPackages = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(wingetPackages))
            {
                return Directory
                    .EnumerateFiles(wingetPackages, $"{name}.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
            }
        }
        catch
        {
            // ignore - just means we couldn't find it this way either
        }
        return null;
    }

    private static bool TryRun(string exe, string args, out string stderr)
    {
        stderr = "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            var stdoutTask = p!.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                p.Kill(entireProcessTree: true);
                return false;
            }
            Task.WaitAll(stdoutTask, stderrTask);
            stderr = stderrTask.Result;
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Queries a media file's actual container duration via ffprobe. This is a more
    /// reliable basis for "frames / seconds" than a codec's reported average frame
    /// rate: for real-world (especially security camera) footage, the reported rate
    /// can be a rounded or otherwise imprecise approximation, and re-encoding at that
    /// rate makes the output's total length not quite match its audio track's length
    /// - which compounds into a growing audio/video gap across the file rather than a
    /// fixed offset. Returns null if ffprobe isn't available or duration couldn't be read.
    /// </summary>
    public static double? GetDurationSeconds(string path)
    {
        ResolveTools();
        if (_ffprobePath == null)
            return null;
        try
        {
            var psi = new ProcessStartInfo(_ffprobePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("format=duration");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("csv=p=0");
            psi.ArgumentList.Add(path);

            using var p = Process.Start(psi);
            var stdoutTask = p!.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000))
            {
                p.Kill(entireProcessTree: true);
                return null;
            }
            Task.WaitAll(stdoutTask, stderrTask);
            string output = stdoutTask.Result.Trim();
            return double.TryParse(output, System.Globalization.CultureInfo.InvariantCulture, out double seconds) && seconds > 0
                ? seconds
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Counts a video's actual decodable frames via ffprobe's -count_frames, which
    /// forces real decoding rather than trusting container metadata. Deliberately NOT
    /// the same thing as OpenCV's CAP_PROP_FRAME_COUNT or ffprobe's plain nb_frames
    /// field: both of those are frequently just an *estimate* (duration * average
    /// fps) for real-world compressed video, not an actual count, and using an
    /// estimate on one side of a frames/duration ratio while writing that many actual
    /// frames on the other reintroduces the exact mismatch this exists to eliminate.
    /// Slower than a metadata read (real decode), so allows a longer timeout. Returns
    /// null if ffprobe isn't available or counting failed/timed out.
    /// </summary>
    public static int? GetActualFrameCount(string path)
    {
        ResolveTools();
        if (_ffprobePath == null)
            return null;
        try
        {
            var psi = new ProcessStartInfo(_ffprobePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-count_frames");
            psi.ArgumentList.Add("-select_streams");
            psi.ArgumentList.Add("v:0");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("stream=nb_read_frames");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("csv=p=0");
            psi.ArgumentList.Add(path);

            using var p = Process.Start(psi);
            var stdoutTask = p!.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120_000))
            {
                p.Kill(entireProcessTree: true);
                return null;
            }
            Task.WaitAll(stdoutTask, stderrTask);
            string output = stdoutTask.Result.Trim();
            return int.TryParse(output, out int count) && count > 0 ? count : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Per-stream duration (video, audio) of a media file, for diagnosing a
    /// mismatch after the fact. Either value is null if that stream/duration couldn't
    /// be read.</summary>
    public static (double? Video, double? Audio) GetStreamDurations(string path)
    {
        ResolveTools();
        if (_ffprobePath == null)
            return (null, null);

        double? Query(string select)
        {
            try
            {
                var psi = new ProcessStartInfo(_ffprobePath!)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-v");
                psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-select_streams");
                psi.ArgumentList.Add(select);
                psi.ArgumentList.Add("-show_entries");
                psi.ArgumentList.Add("stream=duration");
                psi.ArgumentList.Add("-of");
                psi.ArgumentList.Add("csv=p=0");
                psi.ArgumentList.Add(path);

                using var p = Process.Start(psi);
                var stdoutTask = p!.StandardOutput.ReadToEndAsync();
                var stderrTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(15000))
                {
                    p.Kill(entireProcessTree: true);
                    return null;
                }
                Task.WaitAll(stdoutTask, stderrTask);
                string output = stdoutTask.Result.Trim();
                return double.TryParse(output, System.Globalization.CultureInfo.InvariantCulture, out double seconds) && seconds > 0
                    ? seconds
                    : null;
            }
            catch
            {
                return null;
            }
        }

        return (Query("v:0"), Query("a:0"));
    }

    private static bool SourceHasAudio(string sourcePath, out string stderr)
    {
        stderr = "";
        if (_ffprobePath == null)
            return false;
        try
        {
            var psi = new ProcessStartInfo(_ffprobePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-select_streams");
            psi.ArgumentList.Add("a");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("stream=index");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("csv=p=0");
            psi.ArgumentList.Add(sourcePath);

            using var p = Process.Start(psi);
            var stdoutTask = p!.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000))
            {
                p.Kill(entireProcessTree: true);
                return false;
            }
            Task.WaitAll(stdoutTask, stderrTask);
            stderr = stderrTask.Result;
            return !string.IsNullOrWhiteSpace(stdoutTask.Result);
        }
        catch (Exception ex)
        {
            stderr = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Muxes <paramref name="originalSourcePath"/>'s audio track onto the flattened
    /// (video-only) <paramref name="flattenedVideoPath"/>, producing
    /// <paramref name="finalOutputPath"/>. Falls back to a plain video-only copy (no
    /// exception) if ffmpeg isn't available or the source has no audio track -
    /// <see cref="LastSkipReason"/> explains which, and why, for a status message.
    /// Returns true if audio was actually included.
    /// </summary>
    public static bool MuxAudio(string flattenedVideoPath, string originalSourcePath, string finalOutputPath)
    {
        LastSkipReason = null;
        ResolveTools();

        if (_ffmpegPath == null)
        {
            LastSkipReason = "ffmpeg was not found (checked PATH and the winget install folder).";
            MoveToFinal(flattenedVideoPath, finalOutputPath);
            return false;
        }

        if (!SourceHasAudio(originalSourcePath, out string probeErr))
        {
            LastSkipReason = string.IsNullOrWhiteSpace(probeErr)
                ? "No audio track was found in the source file."
                : $"Could not read audio info from the source file: {Tail(probeErr, 300)}";
            MoveToFinal(flattenedVideoPath, finalOutputPath);
            return false;
        }

        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
                 {
                     "-y", "-i", flattenedVideoPath, "-i", originalSourcePath,
                     "-map", "0:v:0", "-map", "1:a:0",
                     "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-shortest",
                     finalOutputPath,
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi);
        // ffmpeg writes its progress/status output to stderr continuously; without
        // draining the redirected streams the OS pipe buffer fills and ffmpeg blocks
        // writing while we block in WaitForExit() - a classic deadlock. Read both
        // streams to completion (async) before waiting for exit.
        var stdoutTask = proc!.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);

        if (proc.ExitCode != 0 || !File.Exists(finalOutputPath))
        {
            LastSkipReason = $"ffmpeg failed to mux audio (exit {proc.ExitCode}): {Tail(stderrTask.Result, 500)}";
            MoveToFinal(flattenedVideoPath, finalOutputPath);
            return false;
        }

        File.Delete(flattenedVideoPath);
        return true;
    }

    private static string Tail(string s, int maxLen) => s.Length <= maxLen ? s.Trim() : s[^maxLen..].Trim();

    private static void MoveToFinal(string source, string finalOutputPath)
    {
        if (File.Exists(finalOutputPath))
            File.Delete(finalOutputPath);
        File.Move(source, finalOutputPath);
    }
}

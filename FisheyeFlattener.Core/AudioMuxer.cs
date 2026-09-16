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

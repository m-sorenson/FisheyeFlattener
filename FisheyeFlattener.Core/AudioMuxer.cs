using System.Diagnostics;
using System.IO;
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
    public static bool IsFfmpegAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
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
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool SourceHasAudio(string sourcePath)
    {
        try
        {
            var psi = new ProcessStartInfo("ffprobe")
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
            return !string.IsNullOrWhiteSpace(stdoutTask.Result);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Muxes <paramref name="originalSourcePath"/>'s audio track onto the flattened
    /// (video-only) <paramref name="flattenedVideoPath"/>, producing
    /// <paramref name="finalOutputPath"/>. Falls back to a plain video-only copy (no
    /// error) if ffmpeg isn't available or the source has no audio track.
    /// Returns true if audio was actually included.
    /// </summary>
    public static bool MuxAudio(string flattenedVideoPath, string originalSourcePath, string finalOutputPath)
    {
        bool hasAudio = IsFfmpegAvailable() && SourceHasAudio(originalSourcePath);

        if (!hasAudio)
        {
            MoveToFinal(flattenedVideoPath, finalOutputPath);
            return false;
        }

        var psi = new ProcessStartInfo("ffmpeg")
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
            MoveToFinal(flattenedVideoPath, finalOutputPath);
            return false;
        }

        File.Delete(flattenedVideoPath);
        return true;
    }

    private static void MoveToFinal(string source, string finalOutputPath)
    {
        if (File.Exists(finalOutputPath))
            File.Delete(finalOutputPath);
        File.Move(source, finalOutputPath);
    }
}

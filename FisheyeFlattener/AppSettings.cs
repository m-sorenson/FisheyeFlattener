using System;
using System.IO;
using System.Text.Json;

namespace FisheyeFlattener;

/// <summary>
/// User preferences persisted across sessions: window placement, which side panels
/// are shown, preferred export resolution, and playback volume.
///
/// Deliberately does NOT include lens calibration (Center X/Y/Radius/FOV), pan/tilt/
/// zoom/roll, or the source flip checkboxes - those describe a specific video/image,
/// not a standing preference. Restoring an old file's calibration onto a brand new
/// one (a different camera, a different mount angle) would silently mis-frame it
/// rather than helpfully "remembering" anything.
/// </summary>
public class AppSettings
{
    public double WindowWidth { get; set; } = 1300;
    public double WindowHeight { get; set; } = 800;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool IsMaximized { get; set; }

    public bool ShowLensCalibrationPanel { get; set; } = true;
    public bool ShowPreviewOutputSettingsPanel { get; set; } = true;

    public int OutputWidth { get; set; } = 1280;
    public int OutputHeight { get; set; } = 720;

    public double Volume { get; set; } = 100;
    public bool IsMuted { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FisheyeFlattener", "settings.json");

    /// <summary>Returns saved settings, or defaults if none exist yet or the file
    /// can't be read (corrupt, wrong format after an app update, etc.) - a bad
    /// preferences file should never stop the app from starting.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                    return loaded;
            }
        }
        catch
        {
            // Fall back to defaults rather than failing to start over a corrupt file.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best effort - losing a preferences save on exit shouldn't crash the app.
        }
    }
}

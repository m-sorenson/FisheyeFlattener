using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FisheyeFlattener;

/// <summary>Non-modal export progress popup (owned by MainWindow, shown via Show() so
/// the caller's async export Task keeps running while it's up). Every mutator marshals
/// onto the UI thread itself so background-thread callers (the export Task.Run body,
/// and ffmpeg's own async stdout-line callback) can call them directly without the
/// caller having to wrap each call in Dispatcher.Invoke.
///
/// Two bars: "Overall progress" is a weighted combination across every export stage
/// (frame reading, ffmpeg encoding, audio muxing - see MainWindow's stage weights),
/// so it doesn't jump backwards or stall between stages; the second bar is the
/// current stage's own detailed progress (e.g. "Encoding video: frame 800/2444").</summary>
public partial class ExportProgressWindow : Window
{
    public event EventHandler? CancelRequested;

    private string? _revealPath;

    public ExportProgressWindow()
    {
        InitializeComponent();
        OpenLocationIcon.Source = GetStandardFolderIcon();
    }

    // Pulls the OS's own stock folder icon (the exact one Explorer uses, whatever
    // Windows version/theme this machine has) rather than a font glyph that only
    // approximates it - SHGetStockIconInfo is the documented way to get standard
    // shell icons by ID instead of guessing at a real file/folder path.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetStockIconInfo(uint siid, uint uFlags, ref ShStockIconInfo psii);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShStockIconInfo
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }

    private const uint SiidFolder = 3;
    private const uint ShgsiIcon = 0x000000100;
    private const uint ShgsiSmallIcon = 0x000000001;

    private static BitmapSource? GetStandardFolderIcon()
    {
        var info = new ShStockIconInfo();
        info.cbSize = (uint)Marshal.SizeOf<ShStockIconInfo>();
        int hr = SHGetStockIconInfo(SiidFolder, ShgsiIcon | ShgsiSmallIcon, ref info);
        if (hr != 0 || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            // CreateBitmapSourceFromHIcon copies the pixel data - the source HICON
            // from the shell isn't needed (or owned by us to leak) after that.
            DestroyIcon(info.hIcon);
        }
    }

    public void UpdateProgress(double overallPercent, double stagePercent, string status)
    {
        Dispatcher.Invoke(() =>
        {
            OverallProgressBar.IsIndeterminate = false;
            OverallProgressBar.Value = Math.Clamp(overallPercent, 0, 100);
            StageProgressBar.IsIndeterminate = false;
            StageProgressBar.Value = Math.Clamp(stagePercent, 0, 100);
            StageTextBlock.Text = status;
        });
    }

    /// <summary>For a stage with no per-item progress to report (currently: the
    /// ffmpeg audio mux, which runs to completion in one call) - the overall bar still
    /// holds a fixed, correct position while the stage bar animates indeterminately.</summary>
    public void SetStageIndeterminate(double overallPercent, string status)
    {
        Dispatcher.Invoke(() =>
        {
            OverallProgressBar.IsIndeterminate = false;
            OverallProgressBar.Value = Math.Clamp(overallPercent, 0, 100);
            StageProgressBar.IsIndeterminate = true;
            StageTextBlock.Text = status;
        });
    }

    /// <summary>Cancellation only takes effect during frame-by-frame processing -
    /// once that's done there's no cheap way to interrupt the ffmpeg step already in
    /// flight, so disable the button rather than accept a click that won't do anything.</summary>
    public void DisableCancel()
    {
        Dispatcher.Invoke(() => CancelButton.IsEnabled = false);
    }

    public void ShowComplete(string message, string? revealPath)
    {
        Dispatcher.Invoke(() =>
        {
            Title = "Export complete";
            OverallProgressBar.IsIndeterminate = false;
            OverallProgressBar.Value = 100;
            StageProgressBar.IsIndeterminate = false;
            StageProgressBar.Value = 100;
            StageTextBlock.Text = message;
            CancelButton.Visibility = Visibility.Collapsed;
            _revealPath = revealPath;
            OpenLocationButton.Visibility = revealPath != null ? Visibility.Visible : Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
        });
    }

    public void ShowError(string message)
    {
        Dispatcher.Invoke(() =>
        {
            Title = "Export failed";
            OverallProgressBar.IsIndeterminate = false;
            StageProgressBar.IsIndeterminate = false;
            StageTextBlock.Text = message;
            CancelButton.Visibility = Visibility.Collapsed;
            OpenLocationButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
        });
    }

    public void ShowCancelled()
    {
        Dispatcher.Invoke(() =>
        {
            Title = "Export cancelled";
            OverallProgressBar.IsIndeterminate = false;
            StageProgressBar.IsIndeterminate = false;
            StageTextBlock.Text = "Export cancelled.";
            CancelButton.Visibility = Visibility.Collapsed;
            OpenLocationButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
        });
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StageTextBlock.Text = "Cancelling...";
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenLocationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_revealPath == null)
            return;
        try
        {
            // /select, highlights the file itself in Explorer rather than just opening
            // its containing folder with nothing selected. MUST be a single arguments
            // STRING with the quotes hugging just the path ("/select,\"C:\...\"") -
            // explorer.exe's argument parsing for this flag is not standard argv
            // parsing, and passing the same content via ArgumentList (which wraps the
            // *entire* "/select,C:\..." token in quotes when the path contains a
            // space, since that's a normal/valid single argv token) makes explorer.exe
            // silently fail to parse it and fall back to opening Documents instead -
            // confirmed by screenshot-testing both forms directly, not just reasoned
            // about, after this shipped and a real user hit it against an installed
            // build. NTFS forbids '"' in filenames, so _revealPath (a SaveFileDialog
            // result) can't break out of this manual quoting.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_revealPath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best effort - not worth failing the export over a shell launch hiccup.
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

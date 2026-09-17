using System;
using System.Diagnostics;
using System.Windows;

namespace FisheyeFlattener;

/// <summary>Non-modal export progress popup (owned by MainWindow, shown via Show() so
/// the caller's async export Task keeps running while it's up). Every mutator marshals
/// onto the UI thread itself so background-thread callers (the export Task.Run body)
/// can call them directly without the caller having to wrap each call in
/// Dispatcher.Invoke.</summary>
public partial class ExportProgressWindow : Window
{
    public event EventHandler? CancelRequested;

    private string? _revealPath;

    public ExportProgressWindow()
    {
        InitializeComponent();
    }

    public void UpdateProgress(double percent, string status)
    {
        Dispatcher.Invoke(() =>
        {
            ProgressBarControl.IsIndeterminate = false;
            ProgressBarControl.Value = Math.Clamp(percent, 0, 100);
            StatusTextBlock.Text = status;
        });
    }

    /// <summary>For steps with no per-item progress to report (e.g. the ffmpeg audio
    /// mux, which runs to completion in one call).</summary>
    public void SetIndeterminate(string status)
    {
        Dispatcher.Invoke(() =>
        {
            ProgressBarControl.IsIndeterminate = true;
            StatusTextBlock.Text = status;
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
            ProgressBarControl.IsIndeterminate = false;
            ProgressBarControl.Value = 100;
            StatusTextBlock.Text = message;
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
            StatusTextBlock.Text = message;
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
            StatusTextBlock.Text = "Export cancelled.";
            CancelButton.Visibility = Visibility.Collapsed;
            OpenLocationButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
        });
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StatusTextBlock.Text = "Cancelling...";
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenLocationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_revealPath == null)
            return;
        try
        {
            // /select, highlights the file itself in Explorer rather than just opening
            // its containing folder with nothing selected.
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

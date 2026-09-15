using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using FisheyeFlattener.Core;
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace FisheyeFlattener;

public partial class MainWindow : System.Windows.Window
{
    private static readonly string[] VideoExts = { ".mp4", ".mov", ".avi", ".mkv" };
    private static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

    private string? _sourcePath;
    private bool _isVideo;
    private Mat? _previewFrame;
    private CircleCalibration? _calib;

    // Field initializer: NumericSlider controls fire ValueChanged as soon as XAML
    // assigns their initial Value during InitializeComponent(), so this must exist
    // before that call happens (constructor-body assignment runs too late).
    private readonly DispatcherTimer _previewDebounce = new() { Interval = TimeSpan.FromMilliseconds(60) };

    public MainWindow()
    {
        _previewDebounce.Tick += (_, _) =>
        {
            _previewDebounce.Stop();
            UpdatePreview();
        };

        InitializeComponent();
    }

    // ------------------------------------------------------------- events --

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open fisheye export",
            Filter = "Media|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.mp4;*.mov;*.avi;*.mkv",
        };
        if (dialog.ShowDialog() != true)
            return;

        string ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        bool isVideo = Array.IndexOf(VideoExts, ext) >= 0;
        if (!isVideo && Array.IndexOf(ImageExts, ext) < 0)
        {
            MessageBox.Show(this, $"Unsupported file type: {ext}", "Unsupported file",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Mat frame;
        try
        {
            frame = isVideo ? VideoProcessor.GetFirstFrame(dialog.FileName) : ImageProcessor.Load(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open file", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _sourcePath = dialog.FileName;
        _isVideo = isVideo;
        _previewFrame?.Dispose();
        _previewFrame = frame;
        _calib = Calibration.DefaultCalibration(frame);
        SyncCalibrationControls();

        ExportButton.IsEnabled = true;
        StatusText.Text = $"Loaded {(isVideo ? "video" : "image")}: {Path.GetFileName(dialog.FileName)}";
        UpdatePreview();
    }

    private void AutoDetectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewFrame == null)
            return;
        _calib = Calibration.DefaultCalibration(_previewFrame);
        SyncCalibrationControls();
        UpdatePreview();
    }

    private void MountCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        switch (MountCombo.SelectedIndex)
        {
            case 0: // Ceiling
                ModeCombo.SelectedIndex = 1; // panorama
                ThetaTopSlider.Value = 90;
                ThetaBottomSlider.Value = 10;
                ReferenceSlider.Value = 0;
                break;
            case 1: // Wall
                ModeCombo.SelectedIndex = 1; // panorama
                AzStartSlider.Value = -90;
                AzEndSlider.Value = 90;
                ThetaTopSlider.Value = 90;
                ThetaBottomSlider.Value = 0;
                ReferenceSlider.Value = -90;
                break;
        }
        UpdatePreview();
    }

    private void ModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        bool perspective = ModeCombo.SelectedIndex == 0;
        PerspectivePanel.Visibility = perspective ? Visibility.Visible : Visibility.Collapsed;
        PanoramaPanel.Visibility = perspective ? Visibility.Collapsed : Visibility.Visible;
        UpdatePreview();
    }

    private void Param_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePath == null)
            return;

        var calib = CurrentCalibration();
        var map = CurrentMap(calib);
        var (mapX, mapY) = map.ToMats();

        if (_isVideo)
        {
            var dialog = new SaveFileDialog { Title = "Export flattened video", Filter = "MP4 video|*.mp4" };
            if (dialog.ShowDialog() != true)
                return;

            ExportButton.IsEnabled = false;
            OpenButton.IsEnabled = false;
            ExportProgress.Visibility = Visibility.Visible;
            ExportProgress.Value = 0;
            StatusText.Text = "Exporting video...";

            string inPath = _sourcePath;
            string outPath = dialog.FileName;

            try
            {
                await Task.Run(() =>
                {
                    VideoProcessor.ProcessVideo(inPath, outPath, mapX, mapY, (done, total) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (total > 0)
                                ExportProgress.Value = 100.0 * done / total;
                            StatusText.Text = $"Exporting video... frame {done}/{(total > 0 ? total.ToString() : "?")}";
                        });
                    });
                });
                StatusText.Text = "Video export complete.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Video export failed.";
                MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                mapX.Dispose();
                mapY.Dispose();
                ExportProgress.Visibility = Visibility.Collapsed;
                ExportButton.IsEnabled = true;
                OpenButton.IsEnabled = true;
            }
        }
        else
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export flattened image",
                Filter = "PNG image|*.png|JPEG image|*.jpg",
            };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                using var fullRes = ImageProcessor.Load(_sourcePath);
                using var flat = ImageProcessor.ApplyMap(fullRes, mapX, mapY);
                ImageProcessor.Save(dialog.FileName, flat);
                StatusText.Text = $"Saved: {dialog.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                mapX.Dispose();
                mapY.Dispose();
            }
        }
    }

    // ------------------------------------------------------------- helpers --

    private void SyncCalibrationControls()
    {
        if (_calib == null)
            return;
        CenterXSlider.Value = _calib.CenterX;
        CenterYSlider.Value = _calib.CenterY;
        RadiusSlider.Value = _calib.Radius;
    }

    private CircleCalibration CurrentCalibration() => new()
    {
        CenterX = CenterXSlider.Value,
        CenterY = CenterYSlider.Value,
        Radius = RadiusSlider.Value,
        MaxFovDeg = MaxFovSlider.Value,
    };

    private DewarpMap CurrentMap(CircleCalibration calib)
    {
        if (ModeCombo.SelectedIndex == 0)
        {
            var p = new PerspectiveParams
            {
                YawDeg = YawSlider.Value,
                PitchDeg = PitchSlider.Value,
                FovDeg = FovSlider.Value,
                OutWidth = (int)PerspWidthSlider.Value,
                OutHeight = (int)PerspHeightSlider.Value,
            };
            return DewarpMath.BuildPerspectiveMap(calib, p);
        }
        else
        {
            var p = new PanoramaParams
            {
                AzimuthStartDeg = AzStartSlider.Value,
                AzimuthEndDeg = AzEndSlider.Value,
                ThetaTopDeg = ThetaTopSlider.Value,
                ThetaBottomDeg = ThetaBottomSlider.Value,
                ReferenceDeg = ReferenceSlider.Value,
                OutWidth = (int)PanoWidthSlider.Value,
                OutHeight = (int)PanoHeightSlider.Value,
            };
            return DewarpMath.BuildPanoramaMap(calib, p);
        }
    }

    private void UpdatePreview()
    {
        if (_previewFrame == null)
            return;

        var calib = CurrentCalibration();
        var map = CurrentMap(calib);
        var (mapX, mapY) = map.ToMats();
        using (mapX)
        using (mapY)
        using (var flat = ImageProcessor.ApplyMap(_previewFrame, mapX, mapY))
        {
            PreviewImage.Source = flat.ToBitmapSource();
        }
    }
}

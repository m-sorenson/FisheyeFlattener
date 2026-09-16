using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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

    private const double DefaultYawDeg = 0.0;

    // Pitch is the angle from the lens's optical axis/nadir (0 = straight down at the
    // fisheye's center, ~90 = at the horizon) - see DewarpMath.PerspectiveParams.
    private const double DefaultPitchDeg = 75.0;
    private const double MinPitchDeg = 1.0;
    private const double MaxPitchDeg = 105.0;

    private string? _sourcePath;
    private bool _isVideo;
    private Mat? _previewFrame; // frame 0, immutable reference used for calibration
    private Mat? _currentFrame; // whatever raw frame is currently displayed (playback/seek swap this)
    private CircleCalibration? _calib;

    private double _yawDeg = DefaultYawDeg;
    private double _pitchDeg = DefaultPitchDeg;
    private const double KeyStepDeg = 5.0;

    private Mat? _cachedMapX;
    private Mat? _cachedMapY;

    // Drag-to-look
    private bool _isDragging;
    private System.Windows.Point _dragStart;
    private double _dragStartYaw;
    private double _dragStartPitch;

    // Snap-level (click two points along an edge that should be horizontal)
    private bool _isSettingLevel;
    private System.Windows.Point? _levelPoint1;

    // Playback
    private VideoCapture? _playbackCapture;
    private DispatcherTimer? _playbackTimer;
    private bool _isPlaying;
    private bool _suppressSeek;
    private double _playbackFps = 30.0;
    private int _playbackTotalFrames;
    private readonly Stopwatch _playbackStopwatch = new();
    private int _playbackStartFrame;

    // Field initializer: NumericSlider controls fire ValueChanged as soon as XAML
    // assigns their initial Value during InitializeComponent(), so this must exist
    // before that call happens (constructor-body assignment runs too late).
    private readonly DispatcherTimer _previewDebounce = new() { Interval = TimeSpan.FromMilliseconds(60) };

    public MainWindow()
    {
        _previewDebounce.Tick += (_, _) =>
        {
            _previewDebounce.Stop();
            OnParametersChanged();
        };

        InitializeComponent();
        Closed += (_, _) => ReleaseVideoResources();
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
        VideoCapture? capture = null;
        try
        {
            if (isVideo)
            {
                capture = new VideoCapture(dialog.FileName);
                if (!capture.IsOpened())
                    throw new FileNotFoundException($"Could not open video: {dialog.FileName}");
                frame = new Mat();
                if (!capture.Read(frame) || frame.Empty())
                    throw new IOException($"Could not read a frame from: {dialog.FileName}");
            }
            else
            {
                frame = ImageProcessor.Load(dialog.FileName);
            }
        }
        catch (Exception ex)
        {
            capture?.Dispose();
            MessageBox.Show(this, ex.Message, "Could not open file", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _sourcePath = dialog.FileName;
        _isVideo = isVideo;

        _previewFrame?.Dispose();
        _previewFrame = frame;

        _currentFrame?.Dispose();
        _currentFrame = frame.Clone();

        ReleaseVideoResources();
        SetUpPlayback(isVideo, capture);

        // fresh file -> fresh orientation/framing/view state
        SourceFlipHCheck.IsChecked = false;
        SourceFlipVCheck.IsChecked = false;
        _yawDeg = DefaultYawDeg;
        _pitchDeg = DefaultPitchDeg;
        FovSlider.Value = 90;
        RollSlider.Value = 0;
        _isSettingLevel = false;
        _levelPoint1 = null;

        _calib = Calibration.DefaultCalibration(frame);
        SyncCalibrationControls();

        ExportButton.IsEnabled = true;
        StatusText.Text = $"Loaded {(isVideo ? "video" : "image")}: {Path.GetFileName(dialog.FileName)}";
        OnParametersChanged();
    }

    private void AutoDetectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewFrame == null)
            return;
        using var working = Transform.FlipClone(_previewFrame, SourceFlipHCheck.IsChecked == true, SourceFlipVCheck.IsChecked == true);
        _calib = Calibration.DefaultCalibration(working);
        SyncCalibrationControls();
        OnParametersChanged();
    }

    private void SourceFlipH_Changed(object sender, RoutedEventArgs e)
    {
        if (_previewFrame != null)
            CenterXSlider.Value = Transform.MirrorX(CenterXSlider.Value, _previewFrame.Cols);
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private void SourceFlipV_Changed(object sender, RoutedEventArgs e)
    {
        if (_previewFrame != null)
            CenterYSlider.Value = Transform.MirrorY(CenterYSlider.Value, _previewFrame.Rows);
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private void ResetViewButton_Click(object sender, RoutedEventArgs e)
    {
        _yawDeg = DefaultYawDeg;
        _pitchDeg = DefaultPitchDeg;
        FovSlider.Value = 90;
        RollSlider.Value = 0;
        _isSettingLevel = false;
        _levelPoint1 = null;
        OnParametersChanged();
    }

    /// <summary>Arrow-key nudge: Up/Down tilt, Left/Right pan, one KeyStepDeg per
    /// press (holding the key repeats it). Only touches yaw/pitch - roll/zoom are
    /// untouched, same as dragging.</summary>
    private void PreviewImage_KeyDown(object sender, KeyEventArgs e)
    {
        if (_currentFrame == null && _previewFrame == null)
            return;

        switch (e.Key)
        {
            case Key.Up:
                _pitchDeg = Clamp(_pitchDeg + KeyStepDeg, MinPitchDeg, MaxPitchDeg);
                break;
            case Key.Down:
                _pitchDeg = Clamp(_pitchDeg - KeyStepDeg, MinPitchDeg, MaxPitchDeg);
                break;
            case Key.Left:
                _yawDeg = NormalizeAngleDeg(_yawDeg - KeyStepDeg);
                break;
            case Key.Right:
                _yawDeg = NormalizeAngleDeg(_yawDeg + KeyStepDeg);
                break;
            default:
                return;
        }

        e.Handled = true;
        OnParametersChanged();
    }

    private void SnapLevelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFrame == null && _previewFrame == null)
            return;
        _isSettingLevel = true;
        _levelPoint1 = null;
        StatusText.Text = "Click two points along an edge that should be level (e.g. a doorframe or wall edge)...";
    }

    private void Param_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    // ----------------------------------------------------------- drag/zoom --

    private void PreviewImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentFrame == null && _previewFrame == null)
            return;

        PreviewImage.Focus();

        if (_isSettingLevel)
        {
            HandleLevelClick(e.GetPosition(PreviewImage));
            return;
        }

        _isDragging = true;
        _dragStart = e.GetPosition(PreviewImage);
        _dragStartYaw = _yawDeg;
        _dragStartPitch = _pitchDeg;
        PreviewImage.CaptureMouse();
    }

    /// <summary>Two clicks define a line that should be horizontal; rotates Roll so it
    /// is. Because panning has no roll drift (verified in DewarpMathTests), a single
    /// level correction holds no matter where you pan/tilt afterward.</summary>
    private void HandleLevelClick(System.Windows.Point pos)
    {
        if (_levelPoint1 == null)
        {
            _levelPoint1 = pos;
            StatusText.Text = "Click the second point along that same edge...";
            return;
        }

        var p1 = _levelPoint1.Value;
        double dx = pos.X - p1.X;
        double dy = pos.Y - p1.Y;

        if (dx != 0 || dy != 0)
        {
            double tiltDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            RollSlider.Value = Clamp(NormalizeAngleDeg(RollSlider.Value + tiltDeg), RollSlider.Minimum, RollSlider.Maximum);
        }

        _isSettingLevel = false;
        _levelPoint1 = null;
        StatusText.Text = "Level set.";
        OnParametersChanged();
    }

    private void PreviewImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
            return;

        var pos = e.GetPosition(PreviewImage);
        double dx = pos.X - _dragStart.X;
        double dy = pos.Y - _dragStart.Y;

        double renderWidth = PreviewImage.ActualWidth > 0 ? PreviewImage.ActualWidth : 1;
        double degPerPixel = FovSlider.Value / renderWidth;

        // "grab and drag the image" feel: dragging right reveals what was to the left
        // (yaw decreases), dragging down reveals what was above (pitch increases).
        // Yaw wraps continuously so you can drag all the way around 360°; pitch is
        // clamped since past the horizon/nadir extremes there's nothing valid to show.
        _yawDeg = NormalizeAngleDeg(_dragStartYaw - dx * degPerPixel);
        _pitchDeg = Clamp(_dragStartPitch + dy * degPerPixel, MinPitchDeg, MaxPitchDeg);

        // Rebuilding the map is cheap (a few ms, parallelized across rows), so render
        // on every move event directly instead of debouncing - debouncing here just
        // made dragging look frozen until the mouse stopped moving.
        OnParametersChanged();
    }

    private void PreviewImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        PreviewImage.ReleaseMouseCapture();
    }

    private void PreviewImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_currentFrame == null && _previewFrame == null)
            return;
        double newFov = Clamp(FovSlider.Value - Math.Sign(e.Delta) * 5, FovSlider.Minimum, FovSlider.Maximum);
        FovSlider.Value = newFov;
        e.Handled = true;
    }

    private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));

    /// <summary>Wraps an angle into (-180, 180] so continuous dragging in one
    /// direction keeps rotating instead of hitting a hard stop at ±180.</summary>
    private static double NormalizeAngleDeg(double deg)
    {
        deg %= 360.0;
        if (deg <= -180.0) deg += 360.0;
        if (deg > 180.0) deg -= 360.0;
        return deg;
    }

    // --------------------------------------------------------- playback --

    private void SetUpPlayback(bool isVideo, VideoCapture? capture)
    {
        _playbackCapture = capture;

        if (!isVideo || capture == null)
        {
            PlaybackPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _playbackFps = capture.Fps > 0 ? capture.Fps : 30.0;
        _playbackTotalFrames = capture.FrameCount;

        // Ticks poll frequently; PlaybackTimer_Tick paces itself against a stopwatch
        // rather than assuming each tick completes within 1/fps - real footage takes
        // longer to remap+render per frame than that interval allows, so a naive
        // fixed-interval "read one frame per tick" timer falls behind and plays back
        // slower than real time.
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        _playbackTimer.Tick += PlaybackTimer_Tick;

        _suppressSeek = true;
        PositionSlider.Minimum = 0;
        PositionSlider.Maximum = Math.Max(_playbackTotalFrames - 1, 0);
        PositionSlider.Value = 0;
        _suppressSeek = false;

        _isPlaying = false;
        PlayPauseButton.Content = "Play";
        PlaybackPanel.Visibility = Visibility.Visible;
        UpdateTimeText(0);
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playbackCapture == null)
            return;
        if (_isPlaying)
            PausePlayback();
        else
            StartPlayback();
    }

    private void StartPlayback()
    {
        if (_playbackCapture == null || _playbackTimer == null)
            return;
        EnsureCachedMap();
        _isPlaying = true;
        PlayPauseButton.Content = "Pause";
        _playbackStartFrame = (int)_playbackCapture.Get(VideoCaptureProperties.PosFrames);
        _playbackStopwatch.Restart();
        _playbackTimer.Start();
    }

    private void PausePlayback()
    {
        _isPlaying = false;
        PlayPauseButton.Content = "Play";
        _playbackTimer?.Stop();
        _playbackStopwatch.Stop();
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_playbackCapture == null)
            return;

        // How many frames SHOULD have played by now, based on wall-clock time, not
        // how many ticks have fired - this is what keeps speed correct even when
        // per-frame remap+render is slower than the video's own frame interval.
        int targetFrame = _playbackStartFrame + (int)(_playbackStopwatch.Elapsed.TotalSeconds * _playbackFps);
        int currentFrame = (int)_playbackCapture.Get(VideoCaptureProperties.PosFrames);
        if (targetFrame <= currentFrame)
            return; // not time for the next frame yet

        // If we've fallen behind, jump straight to the target frame instead of
        // decoding every intermediate one, so playback catches back up to real time
        // rather than staying permanently behind.
        if (targetFrame > currentFrame + 1)
            _playbackCapture.Set(VideoCaptureProperties.PosFrames, targetFrame);

        var frame = new Mat();
        if (!_playbackCapture.Read(frame) || frame.Empty())
        {
            frame.Dispose();
            PausePlayback();
            _playbackCapture.Set(VideoCaptureProperties.PosFrames, 0);
            _suppressSeek = true;
            PositionSlider.Value = 0;
            _suppressSeek = false;
            UpdateTimeText(0);
            return;
        }

        _currentFrame?.Dispose();
        _currentFrame = frame;
        RenderPreviewFrame(_currentFrame);

        int posFrames = (int)_playbackCapture.Get(VideoCaptureProperties.PosFrames);
        _suppressSeek = true;
        PositionSlider.Value = Math.Min(posFrames, PositionSlider.Maximum);
        _suppressSeek = false;
        UpdateTimeText(posFrames);
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSeek || _playbackCapture == null)
            return;
        PausePlayback();
        SeekToFrame((int)e.NewValue);
    }

    private void SeekToFrame(int frameIndex)
    {
        if (_playbackCapture == null)
            return;

        _playbackCapture.Set(VideoCaptureProperties.PosFrames, frameIndex);
        var frame = new Mat();
        if (_playbackCapture.Read(frame) && !frame.Empty())
        {
            _currentFrame?.Dispose();
            _currentFrame = frame;
            RenderPreviewFrame(_currentFrame);
            UpdateTimeText(frameIndex);
        }
        else
        {
            frame.Dispose();
        }
    }

    private void UpdateTimeText(int posFrames)
    {
        double cur = posFrames / _playbackFps;
        double total = _playbackTotalFrames / _playbackFps;
        TimeText.Text = $"{FormatTime(cur)} / {FormatTime(total)}";
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(seconds, 0));
        return ts.Hours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    private void ReleaseVideoResources()
    {
        _playbackTimer?.Stop();
        _playbackTimer = null;
        _playbackCapture?.Dispose();
        _playbackCapture = null;
        _isPlaying = false;
    }

    // ------------------------------------------------------------ export --

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePath == null)
            return;

        var calib = CurrentCalibration();
        var map = CurrentMap(calib);
        var (mapX, mapY) = map.ToMats();
        var transform = CurrentTransform();

        if (_isVideo)
        {
            var dialog = new SaveFileDialog { Title = "Export flattened video", Filter = "MP4 video|*.mp4" };
            if (dialog.ShowDialog() != true)
                return;

            PausePlayback();
            ExportButton.IsEnabled = false;
            OpenButton.IsEnabled = false;
            ExportProgress.Visibility = Visibility.Visible;
            ExportProgress.Value = 0;
            StatusText.Text = "Exporting video...";

            string inPath = _sourcePath;
            string outPath = dialog.FileName;
            string tempVideoOnlyPath = Path.Combine(Path.GetTempPath(), $"ff_{Guid.NewGuid():N}.mp4");

            try
            {
                bool includedAudio = await Task.Run(() =>
                {
                    VideoProcessor.ProcessVideo(inPath, tempVideoOnlyPath, mapX, mapY, transform, (done, total) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (total > 0)
                                ExportProgress.Value = 100.0 * done / total;
                            StatusText.Text = $"Exporting video... frame {done}/{(total > 0 ? total.ToString() : "?")}";
                        });
                    });

                    Dispatcher.Invoke(() => StatusText.Text = "Merging audio...");
                    return AudioMuxer.MuxAudio(tempVideoOnlyPath, inPath, outPath);
                });
                StatusText.Text = includedAudio
                    ? "Video export complete."
                    : $"Video export complete - no audio: {AudioMuxer.LastSkipReason}";
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
                using var flat = FlattenPipeline.Run(fullRes, mapX, mapY, transform);
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

    private TransformOptions CurrentTransform() => new()
    {
        SourceFlipHorizontal = SourceFlipHCheck.IsChecked == true,
        SourceFlipVertical = SourceFlipVCheck.IsChecked == true,
    };

    private DewarpMap CurrentMap(CircleCalibration calib)
    {
        var p = new PerspectiveParams
        {
            YawDeg = _yawDeg,
            PitchDeg = _pitchDeg,
            RollDeg = RollSlider.Value,
            FovDeg = FovSlider.Value,
            OutWidth = (int)PerspWidthSlider.Value,
            OutHeight = (int)PerspHeightSlider.Value,
        };
        return DewarpMath.BuildPerspectiveMap(calib, p);
    }

    /// <summary>Rebuilds the cached remap from current calibration/view controls.
    /// Called whenever a parameter that affects the map changes; playback and dragging
    /// reuse the cached map via RenderPreviewFrame rather than rebuilding synchronously
    /// on every mouse-move (rebuilding a per-pixel trig map at that rate is too slow).</summary>
    private void RebuildMap()
    {
        _cachedMapX?.Dispose();
        _cachedMapY?.Dispose();
        var calib = CurrentCalibration();
        var map = CurrentMap(calib);
        (_cachedMapX, _cachedMapY) = map.ToMats();
    }

    private void EnsureCachedMap()
    {
        if (_cachedMapX == null || _cachedMapY == null)
            RebuildMap();
    }

    /// <summary>Called (via debounce) whenever any calibration/view/flip control changes.</summary>
    private void OnParametersChanged()
    {
        if (_previewFrame == null && _currentFrame == null)
            return;

        RebuildMap();

        // While playing, the next timer tick already picks up the rebuilt map;
        // re-rendering the stale current frame here would just cause a flicker.
        if (!_isPlaying)
            RenderPreviewFrame(_currentFrame ?? _previewFrame);
    }

    private void RenderPreviewFrame(Mat? frame)
    {
        if (frame == null)
            return;
        EnsureCachedMap();
        var transform = CurrentTransform();
        using var flat = FlattenPipeline.Run(frame, _cachedMapX!, _cachedMapY!, transform);
        PreviewImage.Source = flat.ToBitmapSource();
    }
}

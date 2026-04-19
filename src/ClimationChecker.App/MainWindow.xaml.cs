using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClimationChecker.Fli;
using Microsoft.Win32;

namespace ClimationChecker.App;

public partial class MainWindow : Window
{
    private readonly Random _random = new();
    private readonly string _repositoryRoot;
    private readonly string _imageDirectory;
    private readonly string _viewerOutputDirectory;
    private readonly string _captureOutputDirectory;
    private readonly string _pythonModulePath;
    private readonly FliCameraService _cameraService = new();
    private readonly List<StretchProfile> _stretchProfiles = CreateStretchProfiles();
    private readonly PythonAnalysisWorker _pythonWorker;

    private FrameSource? _currentFrameSource;
    private ViewerAnalysisResult? _currentAnalysis;
    private float[]? _currentPreviewData;
    private bool _isSimulationMode;
    private bool _isUpdatingStretchProfile;
    private CancellationTokenSource? _stretchDebounceCts;
    private FliCameraDescriptor? _activeCamera;
    private readonly Queue<string> _simulationFrameQueue = new();

    public MainWindow()
    {
        InitializeComponent();

        _repositoryRoot = FindRepositoryRoot();
        _imageDirectory = Path.Combine(_repositoryRoot, "Image");
        _viewerOutputDirectory = Path.Combine(_repositoryRoot, "output", "viewer");
        _captureOutputDirectory = Path.Combine(_repositoryRoot, "output", "captures");
        _pythonModulePath = Path.Combine(_repositoryRoot, "src");
        _pythonWorker = new PythonAnalysisWorker(_repositoryRoot, _pythonModulePath, HandleWorkerProgress);
        SaveFolderTextBox.Text = Path.Combine(_repositoryRoot, "output", "autosave");

        StretchProfileComboBox.ItemsSource = _stretchProfiles;
        StretchProfileComboBox.DisplayMemberPath = nameof(StretchProfile.Name);
        StretchProfileComboBox.SelectedValuePath = nameof(StretchProfile.Name);

        UpdateStretchLabels();
        Loaded += OnLoadedAsync;
        Closed += OnClosed;
    }

    private void OnLoadedAsync(object? sender, RoutedEventArgs e)
    {
        if (!DetermineRuntimeMode())
        {
            Close();
            return;
        }

        SelectStretchProfile("Medium");
    }

    private async void ExposeButton_OnClick(object sender, RoutedEventArgs e)
    {
        await ExposeAsync();
    }

    private void BrowseSaveFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select a folder for auto-saved FITS files",
            InitialDirectory = Directory.Exists(SaveFolderTextBox.Text)
                ? SaveFolderTextBox.Text
                : _repositoryRoot,
        };

        if (dialog.ShowDialog(this) == true)
        {
            SaveFolderTextBox.Text = dialog.FolderName;
        }
    }

    private void StretchSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized)
        {
            return;
        }

        if (LowStretchSlider.Value >= HighStretchSlider.Value)
        {
            if (ReferenceEquals(sender, LowStretchSlider))
            {
                HighStretchSlider.Value = Math.Min(100, LowStretchSlider.Value + 0.1);
            }
            else
            {
                LowStretchSlider.Value = Math.Max(0, HighStretchSlider.Value - 0.1);
            }
        }

        UpdateStretchLabels();
        if (!_isUpdatingStretchProfile)
        {
            StretchProfileComboBox.SelectedValue = "Manual";
        }

        QueueStretchRefresh();
    }

    private void PreviewImage_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderOverlay();
    }

    private void ShowOverlayCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        RenderOverlay();
    }

    private async void StretchProfileComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || StretchProfileComboBox.SelectedItem is not StretchProfile profile)
        {
            return;
        }

        _isUpdatingStretchProfile = true;
        try
        {
            LowStretchSlider.Value = profile.LowPercentile;
            HighStretchSlider.Value = profile.HighPercentile;
            UpdateStretchLabels();
        }
        finally
        {
            _isUpdatingStretchProfile = false;
        }

        UpdateStretchControlAvailability(profile);
        QueueStretchRefresh();
    }

    private async Task ExposeAsync()
    {
        if (_isSimulationMode)
        {
            await LoadRandomFitsAsync();
            return;
        }

        await CaptureFromCameraAsync();
    }

    private async Task LoadRandomFitsAsync()
    {
        var fitsFiles = Directory.GetFiles(_imageDirectory, "*.fit", SearchOption.TopDirectoryOnly);
        if (fitsFiles.Length == 0)
        {
            SetWorkflowProgress(0, "Idle", "No FITS files were found in the Image folder.");
            StatusTextBlock.Text = "No FITS files were found in the Image folder.";
            return;
        }

        SetWorkflowProgress(20, "Expose", "Selecting a simulation frame from the Image folder.");
        _currentFrameSource = FrameSource.ForFits(GetNextSimulationFrame(fitsFiles));
        await AnalyzeCurrentFileAsync();
    }

    private string GetNextSimulationFrame(string[] fitsFiles)
    {
        if (_simulationFrameQueue.Count == 0)
        {
            foreach (var file in fitsFiles.OrderBy(_ => _random.Next()))
            {
                _simulationFrameQueue.Enqueue(file);
            }
        }

        return _simulationFrameQueue.Dequeue();
    }

    private async Task CaptureFromCameraAsync()
    {
        if (_activeCamera is null)
        {
            StatusTextBlock.Text = "No active FLI camera is available. Switching to simulation.";
            _isSimulationMode = true;
            ApplyModeUi();
            await LoadRandomFitsAsync();
            return;
        }

        ToggleUi(false);
        StatusTextBlock.Text = $"Exposing {_activeCamera.ModelName ?? _activeCamera.SerialNumber}...";
        MetricsTextBlock.Text = string.Empty;
        SetWorkflowProgress(10, "Expose", "Connecting to the FLI camera and preparing the exposure.");

        try
        {
            Directory.CreateDirectory(_captureOutputDirectory);
            var stem = Path.Combine(_captureOutputDirectory, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}");
            var exposureMilliseconds = GetExposureMilliseconds();
            var binning = GetSelectedBinning();
            var coolerEnabled = CoolerEnabledCheckBox.IsChecked == true;
            var coolerSetPoint = coolerEnabled ? GetCoolerSetPointCelsius() : (double?)null;
            await Task.Run(() =>
            {
                using var camera = _cameraService.OpenBySerial(_activeCamera.SerialNumber);
                Dispatcher.Invoke(() => SetWorkflowProgress(25, "Expose", "Camera connected. Starting acquisition."));
                camera.SetCooling(coolerEnabled, coolerSetPoint);
                camera.EnableBackgroundFlush(true);
                var capture = camera.Capture(new FliCaptureRequest(
                    exposureMilliseconds,
                    HorizontalBin: binning,
                    VerticalBin: binning));
                Dispatcher.Invoke(() => SetWorkflowProgress(70, "Expose", "Exposure complete. Writing RAW frame and metadata."));
                FliRawWriter.Save(capture, stem);
            });

            _currentFrameSource = FrameSource.ForRaw($"{stem}.raw", $"{stem}.json");
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"Live capture failed: {exception.Message}. Falling back to simulation.";
            _isSimulationMode = true;
            ApplyModeUi();
            await LoadRandomFitsAsync();
            return;
        }
        finally
        {
            ToggleUi(true);
        }

        await AnalyzeCurrentFileAsync();
    }

    private async Task AnalyzeCurrentFileAsync()
    {
        if (_currentFrameSource is null)
        {
            return;
        }

        ToggleUi(false);
        CurrentFileTextBlock.Text = _currentFrameSource.DisplayPath;
        StatusTextBlock.Text = "Running Python analysis...";
        MetricsTextBlock.Text = string.Empty;
        SetWorkflowProgress(35, "Analyze", "Preparing the current frame for Python processing.");
        var stretchProfile = StretchProfileComboBox.SelectedItem as StretchProfile ?? _stretchProfiles[0];
        var requestedLowPercentile = LowStretchSlider.Value;
        var requestedHighPercentile = HighStretchSlider.Value;

        try
        {
            SetWorkflowProgress(42, "Analyze", "Starting the Python analysis worker.");
            var analysis = await RunPythonAnalysisAsync(_currentFrameSource);
            _currentAnalysis = analysis;
            SetWorkflowProgress(90, "Render", "Loading the preview crop into memory.");
            await Task.Run(() => LoadPreviewData(analysis));
            UpdateMetricsText(analysis);
            SetWorkflowProgress(94, "Render", "Rendering the stretched preview.");
            await Task.Run(() => RenderPreviewBitmap(stretchProfile, requestedLowPercentile, requestedHighPercentile));
            SetWorkflowProgress(97, "Render", "Drawing the detected inner and outer rings.");
            RenderOverlay();
            var saveDetail = await TryAutoSaveFitsAsync();
            SetWorkflowProgress(100, "Ready", saveDetail ?? "Preview and analysis are up to date.");
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = exception.Message;
            MetricsTextBlock.Text = string.Empty;
            OverlayCanvas.Children.Clear();
            SetWorkflowProgress(100, "Error", exception.Message);
        }
        finally
        {
            ToggleUi(true);
        }
    }

    private Task<ViewerAnalysisResult> RunPythonAnalysisAsync(FrameSource frameSource)
    {
        return _pythonWorker.AnalyzeAsync(frameSource, _viewerOutputDirectory);
    }

    private async Task<string?> TryAutoSaveFitsAsync()
    {
        if (AutoSaveFitsCheckBox.IsChecked != true || _currentFrameSource is null)
        {
            return null;
        }

        var destinationFolder = SaveFolderTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            return "Auto Save is enabled, but no destination folder is selected.";
        }

        try
        {
            Directory.CreateDirectory(destinationFolder);
            var savedPath = await Task.Run(() => SaveCurrentFrameAsFits(_currentFrameSource, destinationFolder));
            return $"Preview and analysis are up to date. Saved FITS to {savedPath}.";
        }
        catch (Exception exception)
        {
            return $"Preview and analysis are up to date. Auto Save failed: {exception.Message}";
        }
    }

    private static string SaveCurrentFrameAsFits(FrameSource frameSource, string destinationFolder)
    {
        var destinationPath = BuildAutosaveFitsPath(frameSource, destinationFolder);

        if (frameSource.Kind == FrameSourceKind.Fits)
        {
            File.Copy(frameSource.FramePath, destinationPath, overwrite: true);
            return destinationPath;
        }

        if (frameSource.MetadataPath is null)
        {
            throw new InvalidOperationException("RAW frame metadata is missing.");
        }

        var metadata = JsonSerializer.Deserialize<FliRawFrameMetadata>(File.ReadAllText(frameSource.MetadataPath))
            ?? throw new InvalidOperationException("RAW frame metadata could not be read.");

        var rawBytes = File.ReadAllBytes(frameSource.FramePath);
        var expectedBytes = metadata.Width * metadata.Height * sizeof(ushort);
        if (rawBytes.Length != expectedBytes)
        {
            throw new InvalidOperationException($"RAW frame size mismatch. Expected {expectedBytes} bytes but found {rawBytes.Length}.");
        }

        var pixels = new ushort[metadata.Width * metadata.Height];
        Buffer.BlockCopy(rawBytes, 0, pixels, 0, rawBytes.Length);
        FitsFileWriter.WriteGray16(destinationPath, pixels, metadata);
        return destinationPath;
    }

    private static string BuildAutosaveFitsPath(FrameSource frameSource, string destinationFolder)
    {
        var dayStamp = DateTime.Now.ToString("ddMMyyyy", CultureInfo.InvariantCulture);
        var prefix = $"{dayStamp}_";
        var nextNumber = Directory
            .GetFiles(destinationFolder, $"{prefix}*.fit", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => name?[prefix.Length..])
            .Where(suffix => int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .Select(suffix => int.Parse(suffix!, NumberStyles.Integer, CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0)
            .Max() + 1;

        return Path.Combine(destinationFolder, $"{dayStamp}_{nextNumber:0000}.fit");
    }

    private void LoadPreviewData(ViewerAnalysisResult analysis)
    {
        var bytes = File.ReadAllBytes(analysis.CropDataFile);
        var expectedBytes = analysis.CropWidth * analysis.CropHeight * sizeof(float);
        if (bytes.Length != expectedBytes)
        {
            throw new InvalidOperationException($"Preview crop size mismatch. Expected {expectedBytes} bytes but found {bytes.Length}.");
        }

        var data = new float[analysis.CropWidth * analysis.CropHeight];
        Buffer.BlockCopy(bytes, 0, data, 0, bytes.Length);
        _currentPreviewData = data;
    }

    private void RenderPreviewBitmap(StretchProfile stretchProfile, double requestedLowPercentile, double requestedHighPercentile)
    {
        if (_currentAnalysis is null || _currentPreviewData is null)
        {
            return;
        }

        var rendered = StretchPreview(
            _currentPreviewData,
            _currentAnalysis.CropWidth,
            _currentAnalysis.CropHeight,
            stretchProfile,
            requestedLowPercentile,
            requestedHighPercentile,
            out var effectiveLow,
            out var effectiveHigh);

        _currentAnalysis.StretchProfile = stretchProfile.Name;
        _currentAnalysis.StretchLowPercentile = effectiveLow;
        _currentAnalysis.StretchHighPercentile = effectiveHigh;

        var stride = _currentAnalysis.CropWidth;
        var bitmap = BitmapSource.Create(
            _currentAnalysis.CropWidth,
            _currentAnalysis.CropHeight,
            96,
            96,
            PixelFormats.Gray8,
            null,
            rendered,
            stride);
        bitmap.Freeze();
        Dispatcher.Invoke(() => PreviewImage.Source = bitmap);
    }

    private void UpdateMetricsText(ViewerAnalysisResult analysis)
    {
        MetricsTextBlock.Inlines.Clear();

        if (analysis.RingMetrics is null)
        {
            AnalysisScoreTextBlock.Text = "Score --.--%";
            StatusTextBlock.Text = analysis.Error ?? "No ring metrics available for this frame.";
            AppendMetricLine($"Profile: {analysis.StretchProfile}");
            AppendMetricLine($"Stretch: {analysis.StretchLowPercentile:0.###} - {analysis.StretchHighPercentile:0.###}");
            AppendMetricLine($"Backend: {analysis.Backend}");
            AppendMetricLine($"Preview crop: {analysis.CropWidth} x {analysis.CropHeight}");
            return;
        }

        var deltaX = analysis.RingMetrics.OuterCenterX - analysis.RingMetrics.InnerCenterX;
        var deltaY = analysis.RingMetrics.OuterCenterY - analysis.RingMetrics.InnerCenterY;
        var outerCircularityPercent = analysis.RingMetrics.OuterCircularity * 100.0;
        var innerCircularityPercent = analysis.RingMetrics.InnerCircularity * 100.0;
        var thicknessUniformityPercent = analysis.RingMetrics.ThicknessUniformity * 100.0;
        var brightnessBalancePercent = analysis.RingMetrics.BrightnessBalance * 100.0;
        var detectionConfidencePercent = analysis.RingMetrics.DetectionConfidence * 100.0;
        var collimationScore = ComputeCollimationScore(analysis.RingMetrics);

        AnalysisScoreTextBlock.Text = $"Score {collimationScore:0.00}%";
        StatusTextBlock.Text = BuildDetectionStatus(analysis.RingMetrics);
        AppendMetricLine($"Outer center : ({analysis.RingMetrics.OuterCenterX:0.00}, {analysis.RingMetrics.OuterCenterY:0.00})");
        AppendMetricLine($"Inner center : ({analysis.RingMetrics.InnerCenterX:0.00}, {analysis.RingMetrics.InnerCenterY:0.00})");
        AppendMetricLine($"Delta center : dX {deltaX:0.00} px, dY {deltaY:0.00} px", Brushes.Orange);
        AppendMetricLine($"Outer radius : {analysis.RingMetrics.OuterRadiusPx:0.00} px");
        AppendMetricLine($"Inner radius : {analysis.RingMetrics.InnerRadiusPx:0.00} px");
        AppendMetricLine($"Offset       : {analysis.RingMetrics.CenterOffsetPx:0.00} px");
        AppendMetricLine($"Norm offset  : {analysis.RingMetrics.NormalizedOffset:0.0000}");
        AppendMetricWithHighlightedValue(
            "Outer circle : ",
            $"{outerCircularityPercent:0.0}%",
            $" ({analysis.RingMetrics.OuterCircularity:0.0000})");
        AppendMetricWithHighlightedValue(
            "Inner circle : ",
            $"{innerCircularityPercent:0.0}%",
            $" ({analysis.RingMetrics.InnerCircularity:0.0000})");
        AppendMetricWithHighlightedValue(
            "Thickness    : ",
            $"{thicknessUniformityPercent:0.0}%",
            " uniform");
        AppendMetricWithHighlightedValue(
            "Brightness   : ",
            $"{brightnessBalancePercent:0.0}%",
            $" balanced ({analysis.RingMetrics.BrightestQuadrant} brightest)");
        AppendMetricLine($"Outer ellipse: {analysis.RingMetrics.OuterEllipseRatio:0.000}x");
        AppendMetricLine($"Inner ellipse: {analysis.RingMetrics.InnerEllipseRatio:0.000}x");
        AppendMetricWithHighlightedValue(
            "Confidence   : ",
            $"{detectionConfidencePercent:0.0}%",
            $" {analysis.RingMetrics.ConfidenceLabel}");
        AppendMetricLine($"Profile      : {analysis.StretchProfile}");
        AppendMetricLine($"Stretch      : {analysis.StretchLowPercentile:0.###} - {analysis.StretchHighPercentile:0.###}");
        AppendMetricLine($"Backend      : {analysis.Backend}");
    }

    private static double ComputeCollimationScore(ViewerRingMetrics metrics)
    {
        var centerAlignmentScore = Math.Exp(-6.0 * Math.Max(metrics.NormalizedOffset, 0.0));
        var outerCircleScore = Clamp01(metrics.OuterCircularity);
        var innerCircleScore = Clamp01(metrics.InnerCircularity);
        var thicknessScore = Clamp01(metrics.ThicknessUniformity);
        var brightnessScore = Clamp01(metrics.BrightnessBalance);
        var outerEllipseScore = Clamp01(1.0 - ((metrics.OuterEllipseRatio - 1.0) / 0.20));
        var innerEllipseScore = Clamp01(1.0 - ((metrics.InnerEllipseRatio - 1.0) / 0.20));

        var weightedScore =
            (centerAlignmentScore * 0.45) +
            (outerCircleScore * 0.15) +
            (innerCircleScore * 0.12) +
            (thicknessScore * 0.12) +
            (brightnessScore * 0.08) +
            (outerEllipseScore * 0.05) +
            (innerEllipseScore * 0.03);

        return Math.Round(weightedScore * 100.0, 2);
    }

    private static double Clamp01(double value)
    {
        return Math.Clamp(value, 0.0, 1.0);
    }

    private static string BuildDetectionStatus(ViewerRingMetrics metrics)
    {
        if (metrics.DetectionConfidence < 0.65)
        {
            return "Donut rings detected with low confidence. Verify the overlay before adjusting collimation.";
        }

        if (metrics.DetectionConfidence < 0.85)
        {
            return "Donut rings detected with medium confidence. Use caution on difficult frames.";
        }

        return "Donut rings detected successfully.";
    }

    private void AppendMetricLine(string text, Brush? foreground = null)
    {
        MetricsTextBlock.Inlines.Add(new Run(text)
        {
            Foreground = foreground ?? Brushes.White,
        });
        MetricsTextBlock.Inlines.Add(new LineBreak());
    }

    private void AppendMetricWithHighlightedValue(string prefix, string highlightedValue, string suffix)
    {
        MetricsTextBlock.Inlines.Add(new Run(prefix)
        {
            Foreground = Brushes.White,
        });
        MetricsTextBlock.Inlines.Add(new Run(highlightedValue)
        {
            Foreground = Brushes.Orange,
        });
        MetricsTextBlock.Inlines.Add(new Run(suffix)
        {
            Foreground = Brushes.White,
        });
        MetricsTextBlock.Inlines.Add(new LineBreak());
    }

    private void RenderOverlay()
    {
        OverlayCanvas.Children.Clear();
        if (ShowOverlayCheckBox.IsChecked != true || _currentAnalysis?.RingMetrics is null)
        {
            return;
        }

        var viewportWidth = PreviewImage.ActualWidth;
        var viewportHeight = PreviewImage.ActualHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return;
        }

        OverlayCanvas.Width = viewportWidth;
        OverlayCanvas.Height = viewportHeight;

        var imageWidth = _currentAnalysis.CropWidth;
        var imageHeight = _currentAnalysis.CropHeight;
        var scale = Math.Min(viewportWidth / imageWidth, viewportHeight / imageHeight);
        var displayedWidth = imageWidth * scale;
        var displayedHeight = imageHeight * scale;
        var offsetX = (viewportWidth - displayedWidth) / 2.0;
        var offsetY = (viewportHeight - displayedHeight) / 2.0;

        var metrics = _currentAnalysis.RingMetrics;
        var outerCenterX = (metrics.OuterCenterX - _currentAnalysis.CropOriginX) * scale + offsetX;
        var outerCenterY = (_currentAnalysis.CropHeight - (metrics.OuterCenterY - _currentAnalysis.CropOriginY)) * scale + offsetY;
        var innerCenterX = (metrics.InnerCenterX - _currentAnalysis.CropOriginX) * scale + offsetX;
        var innerCenterY = (_currentAnalysis.CropHeight - (metrics.InnerCenterY - _currentAnalysis.CropOriginY)) * scale + offsetY;

        OverlayCanvas.Children.Add(CreateCircle(outerCenterX, outerCenterY, metrics.OuterRadiusPx * scale, Brushes.Cyan));
        OverlayCanvas.Children.Add(CreateCircle(innerCenterX, innerCenterY, metrics.InnerRadiusPx * scale, Brushes.Magenta));
        OverlayCanvas.Children.Add(CreateCenterMarker(outerCenterX, outerCenterY, Brushes.Cyan));
        OverlayCanvas.Children.Add(CreateCenterMarker(innerCenterX, innerCenterY, Brushes.Magenta));
    }

    private static System.Windows.Shapes.Ellipse CreateCircle(double centerX, double centerY, double radius, Brush stroke)
    {
        var circle = new System.Windows.Shapes.Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Stroke = stroke,
            StrokeThickness = 2.0,
        };

        Canvas.SetLeft(circle, centerX - radius);
        Canvas.SetTop(circle, centerY - radius);
        return circle;
    }

    private static System.Windows.Shapes.Ellipse CreateCenterMarker(double centerX, double centerY, Brush fill)
    {
        var marker = new System.Windows.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = fill,
            Stroke = Brushes.Black,
            StrokeThickness = 1,
        };

        Canvas.SetLeft(marker, centerX - 5);
        Canvas.SetTop(marker, centerY - 5);
        return marker;
    }

    private void ToggleUi(bool enabled)
    {
        ExposeButton.IsEnabled = enabled;
        BrowseSaveFolderButton.IsEnabled = enabled;
        SaveFolderTextBox.IsEnabled = enabled;
        AutoSaveFitsCheckBox.IsEnabled = enabled;
        ExposureSecondsTextBox.IsEnabled = enabled;
        BinningComboBox.IsEnabled = enabled;
        CoolerEnabledCheckBox.IsEnabled = enabled;
        CoolerSetPointTextBox.IsEnabled = enabled && CoolerEnabledCheckBox.IsChecked == true;
    }

    private void UpdateStretchLabels()
    {
        LowStretchValueTextBlock.Text = LowStretchSlider.Value.ToString("0.###", CultureInfo.InvariantCulture);
        HighStretchValueTextBlock.Text = HighStretchSlider.Value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void QueueStretchRefresh()
    {
        if (!IsLoaded || _currentFrameSource is null)
        {
            return;
        }

        _stretchDebounceCts?.Cancel();
        _stretchDebounceCts?.Dispose();
        _stretchDebounceCts = new CancellationTokenSource();
        var token = _stretchDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(180, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    SetWorkflowProgress(15, "Adjusting Stretch", "Updating the preview stretch in real time.");
                    var profile = StretchProfileComboBox.SelectedItem as StretchProfile ?? _stretchProfiles[0];
                    RenderPreviewBitmap(profile, LowStretchSlider.Value, HighStretchSlider.Value);
                    RenderOverlay();
                    UpdateMetricsText(_currentAnalysis!);
                    SetWorkflowProgress(100, "Ready", "Preview and analysis are up to date.");
                });
            }
            catch (TaskCanceledException)
            {
            }
        }, token);
    }

    private bool DetermineRuntimeMode()
    {
        try
        {
            _activeCamera = _cameraService.ListCameras().FirstOrDefault();
            if (_activeCamera is null)
            {
                return ConfirmSimulationMode("Could not connect to the Finger Lakes CCD camera. Run in Simulator mode instead?");
            }

            _isSimulationMode = false;
        }
        catch (Exception exception)
        {
            _activeCamera = null;
            return ConfirmSimulationMode($"Could not connect to the Finger Lakes CCD camera.\n\n{exception.Message}\n\nRun in Simulator mode instead?");
        }

        ApplyModeUi();
        return true;
    }

    private bool ConfirmSimulationMode(string message)
    {
        var choice = MessageBox.Show(
            this,
            message,
            "CCD Connection",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (choice != MessageBoxResult.Yes)
        {
            return false;
        }

        _isSimulationMode = true;
        ApplyModeUi();
        return true;
    }

    private void ApplyModeUi()
    {
        if (_isSimulationMode)
        {
            Title = "Climation Checker Simulator";
            ViewerTitleTextBlock.Text = "Simulation Viewer";
            SetWorkflowProgress(0, "Ready", "Simulation mode is active. Click Expose to load a sample frame.");
        }
        else
        {
            Title = "Climation Checker";
            ViewerTitleTextBlock.Text = "Exposure Viewer";
            SetWorkflowProgress(0, "Ready", "Live camera mode is active. Click Expose to capture a frame.");
        }
    }

    private void SelectStretchProfile(string profileName)
    {
        StretchProfileComboBox.SelectedItem = _stretchProfiles.First(profile => profile.Name == profileName);
    }

    private static List<StretchProfile> CreateStretchProfiles()
    {
        return
        [
            new StretchProfile("Low", 10.0, 99.9, true),
            new StretchProfile("Medium", 5.0, 99.8, true),
            new StretchProfile("High", 1.0, 99.7, true),
            new StretchProfile("Moon", 0.3, 99.5, false),
            new StretchProfile("Planet", 0.1, 99.95, false),
            new StretchProfile("Max Val", 0.0, 100.0, false),
            new StretchProfile("Range", 0.0, 100.0, false),
            new StretchProfile("Floating", 2.0, 99.8, true),
            new StretchProfile("Manual", 1.0, 99.8, true),
        ];
    }

    private void UpdateStretchControlAvailability(StretchProfile profile)
    {
        LowStretchSlider.IsEnabled = profile.Adjustable;
        HighStretchSlider.IsEnabled = profile.Adjustable;
        LowStretchValueTextBlock.Opacity = profile.Adjustable ? 1.0 : 0.55;
        HighStretchValueTextBlock.Opacity = profile.Adjustable ? 1.0 : 0.55;
    }

    private void SetWorkflowProgress(double percentComplete, string headline, string detail)
    {
        var clamped = Math.Clamp(percentComplete, 0, 100);
        WorkflowProgressBar.Value = clamped;
        WorkflowProgressTextBlock.Text = $"{clamped:0}%";
        WorkflowStatusTextBlock.Text = headline;
        WorkflowDetailTextBlock.Text = detail;
    }

    private void CoolerEnabledCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        CoolerSetPointTextBox.IsEnabled = CoolerEnabledCheckBox.IsChecked == true;
    }

    private double GetExposureMilliseconds()
    {
        if (!double.TryParse(ExposureSecondsTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var exposureSeconds))
        {
            throw new InvalidOperationException("Exposure time must be a valid number of seconds.");
        }

        if (exposureSeconds <= 0)
        {
            throw new InvalidOperationException("Exposure time must be greater than 0 seconds.");
        }

        return exposureSeconds * 1000.0;
    }

    private int GetSelectedBinning()
    {
        if (BinningComboBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var binning) &&
            binning >= 1)
        {
            return binning;
        }

        return 1;
    }

    private double GetCoolerSetPointCelsius()
    {
        if (!double.TryParse(CoolerSetPointTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var setPoint))
        {
            throw new InvalidOperationException("Cooler set point must be a valid temperature in Celsius.");
        }

        return setPoint;
    }

    private void HandleWorkerProgress(double percent, string headline, string detail)
    {
        Dispatcher.Invoke(() => SetWorkflowProgress(percent, headline, detail));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _stretchDebounceCts?.Cancel();
        _stretchDebounceCts?.Dispose();
        _pythonWorker.Dispose();
    }

    private string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            if (Directory.Exists(Path.Combine(current, "Image")) &&
                Directory.Exists(Path.Combine(current, "src", "climation_checker")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the application directory.");
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static byte[] StretchPreview(
        float[] sourceData,
        int width,
        int height,
        StretchProfile stretchProfile,
        double requestedLowPercentile,
        double requestedHighPercentile,
        out double effectiveLowPercentile,
        out double effectiveHighPercentile)
    {
        var finite = sourceData.Where(value => !float.IsNaN(value) && !float.IsInfinity(value)).Select(value => (double)value).ToArray();
        if (finite.Length == 0)
        {
            effectiveLowPercentile = 0;
            effectiveHighPercentile = 0;
            return new byte[width * height];
        }

        Array.Sort(finite);
        var minimum = finite[0];
        var maximum = finite[^1];
        var median = PercentileSorted(finite, 50.0);
        var mean = finite.Average();
        var variance = finite.Select(value => (value - mean) * (value - mean)).Average();
        var std = Math.Sqrt(variance);

        double lowValue;
        double highValue;
        double gamma;
        switch (stretchProfile.Name)
        {
            case "Low":
                lowValue = PercentileSorted(finite, requestedLowPercentile);
                highValue = PercentileSorted(finite, Math.Max(requestedHighPercentile, 99.92));
                gamma = 0.9;
                break;
            case "Medium":
                lowValue = PercentileSorted(finite, requestedLowPercentile);
                highValue = PercentileSorted(finite, requestedHighPercentile);
                gamma = 0.7;
                break;
            case "High":
                lowValue = PercentileSorted(finite, requestedLowPercentile);
                highValue = PercentileSorted(finite, Math.Min(requestedHighPercentile, 99.7));
                gamma = 0.55;
                break;
            case "Moon":
                lowValue = PercentileSorted(finite, 0.3);
                highValue = PercentileSorted(finite, 99.25);
                gamma = 0.45;
                break;
            case "Planet":
                lowValue = PercentileSorted(finite, 0.05);
                highValue = PercentileSorted(finite, 99.98);
                gamma = 1.05;
                break;
            case "Max Val":
                lowValue = 0.0;
                highValue = maximum;
                gamma = 1.0;
                break;
            case "Range":
                lowValue = minimum;
                highValue = maximum;
                gamma = 1.0;
                break;
            case "Floating":
                lowValue = Math.Max(minimum, median - 0.45 * std);
                highValue = PercentileSorted(finite, requestedHighPercentile);
                gamma = 0.72;
                break;
            default:
                lowValue = PercentileSorted(finite, requestedLowPercentile);
                highValue = PercentileSorted(finite, requestedHighPercentile);
                gamma = 0.7;
                break;
        }

        effectiveLowPercentile = PercentileRankSorted(finite, lowValue);
        effectiveHighPercentile = PercentileRankSorted(finite, highValue);

        var denominator = Math.Max(highValue - lowValue, 1e-6);
        var output = new byte[width * height];
        for (var index = 0; index < sourceData.Length; index++)
        {
            var value = Math.Clamp(sourceData[index], (float)lowValue, (float)highValue);
            var normalized = (value - lowValue) / denominator;
            var gammaCorrected = Math.Pow(Math.Clamp(normalized, 0.0, 1.0), gamma);
            output[index] = (byte)Math.Round(gammaCorrected * 255.0);
        }

        return output;
    }

    private static double PercentileSorted(double[] sortedData, double percentile)
    {
        if (sortedData.Length == 1)
        {
            return sortedData[0];
        }

        var clamped = Math.Clamp(percentile, 0.0, 100.0);
        var position = (clamped / 100.0) * (sortedData.Length - 1);
        var lowerIndex = (int)Math.Floor(position);
        var upperIndex = (int)Math.Ceiling(position);
        if (lowerIndex == upperIndex)
        {
            return sortedData[lowerIndex];
        }

        var fraction = position - lowerIndex;
        return sortedData[lowerIndex] + ((sortedData[upperIndex] - sortedData[lowerIndex]) * fraction);
    }

    private static double PercentileRankSorted(double[] sortedData, double value)
    {
        var upperIndex = Array.BinarySearch(sortedData, value);
        if (upperIndex < 0)
        {
            upperIndex = ~upperIndex;
        }

        return 100.0 * upperIndex / sortedData.Length;
    }
}

public sealed class ViewerAnalysisResult
{
    [JsonPropertyName("source_file")]
    public string SourceFile { get; set; } = string.Empty;

    [JsonPropertyName("preview_file")]
    public string PreviewFile { get; set; } = string.Empty;

    [JsonPropertyName("crop_data_file")]
    public string CropDataFile { get; set; } = string.Empty;

    [JsonPropertyName("crop_origin_x")]
    public int CropOriginX { get; set; }

    [JsonPropertyName("crop_origin_y")]
    public int CropOriginY { get; set; }

    [JsonPropertyName("crop_width")]
    public int CropWidth { get; set; }

    [JsonPropertyName("crop_height")]
    public int CropHeight { get; set; }

    [JsonPropertyName("backend")]
    public string Backend { get; set; } = string.Empty;

    [JsonPropertyName("stretch_low_percentile")]
    public double StretchLowPercentile { get; set; }

    [JsonPropertyName("stretch_high_percentile")]
    public double StretchHighPercentile { get; set; }

    [JsonPropertyName("stretch_profile")]
    public string StretchProfile { get; set; } = string.Empty;

    [JsonPropertyName("ring_metrics")]
    public ViewerRingMetrics? RingMetrics { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class StretchProfile
{
    public StretchProfile(string name, double lowPercentile, double highPercentile, bool adjustable)
    {
        Name = name;
        LowPercentile = lowPercentile;
        HighPercentile = highPercentile;
        Adjustable = adjustable;
    }

    public string Name { get; }

    public double LowPercentile { get; }

    public double HighPercentile { get; }

    public bool Adjustable { get; }
}

public sealed class ViewerRingMetrics
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("outer_center_x")]
    public double OuterCenterX { get; set; }

    [JsonPropertyName("outer_center_y")]
    public double OuterCenterY { get; set; }

    [JsonPropertyName("inner_center_x")]
    public double InnerCenterX { get; set; }

    [JsonPropertyName("inner_center_y")]
    public double InnerCenterY { get; set; }

    [JsonPropertyName("center_offset_px")]
    public double CenterOffsetPx { get; set; }

    [JsonPropertyName("normalized_offset")]
    public double NormalizedOffset { get; set; }

    [JsonPropertyName("outer_radius_px")]
    public double OuterRadiusPx { get; set; }

    [JsonPropertyName("inner_radius_px")]
    public double InnerRadiusPx { get; set; }

    [JsonPropertyName("outer_circularity")]
    public double OuterCircularity { get; set; }

    [JsonPropertyName("inner_circularity")]
    public double InnerCircularity { get; set; }

    [JsonPropertyName("outer_area_px")]
    public double OuterAreaPx { get; set; }

    [JsonPropertyName("inner_area_px")]
    public double InnerAreaPx { get; set; }

    [JsonPropertyName("threshold_fraction")]
    public double ThresholdFraction { get; set; }

    [JsonPropertyName("thickness_uniformity")]
    public double ThicknessUniformity { get; set; }

    [JsonPropertyName("brightness_balance")]
    public double BrightnessBalance { get; set; }

    [JsonPropertyName("brightest_quadrant")]
    public string BrightestQuadrant { get; set; } = string.Empty;

    [JsonPropertyName("outer_ellipse_ratio")]
    public double OuterEllipseRatio { get; set; }

    [JsonPropertyName("inner_ellipse_ratio")]
    public double InnerEllipseRatio { get; set; }

    [JsonPropertyName("detection_confidence")]
    public double DetectionConfidence { get; set; }

    [JsonPropertyName("confidence_label")]
    public string ConfidenceLabel { get; set; } = string.Empty;
}

public enum FrameSourceKind
{
    Fits,
    Raw,
}

public sealed class FrameSource
{
    private FrameSource(FrameSourceKind kind, string framePath, string? metadataPath)
    {
        Kind = kind;
        FramePath = framePath;
        MetadataPath = metadataPath;
    }

    public FrameSourceKind Kind { get; }

    public string FramePath { get; }

    public string? MetadataPath { get; }

    public string DisplayPath => MetadataPath is null ? FramePath : $"{FramePath} ({MetadataPath})";

    public static FrameSource ForFits(string filePath) => new(FrameSourceKind.Fits, filePath, null);

    public static FrameSource ForRaw(string rawPath, string metadataPath) => new(FrameSourceKind.Raw, rawPath, metadataPath);
}

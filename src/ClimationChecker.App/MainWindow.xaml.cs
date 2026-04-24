using System.Globalization;
using System.IO;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClimationChecker.Fli;
using Microsoft.Win32;

namespace ClimationChecker.App;

public partial class MainWindow : Window
{
    private const double DefaultCoolerSetPointCelsius = 10.0;
    private const double CaptureTimeoutMarginMilliseconds = 90_000.0;

    private readonly Random _random = new();
    private readonly string _repositoryRoot;
    private readonly string _appSettingsPath;
    private readonly string _imageDirectory;
    private readonly string _viewerOutputDirectory;
    private readonly string _captureOutputDirectory;
    private readonly string _pythonModulePath;
    private readonly FliCameraService _cameraService = new();
    private readonly List<StretchProfile> _stretchProfiles = CreateStretchProfiles();
    private readonly PythonAnalysisWorker _pythonWorker;
    private readonly SemaphoreSlim _cameraCommandLock = new(1, 1);
    private readonly ObservableCollection<ExposureHistoryItem> _exposureHistory = [];

    private FrameSource? _currentFrameSource;
    private ViewerAnalysisResult? _currentAnalysis;
    private float[]? _currentPreviewData;
    private float[]? _currentDisplayData;
    private int _currentDisplayOriginX;
    private int _currentDisplayOriginY;
    private int _currentDisplayWidth;
    private int _currentDisplayHeight;
    private bool _isSimulationMode;
    private bool _isUpdatingStretchProfile;
    private bool _isPanningPreview;
    private bool _isSyncingPreviewScrollBars;
    private Point _panStartPoint;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private CancellationTokenSource? _stretchDebounceCts;
    private CancellationTokenSource? _temperaturePollingCts;
    private CancellationTokenSource? _continuousExposureCts;
    private GraphWindow? _graphWindow;
    private DonutOnlyWindow? _donutOnlyWindow;
    private FliCameraDescriptor? _activeCamera;
    private readonly Queue<string> _simulationFrameQueue = new();
    private bool _isContinuousExposureRunning;
    private FliReadoutArea? _lastCaptureImageArea;
    private int _lastCaptureBinning = 1;
    private ExposureHistoryItem? _selectedHistoryItem;
    private int _historySequence;
    private string? _lastAutosaveFitsPath;
    private AppSettings _appSettings = new();

    private sealed record StretchRenderResult(
        BitmapSource PreviewBitmap,
        BitmapSource? DonutOnlyBitmap,
        string ProfileName,
        double EffectiveLowPercentile,
        double EffectiveHighPercentile);

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowTheme.ApplyDarkTitleBar(this);

        _repositoryRoot = FindRepositoryRoot();
        _appSettingsPath = Path.Combine(_repositoryRoot, "AppSettings.json");
        _appSettings = LoadAppSettings();
        _imageDirectory = Path.Combine(_repositoryRoot, "Image");
        _viewerOutputDirectory = Path.Combine(_repositoryRoot, "output", "viewer");
        _captureOutputDirectory = Path.Combine(_repositoryRoot, "output", "captures");
        _pythonModulePath = Path.Combine(_repositoryRoot, "src");
        _pythonWorker = new PythonAnalysisWorker(_repositoryRoot, _pythonModulePath, HandleWorkerProgress);
        SaveFolderTextBox.Text = Path.Combine(_repositoryRoot, "output", "autosave");
        CoolerSetPointTextBox.Text = _appSettings.CoolerSetPointCelsius.ToString("0.##", CultureInfo.InvariantCulture);

        StretchProfileComboBox.ItemsSource = _stretchProfiles;
        StretchProfileComboBox.DisplayMemberPath = nameof(StretchProfile.Name);
        StretchProfileComboBox.SelectedValuePath = nameof(StretchProfile.Name);
        ThumbnailListBox.ItemsSource = _exposureHistory;

        UpdateStretchLabels();
        UpdateExposureActionUi();
        UpdatePreviewFrameChrome();
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
        ArrangeInitialWorkspaceWindows();
    }

    private void ArrangeInitialWorkspaceWindows()
    {
        var workArea = SystemParameters.WorkArea;
        const double gap = 12.0;
        var auxiliaryWidth = Math.Clamp(workArea.Width * 0.21, 300.0, 380.0);
        var mainWidth = workArea.Width - auxiliaryWidth - gap;

        if (mainWidth < MinWidth)
        {
            mainWidth = MinWidth;
            auxiliaryWidth = Math.Max(0, workArea.Width - mainWidth - gap);
        }

        Left = workArea.Left + auxiliaryWidth + gap;
        Top = workArea.Top;
        Width = mainWidth;
        Height = Math.Max(Math.Min(workArea.Height, 1030), Math.Min(MinHeight, workArea.Height));

        ShowOnlyDonutRingCheckBox.IsChecked = true;
        ShowGraphCheckBox.IsChecked = true;
        EnsureDonutOnlyWindow();
        EnsureGraphWindow();
        PositionAuxiliaryWindows();
        Activate();
    }

    private void PositionAuxiliaryWindows()
    {
        var workArea = SystemParameters.WorkArea;
        const double gap = 10.0;
        var availableWidth = Math.Max(250.0, Math.Min(380.0, Left - workArea.Left - gap));
        var donutHeight = Math.Max(250.0, Math.Min(380.0, workArea.Height * 0.42));

        if (_donutOnlyWindow is not null)
        {
            _donutOnlyWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            _donutOnlyWindow.Left = workArea.Left;
            _donutOnlyWindow.Top = workArea.Top;
            _donutOnlyWindow.Width = availableWidth;
            _donutOnlyWindow.Height = donutHeight;
        }

        if (_graphWindow is not null)
        {
            var graphTop = workArea.Top + donutHeight + gap;
            _graphWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            _graphWindow.Left = workArea.Left;
            _graphWindow.Top = graphTop;
            _graphWindow.Width = availableWidth;
            _graphWindow.Height = Math.Max(250.0, workArea.Bottom - graphTop);
        }
    }

    private async void ExposeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isContinuousExposureRunning)
        {
            StopContinuousExposure("Continuous exposure was stopped.");
            return;
        }

        if (GetSelectedExposureMode() == ExposureMode.SingleExpose)
        {
            await ExposeAsync();
            return;
        }

        await StartContinuousExposureAsync();
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

    private void ZoomComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        ApplyPreviewZoom();
    }

    private void ExposureModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        UpdateExposureActionUi();
    }

    private void PreviewScrollViewer_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (PreviewImage.Source is null)
        {
            return;
        }

        _isPanningPreview = true;
        _panStartPoint = e.GetPosition(PreviewScrollViewer);
        _panStartHorizontalOffset = PreviewScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = PreviewScrollViewer.VerticalOffset;
        PreviewScrollViewer.Cursor = Cursors.SizeAll;
        PreviewScrollViewer.CaptureMouse();
        e.Handled = true;
    }

    private void PreviewScrollViewer_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndPreviewPan();
    }

    private void PreviewScrollViewer_OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndPreviewPan();
        }
    }

    private void PreviewScrollViewer_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanningPreview || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(PreviewScrollViewer);
        var deltaX = currentPoint.X - _panStartPoint.X;
        var deltaY = currentPoint.Y - _panStartPoint.Y;
        PreviewScrollViewer.ScrollToHorizontalOffset(_panStartHorizontalOffset - deltaX);
        PreviewScrollViewer.ScrollToVerticalOffset(_panStartVerticalOffset - deltaY);
        e.Handled = true;
    }

    private void PreviewScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        SyncPreviewScrollBarsFromViewer();
    }

    private void PreviewScrollViewer_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsInitialized || _currentAnalysis is null)
        {
            return;
        }

        ApplyPreviewZoom();
    }

    private void PreviewHorizontalScrollBar_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSyncingPreviewScrollBars)
        {
            return;
        }

        PreviewScrollViewer.ScrollToHorizontalOffset(Math.Max(0, PreviewScrollViewer.ScrollableWidth - e.NewValue));
    }

    private void PreviewVerticalScrollBar_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSyncingPreviewScrollBars)
        {
            return;
        }

        PreviewScrollViewer.ScrollToVerticalOffset(Math.Max(0, PreviewScrollViewer.ScrollableHeight - e.NewValue));
    }

    private void PreviewScrollViewer_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ZoomComboBox.Items.Count == 0)
        {
            return;
        }

        var oldScale = GetSelectedZoomScale();
        var viewportCenterX = PreviewScrollViewer.HorizontalOffset + (PreviewScrollViewer.ViewportWidth / 2.0);
        var viewportCenterY = PreviewScrollViewer.VerticalOffset + (PreviewScrollViewer.ViewportHeight / 2.0);
        var relativeCenterX = oldScale <= 0 ? 0.0 : viewportCenterX / oldScale;
        var relativeCenterY = oldScale <= 0 ? 0.0 : viewportCenterY / oldScale;

        var direction = e.Delta > 0 ? 1 : -1;
        var nextIndex = Math.Clamp(ZoomComboBox.SelectedIndex + direction, 0, ZoomComboBox.Items.Count - 1);
        if (nextIndex != ZoomComboBox.SelectedIndex)
        {
            ZoomComboBox.SelectedIndex = nextIndex;
            Dispatcher.BeginInvoke(() =>
            {
                var newScale = GetSelectedZoomScale();
                PreviewScrollViewer.ScrollToHorizontalOffset((relativeCenterX * newScale) - (PreviewScrollViewer.ViewportWidth / 2.0));
                PreviewScrollViewer.ScrollToVerticalOffset((relativeCenterY * newScale) - (PreviewScrollViewer.ViewportHeight / 2.0));
            });
        }

        e.Handled = true;
    }

    private void ShowOverlayCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        RenderOverlay();
        UpdateReferencePreviewDisplay();
    }

    private async void ShowGraphCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        if (ShowGraphCheckBox.IsChecked == true)
        {
            EnsureGraphWindow();
            UpdateGraphWindow();
            return;
        }

        CloseGraphWindow();
    }

    private void ShowOnlyDonutRingCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        if (ShowOnlyDonutRingCheckBox.IsChecked == true)
        {
            EnsureDonutOnlyWindow();
            UpdateDonutOnlyWindow();
            return;
        }

        CloseDonutOnlyWindow();
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

    private async Task ExposeAsync(CancellationToken cancellationToken = default)
    {
        if (_isSimulationMode || UseImageFolderCheckBox.IsChecked == true)
        {
            await LoadRandomFitsAsync(cancellationToken);
            return;
        }

        await CaptureFromCameraAsync(GetSelectedExposureMode() == ExposureMode.ContinuousExposeWithRoi, cancellationToken);
    }

    private async Task LoadRandomFitsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fitsFiles = Directory.GetFiles(_imageDirectory, "*.fit", SearchOption.TopDirectoryOnly);
        if (fitsFiles.Length == 0)
        {
            SetWorkflowProgress(0, "Idle", "No FITS files were found in the Image folder.");
            ShowAnalysisErrorIfEmpty("No FITS files were found in the Image folder.");
            return;
        }

        SetWorkflowProgress(20, "Expose", "Selecting a simulation frame from the Image folder.");
        _currentFrameSource = FrameSource.ForFits(GetNextSimulationFrame(fitsFiles));
        await AnalyzeCurrentFileAsync(cancellationToken);
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

    private async Task CaptureFromCameraAsync(bool preferRoi, CancellationToken cancellationToken = default)
    {
        if (_activeCamera is null)
        {
            const string message = "No active FLI camera is available for live capture.";
            ShowAnalysisErrorIfEmpty(message);
            SetWorkflowProgress(100, "Capture Error", message);
            if (_isContinuousExposureRunning)
            {
                StopContinuousExposure(message);
            }
            return;
        }

        ToggleUi(false);
        SetWorkflowProgress(10, "Expose", "Connecting to the FLI camera and preparing the exposure.");

        try
        {
            Directory.CreateDirectory(_captureOutputDirectory);
            var stem = Path.Combine(_captureOutputDirectory, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}");
            var exposureMilliseconds = GetExposureMilliseconds();
            var binning = GetSelectedBinning();
            var coolerEnabled = CoolerEnabledCheckBox.IsChecked == true;
            var coolerSetPoint = coolerEnabled ? GetCoolerSetPointCelsius() : (double?)null;
            using var captureTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            captureTimeoutCts.CancelAfter(TimeSpan.FromMilliseconds(exposureMilliseconds + CaptureTimeoutMarginMilliseconds));
            await _cameraCommandLock.WaitAsync();
            try
            {
                FliReadoutArea? captureImageArea = null;
                await Task.Run(() =>
                {
                    using var camera = _cameraService.OpenBySerial(_activeCamera.SerialNumber);
                    Dispatcher.Invoke(() => SetWorkflowProgress(25, "Expose", "Camera connected. Starting acquisition."));
                    camera.SetCooling(coolerEnabled, coolerSetPoint);
                    camera.EnableBackgroundFlush(true);
                    var visibleArea = camera.GetVisibleArea();
                    captureImageArea = preferRoi
                        ? BuildRoiImageArea(visibleArea, binning)
                        : visibleArea;
                    Dispatcher.Invoke(() =>
                    {
                        if (preferRoi && captureImageArea != visibleArea)
                        {
                            SetWorkflowProgress(32, "Expose", "ROI mode is active. Capturing a focused donut window.");
                        }
                        else if (preferRoi)
                        {
                            SetWorkflowProgress(32, "Expose", "ROI mode is waiting for a donut lock. Capturing a full frame.");
                        }
                    });
                    var capture = camera.Capture(new FliCaptureRequest(
                        exposureMilliseconds,
                        HorizontalBin: binning,
                        VerticalBin: binning,
                        ImageArea: captureImageArea,
                        Diagnostic: message => LogCameraBreadcrumb(stem, message)),
                        captureTimeoutCts.Token);
                    Dispatcher.Invoke(() => SetWorkflowProgress(70, "Expose", "Exposure complete. Writing RAW frame and metadata."));
                    FliRawWriter.Save(capture, stem);
                }, captureTimeoutCts.Token);

                _lastCaptureImageArea = captureImageArea;
                _lastCaptureBinning = binning;
            }
            finally
            {
                _cameraCommandLock.Release();
            }

            _currentFrameSource = FrameSource.ForRaw($"{stem}.raw", $"{stem}.json");
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                SetWorkflowProgress(100, "Stopping", "Continuous exposure is stopping. Latest image details are preserved.");
                return;
            }

            LogException("Live capture failed", exception);
            var message = exception is OperationCanceledException && !cancellationToken.IsCancellationRequested
                ? $"Live capture timed out after {(GetExposureMilliseconds() + CaptureTimeoutMarginMilliseconds) / 1000.0:0} seconds. The camera did not report a completed frame."
                : $"Live capture failed: {exception.Message}";
            ShowAnalysisErrorIfEmpty(message);
            SetWorkflowProgress(100, "Capture Error", message);
            if (_isContinuousExposureRunning)
            {
                StopContinuousExposure(message);
            }
            return;
        }
        finally
        {
            ToggleUi(true);
        }

        await AnalyzeCurrentFileAsync(cancellationToken);
    }

    private async Task AnalyzeCurrentFileAsync(CancellationToken cancellationToken = default)
    {
        if (_currentFrameSource is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        ToggleUi(false);
        CurrentFileTextBlock.Text = _currentFrameSource.DisplayPath;
        SetWorkflowProgress(35, "Analyze", "Preparing the current frame for Python processing.");
        var stretchProfile = StretchProfileComboBox.SelectedItem as StretchProfile ?? _stretchProfiles[0];
        var requestedLowPercentile = LowStretchSlider.Value;
        var requestedHighPercentile = HighStretchSlider.Value;

        try
        {
            LogAnalysisBreadcrumb("analysis", "Python analysis starting");
            SetWorkflowProgress(42, "Analyze", "Starting the Python analysis worker.");
            var analysis = await RunPythonAnalysisAsync(_currentFrameSource);
            cancellationToken.ThrowIfCancellationRequested();
            LogAnalysisBreadcrumb("analysis", $"Python analysis returned. crop={analysis.CropWidth}x{analysis.CropHeight}, full={analysis.FullFrameWidth}x{analysis.FullFrameHeight}, ring={(analysis.RingMetrics is null ? "null" : "present")}, error={analysis.Error ?? "<none>"}");
            _currentAnalysis = analysis;
            UpdateFrameInfoText(analysis);
            var showFullFrameInMainPanel = GetSelectedExposureMode() != ExposureMode.ContinuousExposeWithRoi;
            LogAnalysisBreadcrumb("analysis", $"LoadPreviewData starting. showFullFrame={showFullFrameInMainPanel}");
            SetWorkflowProgress(90, "Render", "Loading the preview crop into memory.");
            await Task.Run(() => LoadPreviewData(analysis, showFullFrameInMainPanel));
            LogAnalysisBreadcrumb("analysis", $"LoadPreviewData completed. display={_currentDisplayWidth}x{_currentDisplayHeight}, origin={_currentDisplayOriginX},{_currentDisplayOriginY}");
            LogAnalysisBreadcrumb("analysis", "UpdateMetricsText starting");
            UpdateMetricsText(analysis);
            LogAnalysisBreadcrumb("analysis", "UpdateMetricsText completed");
            LogAnalysisBreadcrumb("analysis", $"RenderPreviewBitmap starting. profile={stretchProfile.Name}, low={requestedLowPercentile:0.###}, high={requestedHighPercentile:0.###}");
            SetWorkflowProgress(94, "Render", "Rendering the stretched preview.");
            await Task.Run(() => RenderPreviewBitmap(stretchProfile, requestedLowPercentile, requestedHighPercentile));
            LogAnalysisBreadcrumb("analysis", "RenderPreviewBitmap completed");
            LogAnalysisBreadcrumb("analysis", "UpdateDonutOnlyWindow starting");
            UpdateDonutOnlyWindow();
            LogAnalysisBreadcrumb("analysis", "UpdateDonutOnlyWindow completed");
            LogAnalysisBreadcrumb("analysis", "RenderOverlay starting");
            SetWorkflowProgress(97, "Render", "Drawing the detected inner and outer rings.");
            RenderOverlay();
            LogAnalysisBreadcrumb("analysis", "RenderOverlay completed");
            LogAnalysisBreadcrumb("analysis", "TryAutoSaveFitsAsync starting");
            var saveDetail = await TryAutoSaveFitsAsync();
            LogAnalysisBreadcrumb("analysis", $"TryAutoSaveFitsAsync completed. detail={saveDetail ?? "<none>"}");
            LogAnalysisBreadcrumb("analysis", "CaptureHistorySnapshot starting");
            CaptureHistorySnapshot();
            LogAnalysisBreadcrumb("analysis", "CaptureHistorySnapshot completed");
            LogAnalysisBreadcrumb("analysis", "UpdateGraphWindow starting");
            UpdateGraphWindow();
            LogAnalysisBreadcrumb("analysis", "UpdateGraphWindow completed");
            SetWorkflowProgress(100, "Ready", saveDetail ?? "Preview and analysis are up to date.");
            LogAnalysisBreadcrumb("analysis", "Analysis pipeline completed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetWorkflowProgress(100, "Stopping", "Analysis was canceled. Latest image details are preserved.");
        }
        catch (Exception exception)
        {
            LogException("Analysis pipeline failed", exception);
            LogAnalysisBreadcrumb("analysis", $"Analysis pipeline failed: {exception}");
            ShowAnalysisErrorIfEmpty(exception.Message);
            SetWorkflowProgress(100, "Error", exception.Message);
        }
        finally
        {
            ToggleUi(true);
        }
    }

    private async Task StartContinuousExposureAsync()
    {
        if (_isContinuousExposureRunning)
        {
            return;
        }

        _continuousExposureCts = new CancellationTokenSource();
        _isContinuousExposureRunning = true;
        UpdateExposureActionUi();
        SetWorkflowProgress(0, "Continuous", GetSelectedExposureMode() == ExposureMode.ContinuousExposeWithRoi
            ? "Continuous ROI exposure started. The system will switch to a 3x donut ROI after lock."
            : "Continuous exposure started.");

        try
        {
            var token = _continuousExposureCts.Token;
            while (!token.IsCancellationRequested)
            {
                await ExposeAsync(token);
                token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _continuousExposureCts?.Dispose();
            _continuousExposureCts = null;
            _isContinuousExposureRunning = false;
            UpdateExposureActionUi();
            if (WorkflowStatusTextBlock.Text == "Continuous")
            {
                SetWorkflowProgress(100, "Ready", "Continuous exposure stopped.");
            }
        }
    }

    private void StopContinuousExposure(string detail)
    {
        _continuousExposureCts?.Cancel();
        SetWorkflowProgress(100, "Stopping", detail);
    }

    private Task<ViewerAnalysisResult> RunPythonAnalysisAsync(FrameSource frameSource)
    {
        return _pythonWorker.AnalyzeAsync(frameSource, _viewerOutputDirectory);
    }

    private async Task<string?> TryAutoSaveFitsAsync()
    {
        _lastAutosaveFitsPath = null;
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
            _lastAutosaveFitsPath = savedPath;
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

    private void LoadPreviewData(ViewerAnalysisResult analysis, bool showFullFrameInMainPanel)
    {
        LogAnalysisBreadcrumb("load", $"Crop file read starting: {analysis.CropDataFile}");
        var bytes = File.ReadAllBytes(analysis.CropDataFile);
        var expectedBytes = analysis.CropWidth * analysis.CropHeight * sizeof(float);
        LogAnalysisBreadcrumb("load", $"Crop file read completed. bytes={bytes.Length}, expected={expectedBytes}");
        if (bytes.Length != expectedBytes)
        {
            throw new InvalidOperationException($"Preview crop size mismatch. Expected {expectedBytes} bytes but found {bytes.Length}.");
        }

        LogAnalysisBreadcrumb("load", $"Crop float allocation starting. pixels={analysis.CropWidth * analysis.CropHeight}");
        var data = new float[analysis.CropWidth * analysis.CropHeight];
        Buffer.BlockCopy(bytes, 0, data, 0, bytes.Length);
        bytes = [];
        LogAnalysisBreadcrumb("load", "Crop float allocation completed");
        _currentPreviewData = data;

        if (showFullFrameInMainPanel)
        {
            LogAnalysisBreadcrumb("load", $"Full frame file read starting: {analysis.FullFrameDataFile}");
            var fullFrameBytes = File.ReadAllBytes(analysis.FullFrameDataFile);
            var expectedFullFrameBytes = analysis.FullFrameWidth * analysis.FullFrameHeight * sizeof(float);
            LogAnalysisBreadcrumb("load", $"Full frame file read completed. bytes={fullFrameBytes.Length}, expected={expectedFullFrameBytes}");
            if (fullFrameBytes.Length != expectedFullFrameBytes)
            {
                throw new InvalidOperationException($"Full frame size mismatch. Expected {expectedFullFrameBytes} bytes but found {fullFrameBytes.Length}.");
            }

            LogAnalysisBreadcrumb("load", $"Full frame float allocation starting. pixels={analysis.FullFrameWidth * analysis.FullFrameHeight}");
            var fullFrameData = new float[analysis.FullFrameWidth * analysis.FullFrameHeight];
            Buffer.BlockCopy(fullFrameBytes, 0, fullFrameData, 0, fullFrameBytes.Length);
            fullFrameBytes = [];
            LogAnalysisBreadcrumb("load", "Full frame float allocation completed");
            _currentDisplayData = fullFrameData;
            _currentDisplayOriginX = 0;
            _currentDisplayOriginY = 0;
            _currentDisplayWidth = analysis.FullFrameWidth;
            _currentDisplayHeight = analysis.FullFrameHeight;
            return;
        }

        _currentDisplayData = data;
        _currentDisplayOriginX = analysis.CropOriginX;
        _currentDisplayOriginY = analysis.CropOriginY;
        _currentDisplayWidth = analysis.CropWidth;
        _currentDisplayHeight = analysis.CropHeight;
    }

    private void RenderPreviewBitmap(StretchProfile stretchProfile, double requestedLowPercentile, double requestedHighPercentile)
    {
        if (_currentAnalysis is null || _currentDisplayData is null)
        {
            return;
        }

        var result = RenderStretchBitmaps(
            _currentDisplayData,
            _currentDisplayWidth,
            _currentDisplayHeight,
            null,
            0,
            0,
            stretchProfile,
            requestedLowPercentile,
            requestedHighPercentile,
            false,
            CancellationToken.None);

        _currentAnalysis.StretchProfile = stretchProfile.Name;
        _currentAnalysis.StretchLowPercentile = result.EffectiveLowPercentile;
        _currentAnalysis.StretchHighPercentile = result.EffectiveHighPercentile;
        Dispatcher.Invoke(() =>
        {
            PreviewImage.Source = result.PreviewBitmap;
            ApplyPreviewZoom();
        });
    }

    private void ApplyPreviewZoom()
    {
        if (_currentAnalysis is null)
        {
            return;
        }

        var zoomScale = GetSelectedZoomScale();
        var imageWidth = Math.Max(1.0, _currentDisplayWidth * zoomScale);
        var imageHeight = Math.Max(1.0, _currentDisplayHeight * zoomScale);

        PreviewImageHost.Width = imageWidth;
        PreviewImageHost.Height = imageHeight;
        PreviewImage.Width = imageWidth;
        PreviewImage.Height = imageHeight;
        OverlayCanvas.Width = imageWidth;
        OverlayCanvas.Height = imageHeight;
        SyncPreviewScrollBarsFromViewer();
        RenderOverlay();
    }

    private void SyncPreviewScrollBarsFromViewer()
    {
        if (!IsInitialized)
        {
            return;
        }

        _isSyncingPreviewScrollBars = true;
        try
        {
            PreviewHorizontalScrollBar.Maximum = Math.Max(0, PreviewScrollViewer.ScrollableWidth);
            PreviewHorizontalScrollBar.ViewportSize = Math.Max(0, PreviewScrollViewer.ViewportWidth);
            PreviewHorizontalScrollBar.LargeChange = Math.Max(1, PreviewScrollViewer.ViewportWidth * 0.8);
            PreviewHorizontalScrollBar.SmallChange = 24;
            PreviewHorizontalScrollBar.Value = Math.Clamp(
                PreviewScrollViewer.ScrollableWidth - PreviewScrollViewer.HorizontalOffset,
                PreviewHorizontalScrollBar.Minimum,
                PreviewHorizontalScrollBar.Maximum);

            PreviewVerticalScrollBar.Maximum = Math.Max(0, PreviewScrollViewer.ScrollableHeight);
            PreviewVerticalScrollBar.ViewportSize = Math.Max(0, PreviewScrollViewer.ViewportHeight);
            PreviewVerticalScrollBar.LargeChange = Math.Max(1, PreviewScrollViewer.ViewportHeight * 0.8);
            PreviewVerticalScrollBar.SmallChange = 24;
            PreviewVerticalScrollBar.Value = Math.Clamp(
                PreviewScrollViewer.ScrollableHeight - PreviewScrollViewer.VerticalOffset,
                PreviewVerticalScrollBar.Minimum,
                PreviewVerticalScrollBar.Maximum);
        }
        finally
        {
            _isSyncingPreviewScrollBars = false;
        }
    }

    private double GetSelectedZoomScale()
    {
        var fitScale = GetFitZoomScale();
        if (ZoomComboBox.SelectedItem is not ComboBoxItem item)
        {
            return fitScale;
        }

        var tag = item.Tag?.ToString();
        if (string.Equals(tag, "Fit", StringComparison.OrdinalIgnoreCase))
        {
            return fitScale;
        }

        if (double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) &&
            percent > 0)
        {
            return Math.Max(fitScale, percent / 100.0);
        }

        return fitScale;
    }

    private double GetFitZoomScale()
    {
        if (_currentDisplayWidth <= 0 || _currentDisplayHeight <= 0)
        {
            return 1.0;
        }

        var viewportWidth = PreviewScrollViewer.ViewportWidth > 0
            ? PreviewScrollViewer.ViewportWidth
            : PreviewScrollViewer.ActualWidth;
        var viewportHeight = PreviewScrollViewer.ViewportHeight > 0
            ? PreviewScrollViewer.ViewportHeight
            : PreviewScrollViewer.ActualHeight;

        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return 1.0;
        }

        var fitScale = Math.Min(viewportWidth / _currentDisplayWidth, viewportHeight / _currentDisplayHeight);
        return Math.Clamp(fitScale, 0.01, 1.0);
    }

    private void UpdateMetricsText(ViewerAnalysisResult analysis)
    {
        MetricsTextBlock.Inlines.Clear();

        if (analysis.RingMetrics is null)
        {
            AnalysisScoreTextBlock.Text = "Score --.--%";
            StatusTextBlock.Text = "Donut Ring was not found in this image.";
            StatusTextBlock.Foreground = Brushes.Red;
            return;
        }

        StatusTextBlock.Foreground = Brushes.LightGreen;
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

    private void ShowAnalysisErrorIfEmpty(string message)
    {
        if (_currentAnalysis is not null && (PreviewImage.Source is not null || MetricsTextBlock.Inlines.Count > 0))
        {
            return;
        }

        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = Brushes.OrangeRed;
        MetricsTextBlock.Text = string.Empty;
        OverlayCanvas.Children.Clear();
        FrameInfoTextBlock.Text = "Resolution -- x -- px | -- bit";
    }

    private void UpdateFrameInfoText(ViewerAnalysisResult analysis)
    {
        var bitDepth = TryGetFrameBitDepth(_currentFrameSource);
        var bitDepthText = bitDepth is null
            ? "-- bit"
            : $"{bitDepth.Value}-bit";
        FrameInfoTextBlock.Text = $"Resolution {analysis.FullFrameWidth} x {analysis.FullFrameHeight} px | {bitDepthText}";
    }

    private static int? TryGetFrameBitDepth(FrameSource? frameSource)
    {
        if (frameSource is null)
        {
            return null;
        }

        try
        {
            if (frameSource.Kind == FrameSourceKind.Raw && frameSource.MetadataPath is not null)
            {
                var metadata = JsonSerializer.Deserialize<FliRawFrameMetadata>(File.ReadAllText(frameSource.MetadataPath));
                return metadata?.BitDepth;
            }

            if (frameSource.Kind == FrameSourceKind.Fits)
            {
                return TryReadFitsBitDepth(frameSource.FramePath);
            }
        }
        catch
        {
        }

        return null;
    }

    private static int? TryReadFitsBitDepth(string fitsPath)
    {
        using var stream = File.OpenRead(fitsPath);
        var headerBuffer = new byte[80];
        while (stream.Read(headerBuffer, 0, headerBuffer.Length) == headerBuffer.Length)
        {
            var card = System.Text.Encoding.ASCII.GetString(headerBuffer);
            var keyword = card.Length >= 8 ? card[..8].Trim() : string.Empty;
            if (keyword.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!keyword.Equals("BITPIX", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var equalsIndex = card.IndexOf('=');
            if (equalsIndex < 0)
            {
                return null;
            }

            var valueText = card[(equalsIndex + 1)..].Split('/')[0].Trim();
            if (!int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bitpix))
            {
                return null;
            }

            return Math.Abs(bitpix);
        }

        return null;
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

    private void EnsureGraphWindow()
    {
        if (_graphWindow is not null)
        {
            if (!_graphWindow.IsVisible)
            {
                _graphWindow.Show();
            }

            _graphWindow.Activate();
            return;
        }

        _graphWindow = new GraphWindow();
        _graphWindow.Owner = this;
        _graphWindow.Closed += (_, _) =>
        {
            _graphWindow = null;
            if (ShowGraphCheckBox.IsChecked == true)
            {
                ShowGraphCheckBox.IsChecked = false;
            }
        };
        _graphWindow.Show();
        PositionAuxiliaryWindows();
    }

    private void CloseGraphWindow()
    {
        if (_graphWindow is null)
        {
            return;
        }

        var window = _graphWindow;
        _graphWindow = null;
        window.Close();
    }

    private void EnsureDonutOnlyWindow()
    {
        if (_donutOnlyWindow is not null)
        {
            if (!_donutOnlyWindow.IsVisible)
            {
                _donutOnlyWindow.Show();
            }

            _donutOnlyWindow.Activate();
            return;
        }

        _donutOnlyWindow = new DonutOnlyWindow
        {
            Owner = this,
        };
        _donutOnlyWindow.Closed += (_, _) =>
        {
            _donutOnlyWindow = null;
            if (ShowOnlyDonutRingCheckBox.IsChecked == true)
            {
                ShowOnlyDonutRingCheckBox.IsChecked = false;
            }
        };
        _donutOnlyWindow.Show();
        PositionAuxiliaryWindows();
    }

    private void CloseDonutOnlyWindow()
    {
        if (_donutOnlyWindow is null)
        {
            return;
        }

        var window = _donutOnlyWindow;
        _donutOnlyWindow = null;
        window.Close();
    }

    private void UpdateDonutOnlyWindow()
    {
        if (ShowOnlyDonutRingCheckBox.IsChecked != true || _donutOnlyWindow is null)
        {
            return;
        }

        if (_currentAnalysis is null || _currentPreviewData is null)
        {
            _donutOnlyWindow.SetImage(null);
            return;
        }

        var stretchProfile = StretchProfileComboBox.SelectedItem as StretchProfile ?? _stretchProfiles[0];
        var result = RenderStretchBitmaps(
            _currentPreviewData,
            _currentAnalysis.CropWidth,
            _currentAnalysis.CropHeight,
            null,
            0,
            0,
            stretchProfile,
            LowStretchSlider.Value,
            HighStretchSlider.Value,
            false,
            CancellationToken.None);
        _donutOnlyWindow.SetImage(result.PreviewBitmap);
    }

    private void UpdateGraphWindow()
    {
        if (ShowGraphCheckBox.IsChecked != true || _graphWindow is null || _currentAnalysis is null || _currentPreviewData is null)
        {
            return;
        }

        try
        {
            _graphWindow.SetStatus("Rendering interactive 3D donut graph...", Brushes.LightSkyBlue);
            _graphWindow.UpdateSurface(
                _currentPreviewData,
                _currentAnalysis.CropWidth,
                _currentAnalysis.CropHeight);
        }
        catch (Exception exception)
        {
            _graphWindow.SetStatus(exception.Message, Brushes.OrangeRed);
        }
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

        var renderedWidth = PreviewImage.ActualWidth;
        var renderedHeight = PreviewImage.ActualHeight;
        if (renderedWidth <= 0 || renderedHeight <= 0)
        {
            return;
        }

        OverlayCanvas.Width = renderedWidth;
        OverlayCanvas.Height = renderedHeight;

        var imageWidth = _currentDisplayWidth;
        var imageHeight = _currentDisplayHeight;
        var scaleX = renderedWidth / imageWidth;
        var scaleY = renderedHeight / imageHeight;

        var metrics = _currentAnalysis.RingMetrics;
        var outerCenterX = (metrics.OuterCenterX - _currentDisplayOriginX) * scaleX;
        var outerCenterY = (metrics.OuterCenterY - _currentDisplayOriginY) * scaleY;
        var innerCenterX = (metrics.InnerCenterX - _currentDisplayOriginX) * scaleX;
        var innerCenterY = (metrics.InnerCenterY - _currentDisplayOriginY) * scaleY;
        var radiusScale = (scaleX + scaleY) / 2.0;

        OverlayCanvas.Children.Add(CreateCircle(outerCenterX, outerCenterY, metrics.OuterRadiusPx * radiusScale, Brushes.Cyan));
        OverlayCanvas.Children.Add(CreateCircle(innerCenterX, innerCenterY, metrics.InnerRadiusPx * radiusScale, Brushes.Magenta));
        OverlayCanvas.Children.Add(CreateCenterMarker(outerCenterX, outerCenterY, Brushes.Cyan));
        OverlayCanvas.Children.Add(CreateCenterMarker(innerCenterX, innerCenterY, Brushes.Magenta));
    }

    private void EndPreviewPan()
    {
        if (!_isPanningPreview)
        {
            return;
        }

        _isPanningPreview = false;
        PreviewScrollViewer.Cursor = Cursors.Arrow;
        PreviewScrollViewer.ReleaseMouseCapture();
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
        ExposeButton.IsEnabled = enabled || _isContinuousExposureRunning;
        BrowseSaveFolderButton.IsEnabled = enabled;
        SaveFolderTextBox.IsEnabled = enabled;
        AutoSaveFitsCheckBox.IsEnabled = enabled;
        UseImageFolderCheckBox.IsEnabled = enabled;
        ZoomComboBox.IsEnabled = enabled;
        ExposureSecondsTextBox.IsEnabled = enabled;
        BinningComboBox.IsEnabled = enabled;
        CoolerEnabledCheckBox.IsEnabled = enabled;
        CoolerSetPointTextBox.IsEnabled = enabled;
        ExposureModeComboBox.IsEnabled = enabled && !_isContinuousExposureRunning;
    }

    private void ThumbnailListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedHistoryItem = ThumbnailListBox.SelectedItem as ExposureHistoryItem;
        ReferencePreviewPanel.Visibility = _selectedHistoryItem is null ? Visibility.Collapsed : Visibility.Visible;
        UpdatePreviewFrameChrome();
        UpdateReferencePreviewDisplay();
    }

    private void CloseReferencePreviewButton_OnClick(object sender, RoutedEventArgs e)
    {
        ThumbnailListBox.SelectedItem = null;
    }

    private void UpdateExposureActionUi()
    {
        var mode = GetSelectedExposureMode();
        ExposeButton.Content = _isContinuousExposureRunning
            ? "Stop"
            : "Expose";

        if (_isContinuousExposureRunning)
        {
            ExposureModeComboBox.IsEnabled = false;
            return;
        }

        ExposureModeComboBox.IsEnabled = true;
        WorkflowDetailTextBlock.Text = mode switch
        {
            ExposureMode.SingleExpose => WorkflowDetailTextBlock.Text,
            ExposureMode.ContinuousExpose => "Continuous exposure is ready. Click Expose to start the live loop.",
            ExposureMode.ContinuousExposeWithRoi => "Continuous ROI exposure is ready. The ROI will tighten to 3x the donut size after lock.",
            _ => WorkflowDetailTextBlock.Text,
        };
    }

    private ExposureMode GetSelectedExposureMode()
    {
        if (ExposureModeComboBox.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<ExposureMode>(item.Tag?.ToString(), ignoreCase: false, out var mode))
        {
            return mode;
        }

        return ExposureMode.SingleExpose;
    }

    private FliReadoutArea? BuildRoiImageArea(FliReadoutArea visibleArea, int currentBinning)
    {
        if (_currentAnalysis?.RingMetrics is null || _lastCaptureImageArea is null || _lastCaptureBinning <= 0)
        {
            return visibleArea;
        }

        var metrics = _currentAnalysis.RingMetrics;
        var sensorCenterX = _lastCaptureImageArea.UpperLeftX + (int)Math.Round(metrics.OuterCenterX * _lastCaptureBinning);
        var sensorCenterY = _lastCaptureImageArea.UpperLeftY + (int)Math.Round(metrics.OuterCenterY * _lastCaptureBinning);
        var sensorOuterRadius = Math.Max(1, (int)Math.Round(metrics.OuterRadiusPx * _lastCaptureBinning));
        var desiredSide = Math.Max(sensorOuterRadius * 6, currentBinning * 64);

        var alignedSide = AlignToBinning(Math.Min(Math.Min(desiredSide, visibleArea.Width), visibleArea.Height), currentBinning);
        if (alignedSide <= 0)
        {
            return visibleArea;
        }

        var left = sensorCenterX - (alignedSide / 2);
        var top = sensorCenterY - (alignedSide / 2);
        var minLeft = visibleArea.UpperLeftX;
        var maxLeft = visibleArea.LowerRightX - alignedSide;
        var minTop = visibleArea.UpperLeftY;
        var maxTop = visibleArea.LowerRightY - alignedSide;

        left = Math.Clamp(left, minLeft, Math.Max(minLeft, maxLeft));
        top = Math.Clamp(top, minTop, Math.Max(minTop, maxTop));
        left = AlignToBinning(left - visibleArea.UpperLeftX, currentBinning) + visibleArea.UpperLeftX;
        top = AlignToBinning(top - visibleArea.UpperLeftY, currentBinning) + visibleArea.UpperLeftY;

        var right = Math.Min(visibleArea.LowerRightX, left + alignedSide);
        var bottom = Math.Min(visibleArea.LowerRightY, top + alignedSide);
        right = left + AlignToBinning(right - left, currentBinning);
        bottom = top + AlignToBinning(bottom - top, currentBinning);

        if (right <= left || bottom <= top)
        {
            return visibleArea;
        }

        return new FliReadoutArea(left, top, right, bottom);
    }

    private static int AlignToBinning(int value, int binning)
    {
        var safeBinning = Math.Max(1, binning);
        return Math.Max(safeBinning, (value / safeBinning) * safeBinning);
    }

    private void CaptureHistorySnapshot()
    {
        if (PreviewImage.Source is null)
        {
            return;
        }

        PreviewImageHost.UpdateLayout();
        OverlayCanvas.UpdateLayout();

        var pixelWidth = Math.Max(1, (int)Math.Ceiling(PreviewImageHost.ActualWidth));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(PreviewImageHost.ActualHeight));
        if (pixelWidth <= 1 || pixelHeight <= 1)
        {
            return;
        }

        var snapshot = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        snapshot.Render(PreviewImageHost);
        snapshot.Freeze();

        var plainSnapshot = PreviewImage.Source as ImageSource;

        var item = new ExposureHistoryItem(BuildHistoryCaption(), snapshot, plainSnapshot, _currentFrameSource?.FramePath);
        _exposureHistory.Insert(0, item);

        while (_exposureHistory.Count > 20)
        {
            var removed = _exposureHistory[^1];
            _exposureHistory.RemoveAt(_exposureHistory.Count - 1);
            if (ReferenceEquals(removed, _selectedHistoryItem))
            {
                _selectedHistoryItem = null;
                ReferencePreviewImage.Source = null;
                ReferencePreviewPanel.Visibility = Visibility.Collapsed;
                ThumbnailListBox.SelectedItem = null;
                UpdatePreviewFrameChrome();
            }
        }
    }

    private void UpdateReferencePreviewDisplay()
    {
        if (_selectedHistoryItem is null)
        {
            ReferencePreviewImage.Source = null;
            return;
        }

        ReferencePreviewImage.Source = ShowOverlayCheckBox.IsChecked == true
            ? _selectedHistoryItem.Snapshot
            : _selectedHistoryItem.PlainSnapshot;
    }

    private void UpdatePreviewFrameChrome()
    {
        var showComparisonChrome = ReferencePreviewPanel.Visibility == Visibility.Visible;
        LivePreviewPanel.Padding = showComparisonChrome ? new Thickness(10) : new Thickness(0);
        LivePreviewPanel.BorderThickness = showComparisonChrome ? new Thickness(1) : new Thickness(0);
        LivePreviewPanel.Background = showComparisonChrome
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#05090D"))
            : Brushes.Transparent;
    }

    private string BuildHistoryCaption()
    {
        if (!string.IsNullOrWhiteSpace(_lastAutosaveFitsPath))
        {
            return Path.GetFileName(_lastAutosaveFitsPath);
        }

        _historySequence++;
        var stamp = DateTime.Now.ToString("ddMMyyyy", CultureInfo.InvariantCulture);
        return $"{stamp}-{_historySequence:0000}";
    }

    private void UpdateStretchLabels()
    {
        LowStretchValueTextBlock.Text = LowStretchSlider.Value.ToString("0.###", CultureInfo.InvariantCulture);
        HighStretchValueTextBlock.Text = HighStretchSlider.Value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void QueueStretchRefresh()
    {
        if (!IsLoaded || _currentFrameSource is null || _currentAnalysis is null || _currentDisplayData is null)
        {
            return;
        }

        _stretchDebounceCts?.Cancel();
        _stretchDebounceCts?.Dispose();
        _stretchDebounceCts = new CancellationTokenSource();
        var token = _stretchDebounceCts.Token;
        var profile = StretchProfileComboBox.SelectedItem as StretchProfile ?? _stretchProfiles[0];
        var lowPercentile = LowStretchSlider.Value;
        var highPercentile = HighStretchSlider.Value;
        var displayData = _currentDisplayData;
        var displayWidth = _currentDisplayWidth;
        var displayHeight = _currentDisplayHeight;
        var previewData = _currentPreviewData;
        var cropWidth = _currentAnalysis.CropWidth;
        var cropHeight = _currentAnalysis.CropHeight;
        var renderDonutOnly = ShowOnlyDonutRingCheckBox.IsChecked == true && _donutOnlyWindow is not null && previewData is not null;

        _ = Task.Run(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();

                await Dispatcher.InvokeAsync(() =>
                    SetWorkflowProgress(15, "Adjusting Stretch", "Rendering stretch on a background worker."));

                var result = RenderStretchBitmaps(
                    displayData,
                    displayWidth,
                    displayHeight,
                    previewData,
                    cropWidth,
                    cropHeight,
                    profile,
                    lowPercentile,
                    highPercentile,
                    renderDonutOnly,
                    token);

                token.ThrowIfCancellationRequested();

                await Dispatcher.InvokeAsync(() => ApplyStretchRenderResult(result), System.Windows.Threading.DispatcherPriority.Background, token);
            }
            catch (TaskCanceledException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void ApplyStretchRenderResult(StretchRenderResult result)
    {
        if (_currentAnalysis is null)
        {
            return;
        }

        _currentAnalysis.StretchProfile = result.ProfileName;
        _currentAnalysis.StretchLowPercentile = result.EffectiveLowPercentile;
        _currentAnalysis.StretchHighPercentile = result.EffectiveHighPercentile;
        PreviewImage.Source = result.PreviewBitmap;
        ApplyPreviewZoom();

        if (ShowOnlyDonutRingCheckBox.IsChecked == true && _donutOnlyWindow is not null && result.DonutOnlyBitmap is not null)
        {
            _donutOnlyWindow.SetImage(result.DonutOnlyBitmap);
        }

        UpdateMetricsText(_currentAnalysis);
        SetWorkflowProgress(100, "Ready", "Preview and analysis are up to date.");
    }

    private static StretchRenderResult RenderStretchBitmaps(
        float[] displayData,
        int displayWidth,
        int displayHeight,
        float[]? donutData,
        int donutWidth,
        int donutHeight,
        StretchProfile stretchProfile,
        double requestedLowPercentile,
        double requestedHighPercentile,
        bool renderDonutOnly,
        CancellationToken cancellationToken)
    {
        var renderedDisplay = StretchPreview(
            displayData,
            displayWidth,
            displayHeight,
            stretchProfile,
            requestedLowPercentile,
            requestedHighPercentile,
            out var effectiveLow,
            out var effectiveHigh,
            cancellationToken);
        var previewBitmap = CreateGrayBitmap(renderedDisplay, displayWidth, displayHeight);

        BitmapSource? donutBitmap = null;
        if (renderDonutOnly && donutData is not null && donutWidth > 0 && donutHeight > 0)
        {
            var renderedDonut = StretchPreview(
                donutData,
                donutWidth,
                donutHeight,
                stretchProfile,
                requestedLowPercentile,
                requestedHighPercentile,
                out _,
                out _,
                cancellationToken);
            donutBitmap = CreateGrayBitmap(renderedDonut, donutWidth, donutHeight);
        }

        return new StretchRenderResult(previewBitmap, donutBitmap, stretchProfile.Name, effectiveLow, effectiveHigh);
    }

    private static BitmapSource CreateGrayBitmap(byte[] rendered, int width, int height)
    {
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Gray8,
            null,
            rendered,
            width);
        bitmap.Freeze();
        return bitmap;
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
            StopSensorTemperaturePolling();
            Title = "DonutScope Simulator";
            CcdConnectionStatusTextBlock.Visibility = Visibility.Collapsed;
            SensorTemperatureTextBlock.Text = "--.- °C";
            SetWorkflowProgress(0, "Ready", "Simulation mode is active. Click Expose to load a sample frame.");
        }
        else
        {
            Title = "DonutScope";
            CcdConnectionStatusTextBlock.Visibility = Visibility.Visible;
            StartSensorTemperaturePolling();
            SetWorkflowProgress(0, "Ready", "Live camera mode is active. Click Expose to capture a frame.");
        }
    }

    private void StartSensorTemperaturePolling()
    {
        StopSensorTemperaturePolling();
        if (_activeCamera is null)
        {
            return;
        }

        _temperaturePollingCts = new CancellationTokenSource();
        var token = _temperaturePollingCts.Token;

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (!token.IsCancellationRequested)
            {
                await ReadAndDisplaySensorTemperatureAsync(token);
                try
                {
                    if (!await timer.WaitForNextTickAsync(token))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    private void StopSensorTemperaturePolling()
    {
        _temperaturePollingCts?.Cancel();
        _temperaturePollingCts?.Dispose();
        _temperaturePollingCts = null;
    }

    private async Task ReadAndDisplaySensorTemperatureAsync(CancellationToken token)
    {
        if (_activeCamera is null)
        {
            return;
        }

        try
        {
            await _cameraCommandLock.WaitAsync(token);
            try
            {
                var status = await Task.Run(() =>
                {
                    using var camera = _cameraService.OpenBySerial(_activeCamera.SerialNumber);
                    return camera.GetStatus();
                }, token);

                await Dispatcher.InvokeAsync(() =>
                {
                    SensorTemperatureTextBlock.Text = $"{status.TemperatureCelsius:0.0} °C";
                    SensorTemperatureTextBlock.Foreground = Brushes.LightSkyBlue;
                });
            }
            finally
            {
                _cameraCommandLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            await Dispatcher.InvokeAsync(() =>
            {
                SensorTemperatureTextBlock.Text = "Read error";
                SensorTemperatureTextBlock.Foreground = Brushes.OrangeRed;
            });
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

    private async void CoolerEnabledCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        await ApplyCoolerSettingsAsync(sendSetPoint: CoolerEnabledCheckBox.IsChecked == true);
    }

    private async void CoolerSetPointTextBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        try
        {
            SaveCoolerSetPointSetting(GetCoolerSetPointCelsius());
        }
        catch (Exception exception)
        {
            SetWorkflowProgress(100, "Settings Error", exception.Message);
            return;
        }

        if (CoolerEnabledCheckBox.IsChecked != true)
        {
            return;
        }

        await ApplyCoolerSettingsAsync(sendSetPoint: true);
    }

    private async Task ApplyCoolerSettingsAsync(bool sendSetPoint)
    {
        if (_activeCamera is null)
        {
            SetWorkflowProgress(100, "Cooler", "No active FLI camera is available for cooler control.");
            return;
        }

        var coolerEnabled = CoolerEnabledCheckBox.IsChecked == true;
        double? coolerSetPoint = null;
        if (coolerEnabled && sendSetPoint)
        {
            try
            {
                coolerSetPoint = GetCoolerSetPointCelsius();
                SaveCoolerSetPointSetting(coolerSetPoint.Value);
            }
            catch (Exception exception)
            {
                SetWorkflowProgress(100, "Cooler Error", exception.Message);
                return;
            }
        }

        ToggleUi(false);
        try
        {
            SetWorkflowProgress(10, "Cooler", coolerEnabled
                ? $"Setting CCD cooler to {coolerSetPoint:0.##} °C."
                : "Turning CCD cooler off.");

            await _cameraCommandLock.WaitAsync();
            try
            {
                await Task.Run(() =>
                {
                    using var camera = _cameraService.OpenBySerial(_activeCamera.SerialNumber);
                    camera.SetCooling(coolerEnabled, coolerSetPoint);
                });
            }
            finally
            {
                _cameraCommandLock.Release();
            }

            SetWorkflowProgress(100, "Ready", coolerEnabled
                ? $"CCD cooler is on. Set point: {coolerSetPoint:0.##} °C."
                : "CCD cooler is off.");
        }
        catch (Exception exception)
        {
            LogException("Cooler command failed", exception);
            SetWorkflowProgress(100, "Cooler Error", exception.Message);
        }
        finally
        {
            ToggleUi(true);
        }
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

    private AppSettings LoadAppSettings()
    {
        try
        {
            if (!File.Exists(_appSettingsPath))
            {
                return new AppSettings { CoolerSetPointCelsius = DefaultCoolerSetPointCelsius };
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(_appSettingsPath),
                JsonOptions);
            return settings ?? new AppSettings { CoolerSetPointCelsius = DefaultCoolerSetPointCelsius };
        }
        catch
        {
            return new AppSettings { CoolerSetPointCelsius = DefaultCoolerSetPointCelsius };
        }
    }

    private void SaveCoolerSetPointSetting(double coolerSetPointCelsius)
    {
        _appSettings.CoolerSetPointCelsius = coolerSetPointCelsius;
        Directory.CreateDirectory(Path.GetDirectoryName(_appSettingsPath)!);
        var json = JsonSerializer.Serialize(_appSettings, new JsonSerializerOptions(JsonOptions)
        {
            WriteIndented = true,
        });
        File.WriteAllText(_appSettingsPath, json);
    }

    private void LogException(string context, Exception exception)
    {
        try
        {
            var logDirectory = Path.Combine(_repositoryRoot, "output", "logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "donutscope.log");
            File.AppendAllText(
                logPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private void LogCameraBreadcrumb(string outputStem, string message)
    {
        try
        {
            var logDirectory = Path.Combine(_repositoryRoot, "output", "logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "fli-capture-breadcrumb.log");
            using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {Path.GetFileName(outputStem)}: {message}");
            writer.Flush();
            stream.Flush(true);
        }
        catch
        {
        }
    }

    private void LogAnalysisBreadcrumb(string stage, string message)
    {
        try
        {
            var logDirectory = Path.Combine(_repositoryRoot, "output", "logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "analysis-breadcrumb.log");
            using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {stage}: {message}");
            writer.Flush();
            stream.Flush(true);
        }
        catch
        {
        }
    }

    private void HandleWorkerProgress(double percent, string headline, string detail)
    {
        Dispatcher.Invoke(() => SetWorkflowProgress(percent, headline, detail));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _continuousExposureCts?.Cancel();
        _continuousExposureCts?.Dispose();
        CloseGraphWindow();
        CloseDonutOnlyWindow();
        StopSensorTemperaturePolling();
        _stretchDebounceCts?.Cancel();
        _stretchDebounceCts?.Dispose();
        _pythonWorker.Dispose();
        _cameraCommandLock.Dispose();
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
        out double effectiveHighPercentile,
        CancellationToken cancellationToken = default)
    {
        var finite = new double[sourceData.Length];
        var finiteCount = 0;
        for (var index = 0; index < sourceData.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = sourceData[index];
            if (!float.IsNaN(value) && !float.IsInfinity(value))
            {
                finite[finiteCount++] = value;
            }
        }

        if (finiteCount != finite.Length)
        {
            Array.Resize(ref finite, finiteCount);
        }

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
        var sum = 0.0;
        for (var index = 0; index < finite.Length; index++)
        {
            sum += finite[index];
        }

        var mean = sum / finite.Length;
        var varianceSum = 0.0;
        for (var index = 0; index < finite.Length; index++)
        {
            var delta = finite[index] - mean;
            varianceSum += delta * delta;
        }

        var variance = varianceSum / finite.Length;
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
        if (sourceData.Length > 250_000)
        {
            var options = new ParallelOptions { CancellationToken = cancellationToken };
            Parallel.For(0, sourceData.Length, options, index =>
            {
                output[index] = StretchPixel(sourceData[index], lowValue, highValue, denominator, gamma);
            });
        }
        else
        {
            for (var index = 0; index < sourceData.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output[index] = StretchPixel(sourceData[index], lowValue, highValue, denominator, gamma);
            }
        }

        return output;
    }

    private static byte StretchPixel(float sourceValue, double lowValue, double highValue, double denominator, double gamma)
    {
        var value = Math.Clamp(sourceValue, (float)lowValue, (float)highValue);
        var normalized = (value - lowValue) / denominator;
        var gammaCorrected = Math.Pow(Math.Clamp(normalized, 0.0, 1.0), gamma);
        return (byte)Math.Round(gammaCorrected * 255.0);
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

    [JsonPropertyName("full_frame_data_file")]
    public string FullFrameDataFile { get; set; } = string.Empty;

    [JsonPropertyName("crop_origin_x")]
    public int CropOriginX { get; set; }

    [JsonPropertyName("crop_origin_y")]
    public int CropOriginY { get; set; }

    [JsonPropertyName("crop_width")]
    public int CropWidth { get; set; }

    [JsonPropertyName("crop_height")]
    public int CropHeight { get; set; }

    [JsonPropertyName("full_frame_width")]
    public int FullFrameWidth { get; set; }

    [JsonPropertyName("full_frame_height")]
    public int FullFrameHeight { get; set; }

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

    public override string ToString()
    {
        return Name;
    }
}

public sealed class AppSettings
{
    public double CoolerSetPointCelsius { get; set; } = 10.0;
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

public enum ExposureMode
{
    SingleExpose,
    ContinuousExpose,
    ContinuousExposeWithRoi,
}

public sealed class ExposureHistoryItem
{
    public ExposureHistoryItem(string caption, ImageSource snapshot, ImageSource? plainSnapshot, string? sourcePath)
    {
        Caption = caption;
        Snapshot = snapshot;
        PlainSnapshot = plainSnapshot ?? snapshot;
        SourcePath = sourcePath;
    }

    public string Caption { get; }

    public ImageSource Snapshot { get; }

    public ImageSource PlainSnapshot { get; }

    public string? SourcePath { get; }
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

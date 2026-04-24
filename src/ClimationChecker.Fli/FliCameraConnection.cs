namespace ClimationChecker.Fli;

public sealed class FliCameraConnection : IDisposable
{
    private const double CoolerOffTemperature = 45.0;

    private readonly FliCameraSdk _sdk;
    private bool _disposed;

    internal FliCameraConnection(FliCameraSdk sdk, string serialNumber)
    {
        _sdk = sdk;
        SerialNumber = serialNumber;
        ModelName = _sdk.GetModel();
    }

    public string SerialNumber { get; }

    public string? ModelName { get; }

    public bool CoolingEnabled { get; private set; }

    public double? CoolingSetPointCelsius { get; private set; }

    public IReadOnlyDictionary<int, string> GetCameraModes()
    {
        var modes = new Dictionary<int, string>();
        for (var index = 0; ; index++)
        {
            var name = _sdk.GetCameraModeString(index);
            if (string.IsNullOrWhiteSpace(name))
            {
                break;
            }

            modes[index] = name;
        }

        return modes;
    }

    public void SetCameraMode(int modeIndex)
    {
        _sdk.SetCameraMode(modeIndex);
    }

    public void SetCooling(bool enabled, double? setPointCelsius = null)
    {
        CoolingEnabled = enabled;
        CoolingSetPointCelsius = enabled ? setPointCelsius : null;
        _sdk.SetTemperature(enabled ? setPointCelsius ?? CoolerOffTemperature : CoolerOffTemperature);
    }

    public FliCameraStatus GetStatus()
    {
        var visibleArea = GetVisibleArea();
        var modeIndex = _sdk.GetCameraMode();
        return new FliCameraStatus(
            SerialNumber,
            ModelName,
            DescribeStatus(_sdk.GetDeviceStatus()),
            _sdk.GetTemperature(),
            _sdk.GetCoolerPower(),
            modeIndex,
            _sdk.GetCameraModeString(modeIndex),
            visibleArea,
            CoolingEnabled,
            CoolingSetPointCelsius);
    }

    public FliReadoutArea GetVisibleArea()
    {
        _sdk.GetVisibleArea(out var ulX, out var ulY, out var lrX, out var lrY);
        return new FliReadoutArea(ulX, ulY, lrX, lrY);
    }

    public FliCaptureResult Capture(FliCaptureRequest request, CancellationToken cancellationToken = default)
    {
        var imageArea = request.ImageArea ?? GetVisibleArea();
        var driverImageArea = BuildDriverImageArea(imageArea, request.HorizontalBin, request.VerticalBin);

        request.Diagnostic?.Invoke($"SetHorizontalBin({request.HorizontalBin})");
        _sdk.SetHorizontalBin(request.HorizontalBin);
        request.Diagnostic?.Invoke($"SetVerticalBin({request.VerticalBin})");
        _sdk.SetVerticalBin(request.VerticalBin);
        request.Diagnostic?.Invoke($"SetFrameType({request.FrameType})");
        _sdk.SetFrameType(request.FrameType);
        request.Diagnostic?.Invoke(
            $"SetImageArea({driverImageArea.UpperLeftX},{driverImageArea.UpperLeftY},{driverImageArea.LowerRightX},{driverImageArea.LowerRightY}) from requested {imageArea.UpperLeftX},{imageArea.UpperLeftY},{imageArea.LowerRightX},{imageArea.LowerRightY}");
        _sdk.SetImageArea(driverImageArea);
        var fallbackWidth = imageArea.Width / Math.Max(request.HorizontalBin, 1);
        var fallbackHeight = imageArea.Height / Math.Max(request.VerticalBin, 1);
        var readoutDimensions = _sdk.TryGetReadoutDimensions();
        var width = fallbackWidth;
        var height = fallbackHeight;
        if (readoutDimensions is not null)
        {
            request.Diagnostic?.Invoke(
                $"ReadoutDimensions width={readoutDimensions.Width}, height={readoutDimensions.Height}, hOffset={readoutDimensions.HorizontalOffset}, vOffset={readoutDimensions.VerticalOffset}, hBin={readoutDimensions.HorizontalBin}, vBin={readoutDimensions.VerticalBin}");
            if (readoutDimensions.Width > 0 && readoutDimensions.Height > 0 &&
                (readoutDimensions.Width != fallbackWidth || readoutDimensions.Height != fallbackHeight))
            {
                request.Diagnostic?.Invoke($"Using requested binned dimensions {fallbackWidth} x {fallbackHeight}; driver dimensions are informational.");
            }
        }
        else
        {
            request.Diagnostic?.Invoke($"ReadoutDimensions unavailable. Fallback {width} x {height}");
        }

        request.Diagnostic?.Invoke($"SetExposureTime({request.ExposureMilliseconds:0} ms)");
        _sdk.SetExposureTime((int)Math.Round(request.ExposureMilliseconds));
        request.Diagnostic?.Invoke($"SetTdi({request.TdiRate})");
        _sdk.SetTdi(request.TdiRate);
        request.Diagnostic?.Invoke($"SetFlushCount({request.FlushCount})");
        _sdk.SetFlushCount(request.FlushCount);
        request.Diagnostic?.Invoke("ExposeFrame()");
        _sdk.ExposeFrame();

        try
        {
            while (!_sdk.IsDownloadReady(out var timeLeftMilliseconds))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var delay = Math.Clamp(timeLeftMilliseconds, 25, 250);
                Thread.Sleep(delay);
            }

            request.Diagnostic?.Invoke($"DownloadReady. Grab {width} x {height}, bin {request.HorizontalBin} x {request.VerticalBin}");
            var pixels = new ushort[width * height];
            var row = new ushort[width];

            for (var y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (y == 0 || y == height - 1 || y % 256 == 0)
                {
                    request.Diagnostic?.Invoke($"GrabRow {y + 1}/{height}, row width {width}");
                }

                _sdk.GrabRow(row);
                Array.Copy(row, 0, pixels, y * width, width);
            }

            return new FliCaptureResult(
                pixels,
                width,
                height,
                SerialNumber,
                ModelName,
                request.ExposureMilliseconds,
                request.HorizontalBin,
                request.VerticalBin,
                DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            CancelExposure();
            throw;
        }
    }

    private static FliReadoutArea BuildDriverImageArea(FliReadoutArea requestedArea, int horizontalBin, int verticalBin)
    {
        var safeHorizontalBin = Math.Max(horizontalBin, 1);
        var safeVerticalBin = Math.Max(verticalBin, 1);
        return new FliReadoutArea(
            requestedArea.UpperLeftX,
            requestedArea.UpperLeftY,
            requestedArea.UpperLeftX + requestedArea.Width / safeHorizontalBin,
            requestedArea.UpperLeftY + requestedArea.Height / safeVerticalBin);
    }

    public void CancelExposure()
    {
        _sdk.CancelExposure();
    }

    public void EnableBackgroundFlush(bool enabled)
    {
        _sdk.SetBackgroundFlush(enabled ? FliBackgroundFlush.Start : FliBackgroundFlush.Stop);
    }

    public void LockDevice()
    {
        _sdk.LockDevice();
    }

    public void UnlockDevice()
    {
        _sdk.UnlockDevice();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _sdk.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static string DescribeStatus(FliDeviceStatus status)
    {
        if (status.HasFlag(FliDeviceStatus.CameraStatusUnknown))
        {
            return "Unknown";
        }

        if (status.HasFlag(FliDeviceStatus.CameraStatusReadingCcd))
        {
            return "Reading";
        }

        if (status.HasFlag(FliDeviceStatus.CameraStatusExposing))
        {
            return "Exposing";
        }

        if (status.HasFlag(FliDeviceStatus.CameraStatusWaitingForTrigger))
        {
            return "WaitingForTrigger";
        }

        if (status == FliDeviceStatus.CameraStatusIdle)
        {
            return "Idle";
        }

        return status.ToString();
    }
}

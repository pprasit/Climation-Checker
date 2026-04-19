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

        _sdk.SetHorizontalBin(request.HorizontalBin);
        _sdk.SetVerticalBin(request.VerticalBin);
        _sdk.SetFrameType(request.FrameType);
        _sdk.SetImageArea(imageArea);
        _sdk.SetExposureTime((int)Math.Round(request.ExposureMilliseconds));
        _sdk.SetTdi(request.TdiRate);
        _sdk.SetFlushCount(request.FlushCount);
        _sdk.ExposeFrame();

        while (!_sdk.IsDownloadReady(out var timeLeftMilliseconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var delay = Math.Clamp(timeLeftMilliseconds, 25, 250);
            Thread.Sleep(delay);
        }

        var width = imageArea.Width / Math.Max(request.HorizontalBin, 1);
        var height = imageArea.Height / Math.Max(request.VerticalBin, 1);
        var pixels = new ushort[width * height];
        var row = new ushort[width];

        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

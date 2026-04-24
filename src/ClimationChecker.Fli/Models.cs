namespace ClimationChecker.Fli;

public sealed record FliCameraDescriptor(
    string FileName,
    string? ModelName,
    string SerialNumber);

public sealed record FliReadoutArea(
    int UpperLeftX,
    int UpperLeftY,
    int LowerRightX,
    int LowerRightY)
{
    public int Width => LowerRightX - UpperLeftX;
    public int Height => LowerRightY - UpperLeftY;
}

public sealed record FliCaptureRequest(
    double ExposureMilliseconds,
    int HorizontalBin = 1,
    int VerticalBin = 1,
    FliFrameType FrameType = FliFrameType.Normal,
    FliReadoutArea? ImageArea = null,
    int TdiRate = 0,
    int FlushCount = 0,
    Action<string>? Diagnostic = null);

public sealed record FliCaptureResult(
    ushort[] Pixels,
    int Width,
    int Height,
    string SerialNumber,
    string? ModelName,
    double ExposureMilliseconds,
    int HorizontalBin,
    int VerticalBin,
    DateTimeOffset CapturedAtUtc);

public sealed record FliReadoutDimensions(
    int Width,
    int HorizontalOffset,
    int HorizontalBin,
    int Height,
    int VerticalOffset,
    int VerticalBin);

public sealed record FliCameraStatus(
    string SerialNumber,
    string? ModelName,
    string DeviceStatus,
    double TemperatureCelsius,
    double CoolerPowerPercent,
    int CameraMode,
    string? CameraModeName,
    FliReadoutArea VisibleArea,
    bool CoolingEnabled,
    double? CoolingSetPointCelsius);

public sealed record FliRawFrameMetadata(
    string SerialNumber,
    string? ModelName,
    int Width,
    int Height,
    int BitDepth,
    double ExposureMilliseconds,
    int HorizontalBin,
    int VerticalBin,
    DateTimeOffset CapturedAtUtc,
    string PixelFormat,
    string RawFileName);

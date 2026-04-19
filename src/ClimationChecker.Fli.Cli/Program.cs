using System.Text.Json;
using ClimationChecker.Fli;

var arguments = args.ToList();
if (arguments.Count == 0)
{
    PrintUsage();
    return 1;
}

var service = new FliCameraService();
var command = arguments[0].ToLowerInvariant();

try
{
    switch (command)
    {
        case "list":
            WriteJson(service.ListCameras());
            return 0;

        case "status":
        {
            var serial = RequireOption(arguments, "--serial");
            using var camera = service.OpenBySerial(serial);
            WriteJson(camera.GetStatus());
            return 0;
        }

        case "capture":
        {
            var serial = RequireOption(arguments, "--serial");
            var output = RequireOption(arguments, "--output");
            var exposureMilliseconds = GetDouble(arguments, "--exposure-ms", 1000);
            var horizontalBin = GetInt(arguments, "--hbin", 1);
            var verticalBin = GetInt(arguments, "--vbin", 1);
            var frameType = GetEnum(arguments, "--frame-type", FliFrameType.Normal);
            var flushCount = GetInt(arguments, "--flush-count", 0);
            var tdiRate = GetInt(arguments, "--tdi-rate", 0);
            var imageArea = TryReadArea(arguments);

            using var camera = service.OpenBySerial(serial);
            camera.EnableBackgroundFlush(true);
            var capture = camera.Capture(
                new FliCaptureRequest(
                    exposureMilliseconds,
                    horizontalBin,
                    verticalBin,
                    frameType,
                    imageArea,
                    tdiRate,
                    flushCount));

            var metadata = FliRawWriter.Save(capture, output);
            WriteJson(metadata);
            return 0;
        }

        default:
            throw new ArgumentException($"Unknown command '{command}'.");
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
Usage:
  dotnet run --project src/ClimationChecker.Fli.Cli -- list
  dotnet run --project src/ClimationChecker.Fli.Cli -- status --serial <serial>
  dotnet run --project src/ClimationChecker.Fli.Cli -- capture --serial <serial> --output <path-stem> [--exposure-ms 1000] [--hbin 1] [--vbin 1] [--frame-type Normal|Dark] [--ulx N --uly N --lrx N --lry N]
""");
}

static string RequireOption(List<string> arguments, string name)
{
    var index = arguments.FindIndex(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
    if (index < 0 || index == arguments.Count - 1)
    {
        throw new ArgumentException($"Missing required option '{name}'.");
    }

    return arguments[index + 1];
}

static int GetInt(List<string> arguments, string name, int defaultValue)
{
    var value = TryGetOption(arguments, name);
    return value is null ? defaultValue : int.Parse(value);
}

static double GetDouble(List<string> arguments, string name, double defaultValue)
{
    var value = TryGetOption(arguments, name);
    return value is null ? defaultValue : double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}

static TEnum GetEnum<TEnum>(List<string> arguments, string name, TEnum defaultValue) where TEnum : struct
{
    var value = TryGetOption(arguments, name);
    return value is null ? defaultValue : Enum.Parse<TEnum>(value, ignoreCase: true);
}

static string? TryGetOption(List<string> arguments, string name)
{
    var index = arguments.FindIndex(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
    return index < 0 || index == arguments.Count - 1 ? null : arguments[index + 1];
}

static FliReadoutArea? TryReadArea(List<string> arguments)
{
    var ulx = TryGetOption(arguments, "--ulx");
    var uly = TryGetOption(arguments, "--uly");
    var lrx = TryGetOption(arguments, "--lrx");
    var lry = TryGetOption(arguments, "--lry");
    if (ulx is null || uly is null || lrx is null || lry is null)
    {
        return null;
    }

    return new FliReadoutArea(int.Parse(ulx), int.Parse(uly), int.Parse(lrx), int.Parse(lry));
}

static void WriteJson<T>(T value)
{
    Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions
    {
        WriteIndented = true,
    }));
}

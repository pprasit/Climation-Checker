using System.Text.Json;

namespace ClimationChecker.Fli;

public static class FliRawWriter
{
    public static FliRawFrameMetadata Save(FliCaptureResult capture, string outputStem)
    {
        var rawPath = Path.ChangeExtension(outputStem, ".raw");
        var metadataPath = Path.ChangeExtension(outputStem, ".json");
        var directory = Path.GetDirectoryName(rawPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using (var stream = File.Create(rawPath))
        using (var writer = new BinaryWriter(stream))
        {
            foreach (var value in capture.Pixels)
            {
                writer.Write(value);
            }
        }

        var metadata = new FliRawFrameMetadata(
            capture.SerialNumber,
            capture.ModelName,
            capture.Width,
            capture.Height,
            16,
            capture.ExposureMilliseconds,
            capture.HorizontalBin,
            capture.VerticalBin,
            capture.CapturedAtUtc,
            "Gray16LittleEndian",
            Path.GetFileName(rawPath));

        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions));
        return metadata;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };
}

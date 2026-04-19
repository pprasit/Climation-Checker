using System.Globalization;
using System.IO;
using System.Text;
using ClimationChecker.Fli;

namespace ClimationChecker.App;

internal static class FitsFileWriter
{
    public static void WriteGray16(string outputPath, IReadOnlyList<ushort> pixels, FliRawFrameMetadata metadata)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(outputPath);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        WriteHeader(writer, metadata);
        WritePixels(writer, pixels);
    }

    private static void WriteHeader(BinaryWriter writer, FliRawFrameMetadata metadata)
    {
        var cards = new List<string>
        {
            BuildCard("SIMPLE", "T", isString: false),
            BuildCard("BITPIX", "16", isString: false),
            BuildCard("NAXIS", "2", isString: false),
            BuildCard("NAXIS1", metadata.Width.ToString(CultureInfo.InvariantCulture), isString: false),
            BuildCard("NAXIS2", metadata.Height.ToString(CultureInfo.InvariantCulture), isString: false),
            BuildCard("BZERO", "32768", isString: false),
            BuildCard("BSCALE", "1", isString: false),
            BuildCard("EXPTIME", (metadata.ExposureMilliseconds / 1000.0).ToString("0.######", CultureInfo.InvariantCulture), isString: false),
            BuildCard("XBINNING", metadata.HorizontalBin.ToString(CultureInfo.InvariantCulture), isString: false),
            BuildCard("YBINNING", metadata.VerticalBin.ToString(CultureInfo.InvariantCulture), isString: false),
            BuildCard("DATE-OBS", metadata.CapturedAtUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture), isString: true),
            BuildCard("INSTRUME", metadata.ModelName ?? "FLI", isString: true),
            BuildCard("CAMSN", metadata.SerialNumber, isString: true),
            "END".PadRight(80, ' '),
        };

        var header = string.Concat(cards);
        var headerBytes = Encoding.ASCII.GetBytes(header);
        writer.Write(headerBytes);

        var padding = CalculatePadding(headerBytes.Length);
        if (padding > 0)
        {
            writer.Write(new byte[padding]);
        }
    }

    private static void WritePixels(BinaryWriter writer, IReadOnlyList<ushort> pixels)
    {
        foreach (var pixel in pixels)
        {
            short signedPixel = unchecked((short)(pixel - 32768));
            writer.Write((byte)((signedPixel >> 8) & 0xFF));
            writer.Write((byte)(signedPixel & 0xFF));
        }

        var dataPadding = CalculatePadding(pixels.Count * sizeof(ushort));
        if (dataPadding > 0)
        {
            writer.Write(new byte[dataPadding]);
        }
    }

    private static string BuildCard(string keyword, string value, bool isString)
    {
        var formattedValue = isString ? $"'{value.Replace("'", "''")}'" : value;
        return $"{keyword,-8}= {formattedValue}".PadRight(80, ' ');
    }

    private static int CalculatePadding(int currentLength)
    {
        var remainder = currentLength % 2880;
        return remainder == 0 ? 0 : 2880 - remainder;
    }
}

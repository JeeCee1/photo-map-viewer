using System.Globalization;
using System.Text;

namespace PhotoMap.Core;

/// <summary>Builds an ExifTool argument file that writes GPS into many photos in one run.</summary>
public static class ExifToolArgs
{
    /// <summary>Formats ExifTool can write GPS into safely.</summary>
    public static readonly HashSet<string> Writable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".heic", ".heif", ".png", ".tif", ".tiff", ".webp"
    };

    public static bool CanWrite(string path) => Writable.Contains(Path.GetExtension(path));

    /// <summary>
    /// One block per photo, separated by -execute. Keeps the file's modified date (-P)
    /// and replaces the file in place (no "_original" copies left behind).
    /// </summary>
    public static string Build(IEnumerable<(string FullPath, double Lat, double Lon)> photos)
    {
        var sb = new StringBuilder();
        foreach (var (path, lat, lon) in photos)
        {
            sb.Append("-charset\nfilename=utf8\n");
            sb.Append("-overwrite_original\n-P\n-m\n");
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"-GPSLatitude={Math.Abs(lat):0.000000}\n"));
            sb.Append(lat >= 0 ? "-GPSLatitudeRef=N\n" : "-GPSLatitudeRef=S\n");
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"-GPSLongitude={Math.Abs(lon):0.000000}\n"));
            sb.Append(lon >= 0 ? "-GPSLongitudeRef=E\n" : "-GPSLongitudeRef=W\n");
            sb.Append(path).Append('\n');
            sb.Append("-execute\n");
        }
        return sb.ToString();
    }
}

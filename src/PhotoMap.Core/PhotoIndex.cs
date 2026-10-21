using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoMap.Core;

public enum LocationSource { None = 0, Exif = 1, Guessed = 2, Manual = 3 }

public sealed class PhotoItem
{
    /// <summary>Path relative to the opened folder, with '\' or '/' as the OS uses.</summary>
    public string RelPath { get; set; } = "";
    public long Size { get; set; }
    public long ModifiedTicks { get; set; }
    public bool IsVideo { get; set; }

    public DateTime? Taken { get; set; }
    public int Orientation { get; set; } = 1;

    /// <summary>GPS as stored in the file.</summary>
    public double? ExifLat { get; set; }
    public double? ExifLon { get; set; }

    /// <summary>Set by hand (from the locations file, not cached in the index).</summary>
    [JsonIgnore] public double? ManualLat { get; set; }
    [JsonIgnore] public double? ManualLon { get; set; }

    /// <summary>Location used for display: set by hand, from EXIF, or guessed from neighbours.</summary>
    [JsonIgnore] public double? Lat { get; set; }
    [JsonIgnore] public double? Lon { get; set; }
    [JsonIgnore] public LocationSource Source { get; set; }
    /// <summary>For guessed locations: which photo it was borrowed from.</summary>
    [JsonIgnore] public string? GuessedFrom { get; set; }

    /// <summary>Country of the EXIF location (cached in the index).</summary>
    public string? ExifCountryId { get; set; }
    public string? ExifCountryName { get; set; }

    /// <summary>Country of the displayed location.</summary>
    [JsonIgnore] public string? CountryId { get; set; }
    [JsonIgnore] public string? CountryName { get; set; }

    [JsonIgnore] public string Folder => Path.GetDirectoryName(RelPath) ?? "";
    [JsonIgnore] public string FileName => Path.GetFileName(RelPath);
    [JsonIgnore] public bool HasLocation => Lat.HasValue && Lon.HasValue;
}

/// <summary>The scan result for one opened folder, cached on disk between sessions.</summary>
public sealed class PhotoIndex
{
    public int Version { get; set; } = CurrentVersion;
    public string Root { get; set; } = "";
    public DateTime ScannedAt { get; set; }
    public List<PhotoItem> Items { get; set; } = new();

    public const int CurrentVersion = 2;

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string CacheFileFor(string cacheDir, string root)
    {
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..16];
        return Path.Combine(cacheDir, $"index-{key}.json");
    }

    public static PhotoIndex? TryLoad(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            using var s = File.OpenRead(file);
            var idx = JsonSerializer.Deserialize<PhotoIndex>(s, JsonOpts);
            return idx?.Version == CurrentVersion ? idx : null;
        }
        catch { return null; }
    }

    public void Save(string file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            using (var s = File.Create(tmp)) JsonSerializer.Serialize(s, this, JsonOpts);
            File.Move(tmp, file, true);
        }
        catch { /* cache is best-effort */ }
    }
}

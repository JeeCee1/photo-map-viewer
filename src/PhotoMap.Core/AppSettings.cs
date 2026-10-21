using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoMap.Core;

public enum ZoomMode { Auto, AlwaysWorld, AlwaysEurope, AlwaysNetherlands, Custom }
public enum NoLocationMode { KeepLastSpot, ShowWorld, HideMarker }

public sealed class AppSettings
{
    // Source
    public string? LastFolder { get; set; }
    public string? LastPhoto { get; set; }
    public PlayOrder Order { get; set; } = PlayOrder.ByDate;

    // Slideshow
    public double SecondsPerPhoto { get; set; } = 6;
    public bool ShowCaption { get; set; } = true;
    public bool CaptionPlace { get; set; } = true;
    public bool CaptionDate { get; set; } = true;
    public bool CaptionFileName { get; set; } = false;
    public double MapWidthPercent { get; set; } = 38;

    // Map
    public ZoomMode Zoom { get; set; } = ZoomMode.Auto;
    public int CustomZoomLevel { get; set; } = 12;
    public bool ShowTrail { get; set; } = true;
    public int TrailLength { get; set; } = 40;
    public int RandomTrailJumps { get; set; } = 4;
    public bool TownNamesOnline { get; set; } = true;
    public bool SuggestFromNames { get; set; } = true;
    public bool AnimateMap { get; set; } = true;

    // Missing GPS
    public bool GuessLocations { get; set; } = true;
    public double GuessWindowHours { get; set; } = 2;
    public bool ShowGuessed { get; set; } = true;
    public NoLocationMode NoLocation { get; set; } = NoLocationMode.KeepLastSpot;

    // Videos (playback arrives in the next version)
    public bool ShowVideos { get; set; } = false;

    // Window
    public bool StartFullscreen { get; set; } = false;

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoMapViewer");

    static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { }
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts)!;
}

using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace PhotoMapViewer;

/// <summary>
/// Town names via OpenStreetMap's Nominatim service (online, optional).
/// Results are cached on disk (~1 km grid), and requests are kept to at most
/// one per second, as Nominatim's usage policy asks.
/// </summary>
public sealed class Geocoder
{
    readonly HttpClient _http;
    readonly Dictionary<string, string> _cache;
    readonly string _cacheFile;
    readonly SemaphoreSlim _gate = new(1, 1);
    DateTime _lastRequest = DateTime.MinValue;
    DateTime _pauseUntil = DateTime.MinValue;
    bool _dirty;

    public Geocoder(string cacheFolder)
    {
        _cacheFile = Path.Combine(cacheFolder, "places-cache.json");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PhotoMapViewer/0.1 (personal photo slideshow)");
        _cache = LoadCache();
    }

    static string Key(double lat, double lon) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(lat, 2):0.00},{Math.Round(lon, 2):0.00}");

    public string? Cached(double lat, double lon) =>
        _cache.TryGetValue(Key(lat, lon), out var name) && name.Length > 0 ? name : null;

    public async Task<string?> GetTownAsync(double lat, double lon, CancellationToken ct)
    {
        var key = Key(lat, lon);
        if (_cache.TryGetValue(key, out var hit)) return hit.Length > 0 ? hit : null;
        if (DateTime.UtcNow < _pauseUntil) return null;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out hit)) return hit.Length > 0 ? hit : null;
            var wait = _lastRequest.AddMilliseconds(1100) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            ct.ThrowIfCancellationRequested();
            _lastRequest = DateTime.UtcNow;

            string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            var url = string.Create(CultureInfo.InvariantCulture,
                $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&zoom=10&addressdetails=1&lat={lat:0.#####}&lon={lon:0.#####}&accept-language={lang},en");

            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _pauseUntil = DateTime.UtcNow.AddMinutes(2); // back off politely
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            string name = "";
            if (doc.RootElement.TryGetProperty("address", out var addr))
            {
                foreach (var field in new[] { "city", "town", "village", "municipality", "hamlet", "suburb", "county", "state" })
                {
                    if (addr.TryGetProperty(field, out var v) && v.GetString() is { Length: > 0 } s) { name = s; break; }
                }
            }
            _cache[key] = name;
            _dirty = true;
            return name.Length > 0 ? name : null;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            _pauseUntil = DateTime.UtcNow.AddSeconds(30); // offline or blocked: try again later
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    Dictionary<string, string> LoadCache()
    {
        try
        {
            if (File.Exists(_cacheFile))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_cacheFile)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        if (!_dirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            File.WriteAllText(_cacheFile, JsonSerializer.Serialize(_cache));
            _dirty = false;
        }
        catch { }
    }
}

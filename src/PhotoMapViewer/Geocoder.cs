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

    public sealed record Place(double Lat, double Lon, int Zoom, string Label);

    /// <summary>Find a place by name ("Kreta", "Breasta"). Cached; null when nothing is found.</summary>
    public async Task<Place?> SearchAsync(string query, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length < 2) return null;
        var key = "q:" + query.ToLowerInvariant();
        if (_cache.TryGetValue(key, out var hit)) return ParsePlace(hit);
        if (DateTime.UtcNow < _pauseUntil) return null;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out hit)) return ParsePlace(hit);
            var wait = _lastRequest.AddMilliseconds(1100) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            ct.ThrowIfCancellationRequested();
            _lastRequest = DateTime.UtcNow;

            string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            var url = $"https://nominatim.openstreetmap.org/search?format=jsonv2&limit=1&q={Uri.EscapeDataString(query)}&accept-language={lang},en";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _pauseUntil = DateTime.UtcNow.AddMinutes(2);
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            string value = "";
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var r = doc.RootElement[0];
                double lat = double.Parse(r.GetProperty("lat").GetString()!, CultureInfo.InvariantCulture);
                double lon = double.Parse(r.GetProperty("lon").GetString()!, CultureInfo.InvariantCulture);
                int rank = r.TryGetProperty("place_rank", out var pr) && pr.TryGetInt32(out var n) ? n : 16;
                int zoom = rank <= 4 ? 5 : rank <= 8 ? 7 : rank <= 12 ? 9 : rank <= 16 ? 12 : rank <= 20 ? 14 : 16;
                string label = r.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? query : query;
                value = string.Create(CultureInfo.InvariantCulture, $"{lat}|{lon}|{zoom}|{label}");
            }
            _cache[key] = value;
            _dirty = true;
            return ParsePlace(value);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            _pauseUntil = DateTime.UtcNow.AddSeconds(30);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    static Place? ParsePlace(string s)
    {
        var p = s.Split('|', 4);
        if (p.Length < 4) return null;
        return new Place(double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture),
                         int.Parse(p[2], CultureInfo.InvariantCulture), p[3]);
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

namespace PhotoMap.Core;

/// <summary>
/// Fills in a location for photos without GPS, borrowed from the photo
/// taken closest in time (within a window). Photos in the same folder are preferred.
/// Runs once after scanning, so sequential and random playback behave the same.
/// </summary>
public static class LocationGuesser
{
    public static int Apply(IList<PhotoItem> items, bool enabled, TimeSpan window)
    {
        // Start from what the user set, else what the files say.
        var countries = new Dictionary<(double, double), (string Id, string Name)?>();
        foreach (var it in items)
        {
            it.GuessedFrom = null;
            if (it.ManualLat.HasValue && it.ManualLon.HasValue)
            {
                it.Lat = it.ManualLat; it.Lon = it.ManualLon;
                it.Source = LocationSource.Manual;
                var key = (Math.Round(it.Lat.Value, 3), Math.Round(it.Lon.Value, 3));
                if (!countries.TryGetValue(key, out var c))
                    countries[key] = c = Regions.CountryNear(it.Lat.Value, it.Lon.Value);
                it.CountryId = c?.Id; it.CountryName = c?.Name;
            }
            else if (it.ExifLat.HasValue && it.ExifLon.HasValue)
            {
                it.Lat = it.ExifLat; it.Lon = it.ExifLon;
                it.Source = LocationSource.Exif;
                it.CountryId = it.ExifCountryId; it.CountryName = it.ExifCountryName;
            }
            else
            {
                it.Lat = it.Lon = null;
                it.Source = LocationSource.None;
                it.CountryId = it.CountryName = null;
            }
        }
        if (!enabled) return 0;

        var known = items.Where(i => i.Source is LocationSource.Exif or LocationSource.Manual && i.Taken.HasValue)
                         .OrderBy(i => i.Taken!.Value).ToList();
        if (known.Count == 0) return 0;

        var knownTimes = known.Select(i => i.Taken!.Value.Ticks).ToArray();
        var byFolder = known.GroupBy(i => i.Folder, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var folderTimes = byFolder.ToDictionary(kv => kv.Key, kv => kv.Value.Select(i => i.Taken!.Value.Ticks).ToArray(),
                                                StringComparer.OrdinalIgnoreCase);

        int guessed = 0;
        foreach (var it in items)
        {
            if (it.Source != LocationSource.None || !it.Taken.HasValue) continue;
            long t = it.Taken.Value.Ticks;

            PhotoItem? source = null;
            if (byFolder.TryGetValue(it.Folder, out var folderList))
                source = Nearest(folderList, folderTimes[it.Folder], t, window);
            source ??= Nearest(known, knownTimes, t, window);
            if (source == null) continue;

            it.Lat = source.Lat; it.Lon = source.Lon;
            it.CountryId = source.CountryId; it.CountryName = source.CountryName;
            it.Source = LocationSource.Guessed;
            it.GuessedFrom = source.RelPath;
            guessed++;
        }
        return guessed;
    }

    static PhotoItem? Nearest(List<PhotoItem> list, long[] times, long t, TimeSpan window)
    {
        int i = Array.BinarySearch(times, t);
        if (i < 0) i = ~i;
        PhotoItem? best = null;
        long bestDiff = long.MaxValue;
        foreach (int j in new[] { i - 1, i })
        {
            if (j < 0 || j >= times.Length) continue;
            long diff = Math.Abs(times[j] - t);
            if (diff < bestDiff) { bestDiff = diff; best = list[j]; }
        }
        return bestDiff <= window.Ticks ? best : null;
    }
}

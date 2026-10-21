using System.Globalization;
using System.Reflection;

namespace PhotoMap.Core;

public enum MapView { Netherlands, Europe, World }

/// <summary>
/// Offline country lookup (Natural Earth 1:50m borders, bundled) and the
/// NL / Europe / World view choice.
/// </summary>
public static class Regions
{
    public const string NetherlandsId = "528";

    /// <summary>The Europe view: a photo inside this box gets the Europe view.</summary>
    public static double EuropeSouth = 34, EuropeNorth = 72, EuropeWest = -25, EuropeEast = 45;

    sealed class Ring
    {
        public double[] Lon = Array.Empty<double>();
        public double[] Lat = Array.Empty<double>();
        public double MinLat, MaxLat, MinLon, MaxLon;
        public double Area => (MaxLat - MinLat) * (MaxLon - MinLon);
    }

    sealed class Country
    {
        public string Id = "";
        public string Name = "";
        public List<Ring> Rings = new();
    }

    static readonly Lazy<List<Country>> _countries = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    static List<Country> Load()
    {
        var list = new List<Country>();
        using var s = typeof(Regions).Assembly.GetManifestResourceStream("PhotoMap.Core.countries.txt");
        if (s == null) return list;
        using var r = new StreamReader(s);
        Country? cur = null;
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            if (line == "#") { cur = null; continue; }
            if (cur == null)
            {
                int bar = line.IndexOf('|');
                cur = new Country { Id = line[..bar], Name = line[(bar + 1)..] };
                list.Add(cur);
                continue;
            }
            var pts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var ring = new Ring { Lon = new double[pts.Length], Lat = new double[pts.Length] };
            ring.MinLat = ring.MinLon = double.MaxValue;
            ring.MaxLat = ring.MaxLon = double.MinValue;
            for (int i = 0; i < pts.Length; i++)
            {
                int c = pts[i].IndexOf(',');
                double lon = double.Parse(pts[i].AsSpan(0, c), CultureInfo.InvariantCulture);
                double lat = double.Parse(pts[i].AsSpan(c + 1), CultureInfo.InvariantCulture);
                ring.Lon[i] = lon; ring.Lat[i] = lat;
                if (lat < ring.MinLat) ring.MinLat = lat;
                if (lat > ring.MaxLat) ring.MaxLat = lat;
                if (lon < ring.MinLon) ring.MinLon = lon;
                if (lon > ring.MaxLon) ring.MaxLon = lon;
            }
            cur.Rings.Add(ring);
        }
        return list;
    }

    /// <summary>Country (id, English name) at a point, or null if at sea / unknown.</summary>
    public static (string Id, string Name)? CountryAt(double lat, double lon)
    {
        Country? best = null;
        double bestArea = double.MaxValue;
        foreach (var c in _countries.Value)
        {
            foreach (var ring in c.Rings)
            {
                if (lat < ring.MinLat || lat > ring.MaxLat || lon < ring.MinLon || lon > ring.MaxLon) continue;
                // Holes aren't stored, so an enclave also sits inside its neighbour: take the smallest match.
                if (ring.Area < bestArea && Inside(ring, lat, lon))
                {
                    best = c;
                    bestArea = ring.Area;
                }
            }
        }
        return best == null ? null : (best.Id, best.Name);
    }

    /// <summary>
    /// A point just off the coast (beach, ferry, harbour) falls outside the borders.
    /// Retry a few nearby points (~2 km) before giving up.
    /// </summary>
    public static (string Id, string Name)? CountryNear(double lat, double lon)
    {
        var hit = CountryAt(lat, lon);
        if (hit != null) return hit;
        const double d = 0.02;
        foreach (var (dy, dx) in new[] { (d, 0.0), (-d, 0.0), (0.0, d), (0.0, -d), (d, d), (-d, -d), (d, -d), (-d, d) })
        {
            hit = CountryAt(lat + dy, lon + dx);
            if (hit != null) return hit;
        }
        return null;
    }

    static bool Inside(Ring r, double lat, double lon)
    {
        bool inside = false;
        int n = r.Lat.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if ((r.Lat[i] > lat) != (r.Lat[j] > lat) &&
                lon < (r.Lon[j] - r.Lon[i]) * (lat - r.Lat[i]) / (r.Lat[j] - r.Lat[i]) + r.Lon[i])
                inside = !inside;
        }
        return inside;
    }

    public static bool InEuropeView(double lat, double lon) =>
        lat >= EuropeSouth && lat <= EuropeNorth && lon >= EuropeWest && lon <= EuropeEast;

    /// <summary>Auto zoom rule: in NL → NL, in the Europe view → Europe, else World.</summary>
    public static MapView AutoView(double lat, double lon, string? countryId)
    {
        if (countryId == NetherlandsId) return MapView.Netherlands;
        if (InEuropeView(lat, lon)) return MapView.Europe;
        return MapView.World;
    }
}

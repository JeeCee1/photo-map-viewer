// Small self-check for the core logic. Usage: CoreTests <folder with sample photos>
// Exit code 0 = all checks passed.
using PhotoMap.Core;

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}

// --- Regions ---
Check(Regions.CountryAt(53.0329, 5.6589)?.Id == Regions.NetherlandsId, "Sneek is in the Netherlands");
Check(Regions.CountryAt(51.85, 5.86)?.Id == Regions.NetherlandsId, "Nijmegen is in the Netherlands");
Check(Regions.CountryAt(50.776, 6.083)?.Name == "Germany", "Aachen is in Germany (not NL)");
Check(Regions.CountryAt(44.30, 23.68)?.Name == "Romania", "Breasta is in Romania");
Check(Regions.CountryAt(41.9029, 12.4534) != null, "Vatican/Rome resolves to a country");
Check(Regions.CountryNear(53.40, 5.30)?.Id == Regions.NetherlandsId, "Terschelling coast resolves to NL");
Check(Regions.AutoView(53.03, 5.66, "528") == MapView.Netherlands, "Auto view: NL");
Check(Regions.AutoView(44.3, 23.68, "642") == MapView.Europe, "Auto view: Romania -> Europe");
Check(Regions.AutoView(41.01, 28.98, "792") == MapView.Europe, "Auto view: Istanbul -> Europe");
Check(Regions.AutoView(28.29, -16.63, "724") == MapView.World, "Auto view: Tenerife -> World");
Check(Regions.AutoView(40.71, -74.0, "840") == MapView.World, "Auto view: New York -> World");

// --- Scan + EXIF ---
if (args.Length > 0)
{
    var root = args[0];
    var idx = Scanner.Scan(root, null, null, CancellationToken.None);
    PhotoItem Get(string name) => idx.Items.Single(i => i.FileName == name);

    Check(idx.Items.Count == 8, $"Found 8 media files (got {idx.Items.Count})");
    var sneek = Get("sneek.jpg");
    Check(Math.Abs(sneek.ExifLat!.Value - 53.0329) < 0.0002 && Math.Abs(sneek.ExifLon!.Value - 5.6589) < 0.0002, "JPEG GPS read");
    Check(sneek.Taken == new DateTime(2024, 8, 14, 10, 0, 0), "JPEG date read");
    Check(sneek.Orientation == 6, "JPEG orientation read");
    Check(sneek.CountryName == "Netherlands", "JPEG country lookup");
    Check(Get("nyc.png").ExifLon is < -74 and > -74.01, "PNG GPS read (west longitude)");
    Check(Get("reykjavik.heic").ExifLat is > 64.14 and < 64.15, "HEIC GPS read");
    Check(Get("reykjavik.heic").CountryName == "Iceland", "HEIC country lookup");
    Check(Get("plain.jpg").Taken == null && Get("plain.jpg").ExifLat == null, "JPEG without EXIF is fine");
    Check(Get("clip.mp4").IsVideo, "Video detected");

    int n = LocationGuesser.Apply(idx.Items, true, TimeSpan.FromHours(2));
    var same = Get("nogps_same_day.jpg");
    Check(same.Source == LocationSource.Guessed && same.CountryName == "Netherlands", "Guess within 2h from same folder");
    Check(Get("nogps_next_day.jpg").Source == LocationSource.None, "No guess beyond the window");
    Check(n == 1, $"Exactly one guess (got {n})");

    LocationGuesser.Apply(idx.Items, false, TimeSpan.FromHours(2));
    Check(Get("nogps_same_day.jpg").Source == LocationSource.None, "Guessing can be switched off");

    // Cache round-trip: unchanged files are reused, not re-read.
    var cacheFile = Path.Combine(Path.GetTempPath(), "pmv-test-index.json");
    idx.Save(cacheFile);
    var loaded = PhotoIndex.TryLoad(cacheFile);
    Check(loaded?.Items.Count == 8, "Index cache saves and loads");
    var again = Scanner.Scan(root, loaded, null, CancellationToken.None);
    Check(again.Items.Single(i => i.FileName == "sneek.jpg").CountryName == "Netherlands", "Rescan with cache keeps data");

    // Playlist
    var photos = idx.Items.Where(i => !i.IsVideo).ToList();
    var pl = new Playlist(photos, PlayOrder.ByDate);
    var first = pl.Start();
    Check(first?.FileName == "breasta.jpg", $"By date starts with oldest (got {first?.FileName})");
    var second = pl.Next();
    Check(pl.Previous() == first && pl.Next() == second, "Back and forward in sequence");

    var rnd = new Playlist(photos, PlayOrder.RandomAllFolders);
    rnd.Start();
    var seen = new HashSet<PhotoItem> { rnd.Current! };
    for (int i = 0; i < photos.Count - 1; i++) seen.Add(rnd.Next()!);
    Check(seen.Count >= photos.Count - 1, "Random shows (nearly) everything before repeating");

    var pk = new Playlist(photos, PlayOrder.RandomAllFolders);
    pk.Start();
    var peeked = pk.PeekNext();
    Check(pk.PeekNext() == peeked && pk.Next() == peeked, "PeekNext in random mode matches Next");

    var rf = new Playlist(photos, PlayOrder.RandomThisFolder);
    rf.Start(Path.Combine("2024 Friesland", "sneek.jpg"));
    bool allSameFolder = Enumerable.Range(0, 10).Select(_ => rf.Next()!).All(p => p.Folder == "2024 Friesland");
    Check(allSameFolder, "Random in this folder stays in the folder");
}

Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) failed.");
return failures == 0 ? 0 : 1;

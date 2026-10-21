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

// --- Name hints ---
Check(NameHints.FromFolder("2012 Kreta") == "Kreta", "Folder '2012 Kreta' -> Kreta");
Check(NameHints.FromFolder("Vakantie Breasta juli 2007") == "Breasta", "Folder with holiday/month words -> Breasta");
Check(NameHints.FromFolder("2009-08 Zomervakantie") == null, "Folder without a place -> nothing");
Check(NameHints.FromFolder("La Palma 2015") == "La Palma", "Keeps 'La Palma'");
Check(NameHints.FromFolder("USA 2001") == "USA", "Keeps short names like USA");
Check(NameHints.FromFile("IMG_1234.JPG") == null, "Camera file name -> nothing");
Check(NameHints.FromFile("DSCN0042.jpg") == null, "DSCN file name -> nothing");
Check(NameHints.FromFile("20080714_153012.jpg") == null, "Date file name -> nothing");
Check(NameHints.FromFile("WhatsApp Image 2021-05-01 at 12.00.03.jpeg") == null, "WhatsApp file name -> nothing");
Check(NameHints.FromFile("strand Texel 2.jpg") == "strand Texel", "Descriptive file name -> 'strand Texel'");
var cands = NameHints.Candidates("Kreta met Mark");
Check(cands.SequenceEqual(new[] { "Kreta met Mark", "Kreta Mark", "Kreta", "Mark" }), $"Search candidates ({string.Join(" | ", cands)})");

// --- ExifTool arguments ---
var args1 = ExifToolArgs.Build(new[] { (@"C:\Foto's\a.jpg", 44.3, -23.68) });
Check(args1.Contains("-GPSLatitude=44.300000\n-GPSLatitudeRef=N\n") && args1.Contains("-GPSLongitude=23.680000\n-GPSLongitudeRef=W\n")
      && args1.Contains(@"C:\Foto's\a.jpg") && args1.TrimEnd().EndsWith("-execute") && args1.Contains("-P\n"), "ExifTool argument file");

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
    Check(sneek.ExifCountryName == "Netherlands", "JPEG country lookup");
    Check(Get("nyc.png").ExifLon is < -74 and > -74.01, "PNG GPS read (west longitude)");
    Check(Get("reykjavik.heic").ExifLat is > 64.14 and < 64.15, "HEIC GPS read");
    Check(Get("reykjavik.heic").ExifCountryName == "Iceland", "HEIC country lookup");
    Check(Get("plain.jpg").Taken == null && Get("plain.jpg").ExifLat == null, "JPEG without EXIF is fine");
    Check(Get("clip.mp4").IsVideo, "Video detected");

    int n = LocationGuesser.Apply(idx.Items, true, TimeSpan.FromHours(2));
    var same = Get("nogps_same_day.jpg");
    Check(same.Source == LocationSource.Guessed && same.CountryName == "Netherlands", "Guess within 2h from same folder");
    Check(Get("nogps_next_day.jpg").Source == LocationSource.None, "No guess beyond the window");
    Check(n == 1, $"Exactly one guess (got {n})");

    LocationGuesser.Apply(idx.Items, false, TimeSpan.FromHours(2));
    Check(Get("nogps_same_day.jpg").Source == LocationSource.None, "Guessing can be switched off");

    // --- Manual locations ---
    var tmpRoot = Path.Combine(Path.GetTempPath(), "pmv-store-" + Guid.NewGuid().ToString("N")[..8]);
    var master = Path.Combine(tmpRoot, "appdata");
    var coll = Path.Combine(tmpRoot, "Photos");
    var sub = Path.Combine(coll, "2007 Romania");
    Directory.CreateDirectory(sub);

    var st = LocationStore.Open(sub, master);
    Check(st.StoreDir == Path.GetFullPath(sub), "No file yet: store lives in the opened folder");
    var nogps = Get("nogps_next_day.jpg");
    var fake = new PhotoItem { RelPath = "breasta2.jpg", Size = 123, Taken = new DateTime(2007, 7, 2, 16, 0, 0) };
    st.Set(new[] { fake }, 44.3, 23.68);
    Check(st.Save() && File.Exists(Path.Combine(sub, LocationStore.FileName)) && File.Exists(st.MasterFile), "Saves collection file and backup copy");

    // A file higher up wins.
    var top = LocationStore.Open(coll, master);
    top.Save();
    var again2 = LocationStore.Open(sub, master);
    Check(again2.StoreDir == Path.GetFullPath(coll), "Highest folder with a locations file wins");
    Check(again2.KeyFor("breasta2.jpg") == "2007 Romania/breasta2.jpg", "Paths are stored relative to the locations file");

    // Back in the subfolder's own store: the location is there.
    var check = new PhotoItem { RelPath = "breasta2.jpg", Size = 123, Taken = fake.Taken };
    LocationStore.Open(sub, master); // (top file now wins, so read the subfolder file directly via a fresh copy below)
    File.Delete(Path.Combine(coll, LocationStore.FileName));
    var reopened = LocationStore.Open(sub, master);
    reopened.ApplyTo(new[] { check });
    Check(check.ManualLat == 44.3 && check.ManualLon == 23.68, "Manual location loads back");

    // Renamed file is still recognised by name + size + date.
    var moved = new PhotoItem { RelPath = Path.Combine("other", "breasta2.jpg"), Size = 123, Taken = fake.Taken };
    reopened.ApplyTo(new[] { moved });
    Check(moved.ManualLat == 44.3, "Moved photo recognised by name and size");

    // Undo and remove.
    var undo = reopened.Set(new[] { check }, 10, 10);
    reopened.Restore(undo);
    reopened.ApplyTo(new[] { check });
    Check(check.ManualLat == 44.3, "Undo restores the previous location");
    reopened.Remove(new[] { check });
    reopened.ApplyTo(new[] { check });
    Check(check.ManualLat == null, "Remove clears the manual location");
    reopened.Save();

    // Backup: collection file deleted -> restore offered.
    var setOnce = LocationStore.Open(sub, master);
    setOnce.Set(new[] { check }, 1, 2);
    setOnce.Save();
    File.Delete(Path.Combine(sub, LocationStore.FileName));
    var lost = LocationStore.Open(sub, master);
    Check(lost.RestorableFromBackup == 1, $"Missing collection file: backup offers 1 location (got {lost.RestorableFromBackup})");
    lost.AcceptBackup();
    Check(File.Exists(Path.Combine(sub, LocationStore.FileName)), "Accepting the backup writes the collection file again");

    // Merge: newest wins per photo.
    var a = LocationStore.Open(sub, master);
    a.Set(new[] { check }, 5, 5); a.Save();
    var masterJson = File.ReadAllText(a.MasterFile);
    var b = LocationStore.Open(sub, master);
    b.Set(new[] { check }, 6, 6); b.Save();                  // newer, in both files
    File.WriteAllText(b.MasterFile, masterJson);              // backup now holds the older value
    var m = LocationStore.Open(sub, master);
    m.ApplyTo(new[] { check });
    Check(check.ManualLat == 6, "Merge keeps the newest location");

    // Folder states
    m.SetFolderState("", FolderState.Skip);
    m.SetFolderState("x y", FolderState.Mixed);
    m.Save();
    var fs = LocationStore.Open(sub, master);
    Check(fs.GetFolderState("") == FolderState.Skip && fs.GetFolderState("x y") == FolderState.Mixed, "Folder skip/mixed marks are saved");

    // Manual locations feed the guesser and win over EXIF.
    var sneekItem = Get("sneek.jpg");
    sneekItem.ManualLat = 44.3; sneekItem.ManualLon = 23.68;
    LocationGuesser.Apply(idx.Items, true, TimeSpan.FromHours(2));
    Check(sneekItem.Source == LocationSource.Manual && sneekItem.CountryName == "Romania", "Manual location overrides EXIF");
    Check(Get("nogps_same_day.jpg").CountryName == "Romania", "Guessing borrows from a manual location");
    sneekItem.ManualLat = sneekItem.ManualLon = null;
    LocationGuesser.Apply(idx.Items, true, TimeSpan.FromHours(2));
    Check(sneekItem.Source == LocationSource.Exif && sneekItem.CountryName == "Netherlands", "Without manual: back to EXIF");
    try { Directory.Delete(tmpRoot, true); } catch { }

    // --- Writing GPS with ExifTool (only where ExifTool is available, e.g. the Windows build) ---
    var exiftool = Environment.GetEnvironmentVariable("EXIFTOOL");
    if (!string.IsNullOrEmpty(exiftool) && File.Exists(exiftool))
    {
        var wdir = Path.Combine(Path.GetTempPath(), "pmv-write-" + Guid.NewGuid().ToString("N")[..8], "Foto's ș");
        Directory.CreateDirectory(wdir);
        var targets = new[] { "plain.jpg", "nyc.png", "reykjavik.heic" }
            .Select(n => idx.Items.Single(i => i.FileName == n))
            .Select(i =>
            {
                var dst = Path.Combine(wdir, i.FileName);
                File.Copy(Path.Combine(root, i.RelPath), dst);
                File.SetLastWriteTimeUtc(dst, new DateTime(2010, 1, 2, 3, 4, 5, DateTimeKind.Utc));
                return dst;
            }).ToList();
        var argFile = Path.Combine(wdir, "..", "args.txt");
        File.WriteAllText(argFile, ExifToolArgs.Build(targets.Select(t => (t, 45.7489, 21.2087))), new System.Text.UTF8Encoding(false));
        var psi = new System.Diagnostics.ProcessStartInfo(exiftool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-@"); psi.ArgumentList.Add(argFile);
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        Console.WriteLine(stdout.Trim());
        if (stderr.Length > 0) Console.WriteLine("stderr: " + stderr.Trim());
        foreach (var t in targets)
        {
            var e = ExifReader.Read(t);
            bool ok = e?.Latitude is > 45.748 and < 45.750 && e.Longitude is > 21.208 and < 21.209;
            Check(ok, $"ExifTool wrote GPS into {Path.GetFileName(t)} and it reads back");
            Check(File.GetLastWriteTimeUtc(t) == new DateTime(2010, 1, 2, 3, 4, 5, DateTimeKind.Utc), $"File date kept for {Path.GetFileName(t)}");
        }
        Check(!Directory.EnumerateFiles(wdir, "*_original").Any(), "No _original backup files left behind");
    }
    else Console.WriteLine("(ExifTool not available here: write test skipped)");

    // Cache round-trip: unchanged files are reused, not re-read.
    var cacheFile = Path.Combine(Path.GetTempPath(), "pmv-test-index.json");
    idx.Save(cacheFile);
    var loaded = PhotoIndex.TryLoad(cacheFile);
    Check(loaded?.Items.Count == 8, "Index cache saves and loads");
    var again = Scanner.Scan(root, loaded, null, CancellationToken.None);
    Check(again.Items.Single(i => i.FileName == "sneek.jpg").ExifCountryName == "Netherlands", "Rescan with cache keeps data");

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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoMap.Core;

/// <summary>A location the user set by hand (or removed: <see cref="Removed"/>).</summary>
public sealed class ManualLocation
{
    /// <summary>Path relative to the folder holding the locations file, '/' separated.</summary>
    public string Path { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    /// <summary>When it was set (UTC). On merge, the newest wins.</summary>
    public DateTime SetAt { get; set; }
    /// <summary>True = the user removed their location again (kept so merges don't bring it back).</summary>
    public bool Removed { get; set; }

    // Fallback identity, for photos that were renamed or moved.
    public string? Name { get; set; }
    public long Size { get; set; }
    public DateTime? Taken { get; set; }

    public ManualLocation Copy() => (ManualLocation)MemberwiseClone();
}

public enum FolderState { None, Skip, Mixed }

public sealed class FolderMark
{
    public string Path { get; set; } = "";
    public FolderState State { get; set; }
    public DateTime SetAt { get; set; }
}

/// <summary>
/// Locations set by hand. Stored in "PhotoMap locations.json" in the collection
/// (the highest folder, from the opened one upwards, that has such a file), with a
/// mirror copy in the app's own folder as a backup.
/// </summary>
public sealed class LocationStore
{
    public const string FileName = "PhotoMap locations.json";

    sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public string App { get; set; } = "Photo Map Viewer";
        public List<ManualLocation> Locations { get; set; } = new();
        public List<FolderMark> Folders { get; set; } = new();
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    readonly Dictionary<string, ManualLocation> _byPath = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, FolderMark> _folders = new(StringComparer.OrdinalIgnoreCase);
    readonly string _openedRoot;

    /// <summary>Folder that holds (or will hold) the collection's locations file.</summary>
    public string StoreDir { get; }
    public string CollectionFile => System.IO.Path.Combine(StoreDir, FileName);
    public string MasterFile { get; }

    /// <summary>The collection file is missing but the backup has this many locations.</summary>
    public int RestorableFromBackup { get; private set; }
    /// <summary>False once writing into the collection failed (read-only disk/share).</summary>
    public bool CollectionWritable { get; private set; } = true;

    FileModel? _pendingBackup;

    LocationStore(string openedRoot, string storeDir, string masterDir)
    {
        _openedRoot = System.IO.Path.GetFullPath(openedRoot);
        StoreDir = storeDir;
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(storeDir.ToLowerInvariant())))[..16];
        MasterFile = System.IO.Path.Combine(masterDir, "locations", $"locations-{key}.json");
    }

    /// <summary>Find the collection's file (highest folder wins), load it and merge the backup copy.</summary>
    public static LocationStore Open(string openedRoot, string masterDir)
    {
        var root = System.IO.Path.GetFullPath(openedRoot);
        string storeDir = root;
        try
        {
            for (var d = new DirectoryInfo(root); d != null; d = d.Parent)
                if (File.Exists(System.IO.Path.Combine(d.FullName, FileName))) storeDir = d.FullName;
        }
        catch { /* no permission to look further up: fine */ }

        var store = new LocationStore(root, storeDir, masterDir);
        var collection = Read(store.CollectionFile);
        var master = Read(store.MasterFile);

        if (collection != null) store.Merge(collection);
        if (master != null)
        {
            if (collection == null && master.Locations.Any(l => !l.Removed))
            {
                // Don't merge silently: ask first (see AcceptBackup / DeclineBackup).
                store._pendingBackup = master;
                store.RestorableFromBackup = master.Locations.Count(l => !l.Removed);
            }
            else store.Merge(master);
        }
        return store;
    }

    public void AcceptBackup()
    {
        if (_pendingBackup != null) Merge(_pendingBackup);
        _pendingBackup = null;
        RestorableFromBackup = 0;
        Save();
    }

    /// <summary>Start fresh; the old backup is kept aside under another name.</summary>
    public void DeclineBackup()
    {
        if (_pendingBackup == null) return;
        try
        {
            var aside = MasterFile.Replace(".json", $"-set-aside-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(MasterFile, aside, true);
        }
        catch { }
        _pendingBackup = null;
        RestorableFromBackup = 0;
    }

    static FileModel? Read(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize<FileModel>(File.ReadAllText(file), JsonOpts);
        }
        catch { return null; }
    }

    void Merge(FileModel m)
    {
        foreach (var l in m.Locations)
        {
            if (string.IsNullOrEmpty(l.Path)) continue;
            if (!_byPath.TryGetValue(l.Path, out var cur) || l.SetAt > cur.SetAt) _byPath[l.Path] = l;
        }
        foreach (var f in m.Folders)
        {
            if (!_folders.TryGetValue(f.Path, out var cur) || f.SetAt > cur.SetAt) _folders[f.Path] = f;
        }
    }

    // ---------------------------------------------------------------- paths

    /// <summary>Key for a photo, given its path relative to the opened folder.</summary>
    public string KeyFor(string relToOpened)
    {
        var full = System.IO.Path.Combine(_openedRoot, relToOpened);
        return System.IO.Path.GetRelativePath(StoreDir, full).Replace('\\', '/');
    }

    // ---------------------------------------------------------------- photos

    public int Count => _byPath.Values.Count(l => !l.Removed);

    /// <summary>Copy the manual locations onto the scanned photos (path first, then name+size).</summary>
    public void ApplyTo(IEnumerable<PhotoItem> items)
    {
        var fallback = new Dictionary<string, ManualLocation>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in _byPath.Values)
            if (l.Name != null) fallback[$"{l.Name}|{l.Size}"] = l;

        foreach (var it in items)
        {
            it.ManualLat = it.ManualLon = null;
            if (!_byPath.TryGetValue(KeyFor(it.RelPath), out var l))
            {
                // Renamed folder or moved photo: recognise it by name + size (+ date taken).
                if (!fallback.TryGetValue($"{it.FileName}|{it.Size}", out l)) continue;
                if (l.Taken.HasValue && it.Taken.HasValue && l.Taken != it.Taken) continue;
            }
            if (l.Removed) continue;
            it.ManualLat = l.Lat;
            it.ManualLon = l.Lon;
        }
    }

    public ManualLocation? Get(PhotoItem it) =>
        _byPath.TryGetValue(KeyFor(it.RelPath), out var l) ? l : null;

    /// <summary>Set a location for these photos. Returns what was there before (for undo).</summary>
    public List<(string Key, ManualLocation? Before)> Set(IEnumerable<PhotoItem> items, double lat, double lon)
    {
        var undo = new List<(string, ManualLocation?)>();
        var now = DateTime.UtcNow;
        foreach (var it in items)
        {
            var key = KeyFor(it.RelPath);
            undo.Add((key, _byPath.TryGetValue(key, out var prev) ? prev.Copy() : null));
            _byPath[key] = new ManualLocation
            {
                Path = key, Lat = Math.Round(lat, 6), Lon = Math.Round(lon, 6), SetAt = now,
                Name = it.FileName, Size = it.Size, Taken = it.Taken
            };
            it.ManualLat = Math.Round(lat, 6);
            it.ManualLon = Math.Round(lon, 6);
        }
        return undo;
    }

    /// <summary>Remove the user's location for these photos (falls back to the file's own GPS).</summary>
    public List<(string Key, ManualLocation? Before)> Remove(IEnumerable<PhotoItem> items)
    {
        var undo = new List<(string, ManualLocation?)>();
        var now = DateTime.UtcNow;
        foreach (var it in items)
        {
            var key = KeyFor(it.RelPath);
            if (!_byPath.TryGetValue(key, out var prev) || prev.Removed) continue;
            undo.Add((key, prev.Copy()));
            _byPath[key] = new ManualLocation { Path = key, Removed = true, SetAt = now, Name = it.FileName, Size = it.Size };
            it.ManualLat = it.ManualLon = null;
        }
        return undo;
    }

    /// <summary>Put entries back as they were (undo). Restored entries get a fresh time so merges keep them.</summary>
    public void Restore(IEnumerable<(string Key, ManualLocation? Before)> undo)
    {
        var now = DateTime.UtcNow;
        foreach (var (key, before) in undo)
        {
            if (before == null || before.Removed)
                _byPath[key] = new ManualLocation { Path = key, Removed = true, SetAt = now, Name = before?.Name, Size = before?.Size ?? 0 };
            else
            {
                var c = before.Copy();
                c.SetAt = now;
                _byPath[key] = c;
            }
        }
    }

    // ---------------------------------------------------------------- folders

    string FolderKey(string folderRelToOpened) =>
        KeyFor(folderRelToOpened.Length == 0 ? "." : folderRelToOpened).TrimEnd('/', '.');

    public FolderState GetFolderState(string folderRelToOpened) =>
        _folders.TryGetValue(FolderKey(folderRelToOpened), out var f) ? f.State : FolderState.None;

    public void SetFolderState(string folderRelToOpened, FolderState state)
    {
        var key = FolderKey(folderRelToOpened);
        _folders[key] = new FolderMark { Path = key, State = state, SetAt = DateTime.UtcNow };
    }

    // ---------------------------------------------------------------- saving

    /// <summary>Write the collection file and the backup copy. Returns false if the collection couldn't be written.</summary>
    public bool Save()
    {
        if (_pendingBackup != null) DeclineBackup(); // saving without answering = starting fresh

        var model = new FileModel
        {
            Locations = _byPath.Values.OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            Folders = _folders.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList()
        };
        var json = JsonSerializer.Serialize(model, JsonOpts);

        try { WriteFile(MasterFile, json, hidden: false); } catch { }

        if (!CollectionWritable) return false;
        try
        {
            WriteFile(CollectionFile, json, hidden: true);
            return true;
        }
        catch
        {
            CollectionWritable = false;
            return false;
        }
    }

    static void WriteFile(string file, string content, bool hidden)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        // Windows refuses to overwrite a hidden file, so unhide it first.
        if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal);
        File.Move(tmp, file, true);
        if (hidden && OperatingSystem.IsWindows()) File.SetAttributes(file, FileAttributes.Hidden);
    }
}

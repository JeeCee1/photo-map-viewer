using System.Collections.Concurrent;

namespace PhotoMap.Core;

public sealed record ScanProgress(int Done, int Total, string Phase);

public static class Scanner
{
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".heic", ".heif", ".avif", ".tif", ".tiff", ".bmp", ".gif", ".webp"
    };

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".avi", ".mpg", ".mpeg", ".3gp", ".3g2", ".mkv", ".wmv", ".mts", ".m2ts", ".webm", ".flv"
    };

    /// <summary>
    /// Scan a folder and all subfolders. Files unchanged since the cached index
    /// (same size and modified time) are not re-read.
    /// </summary>
    public static PhotoIndex Scan(string root, PhotoIndex? cached, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new ScanProgress(0, 0, "Finding files"));

        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            ReturnSpecialDirectories = false
        };

        var files = new List<FileInfo>();
        foreach (var path in Directory.EnumerateFiles(root, "*", opts))
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(path);
            if (!ImageExtensions.Contains(ext) && !VideoExtensions.Contains(ext)) continue;
            var fi = new FileInfo(path);
            if (fi.Name.StartsWith("._")) continue; // macOS resource-fork junk
            files.Add(fi);
            if (files.Count % 500 == 0) progress?.Report(new ScanProgress(files.Count, 0, "Finding files"));
        }

        var old = new Dictionary<string, PhotoItem>(StringComparer.OrdinalIgnoreCase);
        if (cached != null)
            foreach (var it in cached.Items) old[it.RelPath] = it;

        var result = new ConcurrentBag<PhotoItem>();
        int done = 0;
        var po = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct };
        Parallel.ForEach(files, po, fi =>
        {
            string rel = Path.GetRelativePath(root, fi.FullName);
            long ticks = fi.LastWriteTimeUtc.Ticks;
            PhotoItem item;
            if (old.TryGetValue(rel, out var prev) && prev.Size == fi.Length && prev.ModifiedTicks == ticks)
            {
                item = prev;
            }
            else
            {
                item = new PhotoItem
                {
                    RelPath = rel,
                    Size = fi.Length,
                    ModifiedTicks = ticks,
                    IsVideo = VideoExtensions.Contains(fi.Extension)
                };
                if (!item.IsVideo)
                {
                    var exif = ExifReader.Read(fi.FullName);
                    if (exif != null)
                    {
                        item.Taken = exif.Taken;
                        item.Orientation = exif.Orientation;
                        item.ExifLat = exif.Latitude;
                        item.ExifLon = exif.Longitude;
                    }
                }
                if (item.ExifLat.HasValue && item.ExifLon.HasValue)
                {
                    var c = Regions.CountryNear(item.ExifLat.Value, item.ExifLon.Value);
                    item.ExifCountryId = c?.Id;
                    item.ExifCountryName = c?.Name;
                }
            }
            result.Add(item);
            int d = Interlocked.Increment(ref done);
            if (d % 50 == 0) progress?.Report(new ScanProgress(d, files.Count, "Reading photos"));
        });

        progress?.Report(new ScanProgress(files.Count, files.Count, "Reading photos"));

        var items = result.ToList();
        items.Sort((a, b) => string.Compare(a.RelPath, b.RelPath, StringComparison.OrdinalIgnoreCase));
        return new PhotoIndex { Root = root, ScannedAt = DateTime.Now, Items = items };
    }
}

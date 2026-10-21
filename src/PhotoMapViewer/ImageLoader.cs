using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace PhotoMapViewer;

/// <summary>
/// Loads photos at screen size, upright, off the UI thread.
/// Windows' own decoders first; ImageMagick (bundled) for HEIC and anything Windows can't read.
/// </summary>
public sealed class ImageLoader
{
    readonly int _maxPx;
    readonly Dictionary<string, Task<BitmapSource?>> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly LinkedList<string> _order = new();
    const int CacheSize = 6;

    static readonly HashSet<string> MagickFirst = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif", ".avif", ".webp"
    };

    public ImageLoader(int maxPx) => _maxPx = Math.Clamp(maxPx, 800, 4096);

    /// <summary>Get (or start loading) a photo. Call from the UI thread.</summary>
    public Task<BitmapSource?> Get(string fullPath, int orientation)
    {
        if (_cache.TryGetValue(fullPath, out var t))
        {
            _order.Remove(fullPath);
            _order.AddLast(fullPath);
            return t;
        }
        t = Task.Run(() => Load(fullPath, orientation, _maxPx));
        _cache[fullPath] = t;
        _order.AddLast(fullPath);
        while (_order.Count > CacheSize)
        {
            _cache.Remove(_order.First!.Value);
            _order.RemoveFirst();
        }
        return t;
    }

    public void Clear()
    {
        _cache.Clear();
        _order.Clear();
    }

    static BitmapSource? Load(string path, int orientation, int maxPx)
    {
        var ext = Path.GetExtension(path);
        if (!MagickFirst.Contains(ext))
        {
            try { return LoadWithWindows(path, orientation, maxPx); }
            catch { /* fall through to ImageMagick */ }
        }
        try { return LoadWithMagick(path, maxPx); }
        catch { return null; }
    }

    static BitmapSource LoadWithWindows(string path, int orientation, int maxPx)
    {
        int w, h;
        using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var dec = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            w = dec.Frames[0].PixelWidth;
            h = dec.Frames[0].PixelHeight;
        }

        var bi = new BitmapImage();
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = fs;
            if (Math.Max(w, h) > maxPx)
            {
                if (w >= h) bi.DecodePixelWidth = maxPx;
                else bi.DecodePixelHeight = maxPx;
            }
            bi.EndInit();
        }
        bi.Freeze();
        return Orient(bi, orientation);
    }

    /// <summary>Apply the EXIF orientation (WPF doesn't do this by itself).</summary>
    static BitmapSource Orient(BitmapSource src, int orientation)
    {
        Transform? t = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
            8 => new RotateTransform(270),
            _ => null
        };
        if (t == null) return src;
        var tb = new TransformedBitmap(src, t);
        tb.Freeze();
        return tb;
    }

    static BitmapSource LoadWithMagick(string path, int maxPx)
    {
        using var img = new MagickImage(path);
        img.AutoOrient();
        if (Math.Max(img.Width, img.Height) > maxPx)
            img.Resize(new MagickGeometry((uint)maxPx, (uint)maxPx) { Greater = true });

        var ms = new MemoryStream();
        img.Quality = 92;
        img.Write(ms, MagickFormat.Jpeg);
        ms.Position = 0;

        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}

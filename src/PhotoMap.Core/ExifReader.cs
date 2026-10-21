using System.Globalization;
using System.Text;

namespace PhotoMap.Core;

/// <summary>What we pull out of a photo's metadata.</summary>
public sealed class ExifData
{
    public DateTime? Taken { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>EXIF orientation 1..8 (1 = upright).</summary>
    public int Orientation { get; set; } = 1;
}

/// <summary>
/// Small, dependency-free EXIF reader. Handles JPEG, HEIC/HEIF/AVIF, PNG and TIFF.
/// Only reads the handful of tags the viewer needs: date taken, GPS, orientation.
/// </summary>
public static class ExifReader
{
    public static ExifData? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            return Read(fs);
        }
        catch
        {
            return null;
        }
    }

    public static ExifData? Read(Stream s)
    {
        var head = new byte[16];
        int n = ReadFully(s, head, 0, head.Length);
        if (n < 12) return null;
        s.Position = 0;

        byte[]? tiff = null;
        if (head[0] == 0xFF && head[1] == 0xD8) tiff = FromJpeg(s);
        else if (Encoding.ASCII.GetString(head, 4, 4) == "ftyp") tiff = FromIsoBmff(s);
        else if (head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G') tiff = FromPng(s);
        else if ((head[0] == 'I' && head[1] == 'I' && head[2] == 42) || (head[0] == 'M' && head[1] == 'M' && head[3] == 42))
        {
            int len = (int)Math.Min(s.Length, 4 * 1024 * 1024);
            tiff = new byte[len];
            ReadFully(s, tiff, 0, len);
        }

        return tiff == null ? null : ParseTiff(tiff);
    }

    // ---------- containers ----------

    static byte[]? FromJpeg(Stream s)
    {
        s.Position = 2;
        var b2 = new byte[2];
        while (s.Position < s.Length)
        {
            int m = s.ReadByte();
            if (m != 0xFF) return null;
            int marker = s.ReadByte();
            while (marker == 0xFF) marker = s.ReadByte();
            if (marker < 0) return null;
            if (marker == 0xD9 || marker == 0xDA) return null; // EOI / start of scan
            if (marker >= 0xD0 && marker <= 0xD7) continue;   // RSTn, no length
            if (ReadFully(s, b2, 0, 2) < 2) return null;
            int len = (b2[0] << 8) | b2[1];
            if (len < 2) return null;
            long next = s.Position + len - 2;
            if (marker == 0xE1 && len > 8)
            {
                var seg = new byte[len - 2];
                ReadFully(s, seg, 0, seg.Length);
                if (seg.Length > 6 && seg[0] == 'E' && seg[1] == 'x' && seg[2] == 'i' && seg[3] == 'f' && seg[4] == 0)
                    return seg.AsSpan(6).ToArray();
            }
            s.Position = next;
        }
        return null;
    }

    static byte[]? FromPng(Stream s)
    {
        s.Position = 8;
        var hdr = new byte[8];
        while (ReadFully(s, hdr, 0, 8) == 8)
        {
            uint len = BE32(hdr, 0);
            string type = Encoding.ASCII.GetString(hdr, 4, 4);
            if (type == "eXIf")
            {
                var data = new byte[len];
                ReadFully(s, data, 0, (int)len);
                return data;
            }
            if (type == "IDAT" || type == "IEND") return null;
            s.Position += len + 4; // data + CRC
        }
        return null;
    }

    /// <summary>HEIC / HEIF / AVIF: find the 'Exif' item via meta/iinf + meta/iloc.</summary>
    static byte[]? FromIsoBmff(Stream s)
    {
        long fileLen = s.Length;
        long pos = 0;
        while (pos + 8 <= fileLen)
        {
            var (size, type, hdrLen) = ReadBoxHeader(s, pos, fileLen);
            if (size <= 0) return null;
            if (type == "meta")
                return FromMeta(s, pos + hdrLen + 4, pos + size); // +4: full-box version/flags
            pos += size;
        }
        return null;
    }

    static byte[]? FromMeta(Stream s, long start, long end)
    {
        uint exifItem = 0;
        long exifOffset = -1, exifLength = 0;
        long ilocStart = -1, ilocEnd = -1;

        long pos = start;
        while (pos + 8 <= end)
        {
            var (size, type, hdrLen) = ReadBoxHeader(s, pos, end);
            if (size <= 0) break;
            if (type == "iinf") exifItem = FindExifItem(s, pos + hdrLen, pos + size);
            else if (type == "iloc") { ilocStart = pos + hdrLen; ilocEnd = pos + size; }
            pos += size;
        }
        if (exifItem == 0 || ilocStart < 0) return null;
        (exifOffset, exifLength) = FindItemLocation(s, ilocStart, ilocEnd, exifItem);
        if (exifOffset < 0 || exifLength < 8 || exifLength > 16 * 1024 * 1024) return null;

        var data = new byte[exifLength];
        s.Position = exifOffset;
        ReadFully(s, data, 0, data.Length);

        // Normally: 4-byte offset to the TIFF header, then the TIFF data.
        int off = (int)BE32(data, 0) + 4;
        if (off >= 4 && off + 8 < data.Length && IsTiffHeader(data, off)) return data.AsSpan(off).ToArray();
        // Some writers differ; just look for the TIFF header near the start.
        for (int i = 0; i < Math.Min(64, data.Length - 8); i++)
            if (IsTiffHeader(data, i)) return data.AsSpan(i).ToArray();
        return null;
    }

    static uint FindExifItem(Stream s, long start, long end)
    {
        s.Position = start;
        int version = s.ReadByte();
        s.Position += 3;
        long count = version == 0 ? ReadBE16(s) : ReadBE32(s);
        long pos = s.Position;
        for (long i = 0; i < count && pos + 8 <= end; i++)
        {
            var (size, type, hdrLen) = ReadBoxHeader(s, pos, end);
            if (size <= 0) break;
            if (type == "infe")
            {
                s.Position = pos + hdrLen;
                int v = s.ReadByte();
                s.Position += 3;
                if (v >= 2)
                {
                    uint id = v == 2 ? ReadBE16(s) : ReadBE32(s);
                    ReadBE16(s); // protection index
                    var t = new byte[4];
                    ReadFully(s, t, 0, 4);
                    if (Encoding.ASCII.GetString(t) == "Exif") return id;
                }
            }
            pos += size;
        }
        return 0;
    }

    static (long offset, long length) FindItemLocation(Stream s, long start, long end, uint wanted)
    {
        s.Position = start;
        int version = s.ReadByte();
        s.Position += 3;
        int b1 = s.ReadByte(), b2 = s.ReadByte();
        int offsetSize = b1 >> 4, lengthSize = b1 & 0xF, baseOffsetSize = b2 >> 4;
        int indexSize = (version == 1 || version == 2) ? (b2 & 0xF) : 0;
        long count = version < 2 ? ReadBE16(s) : ReadBE32(s);

        for (long i = 0; i < count && s.Position < end; i++)
        {
            uint id = version < 2 ? ReadBE16(s) : ReadBE32(s);
            int construction = 0;
            if (version == 1 || version == 2) construction = ReadBE16(s) & 0xF;
            ReadBE16(s); // data reference index
            long baseOffset = ReadN(s, baseOffsetSize);
            int extents = ReadBE16(s);
            long firstOffset = -1, firstLength = 0;
            for (int e = 0; e < extents; e++)
            {
                if (indexSize > 0) ReadN(s, indexSize);
                long eo = ReadN(s, offsetSize);
                long el = ReadN(s, lengthSize);
                if (e == 0) { firstOffset = eo; firstLength = el; }
            }
            if (id == wanted)
                return construction == 0 && extents > 0 ? (baseOffset + firstOffset, firstLength) : (-1, 0);
        }
        return (-1, 0);
    }

    static (long size, string type, int hdrLen) ReadBoxHeader(Stream s, long pos, long limit)
    {
        s.Position = pos;
        var h = new byte[8];
        if (ReadFully(s, h, 0, 8) < 8) return (0, "", 0);
        long size = BE32(h, 0);
        string type = Encoding.ASCII.GetString(h, 4, 4);
        int hdrLen = 8;
        if (size == 1)
        {
            var l = new byte[8];
            ReadFully(s, l, 0, 8);
            size = (long)(((ulong)BE32(l, 0) << 32) | BE32(l, 4));
            hdrLen = 16;
        }
        else if (size == 0) size = limit - pos;
        if (size < hdrLen || pos + size > limit + 8) return (0, type, hdrLen);
        return (size, type, hdrLen);
    }

    // ---------- TIFF / EXIF ----------

    const ushort TagOrientation = 0x0112, TagDateTime = 0x0132, TagExifIfd = 0x8769, TagGpsIfd = 0x8825;
    const ushort TagDateOriginal = 0x9003, TagDateDigitized = 0x9004;

    static bool IsTiffHeader(byte[] d, int i) =>
        (d[i] == 'I' && d[i + 1] == 'I' && d[i + 2] == 42 && d[i + 3] == 0) ||
        (d[i] == 'M' && d[i + 1] == 'M' && d[i + 2] == 0 && d[i + 3] == 42);

    public static ExifData? ParseTiff(byte[] t)
    {
        if (t.Length < 8 || !IsTiffHeader(t, 0)) return null;
        var r = new TiffReader(t, t[0] == 'I');
        var result = new ExifData();

        var ifd0 = r.ReadIfd(r.U32(4));
        if (ifd0 == null) return result;

        if (ifd0.TryGetValue(TagOrientation, out var o))
        {
            int v = (int)r.UInt(o);
            if (v >= 1 && v <= 8) result.Orientation = v;
        }

        string? date = null;
        if (ifd0.TryGetValue(TagExifIfd, out var exifPtr))
        {
            var exif = r.ReadIfd(r.UInt(exifPtr));
            if (exif != null)
            {
                if (exif.TryGetValue(TagDateOriginal, out var d1)) date = r.Ascii(d1);
                if (string.IsNullOrWhiteSpace(date) && exif.TryGetValue(TagDateDigitized, out var d2)) date = r.Ascii(d2);
            }
        }
        if (string.IsNullOrWhiteSpace(date) && ifd0.TryGetValue(TagDateTime, out var d0)) date = r.Ascii(d0);
        result.Taken = ParseDate(date);

        if (ifd0.TryGetValue(TagGpsIfd, out var gpsPtr))
        {
            var gps = r.ReadIfd(r.UInt(gpsPtr));
            if (gps != null &&
                gps.TryGetValue(2, out var latE) && gps.TryGetValue(4, out var lonE))
            {
                double? lat = r.Dms(latE), lon = r.Dms(lonE);
                string latRef = gps.TryGetValue(1, out var lr) ? r.Ascii(lr) : "N";
                string lonRef = gps.TryGetValue(3, out var lo) ? r.Ascii(lo) : "E";
                if (lat.HasValue && lon.HasValue)
                {
                    double la = lat.Value * (latRef.StartsWith('S') ? -1 : 1);
                    double ln = lon.Value * (lonRef.StartsWith('W') ? -1 : 1);
                    // 0,0 is what many cameras write when they have no fix.
                    bool valid = Math.Abs(la) <= 90 && Math.Abs(ln) <= 180 && !(Math.Abs(la) < 1e-6 && Math.Abs(ln) < 1e-6);
                    if (valid) { result.Latitude = la; result.Longitude = ln; }
                }
            }
        }
        return result;
    }

    public static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimEnd('\0').Trim();
        if (s.Length < 19) return null;
        s = s.Substring(0, 19);
        if (s.StartsWith("0000")) return null;
        string[] formats = { "yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy:MM:ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss" };
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Year >= 1900)
            return d;
        return null;
    }

    sealed class Entry
    {
        public ushort Type;
        public uint Count;
        public int ValueOffset; // absolute offset in the TIFF buffer where the value bytes start
    }

    sealed class TiffReader
    {
        readonly byte[] _d;
        readonly bool _le;
        public TiffReader(byte[] d, bool littleEndian) { _d = d; _le = littleEndian; }

        public ushort U16(long o) => o + 2 > _d.Length ? (ushort)0 :
            _le ? (ushort)(_d[o] | _d[o + 1] << 8) : (ushort)(_d[o] << 8 | _d[o + 1]);

        public uint U32(long o) => o + 4 > _d.Length ? 0u :
            _le ? (uint)(_d[o] | _d[o + 1] << 8 | _d[o + 2] << 16 | _d[o + 3] << 24)
                : (uint)(_d[o] << 24 | _d[o + 1] << 16 | _d[o + 2] << 8 | _d[o + 3]);

        static int TypeSize(ushort t) => t switch
        {
            1 or 2 or 6 or 7 => 1,
            3 or 8 => 2,
            4 or 9 or 11 => 4,
            5 or 10 or 12 => 8,
            _ => 1
        };

        public Dictionary<ushort, Entry>? ReadIfd(uint offset)
        {
            if (offset == 0 || offset + 2 > _d.Length) return null;
            int count = U16(offset);
            if (count > 1000) return null;
            var dict = new Dictionary<ushort, Entry>();
            for (int i = 0; i < count; i++)
            {
                long e = offset + 2 + i * 12L;
                if (e + 12 > _d.Length) break;
                ushort tag = U16(e);
                var entry = new Entry { Type = U16(e + 2), Count = U32(e + 4) };
                long total = (long)TypeSize(entry.Type) * entry.Count;
                entry.ValueOffset = total <= 4 ? (int)(e + 8) : (int)U32(e + 8);
                if (entry.ValueOffset < 0 || entry.ValueOffset + Math.Min(total, 1) > _d.Length) continue;
                dict[tag] = entry;
            }
            return dict;
        }

        public uint UInt(Entry e) => e.Type == 3 ? U16(e.ValueOffset) : U32(e.ValueOffset);

        public string Ascii(Entry e)
        {
            int len = (int)Math.Min(e.Count, (uint)Math.Max(0, _d.Length - e.ValueOffset));
            return Encoding.ASCII.GetString(_d, e.ValueOffset, len).TrimEnd('\0', ' ');
        }

        double Rational(long o)
        {
            uint num = U32(o), den = U32(o + 4);
            return den == 0 ? double.NaN : (double)num / den;
        }

        /// <summary>GPS degrees/minutes/seconds (3 rationals) to decimal degrees.</summary>
        public double? Dms(Entry e)
        {
            if (e.Type != 5 || e.Count < 1) return null;
            double deg = Rational(e.ValueOffset);
            double min = e.Count > 1 ? Rational(e.ValueOffset + 8) : 0;
            double sec = e.Count > 2 ? Rational(e.ValueOffset + 16) : 0;
            if (double.IsNaN(deg)) return null;
            if (double.IsNaN(min)) min = 0;
            if (double.IsNaN(sec)) sec = 0;
            return deg + min / 60.0 + sec / 3600.0;
        }
    }

    // ---------- helpers ----------

    static int ReadFully(Stream s, byte[] buf, int off, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buf, off + total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    static uint BE32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);

    static ushort ReadBE16(Stream s)
    {
        int a = s.ReadByte(), b = s.ReadByte();
        return (ushort)((a << 8) | b);
    }

    static uint ReadBE32(Stream s)
    {
        var b = new byte[4];
        ReadFully(s, b, 0, 4);
        return BE32(b, 0);
    }

    static long ReadN(Stream s, int bytes) => bytes switch
    {
        0 => 0,
        2 => ReadBE16(s),
        4 => ReadBE32(s),
        8 => (long)(((ulong)ReadBE32(s) << 32) | ReadBE32(s)),
        _ => 0
    };
}

namespace PhotoMap.Core;

public enum PlayOrder { ByDate, ByName, RandomThisFolder, RandomAllFolders }

/// <summary>
/// Decides which photo comes next. Keeps a history so "previous" also works in random mode.
/// </summary>
public sealed class Playlist
{
    readonly List<PhotoItem> _all;
    readonly List<PhotoItem> _sorted;
    readonly Random _rng = new();
    readonly List<PhotoItem> _history = new();
    int _pos = -1;

    // Random: a shuffled "bag" so nothing repeats until everything has been shown.
    List<PhotoItem> _bag = new();
    string? _bagFolder;

    /// <summary>The last <paramref name="n"/> photos shown, oldest first, ending with the current one.</summary>
    public IReadOnlyList<PhotoItem> Recent(int n)
    {
        if (_pos < 0 || n <= 0) return Array.Empty<PhotoItem>();
        int from = Math.Max(0, _pos - n + 1);
        return _history.GetRange(from, _pos - from + 1);
    }

    public PlayOrder Order { get; private set; }
    public int Count => _all.Count;
    public PhotoItem? Current => _pos >= 0 && _pos < _history.Count ? _history[_pos] : null;

    public Playlist(IEnumerable<PhotoItem> items, PlayOrder order)
    {
        _all = items.ToList();
        _sorted = _all.ToList();
        Order = order;
        SortForOrder();
    }

    public void SetOrder(PlayOrder order)
    {
        if (order == Order) return;
        Order = order;
        SortForOrder();
        _bag.Clear();
        _bagFolder = null;
        // Keep what's on screen; forget the forward history.
        if (_pos >= 0) _history.RemoveRange(_pos + 1, _history.Count - _pos - 1);
    }

    void SortForOrder()
    {
        if (Order == PlayOrder.ByName)
            _sorted.Sort((a, b) => string.Compare(a.RelPath, b.RelPath, StringComparison.OrdinalIgnoreCase));
        else
            _sorted.Sort((a, b) =>
            {
                int c = SortTime(a).CompareTo(SortTime(b));
                return c != 0 ? c : string.Compare(a.RelPath, b.RelPath, StringComparison.OrdinalIgnoreCase);
            });
    }

    static long SortTime(PhotoItem p) => p.Taken?.Ticks ?? new DateTime(p.ModifiedTicks, DateTimeKind.Utc).ToLocalTime().Ticks;

    public bool IsRandom => Order is PlayOrder.RandomThisFolder or PlayOrder.RandomAllFolders;

    /// <summary>Start at a given photo (e.g. where the user left off), or at the beginning.</summary>
    public PhotoItem? Start(string? relPath = null)
    {
        _history.Clear();
        _pos = -1;
        if (_all.Count == 0) return null;
        PhotoItem? first = relPath == null ? null
            : _all.FirstOrDefault(p => string.Equals(p.RelPath, relPath, StringComparison.OrdinalIgnoreCase));
        first ??= IsRandom ? _all[_rng.Next(_all.Count)] : _sorted[0];
        _history.Add(first);
        _pos = 0;
        return first;
    }

    public PhotoItem? Next()
    {
        if (_all.Count == 0) return null;
        if (_pos + 1 < _history.Count) return _history[++_pos];

        PhotoItem next;
        if (!IsRandom)
        {
            int i = Current == null ? -1 : _sorted.IndexOf(Current);
            next = _sorted[(i + 1) % _sorted.Count];
        }
        else next = DrawRandom();

        _history.Add(next);
        if (_history.Count > 5000) _history.RemoveAt(0);
        _pos = _history.Count - 1;
        return next;
    }

    /// <summary>
    /// Which photo Next() will return, without moving there (used to preload it).
    /// In random mode the draw is made now and remembered, so Next() returns the same photo.
    /// </summary>
    public PhotoItem? PeekNext()
    {
        if (_all.Count == 0) return null;
        if (_pos + 1 < _history.Count) return _history[_pos + 1];
        PhotoItem next;
        if (!IsRandom)
        {
            int i = Current == null ? -1 : _sorted.IndexOf(Current);
            next = _sorted[(i + 1) % _sorted.Count];
        }
        else next = DrawRandom();
        _history.Add(next);
        return next;
    }

    public PhotoItem? Previous()
    {
        if (_all.Count == 0) return null;
        if (_pos > 0) return _history[--_pos];
        if (IsRandom) return Current;
        // Sequential: step back through the sorted list.
        int i = Current == null ? 0 : _sorted.IndexOf(Current);
        var prev = _sorted[(i - 1 + _sorted.Count) % _sorted.Count];
        _history.Insert(0, prev);
        _pos = 0;
        return prev;
    }

    PhotoItem DrawRandom()
    {
        string? folder = Order == PlayOrder.RandomThisFolder ? Current?.Folder : null;
        if (_bag.Count == 0 || !string.Equals(_bagFolder, folder, StringComparison.OrdinalIgnoreCase))
        {
            _bag = folder == null
                ? _all.ToList()
                : _all.Where(p => string.Equals(p.Folder, folder, StringComparison.OrdinalIgnoreCase)).ToList();
            if (_bag.Count == 0) _bag = _all.ToList();
            _bagFolder = folder;
            // Fisher–Yates
            for (int i = _bag.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (_bag[i], _bag[j]) = (_bag[j], _bag[i]);
            }
            // Avoid showing the same photo twice in a row when the bag refills.
            if (_bag.Count > 1 && _bag[^1] == Current) (_bag[0], _bag[^1]) = (_bag[^1], _bag[0]);
        }
        var p = _bag[^1];
        _bag.RemoveAt(_bag.Count - 1);
        return p;
    }
}

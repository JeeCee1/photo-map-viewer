using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PhotoMap.Core;

namespace PhotoMapViewer;

/// <summary>Setting locations by hand, the "Add locations" mode, undo, and writing into photo files.</summary>
public partial class MainWindow
{
    enum EditMode { None, Single, Add }

    /// <summary>One step in "Add locations": a folder, or one day of a folder marked "mixed places".</summary>
    sealed class AddTask
    {
        public string Folder = "";
        public DateTime? Day;      // set when the folder is done per day
        public bool PerDay;
        public bool NoDate;        // the "unknown date" group of a per-day folder
    }

    LocationStore? _store;
    EditMode _editMode;
    PhotoItem? _editItem;
    double? _editLat, _editLon;
    int _editSeq;
    bool _warnedReadOnly;
    readonly Stack<List<(string Key, ManualLocation? Before)>> _undo = new();
    readonly List<AddTask> _addQueue = new();
    int _addPos;
    ImageLoader? _thumbs;
    AddTask? _lastAddTask;

    bool Editing => _editMode != EditMode.None;

    // ------------------------------------------------------------------ store

    async Task OpenStoreAsync(string root)
    {
        _store = await Task.Run(() => LocationStore.Open(root, AppSettings.Folder));
        _undo.Clear();
        _warnedReadOnly = false;
        if (_store.RestorableFromBackup > 0)
        {
            var answer = MessageBox.Show(this,
                $"The locations file for this collection is missing, but the app's backup copy has " +
                $"{_store.RestorableFromBackup:N0} locations you set earlier.\n\nRestore them?",
                "Restore locations", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) _store.AcceptBackup();
            else _store.DeclineBackup();
        }
    }

    /// <summary>Manual locations first, then EXIF, then guesses. Call after any change.</summary>
    void ApplyLocations()
    {
        if (_index == null) return;
        _store?.ApplyTo(_index.Items);
        LocationGuesser.Apply(_index.Items, _s.GuessLocations, TimeSpan.FromHours(_s.GuessWindowHours));
    }

    void SaveStore()
    {
        if (_store == null) return;
        if (!_store.Save() && !_warnedReadOnly)
        {
            _warnedReadOnly = true;
            MessageBox.Show(this,
                "This folder can't be written to, so your locations are kept in the app's own backup copy only.",
                "Locations", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    IEnumerable<PhotoItem> Photos => _index?.Items.Where(i => !i.IsVideo) ?? Enumerable.Empty<PhotoItem>();

    // ------------------------------------------------------------------ menu

    void BtnLocations_Click(object sender, RoutedEventArgs e)
    {
        var menu = BtnLocations.ContextMenu;
        MenuUndo.IsEnabled = _undo.Count > 0;
        menu.PlacementTarget = BtnLocations;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void MenuSetLocation_Click(object sender, RoutedEventArgs e) => StartEdit();
    void MenuAddLocations_Click(object sender, RoutedEventArgs e) => StartAddMode();
    void MenuUndo_Click(object sender, RoutedEventArgs e) => UndoLocation();
    async void MenuWritePhoto_Click(object sender, RoutedEventArgs e) => await WriteToPhotosAsync(WriteScope.Photo);
    async void MenuWriteFolder_Click(object sender, RoutedEventArgs e) => await WriteToPhotosAsync(WriteScope.Folder);
    async void MenuWriteAll_Click(object sender, RoutedEventArgs e) => await WriteToPhotosAsync(WriteScope.All);

    void MenuShowFile_Click(object sender, RoutedEventArgs e)
    {
        if (_store == null) return;
        var file = File.Exists(_store.CollectionFile) ? _store.CollectionFile : _store.MasterFile;
        if (!File.Exists(file))
        {
            MessageBox.Show(this, "You haven't set any locations in this collection yet.", "Locations");
            return;
        }
        try { Process.Start("explorer.exe", $"/select,\"{file}\""); } catch { }
    }

    // ------------------------------------------------------------------ single photo

    void StartEdit()
    {
        var item = _playlist?.Current;
        if (item == null || _store == null || Editing) return;
        _editMode = EditMode.Single;
        _editItem = item;
        RestartSlideTimer(); // stops it while editing

        EditTitle.Visibility = Visibility.Collapsed;
        EditThumbs.Visibility = Visibility.Collapsed;
        EditSubtitle.Text = item.RelPath + "  ·  " + SourceText(item);
        BtnSetPhoto.Content = "Set for this photo (Enter)";
        BtnMixed.Visibility = Visibility.Collapsed;
        BtnSkip.Visibility = Visibility.Collapsed;
        BtnRemoveLoc.Visibility = item.Source == LocationSource.Manual ? Visibility.Visible : Visibility.Collapsed;
        BtnEditDone.Content = "Cancel (Esc)";
        int others = FolderTargets(item, includeCurrent: true).Count;
        BtnSetFolder.Content = $"All without location in this folder ({others:N0})";
        BtnSetFolder.Visibility = others > 1 ? Visibility.Visible : Visibility.Collapsed;

        OpenEditPanel();
        if (item.HasLocation)
        {
            SetEditPosition(item.Lat!.Value, item.Lon!.Value, zoom: 13, moveMap: true);
            EditHint.Text = "Drag the pin, click the map, or search for a place.";
        }
        else
        {
            SetEditPosition(null, null, 0, false);
            _ = Map.ExecuteScriptAsync("pm.view('europe')");
            EditHint.Text = "Click the map where this photo was taken, or search for a place.";
            _ = SuggestAsync(item.Folder, new[] { item }, ++_editSeq);
        }
    }

    static string SourceText(PhotoItem it) => it.Source switch
    {
        LocationSource.Manual => "location set by you",
        LocationSource.Exif => "location from the photo file",
        LocationSource.Guessed => "location guessed from a nearby photo",
        _ => "no location"
    };

    /// <summary>Photos in the same folder without a real location (none or guessed), plus the current one.</summary>
    List<PhotoItem> FolderTargets(PhotoItem item, bool includeCurrent)
    {
        var list = Photos.Where(p => string.Equals(p.Folder, item.Folder, StringComparison.OrdinalIgnoreCase) &&
                                     (p.Source is LocationSource.None or LocationSource.Guessed)).ToList();
        if (includeCurrent && !list.Contains(item)) list.Add(item);
        return list;
    }

    void OpenEditPanel()
    {
        Caption.Visibility = Visibility.Collapsed;
        EditPanel.Visibility = Visibility.Visible;
        EditSearch.Text = "";
        if (_mapReady) _ = Map.ExecuteScriptAsync("pm.edit(true, null)");
        UpdateEditButtons();
        if (!_mapVisible) ToggleMap();
    }

    void SetEditPosition(double? lat, double? lon, int zoom, bool moveMap)
    {
        _editLat = lat;
        _editLon = lon;
        if (moveMap && lat.HasValue && _mapReady)
            _ = Map.ExecuteScriptAsync(string.Create(CultureInfo.InvariantCulture,
                $"pm.edit(true, {{lat:{lat.Value}, lon:{lon!.Value}, zoom:{zoom}}})"));
        UpdateEditButtons();
    }

    void UpdateEditButtons()
    {
        bool has = _editLat.HasValue;
        BtnSetPhoto.IsEnabled = has;
        BtnSetFolder.IsEnabled = has;
    }

    /// <summary>The pin was dragged or the map clicked.</summary>
    void OnEditPinMoved(double lat, double lon)
    {
        if (!Editing) return;
        _editSeq++; // a suggestion still on its way must not move the pin anymore
        SetEditPosition(lat, lon, 0, moveMap: false);
        EditHint.Text = string.Create(CultureInfo.InvariantCulture, $"Pin at {lat:0.0000}, {lon:0.0000}") +
                        (Regions.CountryNear(lat, lon) is { } c ? $" ({c.Name})" : "");
    }

    void EndEdit()
    {
        if (!Editing) return;
        _editMode = EditMode.None;
        _editItem = null;
        _editSeq++;
        EditPanel.Visibility = Visibility.Collapsed;
        EditThumbs.Children.Clear();
        if (_mapReady) _ = Map.ExecuteScriptAsync("pm.edit(false)");
        Keyboard.Focus(this);
        if (_playlist?.Current is { } cur)
        {
            Show(cur, animateMap: false);
        }
        RestartSlideTimer();
    }

    void BtnSetPhoto_Click(object sender, RoutedEventArgs e) => ApplyEdit(folder: false);
    void BtnSetFolder_Click(object sender, RoutedEventArgs e) => ApplyEdit(folder: true);
    void BtnEditDone_Click(object sender, RoutedEventArgs e) => EndEdit();

    void ApplyEdit(bool folder)
    {
        if (!Editing || _editItem == null || _store == null) return;
        if (!_editLat.HasValue)
        {
            EditHint.Text = "First click the map (or search) to place the pin.";
            return;
        }

        List<PhotoItem> targets;
        if (_editMode == EditMode.Add && folder) targets = TaskTargets(_addQueue[_addPos]);
        else if (folder) targets = FolderTargets(_editItem, includeCurrent: true);
        else targets = new List<PhotoItem> { _editItem };

        _undo.Push(_store.Set(targets, _editLat.Value, _editLon!.Value));
        _lastAddTask = _editMode == EditMode.Add ? _addQueue[_addPos] : null;
        SaveStore();
        ApplyLocations();
        ShowToast(targets.Count == 1 ? "Location set" : $"Location set for {targets.Count:N0} photos");

        if (_editMode == EditMode.Single) { EndEdit(); return; }

        // Add mode: stay on this folder if photos are still missing after "just this photo".
        if (!folder && TaskMissing(_addQueue[_addPos]).Count > 0) ShowAddTask();
        else AdvanceAdd(+1);
    }

    void BtnRemoveLoc_Click(object sender, RoutedEventArgs e)
    {
        if (_editItem == null || _store == null) return;
        var undo = _store.Remove(new[] { _editItem });
        if (undo.Count > 0) _undo.Push(undo);
        SaveStore();
        ApplyLocations();
        ShowToast("Your location removed");
        EndEdit();
    }

    void UndoLocation()
    {
        if (_store == null || _undo.Count == 0) { ShowToast("Nothing to undo"); return; }
        _store.Restore(_undo.Pop());
        SaveStore();
        ApplyLocations();
        ShowToast("Undone");
        if (_editMode == EditMode.Add)
        {
            // Go back to the folder the undone change was made in.
            if (_lastAddTask != null) RebuildAddQueueAt(_lastAddTask.Folder, _lastAddTask.Day, _lastAddTask.NoDate);
            else BuildAddQueue();
            AdvanceAdd(0);
        }
        else if (_playlist?.Current is { } cur) { ShowCaption(cur, false); UpdateMap(cur, animate: true); }
    }

    // ------------------------------------------------------------------ search & suggestions

    void EditFind_Click(object sender, RoutedEventArgs e) => _ = FindAsync(EditSearch.Text);

    void EditSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _ = FindAsync(EditSearch.Text); e.Handled = true; }
        else if (e.Key == Key.Escape) { Keyboard.Focus(this); e.Handled = true; }
    }

    async Task FindAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        int seq = ++_editSeq;
        EditHint.Text = "Searching…";
        var place = await _geocoder.SearchAsync(text, CancellationToken.None);
        if (seq != _editSeq || !Editing) return;
        if (place == null) { EditHint.Text = $"Nothing found for “{text.Trim()}”. Try another spelling, or click the map."; return; }
        SetEditPosition(place.Lat, place.Lon, place.Zoom, moveMap: true);
        EditHint.Text = "Found: " + place.Label;
    }

    /// <summary>Place the pin from the folder name (or file names), if that finds anything.</summary>
    async Task SuggestAsync(string folder, IReadOnlyList<PhotoItem> photos, int seq)
    {
        if (!_s.SuggestFromNames) return;

        var texts = new List<(string Text, string From)>();
        var segments = folder.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0 && NameHints.FromFolder(segments[^1]) is { } t1) texts.Add((t1, $"folder name “{segments[^1]}”"));
        if (segments.Length > 1 && NameHints.FromFolder(segments[^2]) is { } t2) texts.Add((t2, $"folder name “{segments[^2]}”"));
        foreach (var p in photos.Take(8))
            if (NameHints.FromFile(p.FileName) is { } t3 && texts.All(x => x.Text != t3))
                texts.Add((t3, $"file name “{p.FileName}”"));
        if (texts.Count == 0) return;

        try
        {
            foreach (var (text, from) in texts.Take(3))
            {
                foreach (var q in NameHints.Candidates(text, 3))
                {
                    var place = await _geocoder.SearchAsync(q, CancellationToken.None);
                    if (seq != _editSeq || !Editing) return; // user moved on or placed the pin
                    if (place == null) continue;
                    SetEditPosition(place.Lat, place.Lon, Math.Min(place.Zoom, 12), moveMap: true);
                    EditHint.Text = $"Suggested from the {from}: {place.Label}. Drag the pin if it's not right.";
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ "Add locations" mode

    void StartAddMode()
    {
        if (_store == null || _index == null || Editing) return;
        BuildAddQueue();
        if (_addQueue.Count == 0)
        {
            ShowToast("Every folder already has locations");
            return;
        }
        _editMode = EditMode.Add;
        RestartSlideTimer();
        _thumbs ??= new ImageLoader(320, cacheSize: 24);

        EditTitle.Visibility = Visibility.Visible;
        EditThumbs.Visibility = Visibility.Visible;
        BtnSetPhoto.Content = "Just this photo";
        BtnSkip.Visibility = Visibility.Visible;
        BtnRemoveLoc.Visibility = Visibility.Collapsed;
        BtnEditDone.Content = "Stop (Esc)";
        OpenEditPanel();

        _addPos = 0;
        _lastAddTask = null;
        AdvanceAdd(0);
    }

    void BuildAddQueue()
    {
        _addQueue.Clear();
        if (_store == null) return;
        var weighted = new List<(AddTask Task, int Missing)>();
        foreach (var g in Photos.GroupBy(p => p.Folder, StringComparer.OrdinalIgnoreCase))
        {
            var state = _store.GetFolderState(g.Key);
            if (state == FolderState.Skip) continue;
            var missing = g.Where(p => p.Source == LocationSource.None).ToList();
            if (missing.Count == 0) continue;
            if (state == FolderState.Mixed)
            {
                foreach (var day in missing.GroupBy(p => p.Taken?.Date).OrderBy(d => d.Key ?? DateTime.MaxValue))
                    weighted.Add((new AddTask { Folder = g.Key, PerDay = true, Day = day.Key, NoDate = day.Key == null }, day.Count()));
            }
            else weighted.Add((new AddTask { Folder = g.Key }, missing.Count));
        }
        // Biggest gaps first; days of one folder stay together in date order.
        foreach (var grp in weighted.GroupBy(w => w.Task.Folder, StringComparer.OrdinalIgnoreCase)
                                    .OrderByDescending(f => f.Sum(w => w.Missing)))
            _addQueue.AddRange(grp.Select(w => w.Task));
    }

    /// <summary>Rebuild the list and go back to a given folder (and day), or its first day.</summary>
    void RebuildAddQueueAt(string folder, DateTime? day, bool noDate)
    {
        BuildAddQueue();
        int i = _addQueue.FindIndex(t => string.Equals(t.Folder, folder, StringComparison.OrdinalIgnoreCase) &&
                                         t.Day == day && t.NoDate == noDate);
        if (i < 0) i = _addQueue.FindIndex(t => string.Equals(t.Folder, folder, StringComparison.OrdinalIgnoreCase));
        _addPos = Math.Max(0, i);
    }

    IEnumerable<PhotoItem> TaskPhotos(AddTask t) =>
        Photos.Where(p => string.Equals(p.Folder, t.Folder, StringComparison.OrdinalIgnoreCase) &&
                          (!t.PerDay || (t.NoDate ? p.Taken == null : p.Taken?.Date == t.Day)));

    List<PhotoItem> TaskMissing(AddTask t) => TaskPhotos(t).Where(p => p.Source == LocationSource.None).ToList();

    /// <summary>"Whole folder" also replaces guesses: a location you set beats a guess.</summary>
    List<PhotoItem> TaskTargets(AddTask t) =>
        TaskPhotos(t).Where(p => p.Source is LocationSource.None or LocationSource.Guessed).ToList();

    /// <summary>
    /// Go to the next (+1) or previous (-1) folder that still has photos without a location;
    /// 0 = the current position (or the first open one after it).
    /// </summary>
    void AdvanceAdd(int step)
    {
        int dir = step >= 0 ? 1 : -1;
        int pos = _addPos + step;
        while (pos >= 0 && pos < _addQueue.Count && TaskMissing(_addQueue[pos]).Count == 0) pos += dir;

        if (pos >= _addQueue.Count)
        {
            EndEdit();
            ShowToast("All folders done");
            return;
        }
        if (pos < 0) { ShowToast("This is the first one"); return; }
        _addPos = pos;
        ShowAddTask();
    }

    void ShowAddTask()
    {
        var task = _addQueue[_addPos];
        var missing = TaskMissing(task);
        if (missing.Count == 0) { AdvanceAdd(+1); return; }
        int seq = ++_editSeq;

        var rep = missing[missing.Count / 2];
        _editItem = rep;
        Show(rep, animateMap: false);

        string name = task.Folder.Length == 0 ? "(the opened folder itself)" : Path.GetFileName(task.Folder);
        string parent = Path.GetDirectoryName(task.Folder) ?? "";
        EditTitle.Text = task.PerDay
            ? $"{name}  —  {(task.NoDate ? "photos without a date" : task.Day!.Value.ToString("d MMMM yyyy", CultureInfo.CurrentCulture))}"
            : name;
        EditSubtitle.Text = (parent.Length > 0 ? parent + "  ·  " : "") +
                            $"{(task.PerDay ? "Day" : "Folder")} {_addPos + 1:N0} of {_addQueue.Count:N0}  ·  " +
                            $"{missing.Count:N0} photo{(missing.Count == 1 ? "" : "s")} without location";

        int targets = TaskTargets(task).Count;
        BtnSetFolder.Content = task.PerDay ? $"This day ({targets:N0})" : $"Whole folder ({targets:N0})";
        BtnSetFolder.Visibility = Visibility.Visible;
        bool severalDays = !task.PerDay && TaskPhotos(task).Select(p => p.Taken?.Date).Distinct().Count() > 1;
        BtnMixed.Visibility = severalDays ? Visibility.Visible : Visibility.Collapsed;
        BtnSkip.Content = task.PerDay ? "Skip this day" : "Skip folder";

        FillThumbs(missing);

        // Pin: start at a nearby photo's location in the same folder if there is one, else from the name.
        var anchor = TaskPhotos(task).FirstOrDefault(p => p.Source is LocationSource.Exif or LocationSource.Manual);
        if (anchor != null)
        {
            SetEditPosition(anchor.Lat, anchor.Lon, 12, moveMap: true);
            EditHint.Text = "Placed at another photo from this folder that has a location. Drag the pin if it's not right.";
        }
        else
        {
            SetEditPosition(null, null, 0, false);
            if (_mapReady) { _ = Map.ExecuteScriptAsync("pm.edit(true, null)"); _ = Map.ExecuteScriptAsync("pm.view('europe')"); }
            EditHint.Text = "Click the map where these photos were taken, or search for a place.";
            _ = SuggestAsync(task.Folder, missing, seq);
        }
    }

    void FillThumbs(List<PhotoItem> missing)
    {
        EditThumbs.Children.Clear();
        if (_root == null || _thumbs == null) return;
        int n = Math.Min(7, missing.Count);
        for (int i = 0; i < n; i++)
        {
            var p = missing[(int)((long)i * missing.Count / n)];
            var img = new Image { Height = 64, Stretch = System.Windows.Media.Stretch.Uniform, MaxWidth = 110 };
            var btn = new Button { Style = (Style)FindResource("ThumbButton"), Content = img, ToolTip = p.FileName, Tag = p };
            btn.Click += (_, _) => { _editItem = (PhotoItem)btn.Tag; Show(_editItem, animateMap: false); };
            EditThumbs.Children.Add(btn);
            _ = LoadThumbAsync(img, p);
        }
    }

    async Task LoadThumbAsync(Image img, PhotoItem p)
    {
        if (_thumbs == null || _root == null) return;
        var bmp = await _thumbs.Get(Path.Combine(_root, p.RelPath), p.Orientation);
        img.Source = bmp;
    }

    void BtnMixed_Click(object sender, RoutedEventArgs e)
    {
        if (_editMode != EditMode.Add || _store == null) return;
        var task = _addQueue[_addPos];
        _store.SetFolderState(task.Folder, FolderState.Mixed);
        SaveStore();
        RebuildAddQueueAt(task.Folder, null, false);
        AdvanceAdd(0);
    }

    void BtnSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_editMode != EditMode.Add || _store == null) return;
        var task = _addQueue[_addPos];
        if (!task.PerDay)
        {
            _store.SetFolderState(task.Folder, FolderState.Skip);
            SaveStore();
        }
        AdvanceAdd(+1);
    }

    /// <summary>Keys while editing. Returns true when handled.</summary>
    bool HandleEditKey(Key key, bool ctrl)
    {
        if (ctrl && key == Key.Z) { UndoLocation(); return true; }
        switch (key)
        {
            case Key.Escape: EndEdit(); return true;
            case Key.Enter: ApplyEdit(folder: _editMode == EditMode.Add); return true;
            case Key.Right: if (_editMode == EditMode.Add) AdvanceAdd(+1); return true;
            case Key.Left: if (_editMode == EditMode.Add) AdvanceAdd(-1); return true;
            case Key.M: ToggleMap(); return true;
            case Key.F11: case Key.F: ToggleFullscreen(); return true;
            default: return true; // nothing else while editing
        }
    }

    // ------------------------------------------------------------------ writing into photo files

    enum WriteScope { Photo, Folder, All }

    static string ExifToolPath => Path.Combine(AppContext.BaseDirectory, "tools", "exiftool", "exiftool.exe");

    async Task WriteToPhotosAsync(WriteScope scope)
    {
        if (_index == null || _root == null) return;
        var cur = _playlist?.Current;
        IEnumerable<PhotoItem> pool = scope switch
        {
            WriteScope.Photo => cur == null ? Enumerable.Empty<PhotoItem>() : new[] { cur },
            WriteScope.Folder => cur == null ? Enumerable.Empty<PhotoItem>()
                : Photos.Where(p => string.Equals(p.Folder, cur.Folder, StringComparison.OrdinalIgnoreCase)),
            _ => Photos
        };

        bool SameAsFile(PhotoItem p) =>
            p.ExifLat.HasValue && Math.Abs(p.ExifLat.Value - p.Lat!.Value) < 1e-5 && Math.Abs(p.ExifLon!.Value - p.Lon!.Value) < 1e-5;

        var candidates = pool.Where(p => p.HasLocation && ExifToolArgs.CanWrite(p.RelPath) && !SameAsFile(p)).ToList();
        var manual = candidates.Where(p => p.Source == LocationSource.Manual).ToList();
        var guessed = candidates.Where(p => p.Source == LocationSource.Guessed).ToList();
        int unsupported = pool.Count(p => p.Source == LocationSource.Manual && !ExifToolArgs.CanWrite(p.RelPath));

        if (manual.Count + guessed.Count == 0)
        {
            MessageBox.Show(this, scope == WriteScope.Photo
                    ? "This photo has no location of yours to write (or the file already has it)."
                    : "There are no locations of yours here that aren't already in the photo files.",
                "Write locations", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targets = manual;
        if (guessed.Count > 0)
        {
            var include = MessageBox.Show(this,
                $"{guessed.Count:N0} photo{(guessed.Count == 1 ? " has a" : "s have a")} guessed location (≈, borrowed from a photo taken around the same time).\n\n" +
                "Write those guesses into the files as well?",
                "Write locations", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (include == MessageBoxResult.Cancel) return;
            if (include == MessageBoxResult.Yes) targets = manual.Concat(guessed).ToList();
        }
        if (targets.Count == 0) return;

        var ok = MessageBox.Show(this,
            $"Write locations into {targets.Count:N0} photo file{(targets.Count == 1 ? "" : "s")}?\n\n" +
            "The location is added to each file; the picture itself and the file's date stay the same. " +
            "This can't be undone from the app." +
            (unsupported > 0 ? $"\n\n({unsupported:N0} file{(unsupported == 1 ? " is" : "s are")} in a format that can't hold a location and will be left out.)" : ""),
            "Write locations", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;

        if (!File.Exists(ExifToolPath))
        {
            MessageBox.Show(this, "The ExifTool helper is missing from the app folder (tools\\exiftool). Please download the app again.",
                "Write locations", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var root = _root;
        var jobs = targets.Select(p => (Path.Combine(root, p.RelPath), p.Lat!.Value, p.Lon!.Value)).ToList();
        SetStatus($"Writing locations: 0 of {jobs.Count:N0}");
        var progress = new Progress<int>(n => SetStatus($"Writing locations: {n:N0} of {jobs.Count:N0}"));
        var result = await Task.Run(() => RunExifTool(jobs, progress));
        SetStatus(null);

        if (result.Errors.Count > 0)
        {
            LogError("ExifTool:\n" + string.Join("\n", result.Errors));
            MessageBox.Show(this,
                $"{result.Written:N0} photos updated, {result.Errors.Count:N0} problem{(result.Errors.Count == 1 ? "" : "s")}:\n\n" +
                string.Join("\n", result.Errors.Take(6)) + (result.Errors.Count > 6 ? "\n…" : ""),
                "Write locations", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else ShowToast($"Location written into {result.Written:N0} photo{(result.Written == 1 ? "" : "s")}");

        // Rescan so the app sees the new GPS in the files.
        await OpenFolderAsync(root, _playlist?.Current?.RelPath);
    }

    sealed record ExifResult(int Written, List<string> Errors);

    static ExifResult RunExifTool(List<(string Path, double Lat, double Lon)> jobs, IProgress<int> progress)
    {
        var errors = new List<string>();
        int written = 0, done = 0;
        var argFile = Path.Combine(Path.GetTempPath(), $"pmv-exiftool-{Guid.NewGuid():N}.args");
        try
        {
            File.WriteAllText(argFile, ExifToolArgs.Build(jobs), new UTF8Encoding(false));
            var psi = new ProcessStartInfo(ExifToolPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-@");
            psi.ArgumentList.Add(argFile);

            using var proc = Process.Start(psi)!;
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) lock (errors) errors.Add(e.Data.Trim());
            };
            proc.BeginErrorReadLine();
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) != null)
            {
                // One summary line per photo: "1 image files updated" / "0 image files updated ... 1 files weren't updated"
                if (line.Contains("image files updated"))
                {
                    if (line.TrimStart().StartsWith("1 ")) written++;
                    progress.Report(++done);
                }
            }
            proc.WaitForExit();
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }
        finally
        {
            try { File.Delete(argFile); } catch { }
        }
        return new ExifResult(written, errors);
    }

    static void LogError(string text)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(Path.Combine(AppSettings.Folder, "errors.log"), $"{DateTime.Now:u}  {text}\n\n");
        }
        catch { }
    }
}

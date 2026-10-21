using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using PhotoMap.Core;

namespace PhotoMapViewer;

public partial class MainWindow : Window
{
    AppSettings _s;
    PhotoIndex? _index;
    Playlist? _playlist;
    string? _root;

    ImageLoader _images;
    readonly Geocoder _geocoder;
    readonly DispatcherTimer _slideTimer = new();
    readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(1.6) };

    bool _playing = true;
    bool _mapReady;
    bool _mapVisible = true;
    int _showSeq;                    // increases on every Show(); stale loads are dropped
    int _showsSinceSave;
    CancellationTokenSource? _scanCts;
    CancellationTokenSource? _placeCts;

    // Full screen bookkeeping
    bool _fullscreen;
    WindowState _prevState;
    WindowStyle _prevStyle;
    ResizeMode _prevResize;

    static readonly JsonSerializerOptions MapJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public MainWindow()
    {
        InitializeComponent();
        _s = AppSettings.Load();

        _images = new ImageLoader(ScreenPixels(1.0));
        _geocoder = new Geocoder(AppSettings.Folder);

        _slideTimer.Tick += (_, _) => Next();
        _idleTimer.Tick += (_, _) => HideChrome();
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Fade(Toast, 0); };

        ApplyLayoutSettings();
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnKeyDown;
        MouseMove += (_, _) => ShowChrome();
    }

    /// <summary>Longest screen edge in real pixels: photos are decoded at this size, not larger.</summary>
    static int ScreenPixels(double dpiScale) =>
        (int)(Math.Max(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight) * dpiScale);

    // ------------------------------------------------------------------ startup / shutdown

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _images = new ImageLoader(ScreenPixels(System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX));
        UpdatePlayButton();
        UpdateOrderButton();
        await InitMapAsync();

        if (_s.StartFullscreen) ToggleFullscreen();
        if (!string.IsNullOrEmpty(_s.LastFolder) && Directory.Exists(_s.LastFolder))
            await OpenFolderAsync(_s.LastFolder, _s.LastPhoto);
        else
            ShowChrome();
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _scanCts?.Cancel();
        _s.LastPhoto = _playlist?.Current?.RelPath;
        SaveMapWidth();
        _s.Save();
        _geocoder.Save();
    }

    async Task InitMapAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppSettings.Folder, "WebView2"));
            await Map.EnsureCoreWebView2Async(env);
            var core = Map.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping("pmv.local",
                Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnMapMessage;
            core.NewWindowRequested += (_, a) =>
            {
                // Links in the map (OpenStreetMap attribution) open in the normal browser.
                a.Handled = true;
                OpenUrl(a.Uri);
            };
            core.Navigate("https://pmv.local/map.html");
        }
        catch (Exception)
        {
            Map.Visibility = Visibility.Collapsed;
            MapError.Visibility = Visibility.Visible;
        }
    }

    void OnMapMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string msg;
        try { msg = e.TryGetWebMessageAsString(); } catch { return; }
        if (msg == "ready")
        {
            _mapReady = true;
            if (_playlist?.Current is { } cur) UpdateMap(cur, animate: false);
        }
        else if (msg.StartsWith("key:"))
        {
            HandleKey(msg[4..] switch
            {
                "ArrowLeft" => Key.Left,
                "ArrowRight" => Key.Right,
                " " => Key.Space,
                "F11" => Key.F11,
                "Escape" => Key.Escape,
                "o" or "O" => Key.O,
                "s" or "S" => Key.S,
                "m" or "M" => Key.M,
                "r" or "R" => Key.R,
                _ => Key.None
            });
        }
    }

    // ------------------------------------------------------------------ opening & scanning

    async void BtnOpen_Click(object sender, RoutedEventArgs e) => await PickFolderAsync();

    async Task PickFolderAsync()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a photo folder",
            InitialDirectory = _root ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dlg.ShowDialog(this) == true)
            await OpenFolderAsync(dlg.FolderName, null);
    }

    async Task OpenFolderAsync(string root, string? resumeAt)
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        _root = root;
        _s.LastFolder = root;
        _s.Save();
        Title = $"{Path.GetFileName(root.TrimEnd('\\', '/'))} — Photo Map Viewer";

        Directory.CreateDirectory(AppSettings.Folder);
        string cacheFile = PhotoIndex.CacheFileFor(AppSettings.Folder, root);
        var cached = await Task.Run(() => PhotoIndex.TryLoad(cacheFile));

        // Known folder: start straight away from the saved index, then look for changes.
        if (cached != null && cached.Items.Count > 0)
        {
            UseIndex(cached, resumeAt);
            SetStatus("Checking for new photos…");
        }
        else
        {
            _index = null;
            _playlist = null;
            ImgFront.Source = ImgBack.Source = null;
            Caption.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            SetStatus("Looking for photos…");
        }

        var progress = new Progress<ScanProgress>(p =>
        {
            if (cts.IsCancellationRequested) return;
            SetStatus(p.Total > 0
                ? $"{p.Phase}: {p.Done:N0} of {p.Total:N0}"
                : $"{p.Phase}: {p.Done:N0}");
        });

        PhotoIndex fresh;
        try
        {
            fresh = await Task.Run(() => Scanner.Scan(root, cached, progress, cts.Token), cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            SetStatus(null);
            ShowToast($"Couldn't read the folder: {ex.Message}");
            return;
        }
        if (cts.IsCancellationRequested) return;

        await Task.Run(() => fresh.Save(cacheFile));
        SetStatus(null);

        bool changed = cached == null || cached.Items.Count != fresh.Items.Count ||
                       !cached.Items.Select(i => i.RelPath + i.ModifiedTicks)
                              .SequenceEqual(fresh.Items.Select(i => i.RelPath + i.ModifiedTicks));
        if (changed || _playlist == null)
        {
            int before = cached?.Items.Count ?? 0;
            UseIndex(fresh, _playlist?.Current?.RelPath ?? resumeAt);
            if (cached != null && fresh.Items.Count > before) ShowToast($"{fresh.Items.Count - before:N0} new photos found");
        }
    }

    /// <summary>Take a scan result into use: guess missing locations, build the playlist, show a photo.</summary>
    void UseIndex(PhotoIndex index, string? startAt)
    {
        _index = index;
        LocationGuesser.Apply(index.Items, _s.GuessLocations, TimeSpan.FromHours(_s.GuessWindowHours));

        // Videos: playback comes in the next version, so they're left out for now.
        var items = index.Items.Where(i => !i.IsVideo).ToList();
        _playlist = new Playlist(items, _s.Order);
        _images.Clear();

        if (items.Count == 0)
        {
            ImgFront.Source = ImgBack.Source = null;
            Caption.Visibility = Visibility.Collapsed;
            EmptyTitle.Text = "No photos here";
            EmptyText.Text = "This folder and its subfolders don't contain any photos. Try another folder.";
            EmptyState.Visibility = Visibility.Visible;
            if (_mapReady) _ = Map.ExecuteScriptAsync("pm.clear()");
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        var first = _playlist.Start(startAt);
        if (first != null) Show(first, animateMap: false);
    }

    // ------------------------------------------------------------------ slideshow

    void Next()
    {
        var p = _playlist?.Next();
        if (p != null) Show(p, animateMap: _s.AnimateMap);
    }

    void Previous()
    {
        var p = _playlist?.Previous();
        if (p != null) Show(p, animateMap: _s.AnimateMap);
    }

    async void Show(PhotoItem item, bool animateMap)
    {
        if (_root == null || _playlist == null) return;
        int seq = ++_showSeq;
        RestartSlideTimer();

        var bmp = await _images.Get(Path.Combine(_root, item.RelPath), item.Orientation);
        if (seq != _showSeq) return; // the user already moved on

        if (bmp == null)
        {
            // Unreadable file (damaged, or a format nobody can decode): skip it quietly.
            if (_playing) _slideTimer.Interval = TimeSpan.FromMilliseconds(300);
            ShowCaption(item, unreadable: true);
            UpdateMap(item, animateMap);
            return;
        }

        CrossFade(bmp);
        ShowCaption(item, unreadable: false);
        UpdateMap(item, animateMap);

        // Preload the next photo while this one is on screen.
        if (_playlist.PeekNext() is { } next)
            _ = _images.Get(Path.Combine(_root, next.RelPath), next.Orientation);

        if (++_showsSinceSave >= 25)
        {
            _showsSinceSave = 0;
            _s.LastPhoto = item.RelPath;
            _s.Save();
            _geocoder.Save();
        }
    }

    void CrossFade(BitmapSource bmp)
    {
        ImgBack.Source = ImgFront.Source;
        ImgBack.Opacity = 1;
        ImgFront.BeginAnimation(OpacityProperty, null);
        ImgFront.Opacity = 0;
        ImgFront.Source = bmp;
        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = new QuadraticEase() };
        anim.Completed += (_, _) => { if (ImgFront.Source == bmp) ImgBack.Source = null; };
        ImgFront.BeginAnimation(OpacityProperty, anim);
    }

    void RestartSlideTimer()
    {
        _slideTimer.Stop();
        _slideTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _s.SecondsPerPhoto));
        if (_playing && _playlist != null) _slideTimer.Start();
    }

    void TogglePlay()
    {
        _playing = !_playing;
        UpdatePlayButton();
        RestartSlideTimer();
        ShowToast(_playing ? "Playing" : "Paused");
    }

    void UpdatePlayButton()
    {
        BtnPlay.Content = _playing ? "" : "";
        BtnPlay.ToolTip = _playing ? "Pause (Space)" : "Play (Space)";
    }

    void CycleOrder()
    {
        _s.Order = _s.Order switch
        {
            PlayOrder.ByDate => PlayOrder.ByName,
            PlayOrder.ByName => PlayOrder.RandomThisFolder,
            PlayOrder.RandomThisFolder => PlayOrder.RandomAllFolders,
            _ => PlayOrder.ByDate
        };
        _playlist?.SetOrder(_s.Order);
        _s.Save();
        UpdateOrderButton();
        ShowToast("Order: " + OrderName(_s.Order));
        if (_playlist?.Current is { } cur) UpdateMap(cur, animate: false);
    }

    void UpdateOrderButton()
    {
        bool random = _s.Order is PlayOrder.RandomThisFolder or PlayOrder.RandomAllFolders;
        BtnOrder.Content = random ? "" : "";
        BtnOrder.ToolTip = $"Order: {OrderName(_s.Order)} (R to change)";
    }

    public static string OrderName(PlayOrder o) => o switch
    {
        PlayOrder.ByDate => "by date taken",
        PlayOrder.ByName => "by folder and file name",
        PlayOrder.RandomThisFolder => "random, within the current folder",
        _ => "random, all folders"
    };

    // ------------------------------------------------------------------ caption

    void ShowCaption(PhotoItem item, bool unreadable)
    {
        _placeCts?.Cancel();
        if (!_s.ShowCaption) { Caption.Visibility = Visibility.Collapsed; return; }

        bool useLoc = item.HasLocation && (item.Source == LocationSource.Exif || _s.ShowGuessed);
        CaptionPlace.Visibility = _s.CaptionPlace || unreadable ? Visibility.Visible : Visibility.Collapsed;
        if (unreadable) CaptionPlace.Text = "This file can't be shown";
        else if (_s.CaptionPlace) CaptionPlace.Text = PlaceText(item, useLoc, town: useLoc ? _geocoder.Cached(item.Lat!.Value, item.Lon!.Value) : null);

        CaptionDate.Visibility = _s.CaptionDate && item.Taken.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (item.Taken.HasValue)
            CaptionDate.Text = item.Taken.Value.ToString("d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture);

        CaptionFile.Visibility = _s.CaptionFileName || unreadable ? Visibility.Visible : Visibility.Collapsed;
        CaptionFile.Text = item.RelPath;

        Caption.Visibility = CaptionPlace.Visibility == Visibility.Visible ||
                             CaptionDate.Visibility == Visibility.Visible ||
                             CaptionFile.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;

        // Town name arrives later (online lookup).
        if (!unreadable && useLoc && _s.CaptionPlace && _s.TownNamesOnline &&
            _geocoder.Cached(item.Lat!.Value, item.Lon!.Value) == null)
        {
            var cts = _placeCts = new CancellationTokenSource();
            _ = FillTownAsync(item, cts.Token);
        }
    }

    async Task FillTownAsync(PhotoItem item, CancellationToken ct)
    {
        try
        {
            var town = await _geocoder.GetTownAsync(item.Lat!.Value, item.Lon!.Value, ct);
            if (ct.IsCancellationRequested || town == null || _playlist?.Current != item) return;
            CaptionPlace.Text = PlaceText(item, true, town);
        }
        catch (OperationCanceledException) { }
    }

    static string PlaceText(PhotoItem item, bool useLoc, string? town)
    {
        if (!useLoc) return "Location unknown";
        string prefix = item.Source == LocationSource.Guessed ? "≈ " : "";
        string country = item.CountryName ?? "";
        if (!string.IsNullOrEmpty(town) && !string.Equals(town, country, StringComparison.OrdinalIgnoreCase))
            return prefix + (country.Length > 0 ? $"{town}, {country}" : town);
        if (country.Length > 0) return prefix + country;
        return prefix + string.Create(CultureInfo.InvariantCulture, $"{item.Lat:0.000}, {item.Lon:0.000}");
    }

    // ------------------------------------------------------------------ map

    void UpdateMap(PhotoItem item, bool animate)
    {
        if (!_mapReady || !_mapVisible || _playlist == null) return;

        bool useLoc = item.HasLocation && (item.Source == LocationSource.Exif || _s.ShowGuessed);
        if (!useLoc)
        {
            string mode = _s.NoLocation switch
            {
                NoLocationMode.ShowWorld => "world",
                NoLocationMode.HideMarker => "hide",
                _ => "keep"
            };
            _ = Map.ExecuteScriptAsync($"pm.none({JsonSerializer.Serialize(mode)}, {JsonSerializer.Serialize("No location for this photo")})");
            return;
        }

        double lat = item.Lat!.Value, lon = item.Lon!.Value;
        string view = _s.Zoom switch
        {
            ZoomMode.AlwaysWorld => "world",
            ZoomMode.AlwaysEurope => "europe",
            ZoomMode.AlwaysNetherlands => "nl",
            ZoomMode.Custom => "custom",
            _ => Regions.AutoView(lat, lon, item.CountryId) switch
            {
                MapView.Netherlands => "nl",
                MapView.Europe => "europe",
                _ => "world"
            }
        };

        // Trail: recently shown photos. A path in date/name order, a few jumps in random order.
        double[][] trail = Array.Empty<double[]>();
        string trailMode = _playlist.IsRandom ? "jumps" : "path";
        if (_s.ShowTrail)
        {
            int n = _playlist.IsRandom ? _s.RandomTrailJumps + 1 : _s.TrailLength;
            var pts = new List<double[]>();
            foreach (var p in _playlist.Recent(Math.Max(1, n) * 3))
            {
                if (!p.HasLocation || (p.Source == LocationSource.Guessed && !_s.ShowGuessed)) continue;
                var pt = new[] { p.Lat!.Value, p.Lon!.Value };
                if (pts.Count > 0 && pts[^1][0] == pt[0] && pts[^1][1] == pt[1]) continue; // same spot twice
                pts.Add(pt);
            }
            if (pts.Count > n) pts = pts.GetRange(pts.Count - n, n);
            trail = pts.ToArray();
        }

        var payload = new
        {
            lat,
            lon,
            approx = item.Source == LocationSource.Guessed,
            view,
            zoom = _s.CustomZoomLevel,
            trail,
            trailMode,
            animate,
            badge = item.Source == LocationSource.Guessed ? "≈ Location guessed from a photo taken around the same time" : ""
        };
        _ = Map.ExecuteScriptAsync($"pm.show({JsonSerializer.Serialize(payload, MapJson)})");
    }

    // ------------------------------------------------------------------ keys, toolbar, chrome

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (HandleKey(key)) e.Handled = true;
    }

    bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Right: case Key.PageDown: Next(); return true;
            case Key.Left: case Key.PageUp: Previous(); return true;
            case Key.Space: TogglePlay(); return true;
            case Key.F11: case Key.F: ToggleFullscreen(); return true;
            case Key.Escape: if (_fullscreen) { ToggleFullscreen(); return true; } return false;
            case Key.O: _ = PickFolderAsync(); return true;
            case Key.S: OpenSettings(); return true;
            case Key.M: ToggleMap(); return true;
            case Key.R: CycleOrder(); return true;
            default: return false;
        }
    }

    void BtnPrev_Click(object sender, RoutedEventArgs e) => Previous();
    void BtnNext_Click(object sender, RoutedEventArgs e) => Next();
    void BtnPlay_Click(object sender, RoutedEventArgs e) => TogglePlay();
    void BtnOrder_Click(object sender, RoutedEventArgs e) => CycleOrder();
    void BtnMap_Click(object sender, RoutedEventArgs e) => ToggleMap();
    void BtnSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    void BtnFull_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    void ShowChrome()
    {
        if (Toolbar.Opacity < 1) Fade(Toolbar, 1);
        Toolbar.IsHitTestVisible = true;
        if (Cursor == Cursors.None) Cursor = null;
        _idleTimer.Stop();
        if (_playlist != null) _idleTimer.Start();
    }

    void HideChrome()
    {
        _idleTimer.Stop();
        if (Toolbar.IsMouseOver) { _idleTimer.Start(); return; }
        Fade(Toolbar, 0);
        Toolbar.IsHitTestVisible = false;
        if (_fullscreen) Cursor = Cursors.None;
    }

    static void Fade(UIElement el, double to) =>
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(250)));

    void ShowToast(string text)
    {
        ToastText.Text = text;
        Fade(Toast, 1);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    void SetStatus(string? text)
    {
        Status.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = text ?? "";
    }

    void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _prevState = WindowState;
            _prevStyle = WindowStyle;
            _prevResize = ResizeMode;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal; // needed so Maximized covers the taskbar
            WindowState = WindowState.Maximized;
            _fullscreen = true;
            BtnFull.Content = "";
        }
        else
        {
            WindowStyle = _prevStyle;
            ResizeMode = _prevResize;
            WindowState = _prevState;
            _fullscreen = false;
            BtnFull.Content = "";
            Cursor = null;
        }
    }

    void ToggleMap()
    {
        _mapVisible = !_mapVisible;
        if (_mapVisible)
        {
            ApplyLayoutSettings();
            if (_playlist?.Current is { } cur) UpdateMap(cur, animate: false);
        }
        else
        {
            SaveMapWidth();
            MapCol.MinWidth = 0;
            MapCol.Width = new GridLength(0);
            SplitCol.Width = new GridLength(0);
            Splitter.Visibility = Visibility.Collapsed;
        }
    }

    void ApplyLayoutSettings()
    {
        double map = Math.Clamp(_s.MapWidthPercent, 15, 70);
        PhotoCol.Width = new GridLength(100 - map, GridUnitType.Star);
        MapCol.Width = new GridLength(map, GridUnitType.Star);
        MapCol.MinWidth = 160;
        SplitCol.Width = new GridLength(5);
        Splitter.Visibility = Visibility.Visible;
    }

    void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) => SaveMapWidth();

    void SaveMapWidth()
    {
        if (!_mapVisible) return;
        double total = PhotoCol.ActualWidth + MapCol.ActualWidth;
        if (total > 0) _s.MapWidthPercent = Math.Round(MapCol.ActualWidth / total * 100, 1);
    }

    // ------------------------------------------------------------------ settings

    void OpenSettings()
    {
        SaveMapWidth();
        var old = _s.Clone();
        var dlg = new SettingsWindow(_s.Clone()) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        _s = dlg.Result;
        _s.LastFolder = old.LastFolder;
        _s.LastPhoto = _playlist?.Current?.RelPath;
        _s.Save();

        UpdateOrderButton();
        if (_playlist != null && _s.Order != old.Order) _playlist.SetOrder(_s.Order);

        bool guessChanged = _s.GuessLocations != old.GuessLocations || _s.GuessWindowHours != old.GuessWindowHours;
        if (guessChanged && _index != null)
            LocationGuesser.Apply(_index.Items, _s.GuessLocations, TimeSpan.FromHours(_s.GuessWindowHours));

        RestartSlideTimer();
        if (_playlist?.Current is { } cur)
        {
            ShowCaption(cur, unreadable: false);
            UpdateMap(cur, animate: true);
        }
    }

    // ------------------------------------------------------------------ misc

    void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}

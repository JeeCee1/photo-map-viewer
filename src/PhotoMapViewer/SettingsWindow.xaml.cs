using System.Windows;
using System.Windows.Controls;
using PhotoMap.Core;

namespace PhotoMapViewer;

public partial class SettingsWindow : Window
{
    public AppSettings Result { get; private set; }
    bool _loading = true; // sliders fire events while the window is still being built

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Result = settings;
        Load(settings);
    }

    void Load(AppSettings s)
    {
        _loading = true;

        Seconds.Value = Math.Clamp(s.SecondsPerPhoto, 2, 60);
        Order.SelectedIndex = (int)s.Order;
        StartFullscreen.IsChecked = s.StartFullscreen;

        ShowCaption.IsChecked = s.ShowCaption;
        CaptionPlace.IsChecked = s.CaptionPlace;
        CaptionDate.IsChecked = s.CaptionDate;
        CaptionFileName.IsChecked = s.CaptionFileName;

        Zoom.SelectedIndex = (int)s.Zoom;
        CustomZoom.Value = Math.Clamp(s.CustomZoomLevel, 3, 18);
        AnimateMap.IsChecked = s.AnimateMap;
        ShowTrail.IsChecked = s.ShowTrail;
        TrailLength.Value = Math.Clamp(s.TrailLength, 2, 200);
        RandomJumps.Value = Math.Clamp(s.RandomTrailJumps, 0, 15);
        TownNames.IsChecked = s.TownNamesOnline;
        SuggestFromNames.IsChecked = s.SuggestFromNames;

        GuessLocations.IsChecked = s.GuessLocations;
        GuessWindow.Value = Math.Clamp(s.GuessWindowHours, 1, 12);
        ShowGuessed.IsChecked = s.ShowGuessed;
        NoLocation.SelectedIndex = (int)s.NoLocation;

        ShowVideos.IsChecked = s.ShowVideos;

        _loading = false;
        UpdateLabels();
        UpdateEnabled();
    }

    void Save(AppSettings s)
    {
        s.SecondsPerPhoto = Seconds.Value;
        s.Order = (PlayOrder)Math.Max(0, Order.SelectedIndex);
        s.StartFullscreen = StartFullscreen.IsChecked == true;

        s.ShowCaption = ShowCaption.IsChecked == true;
        s.CaptionPlace = CaptionPlace.IsChecked == true;
        s.CaptionDate = CaptionDate.IsChecked == true;
        s.CaptionFileName = CaptionFileName.IsChecked == true;

        s.Zoom = (ZoomMode)Math.Max(0, Zoom.SelectedIndex);
        s.CustomZoomLevel = (int)CustomZoom.Value;
        s.AnimateMap = AnimateMap.IsChecked == true;
        s.ShowTrail = ShowTrail.IsChecked == true;
        s.TrailLength = (int)TrailLength.Value;
        s.RandomTrailJumps = (int)RandomJumps.Value;
        s.TownNamesOnline = TownNames.IsChecked == true;
        s.SuggestFromNames = SuggestFromNames.IsChecked == true;

        s.GuessLocations = GuessLocations.IsChecked == true;
        s.GuessWindowHours = GuessWindow.Value;
        s.ShowGuessed = ShowGuessed.IsChecked == true;
        s.NoLocation = (NoLocationMode)Math.Max(0, NoLocation.SelectedIndex);
    }

    void UpdateLabels()
    {
        if (SecondsLabel == null) return;
        SecondsLabel.Text = $"{Seconds.Value:0} sec";
        CustomZoomLabel.Text = CustomZoom.Value switch
        {
            <= 5 => $"{CustomZoom.Value:0} (country)",
            <= 8 => $"{CustomZoom.Value:0} (region)",
            <= 12 => $"{CustomZoom.Value:0} (town)",
            <= 15 => $"{CustomZoom.Value:0} (streets)",
            _ => $"{CustomZoom.Value:0} (buildings)"
        };
        TrailLabel.Text = $"{TrailLength.Value:0} photos";
        JumpsLabel.Text = RandomJumps.Value == 0 ? "off" : $"{RandomJumps.Value:0} jumps";
        WindowLabel.Text = GuessWindow.Value == 1 ? "1 hour apart" : $"{GuessWindow.Value:0} hours apart";
    }

    void UpdateEnabled()
    {
        if (CaptionOptions == null) return;
        CaptionOptions.IsEnabled = ShowCaption.IsChecked == true;
        TrailOptions.IsEnabled = ShowTrail.IsChecked == true;
        GuessOptions.IsEnabled = GuessLocations.IsChecked == true;
        bool custom = Zoom.SelectedIndex == (int)ZoomMode.Custom;
        CustomZoomPanel.IsEnabled = custom;
        CustomZoomText.Opacity = custom ? 1 : 0.45;
    }

    void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loading) UpdateLabels();
    }

    void Update_Enabled(object sender, RoutedEventArgs e)
    {
        if (!_loading) UpdateEnabled();
    }

    void Zoom_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) UpdateEnabled();
    }

    void Reset_Click(object sender, RoutedEventArgs e) => Load(new AppSettings());

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        Save(Result);
        DialogResult = true;
    }
}

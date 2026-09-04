using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinFinger.Services;
using WinFinger.ViewModels;

namespace WinFinger.Views;

/// <summary>Live-preview appearance panel: background mode, any color, image, glass tuning.</summary>
public partial class AppearanceWindow : Window
{
    private static readonly string[] Swatches =
    {
        "#0A0A0F", "#1A1A22", "#232330", "#16283E", "#1D1440", "#3A1030",
        "#3D0F14", "#0F3324", "#33270F", "#0E3338", "#26262B", "#3C3C46"
    };

    private readonly AppViewModel _model;
    private readonly IslandWindow _island;
    private readonly DispatcherTimer _saveTimer;
    private bool _savePending;
    private bool _loading = true;

    public AppearanceWindow(AppViewModel model, IslandWindow island)
    {
        _model = model;
        _island = island;
        InitializeComponent();
        // 滑块每帧都会触发 ValueChanged：预览立即生效，落盘去抖 400ms
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => FlushSave();
        Closed += (_, _) => FlushSave();
        BuildSwatches();
        LoadFromSettings();
        _loading = false;
    }

    private AppSettings S => _model.SettingsStore.Settings;

    private void LoadFromSettings()
    {
        _loading = true;
        ModeGlass.IsChecked = S.BackgroundMode == "glass";
        ModeColor.IsChecked = S.BackgroundMode == "color";
        ModeImage.IsChecked = S.BackgroundMode == "image";
        SetHexUi(S.BackgroundColor);
        ImagePathText.Text = string.IsNullOrEmpty(S.BackgroundImagePath) ? "未选择图片" : S.BackgroundImagePath;
        DimSlider.Value = S.ImageDim;
        DarkSlider.Value = S.GlassDarkness;
        GlassSatSlider.Value = S.GlassSaturation;
        GhostSlider.Value = S.GhostOpacity;
        GlintCheck.IsChecked = S.GlintEnabled;
        ChromaCheck.IsChecked = S.ChromaticEnabled;
        PowerSaverCheck.IsChecked = S.PowerSaver;
        UpdatePanelVisibility();
        _loading = false;
    }

    private void BuildSwatches()
    {
        foreach (var hex in Swatches)
        {
            var b = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 7, 7),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                BorderBrush = (Brush)FindResource("Brush.Stroke"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            b.MouseLeftButtonUp += (_, e) =>
            {
                SetHexUi(hex);
                CommitColor(hex);
                e.Handled = true;
            };
            SwatchPanel.Children.Add(b);
        }
    }

    private void UpdatePanelVisibility()
    {
        ColorPanel.Visibility = ModeColor.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ImagePanel.Visibility = ModeImage.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SatLabel.Visibility = GlassSatSlider.Visibility =
            ModeGlass.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── events ──

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.BackgroundMode = ModeColor.IsChecked == true ? "color"
            : ModeImage.IsChecked == true ? "image" : "glass";
        UpdatePanelVisibility();
        SaveAndApply();
    }

    private void OnHsvChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        var c = FromHsv(HueSlider.Value, SatSlider.Value, ValSlider.Value);
        string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        _loading = true;
        HexBox.Text = hex;
        ColorPreview.Background = new SolidColorBrush(c);
        _loading = false;
        CommitColor(hex);
    }

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(HexBox.Text.Trim());
            SetHexUi($"#{c.R:X2}{c.G:X2}{c.B:X2}");
            CommitColor(HexBox.Text.Trim());
        }
        catch
        {
            HexBox.Text = S.BackgroundColor;
        }
    }

    private void OnPickImage(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
            Title = "选择岛背景图片"
        };
        if (dlg.ShowDialog(this) != true) return;
        S.BackgroundImagePath = dlg.FileName;
        ImagePathText.Text = dlg.FileName;
        if (ModeImage.IsChecked != true) { ModeImage.IsChecked = true; return; } // OnModeChanged saves
        SaveAndApply();
    }

    private void OnTuneChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ImageDim = DimSlider.Value;
        S.GlassDarkness = DarkSlider.Value;
        S.GlassSaturation = GlassSatSlider.Value;
        S.GhostOpacity = GhostSlider.Value;
        S.GlintEnabled = GlintCheck.IsChecked == true;
        S.ChromaticEnabled = ChromaCheck.IsChecked == true;
        S.PowerSaver = PowerSaverCheck.IsChecked == true;
        SaveAndApply();
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        S.BackgroundMode = "glass";
        S.BackgroundColor = "#1A1A22";
        S.BackgroundImagePath = "";
        S.ImageDim = 0.3;
        S.GlassDarkness = 0.55;
        S.GlassSaturation = 1.6;
        S.GhostOpacity = 0.4;
        S.GlintEnabled = true;
        S.ChromaticEnabled = true;
        LoadFromSettings();
        SaveAndApply();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnDragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not (TextBox or Slider or Button or CheckBox))
            try { DragMove(); } catch { }
    }

    // ── helpers ──

    private void CommitColor(string hex)
    {
        S.BackgroundColor = hex;
        if (ModeColor.IsChecked != true) return;
        SaveAndApply();
    }

    /// <summary>预览立即刷新，设置文件的写入排到去抖计时器里。</summary>
    private void SaveAndApply()
    {
        _island.ApplyBackground();
        _model.ApplyPowerSaver();
        _savePending = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>把挂起的保存立刻落盘（去抖到期 / 窗口关闭）。</summary>
    private void FlushSave()
    {
        _saveTimer.Stop();
        if (!_savePending) return;
        _savePending = false;
        _model.SettingsStore.Save();
    }

    private void SetHexUi(string hex)
    {
        _loading = true;
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            HexBox.Text = hex;
            ColorPreview.Background = new SolidColorBrush(c);
            var (h, s, v) = ToHsv(c);
            HueSlider.Value = h;
            SatSlider.Value = s;
            ValSlider.Value = v;
        }
        catch
        {
        }
        _loading = false;
    }

    private static (double h, double s, double v) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        double h = d == 0 ? 0
            : max == r ? 60 * (((g - b) / d) % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}

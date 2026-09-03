using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace WinFinger.Views;

/// <summary>把一张截图钉在桌面上：拖动移动、滚轮缩放、Esc / 双击关闭。</summary>
public partial class PinnedImageWindow : Window
{
    private const double MaxInitialSide = 520;
    private double _scale = 1;
    private readonly double _baseWidth;
    private readonly double _baseHeight;

    public PinnedImageWindow(string imagePath)
    {
        InitializeComponent();
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(imagePath);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        Pinned.Source = bmp;

        double fit = Math.Min(1, Math.Min(MaxInitialSide / bmp.PixelWidth, MaxInitialSide / bmp.PixelHeight));
        _baseWidth = Math.Max(48, Math.Round(bmp.PixelWidth * fit));
        _baseHeight = Math.Max(48, Math.Round(bmp.PixelHeight * fit));
        Pinned.Width = _baseWidth;
        Pinned.Height = _baseHeight;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            Close();
            return;
        }
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已抬起
        }
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.2, 6.0);
        Pinned.Width = Math.Round(_baseWidth * _scale);
        Pinned.Height = Math.Round(_baseHeight * _scale);
        e.Handled = true;
    }
}

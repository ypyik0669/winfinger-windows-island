using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinFinger.Interop;

namespace WinFinger.Views;

/// <summary>
/// 单个显示器上的截图遮罩层：铺满该显示器、显示冻结画面、拖选区域。
/// 结果（设备像素、虚拟屏坐标）通过共享的 TaskCompletionSource 回给 ScreenshotService；
/// v1 选区限定在按下时所在的这块显示器内。
/// </summary>
public partial class ScreenCaptureOverlay : Window
{
    /// <summary>小于这个尺寸（设备像素）视为误点，按取消处理。</summary>
    private const int MinSide = 4;

    private readonly NativeMethods.RECT _monitor;
    private readonly TaskCompletionSource<Int32Rect?> _result;
    private readonly int _widthDev;
    private readonly int _heightDev;

    private double _scaleX = 1;
    private double _scaleY = 1;
    private bool _dragging;
    private Point _startDev;
    private Point _currentDev;
    private bool _hasSelection;

    internal ScreenCaptureOverlay(BitmapSource frozenCrop, NativeMethods.RECT monitor,
        TaskCompletionSource<Int32Rect?> result)
    {
        InitializeComponent();
        _monitor = monitor;
        _result = result;
        _widthDev = monitor.Right - monitor.Left;
        _heightDev = monitor.Bottom - monitor.Top;
        Frozen.Source = frozenCrop;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        // PerMonitorV2 下 WPF 的 Left/Top 是 DIP，跨显示器不可靠 —— 直接用设备像素摆位。
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, _monitor.Left, _monitor.Top,
            _widthDev, _heightDev, NativeMethods.SWP_SHOWWINDOW);
        // 遮罩层自己也不入镜，避免连拍时拍到上一层遮罩（自动化验证时用环境变量放行）。
        if (Environment.GetEnvironmentVariable("WINFINGER_CAPTURABLE") != "1")
            NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);

        var target = PresentationSource.FromVisual(this)?.CompositionTarget;
        if (target is not null)
        {
            var m = target.TransformToDevice;
            if (m.M11 > 0) _scaleX = m.M11;
            if (m.M22 > 0) _scaleY = m.M22;
        }
        Redraw();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Keyboard.Focus(this);
    }

    /// <summary>鼠标位置（窗口 DIP）→ 该显示器内的设备像素，并夹到显示器范围内。</summary>
    private Point ToDevice(Point dip) => new(
        Math.Clamp(dip.X * _scaleX, 0, _widthDev),
        Math.Clamp(dip.Y * _scaleY, 0, _heightDev));

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _hasSelection = false;
        _startDev = _currentDev = ToDevice(e.GetPosition(this));
        CaptureMouse();
        Redraw();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _currentDev = ToDevice(e.GetPosition(this));
        _hasSelection = true;
        Redraw();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _currentDev = ToDevice(e.GetPosition(this));
        ReleaseMouseCapture();

        var rect = SelectionDevice();
        if (rect.Width >= MinSide && rect.Height >= MinSide) Confirm(rect);
        else Cancel();
        e.Handled = true;
    }

    private void OnMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        Cancel();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Return)
        {
            var rect = SelectionDevice();
            if (_hasSelection && rect.Width >= MinSide && rect.Height >= MinSide) Confirm(rect);
            else Cancel();
            e.Handled = true;
        }
    }

    /// <summary>当前选区（该显示器内的设备像素，整数）。</summary>
    private Int32Rect SelectionDevice()
    {
        if (!_hasSelection) return Int32Rect.Empty;
        int x1 = (int)Math.Round(Math.Min(_startDev.X, _currentDev.X));
        int y1 = (int)Math.Round(Math.Min(_startDev.Y, _currentDev.Y));
        int x2 = (int)Math.Round(Math.Max(_startDev.X, _currentDev.X));
        int y2 = (int)Math.Round(Math.Max(_startDev.Y, _currentDev.Y));
        return new Int32Rect(x1, y1, x2 - x1, y2 - y1);
    }

    private void Confirm(Int32Rect local)
    {
        // 换算到虚拟屏设备坐标，服务端据此从冻结图上裁剪。
        _result.TrySetResult(new Int32Rect(_monitor.Left + local.X, _monitor.Top + local.Y, local.Width, local.Height));
    }

    private void Cancel() => _result.TrySetResult(null);

    private void Redraw()
    {
        double fullW = _widthDev / _scaleX;
        double fullH = _heightDev / _scaleY;
        var full = new RectangleGeometry(new Rect(0, 0, fullW, fullH));
        var rect = SelectionDevice();

        if (rect.Width <= 0 || rect.Height <= 0)
        {
            Mask.Data = full;
            SelectionBorder.Visibility = Visibility.Collapsed;
            SizeLabel.Visibility = Visibility.Collapsed;
            Hint.Visibility = Visibility.Visible;
            return;
        }

        double x = rect.X / _scaleX, y = rect.Y / _scaleY;
        double w = rect.Width / _scaleX, h = rect.Height / _scaleY;
        Mask.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full,
            new RectangleGeometry(new Rect(x, y, w, h)));

        SelectionBorder.Visibility = Visibility.Visible;
        SelectionBorder.Width = w;
        SelectionBorder.Height = h;
        Canvas.SetLeft(SelectionBorder, x);
        Canvas.SetTop(SelectionBorder, y);

        SizeText.Text = $"{rect.Width} × {rect.Height}";
        SizeLabel.Visibility = Visibility.Visible;
        SizeLabel.UpdateLayout();
        double labelW = SizeLabel.ActualWidth, labelH = SizeLabel.ActualHeight;
        double ly = y - labelH - 6;
        if (ly < 0) ly = Math.Min(y + 6, fullH - labelH);
        Canvas.SetLeft(SizeLabel, Math.Clamp(x, 0, Math.Max(0, fullW - labelW)));
        Canvas.SetTop(SizeLabel, Math.Max(0, ly));

        // 选区压到提示条上时把提示藏起来，别挡着看画面。
        Hint.Visibility = y < 90 ? Visibility.Collapsed : Visibility.Visible;
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinFinger.Controls;
using WinFinger.Interop;
using WinFinger.ViewModels;

namespace WinFinger.Views;

public partial class IslandWindow : Window
{
    // Island geometry (DIP)
    private const double CompactWidth = 300;
    private const double CompactHeight = 36;
    private const double CompactRadius = 18;
    private const double ExpandedRadius = 24;               // mac: 24
    private const double DesignWidth = 920;                 // mac NotchLayout.expandedDesignSize
    private const double DesignHeight = 520;
    private const double MinExpandedWidth = 560;            // mac minimumExpandedSize
    private const double StageTopInset = 8;                 // IslandBorder top margin (shadow room)
    private const double SnapToTopDistance = 16;            // mac finishDrag: distanceToTop <= 16

    private const double NotificationWidth = 430;
    private const double HoverWidth = 390;

    private readonly AppViewModel _model;
    private readonly ScaleTransform ExpandedScale = new(1, 1); // mac scaleEffect on the design-size panel
    private IntPtr _hwnd;
    private IntPtr _mouseHook;
    private NativeMethods.LowLevelMouseProc? _mouseProc; // field: keeps delegate alive against GC
    private readonly System.Windows.Threading.DispatcherTimer _notificationTimer;
    private bool _notificationShowing;
    private bool _hovering;

    // ghost mode (fade + click-through when the cursor is far away)
    private readonly System.Windows.Threading.DispatcherTimer _ghostTimer;
    private bool _ghosted;
    private const double GhostEnterDistance = 160; // px, become ghost beyond this
    private const double GhostExitDistance = 100;  // px, solidify within this

    // self-made frosted glass (live capture behind the island)
    private LiveGlassCapture? _glass;
    private System.Windows.Threading.DispatcherTimer? _glassTimer;
    private bool _morphing; // size animation in flight: skip captures so they don't fight for frames
    private DateTime _lastDragCapture;

    // drag (compact bar or expanded header) → floating
    private bool _dragging;
    private bool _dragArmed;
    private System.Windows.Point _dragStartScreen;
    private double _dragStartLeft;
    private double _dragStartTop;
    private double? _preExpandTop;  // set when the window is shifted up to fit the expanded panel
    private double? _preExpandLeft; // set when the window is shifted sideways to fit the expanded panel
    private DateTime _lastCompactClick;

    // corner resize (expanded)
    private bool _resizing;
    private FrameworkElement? _resizeHandle;
    private double _resizeWidth;

    public IslandWindow(AppViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        CompactView.Initialize(model);
        ExpandedView.Initialize(model);
        ExpandedView.LayoutTransform = ExpandedScale;
        ExpandedView.HeaderDragged += OnHeaderDragged;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            MigrateLegacyPosition();
            ApplyDockPosition(animated: false);
            _model.ClipboardMonitor.Attach(this);
            _model.Hotkeys.Attach(this);
            RegisterClipboardHotkey();
            RegisterScreenshotHotkeys();
            _glass = new LiveGlassCapture();
            GlassBrush.ImageSource = _glass.Bitmap;
            _glassTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(160)
            };
            _glassTimer.Tick += (_, _) => CaptureGlass();
            // migrate the legacy toggle once: old "off" becomes solid-color mode
            if (!_model.SettingsStore.Settings.LiveGlassEnabled &&
                _model.SettingsStore.Settings.BackgroundMode == "glass")
                _model.SettingsStore.Settings.BackgroundMode = "color";
            ApplyBackground();
            UpdateExpandedScale();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        model.PropertyChanged += OnModelPropertyChanged;
        model.Theme.PaletteChanged += _ => Dispatcher.BeginInvoke(ApplyBackground);

        _notificationTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(2600)
        };
        _notificationTimer.Tick += (_, _) => HideNotification();
        model.Notifications.NotificationPosted += OnNotificationPosted;
        model.Media.PropertyChanged += OnMediaChangedForGlow;

        _ghostTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _ghostTimer.Tick += (_, _) => UpdateGhostState();
        _ghostTimer.Start();

        StartGlintBreathing();

        // periodic working-set trim keeps the Task Manager footprint honest for a tray-style app
        var trimTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        trimTimer.Tick += (_, _) => { if (!_model.IsExpanded) TrimWorkingSet(); };
        trimTimer.Start();

        // first trim shortly after startup, once JIT/first-render churn is over
        var firstTrim = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        firstTrim.Tick += (_, _) => { firstTrim.Stop(); TrimWorkingSet(); };
        firstTrim.Start();
    }

    private static void TrimWorkingSet()
    {
        GC.Collect(2, GCCollectionMode.Optimized);
        GC.WaitForPendingFinalizers();
        NativeMethods.SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle,
            new IntPtr(-1), new IntPtr(-1));
    }

    // ── Geometry helpers ──

    private double DeviceScale => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

    /// <summary>Largest expanded width allowed on the current monitor (mac expandedSize(in:)).</summary>
    private double MaxExpandedWidth()
    {
        var monitor = MonitorRectDip(CurrentMonitor());
        return Math.Min(DesignWidth, Math.Max(MinExpandedWidth, monitor.Width - 64));
    }

    /// <summary>Requested expanded size honouring the user's width, clamped and aspect-locked.</summary>
    private (double width, double height) ExpandedSize()
    {
        double max = MaxExpandedWidth();
        double width = max;
        if (_model.ExpandedUserWidth > 0)
            width = Math.Min(Math.Max(_model.ExpandedUserWidth, Math.Min(MinExpandedWidth, max)), max);
        return (width, width * DesignHeight / DesignWidth);
    }

    private void UpdateExpandedScale()
    {
        var (width, _) = ExpandedSize();
        double scale = width / DesignWidth;
        ExpandedScale.ScaleX = scale;
        ExpandedScale.ScaleY = scale;
    }

    private IntPtr CurrentMonitor()
    {
        var center = IslandCenterDevice();
        return NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = (int)center.X, Y = (int)center.Y },
            NativeMethods.MONITOR_DEFAULTTONEAREST);
    }

    private static IntPtr PrimaryMonitor() =>
        NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = 0, Y = 0 }, NativeMethods.MONITOR_DEFAULTTONEAREST);

    private Rect MonitorRectDip(IntPtr monitor)
    {
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        double s = DeviceScale;
        var r = info.rcMonitor;
        return new Rect(r.Left / s, r.Top / s, (r.Right - r.Left) / s, (r.Bottom - r.Top) / s);
    }

    private Rect VirtualScreenDip() => new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

    private System.Windows.Point IslandCenterDevice()
    {
        try
        {
            return IslandBorder.PointToScreen(new Point(IslandBorder.ActualWidth / 2, IslandBorder.ActualHeight / 2));
        }
        catch
        {
            double s = DeviceScale;
            return new Point((Left + Width / 2) * s, (Top + StageTopInset + CompactHeight / 2) * s);
        }
    }

    /// <summary>Compact island rect in window-stage DIP coordinates: top-centred in the stage.</summary>
    private Rect CompactRectDip() => new(Left + Width / 2 - CompactWidth / 2, Top + StageTopInset, CompactWidth, CompactHeight);

    private bool IsFloating => _model.DockMode == "floating";

    private void MigrateLegacyPosition()
    {
        var s = _model.SettingsStore.Settings;
        if (!double.IsNaN(s.FloatingLeft) && !double.IsNaN(s.FloatingTop)) return;
        // old free-drag offsets: a pill dragged away from the top edge becomes a floating island
        if (s.IslandOffsetY > SnapToTopDistance)
        {
            s.FloatingLeft = (SystemParameters.PrimaryScreenWidth - Width) / 2 + s.IslandOffsetX;
            s.FloatingTop = s.IslandOffsetY;
            _model.DockMode = "floating";
        }
        else
        {
            s.FloatingLeft = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            s.FloatingTop = 0;
        }
        _model.SettingsStore.Save();
    }

    /// <summary>mac updateFrame: top → flush with the monitor's top edge, centred; floating → stored origin.</summary>
    private void ApplyDockPosition(bool animated)
    {
        var s = _model.SettingsStore.Settings;
        if (IsFloating)
        {
            double left = double.IsNaN(s.FloatingLeft) ? (SystemParameters.PrimaryScreenWidth - Width) / 2 : s.FloatingLeft;
            double top = double.IsNaN(s.FloatingTop) ? 0 : s.FloatingTop;
            if (double.IsNaN(s.FloatingTop) || (top <= 0 && !double.IsNaN(s.FloatingLeft) && s.FloatingTop == 0))
            {
                // mac defaultFloatingOrigin: upper-middle of the screen
                var monitor = MonitorRectDip(PrimaryMonitor());
                left = monitor.Left + monitor.Width / 2 - Width / 2;
                top = monitor.Top + monitor.Height * 0.32 - StageTopInset;
            }
            Left = left;
            Top = top;
            ClampPosition();
            IslandBorder.CornerRadius = new CornerRadius(_model.IsExpanded ? ExpandedRadius : CompactRadius);
        }
        else
        {
            // dock to the monitor holding the stored/last position
            var probe = new NativeMethods.POINT
            {
                X = (int)((double.IsNaN(s.FloatingLeft) ? 0 : s.FloatingLeft + Width / 2) * DeviceScale),
                Y = (int)((double.IsNaN(s.FloatingTop) ? 0 : s.FloatingTop + StageTopInset) * DeviceScale)
            };
            var monitor = MonitorRectDip(NativeMethods.MonitorFromPoint(probe, NativeMethods.MONITOR_DEFAULTTONEAREST));
            Left = monitor.Left + monitor.Width / 2 - Width / 2;
            Top = monitor.Top - StageTopInset;
            if (!_model.IsExpanded)
                IslandBorder.CornerRadius = new CornerRadius(2, 2, 14, 14); // mac: top corners meet the screen edge
        }
        IslandShadow.Opacity = IsFloating && !_model.IsExpanded ? 0.45 : 0.35;
        CaptureGlass();
    }

    /// <summary>Counter-phased opacity loops so light appears to drift around the glass rim.</summary>
    private void StartGlintBreathing()
    {
        var breathe = new DoubleAnimation(0.2, 0.95, TimeSpan.FromSeconds(2.8))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        var counter = new DoubleAnimation(0.9, 0.15, TimeSpan.FromSeconds(2.8))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        // slow ambience: no need to re-render the shadowed island subtree at 60fps
        Timeline.SetDesiredFrameRate(breathe, 20);
        Timeline.SetDesiredFrameRate(counter, 20);
        GlintA.BeginAnimation(OpacityProperty, breathe);
        GlintB.BeginAnimation(OpacityProperty, counter);
    }

    /// <summary>mac BreathingBorder: accent-tinted stroke that pulses (1.6s) and drifts while expanded.</summary>
    private void StartAccentBorder()
    {
        var accent = _model.Media.HasSession ? _model.Media.AccentColor
            : (TryFindResource("Brush.Teal") as SolidColorBrush)?.Color ?? Colors.Teal;
        AccentStop0.Color = Color.FromArgb(0x20, accent.R, accent.G, accent.B);
        AccentStop1.Color = Color.FromArgb(0xE0, accent.R, accent.G, accent.B);
        AccentStop2.Color = Color.FromArgb(0x70, accent.R, accent.G, accent.B);
        AccentStop3.Color = Color.FromArgb(0x20, accent.R, accent.G, accent.B);
        var pulse = new DoubleAnimation(0.5, 1.0, TimeSpan.FromSeconds(1.6))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        var drift = new PointAnimation(new Point(0, 0), new Point(1, 1), TimeSpan.FromSeconds(6))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(pulse, 20);
        Timeline.SetDesiredFrameRate(drift, 20);
        AccentBorder.BeginAnimation(OpacityProperty, pulse);
        AccentBorderBrush.BeginAnimation(LinearGradientBrush.StartPointProperty, drift);
    }

    private void StopAccentBorder()
    {
        AccentBorder.BeginAnimation(OpacityProperty, null);
        AccentBorderBrush.BeginAnimation(LinearGradientBrush.StartPointProperty, null);
        AccentBorder.Opacity = 0;
    }

    // ── Ghost mode: far cursor → translucent + click-through, near cursor → solid ──

    private void UpdateGhostState()
    {
        if (_hwnd == IntPtr.Zero || !IslandBorder.IsLoaded) return;

        if (_model.IsExpanded || _notificationShowing || _hovering || _dragging || _resizing)
        {
            if (_ghosted) SetGhosted(false);
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor)) return;
        Rect bounds;
        try
        {
            var topLeft = IslandBorder.PointToScreen(new Point(0, 0));
            var bottomRight = IslandBorder.PointToScreen(new Point(IslandBorder.ActualWidth, IslandBorder.ActualHeight));
            bounds = new Rect(topLeft, bottomRight);
        }
        catch
        {
            return;
        }
        double dx = Math.Max(Math.Max(bounds.Left - cursor.X, cursor.X - bounds.Right), 0);
        double dy = Math.Max(Math.Max(bounds.Top - cursor.Y, cursor.Y - bounds.Bottom), 0);
        double distance = Math.Sqrt(dx * dx + dy * dy);

        if (!_ghosted && distance > GhostEnterDistance) SetGhosted(true);
        else if (_ghosted && distance < GhostExitDistance) SetGhosted(false);
    }

    private void SetGhosted(bool ghosted)
    {
        _ghosted = ghosted;
        int style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        style = ghosted ? style | NativeMethods.WS_EX_TRANSPARENT : style & ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, style);
        double ghostOpacity = Math.Clamp(_model.SettingsStore.Settings.GhostOpacity, 0.1, 1.0);
        IslandBorder.BeginAnimation(OpacityProperty,
            new DoubleAnimation(ghosted ? ghostOpacity : 1.0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        // faded island doesn't need ambience: stop all recurring animation while ghosted,
        // but keep a slow glass refresh so the background never goes stale behind a new scene
        if (ghosted)
        {
            GlintA.BeginAnimation(OpacityProperty, null);
            GlintB.BeginAnimation(OpacityProperty, null);
            GlintA.Opacity = 0.3;
            GlintB.Opacity = 0.3;
            if (_glassTimer is not null) _glassTimer.Interval = TimeSpan.FromMilliseconds(1200);
        }
        else
        {
            if (_glassTimer is not null) _glassTimer.Interval = TimeSpan.FromMilliseconds(160);
            StartGlintBreathing();
            CaptureGlass();
        }
    }

    /// <summary>Applies the configured background: 纯黑 → solid, otherwise live glass / solid color / image.</summary>
    public void ApplyBackground()
    {
        if (_glassTimer is null) return;
        var s = _model.SettingsStore.Settings;
        _glassTimer.Stop();

        if (_model.AppearanceStyle == "black")
        {
            // mac 纯黑: palette background (expanded) / black @0.96 (compact) — always dark
            GlassLayer.Background = new SolidColorBrush(Color.FromRgb(0x05, 0x06, 0x07));
            ApplyAppearance();
            return;
        }

        switch (s.BackgroundMode)
        {
            case "image" when TryLoadImage(s.BackgroundImagePath, out var img):
                GlassLayer.Background = new ImageBrush(img)
                {
                    Stretch = Stretch.UniformToFill
                };
                break;
            case "color":
            case "image": // image failed to load: fall back to the solid color
                GlassLayer.Background = new SolidColorBrush(ParseColor(s.BackgroundColor));
                break;
            default: // glass
                GlassLayer.Background = GlassBrush;
                if (_glass is not null) _glass.Saturation = s.GlassSaturation;
                _glassTimer.Start();
                CaptureGlass();
                break;
        }
        ApplyAppearance();
    }

    /// <summary>Applies glass darkness and optional light-effect layers.</summary>
    public void ApplyAppearance()
    {
        var s = _model.SettingsStore.Settings;
        // 0.55 maps to the design-default brush alphas; beyond that, stack extra black on the dim layer
        double darkness = Math.Clamp(s.GlassDarkness, 0, 1);
        BodyTintLayer.Opacity = Math.Min(1, darkness / 0.55);
        // light palette (mac Liquid Glass 浅色): the whole panel whitens regardless of what's behind
        if (!_model.Theme.IsDark) BodyTintLayer.Opacity = Math.Max(BodyTintLayer.Opacity, 0.92);
        double extraDark = Math.Max(0, (darkness - 0.55) / 0.45) * 0.6;
        double imageDim = s.BackgroundMode == "image" && _model.AppearanceStyle != "black" ? Math.Clamp(s.ImageDim, 0, 0.8) : 0;
        ImageDimLayer.Opacity = _model.Theme.IsDark ? Math.Min(0.9, imageDim + extraDark) : imageDim * 0.5;
        if (_glass is not null) _glass.Saturation = s.GlassSaturation;
        ChromaticLayer.Visibility = s.ChromaticEnabled ? Visibility.Visible : Visibility.Collapsed;
        bool glints = s.GlintEnabled;
        GlintA.Visibility = glints ? Visibility.Visible : Visibility.Collapsed;
        GlintB.Visibility = glints ? Visibility.Visible : Visibility.Collapsed;
        // if the island is currently ghosted, reflect a changed fade opacity immediately
        if (_ghosted)
            IslandBorder.BeginAnimation(OpacityProperty,
                new DoubleAnimation(Math.Clamp(s.GhostOpacity, 0.1, 1.0), TimeSpan.FromMilliseconds(200)));
    }

    private static bool TryLoadImage(string path, out System.Windows.Media.Imaging.BitmapImage image)
    {
        image = null!;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return false;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 800;
            bmp.EndInit();
            bmp.Freeze();
            image = bmp;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Color.FromRgb(0x1A, 0x1A, 0x22);
        }
    }

    /// <summary>One glass frame: grab what's behind IslandBorder (device px) into the ImageBrush.</summary>
    private void CaptureGlass()
    {
        if (_glass is null || !IslandBorder.IsLoaded || _morphing) return;
        if (_model.AppearanceStyle == "black" || _model.SettingsStore.Settings.BackgroundMode != "glass") return;
        try
        {
            var topLeft = IslandBorder.PointToScreen(new Point(0, 0));
            var bottomRight = IslandBorder.PointToScreen(new Point(IslandBorder.ActualWidth, IslandBorder.ActualHeight));
            _glass.Capture((int)topLeft.X, (int)topLeft.Y,
                (int)(bottomRight.X - topLeft.X), (int)(bottomRight.Y - topLeft.Y));
        }
        catch
        {
            // island not on screen yet
        }
    }

    // ── Cover-color glow (pulses while music plays) ──

    private void OnMediaChangedForGlow(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Services.MediaService.IsPlaying) or nameof(Services.MediaService.AccentColor))
        {
            UpdateGlow();
            if (_model.IsExpanded && !_model.IsDraggingPanel) StartAccentBorder();
        }
    }

    private void UpdateGlow()
    {
        if (_model.Media.IsPlaying)
        {
            // adaptive tint: bleed the album accent into the glass
            TintBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(_model.Media.AccentColor, TimeSpan.FromMilliseconds(600)));
            TintLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0.12, TimeSpan.FromMilliseconds(600)));
            IslandShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ColorProperty,
                new ColorAnimation(_model.Media.AccentColor, TimeSpan.FromMilliseconds(600)));
            var pulse = new DoubleAnimation(0.45, 0.85, TimeSpan.FromMilliseconds(1600))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(pulse, 20);
            IslandShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, pulse);
        }
        else
        {
            TintLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(600)));
            IslandShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ColorProperty,
                new ColorAnimation(Colors.Black, TimeSpan.FromMilliseconds(600)));
            IslandShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                new DoubleAnimation(0.35, TimeSpan.FromMilliseconds(600)));
        }
    }

    // ── Drag (compact bar): moves the island, becomes floating, snaps back to the top edge ──

    private void OnIslandMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var screen = CursorScreen();
        ContinueDrag(new Point(screen.X - _dragStartScreen.X, screen.Y - _dragStartScreen.Y));
    }

    /// <summary>
    /// 拖动一律用系统光标位置取样。PointToScreen(e.GetPosition(…)) 把消息里的窗口内坐标
    /// 加上窗口的当前位置，而拖动过程中窗口自己一直在动 —— 两者不同步就会自激振荡
    /// （贴边、面板展开时最明显）。
    /// </summary>
    private static Point CursorScreen() =>
        NativeMethods.GetCursorPos(out var p) ? new Point(p.X, p.Y) : new Point(0, 0);

    private void BeginDrag(System.Windows.Point screenPoint)
    {
        _dragArmed = true;
        _dragging = false;
        _dragStartScreen = screenPoint;
        _dragStartLeft = Left;
        _dragStartTop = Top;
    }

    private void ContinueDrag(System.Windows.Point deltaDevice)
    {
        if (!_dragging && (Math.Abs(deltaDevice.X) > 4 || Math.Abs(deltaDevice.Y) > 4))
        {
            _dragging = true;
            _model.IsDraggingPanel = true;
            // mac beginFloating: the island detaches as soon as a drag starts
            if (!IsFloating) _model.DockMode = "floating";
            IslandBorder.CornerRadius = new CornerRadius(_model.IsExpanded ? ExpandedRadius : CompactRadius);
            StopAccentBorder();
        }
        if (!_dragging) return;
        double scale = DeviceScale;
        Left = _dragStartLeft + deltaDevice.X / scale;
        Top = _dragStartTop + deltaDevice.Y / scale;
        ClampPosition();
        // lightweight glass while dragging: at most ~15 captures/s
        if ((DateTime.UtcNow - _lastDragCapture).TotalMilliseconds > 66)
        {
            _lastDragCapture = DateTime.UtcNow;
            CaptureGlass();
        }
    }

    /// <summary>mac finishDrag: snap to the drop monitor's top edge when within 16 DIP, else persist floating.</summary>
    private void FinishDrag()
    {
        _dragArmed = false;
        if (!_dragging) return;
        _dragging = false;
        _model.IsDraggingPanel = false;

        var s = _model.SettingsStore.Settings;
        var monitor = MonitorRectDip(CurrentMonitor());
        double islandTop = Top + StageTopInset;
        if (islandTop - monitor.Top <= SnapToTopDistance)
        {
            s.FloatingLeft = monitor.Left + monitor.Width / 2 - Width / 2;
            s.FloatingTop = monitor.Top;
            _model.SettingsStore.Save();
            _model.DockMode = "top"; // triggers ApplyDockPosition
            if (_model.DockMode == "top") ApplyDockPosition(animated: true);
        }
        else
        {
            s.FloatingLeft = Left;
            s.FloatingTop = Top;
            _model.SettingsStore.Save();
        }
        if (_model.IsExpanded) StartAccentBorder();
        CaptureGlass();
    }

    private void OnHeaderDragged(System.Windows.Point deltaDevice, bool ended)
    {
        if (!_dragArmed)
        {
            // first callback: anchor at the current pointer position minus the delta already travelled
            var now = new Point(0, 0);
            if (NativeMethods.GetCursorPos(out var cursor)) now = new Point(cursor.X, cursor.Y);
            BeginDrag(new Point(now.X - deltaDevice.X, now.Y - deltaDevice.Y));
        }
        ContinueDrag(deltaDevice);
        if (ended) FinishDrag();
    }

    /// <summary>Sweeps a diagonal sheen band across the island (liquid glass "reacts" moment).</summary>
    private void PlaySheen()
    {
        double travel = IslandBorder.ActualWidth + 300;
        SheenBand.Opacity = 1;
        SheenTranslate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-220, travel, TimeSpan.FromMilliseconds(700))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            });
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)) { BeginTime = TimeSpan.FromMilliseconds(600) };
        SheenBand.BeginAnimation(OpacityProperty, fade);
    }

    // ── Hover pre-expand (compact state only) ──

    private void OnIslandMouseEnter(object sender, MouseEventArgs e)
    {
        if (_model.IsExpanded || _notificationShowing || _hovering || _dragging) return;
        _hovering = true;
        AnimateIsland(toWidth: HoverWidth, toHeight: CompactHeight + 6, toRadius: CompactCornerRadius(CompactHeight + 6),
            duration: TimeSpan.FromMilliseconds(220),
            easing: new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 });
        CompactView.SetHoverState(true);
    }

    private void OnIslandMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_hovering) return;
        _hovering = false;
        CompactView.SetHoverState(false);
        if (_model.IsExpanded || _notificationShowing) return; // another state took over
        AnimateIsland(toWidth: CompactWidth, toHeight: CompactHeight, toRadius: CompactCornerRadius(CompactHeight),
            duration: TimeSpan.FromMilliseconds(180),
            easing: new CubicEase { EasingMode = EasingMode.EaseOut });
    }

    /// <summary>Docked: flat top corners meeting the screen edge; floating: full capsule.</summary>
    private CornerRadius CompactCornerRadius(double height) =>
        IsFloating ? new CornerRadius(height / 2) : new CornerRadius(2, 2, Math.Min(14, height / 2), Math.Min(14, height / 2));

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        int style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE,
            style | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
        // keep the island out of screen captures so the live glass never captures itself
        // (dev hook: WINFINGER_CAPTURABLE=1 keeps it visible to screenshots for UI checks)
        if (Environment.GetEnvironmentVariable("WINFINGER_CAPTURABLE") != "1")
            NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void OnClosed(EventArgs e)
    {
        _ghostTimer.Stop();
        _glassTimer?.Stop();
        _glass?.Dispose();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        RemoveMouseHook();
        _model.Hotkeys.Detach();
        base.OnClosed(e);
    }

    /// <summary>注册剪贴板全局热键（默认 Ctrl+Shift+V）。已展开在剪贴板页则再按一次收起。</summary>
    private void RegisterClipboardHotkey()
    {
        if (ApplyHotkey(Services.HotkeyService.HotkeyClipboard)) return;
        var gesture = _model.SettingsStore.Settings.ClipboardHotkey;
        if (!string.IsNullOrWhiteSpace(gesture)) _model.Notifications.Post("⌨", $"快捷键 {gesture} 被占用");
    }

    /// <summary>注册截图热键（默认 Ctrl+Shift+A 区域截图 / Ctrl+Shift+T 截图识字）。</summary>
    private void RegisterScreenshotHotkeys()
    {
        Bind(Services.HotkeyService.HotkeyScreenshot, _model.SettingsStore.Settings.HotkeyScreenshot);
        Bind(Services.HotkeyService.HotkeyScreenshotOcr, _model.SettingsStore.Settings.HotkeyScreenshotOcr);

        void Bind(int id, string gesture)
        {
            if (ApplyHotkey(id) || string.IsNullOrWhiteSpace(gesture)) return;
            _model.Notifications.Post("⌨", $"截图快捷键 {gesture} 被占用，请在功能设置中更换");
        }
    }

    /// <summary>按设置里当前记录的手势（重新）注册指定 id 的全局热键。</summary>
    public bool ApplyHotkey(int id) => ApplyHotkey(id, GestureFor(id));

    /// <summary>
    /// 用给定手势（重新）注册指定 id 的全局热键，不碰设置文件——
    /// 功能设置窗口先试注册、成功了再落盘，避免把注册不上的手势写进 settings.json。
    /// 手势为空视为"不注册"（返回 true，并解掉旧绑定）；被占用返回 false 且旧绑定保持有效。
    /// </summary>
    public bool ApplyHotkey(int id, string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            _model.Hotkeys.Unregister(id);
            return true;
        }
        return _model.Hotkeys.Rebind(id, gesture, HandlerFor(id));
    }

    private string GestureFor(int id) => id switch
    {
        Services.HotkeyService.HotkeyClipboard => _model.SettingsStore.Settings.ClipboardHotkey,
        Services.HotkeyService.HotkeyScreenshot => _model.SettingsStore.Settings.HotkeyScreenshot,
        Services.HotkeyService.HotkeyScreenshotOcr => _model.SettingsStore.Settings.HotkeyScreenshotOcr,
        _ => ""
    };

    private Action HandlerFor(int id) => id switch
    {
        Services.HotkeyService.HotkeyClipboard => () =>
        {
            if (_model.IsExpanded && _model.SelectedPage == AppPage.Clipboard) _model.Collapse();
            else _model.Select(AppPage.Clipboard);
        },
        Services.HotkeyService.HotkeyScreenshot => () => _ = _model.Screenshot.CaptureToHistoryAsync(false),
        Services.HotkeyService.HotkeyScreenshotOcr => () => _ = _model.Screenshot.CaptureToHistoryAsync(true),
        _ => () => { }
    };

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            UpdateExpandedScale();
            ApplyDockPosition(animated: false);
        });

    private void ClampPosition()
    {
        // keep the visible island (top-centred inside the stage window) on the displays
        var bounds = IsFloating ? VirtualScreenDip() : MonitorRectDip(CurrentMonitor());
        double islandHalf = Math.Max(IslandBorder.ActualWidth, CompactWidth) / 2;
        double minX = bounds.Left + 8 - (Width / 2 - islandHalf);
        double maxX = bounds.Right - 8 - (Width / 2 + islandHalf);
        Left = Math.Clamp(Left, minX, Math.Max(minX, maxX));
        double minY = bounds.Top - StageTopInset;
        double maxY = bounds.Bottom - CompactHeight - 16;
        Top = Math.Clamp(Top, minY, Math.Max(minY, maxY));
    }

    // ── Click vs drag (compact) ──

    private void OnIslandMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_model.IsExpanded) return;
        BeginDrag(CursorScreen());
        IslandBorder.CaptureMouse();
    }

    private void OnIslandClicked(object sender, MouseButtonEventArgs e)
    {
        if (_dragArmed)
        {
            bool dragged = _dragging;
            IslandBorder.ReleaseMouseCapture();
            FinishDrag();
            if (dragged) return; // a drag is not a click
        }
        if (_model.IsExpanded) return;
        // mac openFromCompactClick: 120 ms debounce
        var now = DateTime.UtcNow;
        if ((now - _lastCompactClick).TotalMilliseconds < 120) return;
        _lastCompactClick = now;
        _model.IsExpanded = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_model.IsExpanded) return;

        if (e.Key == Key.Escape)
        {
            // 页面优先处理（内部弹层、预览等），未处理才收起面板
            if (ExpandedView.CurrentPage is IIslandPage page && page.HandleEscape())
            {
                e.Handled = true;
                return;
            }
            _model.Collapse();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            AppPage? page = e.Key switch
            {
                Key.D1 => AppPage.Clipboard,
                Key.D2 => AppPage.Media,
                Key.D3 => AppPage.Notes,
                Key.D4 => AppPage.Shortcuts,
                Key.D5 => AppPage.Pomodoro,
                _ => null
            };
            if (page is { } p)
            {
                _model.SelectedPage = p;
                e.Handled = true;
                return;
            }
            // mac ⌘N on the notes page
            if (e.Key == Key.N && _model.SelectedPage == AppPage.Notes)
            {
                _model.RequestNewNote();
                e.Handled = true;
            }
        }
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.IsExpanded):
                if (_model.IsExpanded) Expand();
                else Collapse();
                break;
            case nameof(AppViewModel.DockMode):
                if (!_dragging) ApplyDockPosition(animated: true);
                break;
            case nameof(AppViewModel.AppearanceStyle):
                ApplyBackground();
                break;
            case nameof(AppViewModel.ExpandedUserWidth):
                UpdateExpandedScale();
                break;
        }
    }

    // ── Notification bulge (compact-state only) ──

    private void OnNotificationPosted(Services.IslandNotification notification)
    {
        if (_model.IsExpanded || _dragging) return;
        if (_hovering)
        {
            _hovering = false;
            CompactView.SetHoverState(false);
        }
        NotificationIcon.Text = notification.Icon;
        NotificationText.Text = notification.Message;
        _notificationTimer.Stop();
        _notificationTimer.Start();
        if (_notificationShowing) return;
        _notificationShowing = true;

        AnimateIsland(toWidth: NotificationWidth, toHeight: CompactHeight, toRadius: CompactCornerRadius(CompactHeight),
            duration: TimeSpan.FromMilliseconds(240),
            easing: new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 });

        NotificationView.Visibility = Visibility.Visible;
        FadeTo(CompactView, 0, TimeSpan.FromMilliseconds(80), () => CompactView.Visibility = Visibility.Collapsed);
        NotificationView.Opacity = 0;
        NotificationView.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { BeginTime = TimeSpan.FromMilliseconds(120) });
        PlaySheen();
    }

    private void HideNotification()
    {
        _notificationTimer.Stop();
        if (!_notificationShowing) return;
        _notificationShowing = false;
        if (_model.IsExpanded) return; // expand animation already took over

        AnimateIsland(toWidth: CompactWidth, toHeight: CompactHeight, toRadius: CompactCornerRadius(CompactHeight),
            duration: TimeSpan.FromMilliseconds(200),
            easing: new CubicEase { EasingMode = EasingMode.EaseInOut });

        CompactView.Visibility = Visibility.Visible;
        FadeTo(NotificationView, 0, TimeSpan.FromMilliseconds(80), () => NotificationView.Visibility = Visibility.Collapsed);
        CompactView.Opacity = 0;
        CompactView.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { BeginTime = TimeSpan.FromMilliseconds(100) });
    }

    // ── Expand / collapse choreography ──

    private void Expand()
    {
        if (_hovering)
        {
            _hovering = false;
            CompactView.SetHoverState(false);
        }
        if (_notificationShowing)
        {
            _notificationTimer.Stop();
            _notificationShowing = false;
            NotificationView.Visibility = Visibility.Collapsed;
            NotificationView.Opacity = 0;
        }
        // 必须在岛拿到焦点之前记住上一个前台窗口，否则记到的是我们自己
        _model.RestoreFocusOnCollapse = true;
        _model.FocusRestore.Remember(_model.ForegroundApp.Hwnd);
        SetNoActivate(false);
        Activate();
        Focus();

        UpdateExpandedScale();
        var (width, height) = ExpandedSize();

        // mac clampedOrigin: keep the expanded frame on the display — shift the stage and restore on collapse
        var monitor = MonitorRectDip(CurrentMonitor());
        double needed = StageTopInset + height + 12;
        if (Top + needed > monitor.Bottom)
        {
            _preExpandTop = Top;
            Top = Math.Max(monitor.Top - StageTopInset, monitor.Bottom - needed);
        }
        double islandLeft = Left + Width / 2 - width / 2;
        double islandRight = islandLeft + width;
        if (islandLeft < monitor.Left + 8 || islandRight > monitor.Right - 8)
        {
            _preExpandLeft = Left;
            double target = islandLeft < monitor.Left + 8 ? monitor.Left + 8 : monitor.Right - 8 - width;
            target = Math.Max(monitor.Left + 8, Math.Min(target, monitor.Right - 8 - width));
            Left = target - (Width / 2 - width / 2);
        }

        AnimateIsland(toWidth: width, toHeight: height, toRadius: new CornerRadius(ExpandedRadius),
            duration: TimeSpan.FromMilliseconds(280),
            easing: new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.32 });

        // Content crossfade: compact out fast, expanded in after ~70% of the resize.
        ExpandedView.Visibility = Visibility.Visible;
        FadeTo(CompactView, 0, TimeSpan.FromMilliseconds(90), () => CompactView.Visibility = Visibility.Collapsed);
        ExpandedView.Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        {
            BeginTime = TimeSpan.FromMilliseconds(190),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ExpandedView.BeginAnimation(OpacityProperty, fadeIn);
        PlaySheen();
        ResizeLeft.Visibility = Visibility.Visible;
        ResizeRight.Visibility = Visibility.Visible;
        StartAccentBorder();
        IslandShadow.Opacity = 0.35;

        InstallMouseHook();

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
            () => (ExpandedView.CurrentPage as IIslandPage)?.OnExpanded());
    }

    private void Collapse()
    {
        RemoveMouseHook();
        SetNoActivate(true);
        // 用户点到别的窗口 / 粘贴流程自己抢前台时不要再抢回去
        if (_model.RestoreFocusOnCollapse) _model.FocusRestore.Restore();
        _model.RestoreFocusOnCollapse = true;
        ResizeLeft.Visibility = Visibility.Collapsed;
        ResizeRight.Visibility = Visibility.Collapsed;
        StopAccentBorder();
        if (_preExpandTop is { } restore)
        {
            _preExpandTop = null;
            Top = restore;
        }
        if (_preExpandLeft is { } restoreLeft)
        {
            _preExpandLeft = null;
            Left = restoreLeft;
        }

        // release the expanded panel's garbage once the animation settles
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { t.Stop(); TrimWorkingSet(); };
        t.Start();

        AnimateIsland(toWidth: CompactWidth, toHeight: CompactHeight, toRadius: CompactCornerRadius(CompactHeight),
            duration: TimeSpan.FromMilliseconds(180),
            easing: new CubicEase { EasingMode = EasingMode.EaseIn });

        CompactView.Visibility = Visibility.Visible;
        FadeTo(ExpandedView, 0, TimeSpan.FromMilliseconds(90), () => ExpandedView.Visibility = Visibility.Collapsed);
        CompactView.Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
        {
            BeginTime = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        CompactView.BeginAnimation(OpacityProperty, fadeIn);
        IslandShadow.Opacity = IsFloating ? 0.45 : 0.35;
    }

    private void AnimateIsland(double toWidth, double toHeight, CornerRadius toRadius, TimeSpan duration, IEasingFunction easing)
    {
        _morphing = true;
        var widthAnim = new DoubleAnimation(toWidth, duration) { EasingFunction = easing };
        widthAnim.Completed += (_, _) =>
        {
            _morphing = false;
            CaptureGlass();
        };
        var heightAnim = new DoubleAnimation(toHeight, duration) { EasingFunction = easing };
        var radiusAnim = new CornerRadiusAnimation
        {
            From = IslandBorder.CornerRadius,
            To = toRadius,
            Duration = duration,
            EasingFunction = easing
        };
        IslandBorder.BeginAnimation(WidthProperty, widthAnim);
        IslandBorder.BeginAnimation(HeightProperty, heightAnim);
        IslandBorder.BeginAnimation(System.Windows.Controls.Border.CornerRadiusProperty, radiusAnim);
    }

    // ── Corner resize (mac handlePanelResize): aspect-locked, 560…max, persisted ──

    private void OnResizeDown(object sender, MouseButtonEventArgs e)
    {
        if (!_model.IsExpanded) return;
        _resizing = true;
        _resizeHandle = (FrameworkElement)sender;
        _resizeWidth = IslandBorder.ActualWidth;
        // detach running size animations so direct sets take effect
        IslandBorder.BeginAnimation(WidthProperty, null);
        IslandBorder.BeginAnimation(HeightProperty, null);
        IslandBorder.Width = IslandBorder.ActualWidth;
        IslandBorder.Height = IslandBorder.ActualHeight;
        _resizeHandle.CaptureMouse();
        e.Handled = true;
    }

    private void OnResizeMove(object sender, MouseEventArgs e)
    {
        if (!_resizing || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(IslandBorder); // island-local DIP; island is top-centred
        double centerX = IslandBorder.ActualWidth / 2;
        double proposedWidth = Math.Abs(p.X - centerX) * 2;
        double proposedHeight = p.Y;
        double ratio = DesignWidth / DesignHeight;
        double width = Math.Max(proposedWidth, proposedHeight * ratio);
        double max = MaxExpandedWidth();
        width = Math.Min(Math.Max(width, Math.Min(MinExpandedWidth, max)), max);
        double height = width / ratio;
        _resizeWidth = width;
        IslandBorder.Width = width;
        IslandBorder.Height = height;
        double scale = width / DesignWidth;
        ExpandedScale.ScaleX = scale;
        ExpandedScale.ScaleY = scale;
        e.Handled = true;
    }

    private void OnResizeUp(object sender, MouseButtonEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        _resizeHandle?.ReleaseMouseCapture();
        _resizeHandle = null;
        _model.ExpandedUserWidth = Math.Round(_resizeWidth);
        CaptureGlass();
        e.Handled = true;
    }

    private static void FadeTo(UIElement element, double to, TimeSpan duration, Action? completed = null)
    {
        var anim = new DoubleAnimation(to, duration);
        if (completed is not null)
            anim.Completed += (_, _) => completed();
        element.BeginAnimation(OpacityProperty, anim);
    }

    private void SetNoActivate(bool enabled)
    {
        if (_hwnd == IntPtr.Zero) return;
        int style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        style = enabled ? style | NativeMethods.WS_EX_NOACTIVATE : style & ~NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, style);
    }

    // ── Click-outside detection (low-level mouse hook, installed only while expanded) ──

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseProc = MouseHookCallback;
        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc,
            NativeMethods.GetModuleHandle(null), 0);
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
        _mouseProc = null;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Zero blocking work here: capture the point, decide on the dispatcher.
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN)
            {
                var data = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var screenPoint = new Point(data.pt.X, data.pt.Y);
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_model.IsExpanded || _model.IsExpandedPinned || _resizing) return; // mac: pinned panels stay open
                    if (!IsOwnWindowAt(screenPoint, data.pt))
                    {
                        // 焦点已经被用户点走了，收起时不能再把前台抢回旧窗口
                        _model.FocusRestore.Forget();
                        _model.CollapseWithoutFocusRestore();
                    }
                });
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>点在本进程任意窗口上（岛、右键菜单、Popup、灯箱）都算"内部"，不触发收起。</summary>
    private bool IsOwnWindowAt(Point screenPoint, NativeMethods.POINT rawPoint)
    {
        IntPtr hit = NativeMethods.WindowFromPoint(rawPoint);
        if (hit != IntPtr.Zero)
        {
            IntPtr root = NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT);
            if (root == IntPtr.Zero) root = hit;
            NativeMethods.GetWindowThreadProcessId(root, out uint pid);
            if (pid == (uint)Environment.ProcessId) return true;
            return false;
        }

        // WindowFromPoint 失手时回退到岛的矩形（PointToScreen 与钩子同为设备像素）
        var topLeft = IslandBorder.PointToScreen(new Point(0, 0));
        var bottomRight = IslandBorder.PointToScreen(new Point(IslandBorder.ActualWidth, IslandBorder.ActualHeight));
        return new Rect(topLeft, bottomRight).Contains(screenPoint);
    }
}

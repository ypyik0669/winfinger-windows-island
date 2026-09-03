using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinFinger.Services;
using WinFinger.ViewModels;
using WinFinger.Views.Pages;

namespace WinFinger.Views;

public partial class ExpandedPanelView : UserControl
{
    private AppViewModel? _model;
    private readonly Dictionary<AppPage, UserControl> _pages = new();
    private readonly Dictionary<AppPage, RadioButton> _tabs = new();
    private bool _syncing;

    /// <summary>Raised by the header drag region: (screen delta, ended). The window moves itself.</summary>
    public event Action<Point, bool>? HeaderDragged;

    private Point _dragStart;
    private bool _dragArmed;
    private bool _dragging;

    public ExpandedPanelView()
    {
        InitializeComponent();
    }

    public void Initialize(AppViewModel model)
    {
        _model = model;

        _pages[AppPage.Clipboard] = new ClipboardPage();
        _pages[AppPage.Media] = new MediaPage();
        _pages[AppPage.Notes] = new NotesPage();
        _pages[AppPage.Shortcuts] = new ShortcutsPage();
        _pages[AppPage.Pomodoro] = new PomodoroPage();
        foreach (var page in _pages.Values)
            (page as IIslandPage)?.Initialize(model);

        foreach (var page in Enum.GetValues<AppPage>())
        {
            var tab = BuildTab(page);
            _tabs[page] = tab;
            TabStrip.Children.Add(tab);
        }

        BrandButton.Click += (_, _) => model.Collapse();
        PinButton.Click += (_, _) => model.ToggleExpandedPinned();

        DragRegion.MouseLeftButtonDown += OnDragDown;
        DragRegion.MouseMove += OnDragMove;
        DragRegion.MouseLeftButtonUp += OnDragUp;

        UploadLabel.SetBinding(TextBlock.TextProperty, new Binding(nameof(MetricsService.UploadText)) { Source = model.Metrics });
        DownloadLabel.SetBinding(TextBlock.TextProperty, new Binding(nameof(MetricsService.DownloadText)) { Source = model.Metrics });
        HeaderRing.SetBinding(Controls.MemoryRing.ValueProperty, new Binding(nameof(MetricsService.MemoryUsedRatio)) { Source = model.Metrics });

        model.PropertyChanged += OnModelPropertyChanged;
        SyncFromModel(animated: false);
        RefreshPin();
    }

    private RadioButton BuildTab(AppPage page)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            FontFamily = (FontFamily)FindResource("Font.Icon"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Text = page.Glyph(),
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = page.Title(),
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        var tab = new RadioButton
        {
            Style = (Style)FindResource("Button.FilterPill"),
            GroupName = "Pages",
            Content = content,
            Padding = new Thickness(11, 0, 11, 0),
            Margin = new Thickness(page == AppPage.Clipboard ? 0 : 2, 0, 0, 0)
        };
        foreach (TextBlock label in content.Children)
            label.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Foreground)) { Source = tab });
        tab.Checked += (_, _) =>
        {
            if (_syncing || _model is null) return;
            _model.SelectedPage = page;
        };
        return tab;
    }

    // ── header drag region (mac HeaderDragRegion): drag moves the panel, a tap collapses unless pinned ──

    private void OnDragDown(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = true;
        _dragging = false;
        _dragStart = DragRegion.PointToScreen(e.GetPosition(DragRegion));
        DragRegion.CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var now = DragRegion.PointToScreen(e.GetPosition(DragRegion));
        var delta = new Point(now.X - _dragStart.X, now.Y - _dragStart.Y);
        if (!_dragging && Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) <= 3) return;
        _dragging = true;
        HeaderDragged?.Invoke(delta, false);
    }

    private void OnDragUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragArmed) return;
        _dragArmed = false;
        DragRegion.ReleaseMouseCapture();
        if (_dragging)
        {
            _dragging = false;
            var now = DragRegion.PointToScreen(e.GetPosition(DragRegion));
            HeaderDragged?.Invoke(new Point(now.X - _dragStart.X, now.Y - _dragStart.Y), true);
            return;
        }
        if (_model is { IsExpandedPinned: false })
            _model.Collapse();
    }

    private void OnRingClicked(object sender, MouseButtonEventArgs e)
    {
        // mac: opens Activity Monitor
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
        }
        catch
        {
            // task manager unavailable
        }
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SelectedPage))
            SyncFromModel(animated: true);
        else if (e.PropertyName == nameof(AppViewModel.IsExpandedPinned))
            RefreshPin();
    }

    private void RefreshPin()
    {
        bool pinned = _model?.IsExpandedPinned == true;
        PinGlyph.Text = pinned ? "\uE72E" : "\uE785";
        PinGlyph.SetResourceReference(TextBlock.ForegroundProperty, pinned ? "Brush.Teal" : "Brush.TextSecondary");
        PinButton.SetResourceReference(BackgroundProperty, pinned ? "Brush.FillStrong" : "Brush.Fill");
        PinRing.Opacity = pinned ? 1 : 0.7;
        PinButton.ToolTip = pinned ? "已锁定，点击外部不会收起" : "锁定后点击外部不会收起";
        DragRegion.ToolTip = pinned ? "已锁定，按住这里可以拖出面板，轻点不会收起" : "按住拖出面板，轻点可收起";
    }

    /// <summary>当前选中页对应的控件（供窗口层转发 Esc / 展开通知）。</summary>
    public UserControl? CurrentPage =>
        _model is not null && _pages.TryGetValue(_model.SelectedPage, out var page) ? page : null;

    private void SyncFromModel(bool animated)
    {
        if (_model is null) return;
        _syncing = true;
        try
        {
            foreach (var (page, tab) in _tabs)
                tab.IsChecked = _model.SelectedPage == page;
        }
        finally
        {
            _syncing = false;
        }

        var current = _pages[_model.SelectedPage];
        if (ReferenceEquals(PageHost.Content, current)) return;
        PageHost.Content = current;
        (current as IIslandPage)?.OnShown();
        if (_model.IsExpanded) (current as IIslandPage)?.OnExpanded();

        if (animated)
        {
            current.Opacity = 0;
            var translate = new TranslateTransform(0, 14);
            current.RenderTransform = translate;
            current.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            translate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }
}

/// <summary>Implemented by expanded-panel pages to receive the shared model.</summary>
public interface IIslandPage
{
    void Initialize(AppViewModel model);

    /// <summary>Called each time the page becomes the visible tab.</summary>
    void OnShown()
    {
    }

    /// <summary>页面先接管 Esc（关闭内部弹层等）；返回 true 表示已处理，面板不收起。</summary>
    bool HandleEscape() => false;

    /// <summary>面板展开且本页可见时调用（抢焦点、聚焦搜索框等）。</summary>
    void OnExpanded()
    {
    }
}

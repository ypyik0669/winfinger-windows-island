using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using WinFinger.ViewModels;
using WinFinger.Views;

namespace WinFinger;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private TaskbarIcon? _trayIcon;
    private IslandWindow? _islandWindow;
    private AppearanceWindow? _appearanceWindow;
    private FeatureSettingsWindow? _featureWindow;

    public AppViewModel Model { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.Handled = true; // an appearance/menu mishap must never take the island down
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);

        _singleInstanceMutex = new Mutex(true, @"Global\WinFinger.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        Model.Start();

        _islandWindow = new IslandWindow(Model);
        _islandWindow.Show();

        CreateTrayIcon();

        // Task 9: AI 动作没配 Key 时直接把功能设置窗口推到用户面前
        Model.FeatureSettingsRequested += OpenFeatureSettingsWindow;

        // dev hook: WINFINGER_OPENSETTINGS=1 startup 后 1s 打开功能设置窗口（托盘菜单不好自动化）
        if (Environment.GetEnvironmentVariable("WINFINGER_OPENSETTINGS") == "1")
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            t.Tick += (_, _) => { t.Stop(); OpenFeatureSettingsWindow(); };
            t.Start();
        }

        // dev hook: WINFINGER_AUTOEXPAND=<page index 1-5> expands to that page 2s after startup
        if (int.TryParse(Environment.GetEnvironmentVariable("WINFINGER_AUTOEXPAND"), out int autoPage) && autoPage is >= 1 and <= 6)
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                Model.SelectedPage = (AppPage)(autoPage - 1);
                Model.IsExpanded = true;
            };
            t.Start();
        }

        // repro hook: WINFINGER_PICKTEST=1 fires the tray 选择图片 flow 3s after startup
        if (Environment.GetEnvironmentVariable("WINFINGER_PICKTEST") == "1")
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, _) => { t.Stop(); PickBackgroundImage(); };
            t.Start();
        }
        if (Environment.GetEnvironmentVariable("WINFINGER_PICKTEST") == "2")
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                Model.SettingsStore.Settings.BackgroundImagePath =
                    Environment.GetEnvironmentVariable("WINFINGER_PICKTEST_FILE") ?? "";
                SetBackground("image", null);
            };
            t.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        if (_ownsMutex)
        {
            try { Model.ClipboardStore.SaveNow(); } catch (Exception ex) { LogCrash(ex); }
            try { Model.Chat.SaveNow(); } catch (Exception ex) { LogCrash(ex); }
            try { Model.Stop(); } catch (Exception ex) { LogCrash(ex); }
            try { _singleInstanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void CreateTrayIcon()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var openItem = new System.Windows.Controls.MenuItem { Header = "打开 WinFinger" };
        openItem.Click += (_, _) => Model.ToggleExpanded();
        menu.Items.Add(openItem);

        // 外观: 纯黑 / Liquid Glass (mac MacFingerAppearance)
        var appearanceMenu = new System.Windows.Controls.MenuItem { Header = "外观" };
        var blackItem = new System.Windows.Controls.MenuItem { Header = "纯黑", IsCheckable = true };
        var glassItem = new System.Windows.Controls.MenuItem { Header = "Liquid Glass", IsCheckable = true };
        void SyncAppearance()
        {
            blackItem.IsChecked = Model.AppearanceStyle == "black";
            glassItem.IsChecked = Model.AppearanceStyle != "black";
        }
        blackItem.Click += (_, _) => { Model.AppearanceStyle = "black"; SyncAppearance(); };
        glassItem.Click += (_, _) => { Model.AppearanceStyle = "glass"; SyncAppearance(); };
        appearanceMenu.Items.Add(blackItem);
        appearanceMenu.Items.Add(glassItem);
        appearanceMenu.Items.Add(new System.Windows.Controls.Separator());
        var appearanceSettingsItem = new System.Windows.Controls.MenuItem { Header = "外观设置…" };
        appearanceSettingsItem.Click += (_, _) => OpenAppearanceWindow();
        appearanceMenu.Items.Add(appearanceSettingsItem);
        menu.Items.Add(appearanceMenu);

        var featureSettingsItem = new System.Windows.Controls.MenuItem { Header = "功能设置…" };
        featureSettingsItem.Click += (_, _) => OpenFeatureSettingsWindow();
        menu.Items.Add(featureSettingsItem);

        // 收起位置: 顶部 / 悬浮 (mac MacFingerDockMode)
        var dockMenu = new System.Windows.Controls.MenuItem { Header = "收起位置" };
        var topItem = new System.Windows.Controls.MenuItem { Header = "顶部", IsCheckable = true };
        var floatingItem = new System.Windows.Controls.MenuItem { Header = "悬浮", IsCheckable = true };
        void SyncDock()
        {
            topItem.IsChecked = Model.DockMode == "top";
            floatingItem.IsChecked = Model.DockMode == "floating";
        }
        topItem.Click += (_, _) => { Model.DockMode = "top"; SyncDock(); };
        floatingItem.Click += (_, _) => { Model.DockMode = "floating"; SyncDock(); };
        dockMenu.Items.Add(topItem);
        dockMenu.Items.Add(floatingItem);
        menu.Items.Add(dockMenu);
        menu.Opened += (_, _) => { SyncAppearance(); SyncDock(); };
        SyncAppearance();
        SyncDock();

        var pauseItem = new System.Windows.Controls.MenuItem
        {
            Header = "暂停记录剪贴板",
            IsCheckable = true,
            IsChecked = Model.ClipboardMonitor.IsPaused
        };
        pauseItem.Click += (_, _) => Model.ClipboardMonitor.IsPaused = pauseItem.IsChecked;
        menu.Items.Add(pauseItem);

        var clearKeepItem = new System.Windows.Controls.MenuItem { Header = "清空历史（保留收藏）" };
        clearKeepItem.Click += (_, _) => Model.ClipboardStore.Clear(includeFavorites: false);
        menu.Items.Add(clearKeepItem);

        var clearAllItem = new System.Windows.Controls.MenuItem { Header = "全部清空…" };
        clearAllItem.Click += (_, _) => ConfirmClearAll();
        menu.Items.Add(clearAllItem);

        var bgMenu = new System.Windows.Controls.MenuItem { Header = "岛背景" };
        var bgGlass = new System.Windows.Controls.MenuItem { Header = "动态玻璃" };
        bgGlass.Click += (_, _) => SetBackground("glass", null);
        bgMenu.Items.Add(bgGlass);
        (string name, string hex)[] presets =
        {
            ("经典深灰", "#1A1A22"), ("纯黑", "#0A0A0F"), ("深蓝", "#16283E"),
            ("深紫", "#1D1440"), ("酒红", "#3D0F14"), ("墨绿", "#0F3324"),
            ("暖棕", "#33270F"), ("青黛", "#0E3338")
        };
        foreach (var (name, hex) in presets)
        {
            var item = new System.Windows.Controls.MenuItem { Header = name };
            item.Click += (_, _) => SetBackground("color", hex);
            bgMenu.Items.Add(item);
        }
        var bgImage = new System.Windows.Controls.MenuItem { Header = "选择图片…" };
        bgImage.Click += (_, _) => PickBackgroundImage();
        bgMenu.Items.Add(bgImage);
        menu.Items.Add(bgMenu);

        var autoStartItem = new System.Windows.Controls.MenuItem
        {
            Header = "开机自启动",
            IsCheckable = true,
            IsChecked = Model.SettingsStore.Settings.AutoStart
        };
        autoStartItem.Click += (_, _) => Model.SettingsStore.SetAutoStart(autoStartItem.IsChecked);
        menu.Items.Add(autoStartItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var quitItem = new System.Windows.Controls.MenuItem { Header = "退出 WinFinger" };
        quitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(quitItem);

        _trayIcon = new TaskbarIcon
        {
            Icon = CreatePillIcon(),
            ToolTipText = "WinFinger",
            ContextMenu = menu
        };
        _trayIcon.TrayLeftMouseUp += (_, _) => Model.ToggleExpanded();

        // dev hook: WINFINGER_TRAYMENU=1 启动 1.5s 后在鼠标处弹出托盘菜单（托盘图标常在溢出区，自动化点不到）
        if (Environment.GetEnvironmentVariable("WINFINGER_TRAYMENU") == "1")
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse;
                menu.IsOpen = true;
            };
            t.Start();
        }
    }

    private void PickBackgroundImage()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
                Title = "选择岛背景图片"
            };
            // the island window is NOACTIVATE and the tray menu's host is transient,
            // so the dialog needs a real activatable owner or it can't take focus
            if (DialogOwner.WithOwner(owner => dlg.ShowDialog(owner)) != true) return;
            Model.SettingsStore.Settings.BackgroundImagePath = dlg.FileName;
            SetBackground("image", null);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
    }

    /// <summary>"全部清空"：连收藏一起删，先确认。</summary>
    private void ConfirmClearAll()
    {
        try
        {
            var result = DialogOwner.WithOwner(owner => MessageBox.Show(owner,
                "将删除全部剪贴板记录，包括收藏项。确定继续吗？", "全部清空",
                MessageBoxButton.YesNo, MessageBoxImage.Warning));
            if (result == MessageBoxResult.Yes) Model.ClipboardStore.Clear(includeFavorites: true);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinFinger");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch { }
    }

    private void SetBackground(string mode, string? color)
    {
        var s = Model.SettingsStore.Settings;
        s.BackgroundMode = mode;
        if (color is not null) s.BackgroundColor = color;
        Model.SettingsStore.Save();
        _islandWindow?.ApplyBackground();
    }

    private void OpenAppearanceWindow()
    {
        if (_appearanceWindow is { IsLoaded: true })
        {
            _appearanceWindow.Activate();
            return;
        }
        if (_islandWindow is null) return;
        _appearanceWindow = new AppearanceWindow(Model, _islandWindow);
        _appearanceWindow.Closed += (_, _) => _appearanceWindow = null;
        _appearanceWindow.Show();
        _appearanceWindow.Activate();
    }

    private void OpenFeatureSettingsWindow()
    {
        if (_featureWindow is { IsLoaded: true })
        {
            _featureWindow.Activate();
            return;
        }
        if (_islandWindow is null) return;
        _featureWindow = new FeatureSettingsWindow(Model, _islandWindow);
        _featureWindow.Closed += (_, _) => _featureWindow = null;
        _featureWindow.Show();
        _featureWindow.Activate();
    }

    /// <summary>Draws the island pill as a 32x32 tray icon at runtime (no .ico asset needed).</summary>
    private static Icon CreatePillIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            var rect = new Rectangle(2, 10, 28, 12);
            int r = rect.Height;
            path.AddArc(rect.X, rect.Y, r, r, 90, 180);
            path.AddArc(rect.Right - r, rect.Y, r, r, 270, 180);
            path.CloseFigure();
            using var fill = new SolidBrush(System.Drawing.Color.FromArgb(255, 20, 20, 22));
            using var stroke = new Pen(System.Drawing.Color.FromArgb(200, 235, 235, 240), 1.6f);
            g.FillPath(fill, path);
            g.DrawPath(stroke, path);
        }
        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

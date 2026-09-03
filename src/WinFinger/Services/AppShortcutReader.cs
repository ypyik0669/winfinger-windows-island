using System.Text;
using System.Windows.Automation;
using WinFinger.Interop;
using WinFinger.Models;

namespace WinFinger.Services;

/// <summary>
/// Reads the live menu shortcuts of the foreground window (Windows analog of mac AppShortcutReader's
/// accessibility menu walk): Win32 menu bars first (accelerator text after the tab), then UI Automation
/// menu items exposing AcceleratorKey. Limits mirror mac: ≤36 items per top menu, ≤180 total, depth ≤5.
/// </summary>
public static class AppShortcutReader
{
    private const int MaxItemsPerMenu = 36;
    private const int MaxTotalItems = 180;
    private const int MaxDepth = 5;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(1.5);

    public static async Task<ShortcutReadResult> ReadAsync(IntPtr hwnd, int processId)
    {
        if (hwnd == IntPtr.Zero || processId <= 0) return ShortcutReadResult.Unavailable;
        using var cts = new CancellationTokenSource(Budget);
        try
        {
            var work = Task.Run(() => Read(hwnd, cts.Token), cts.Token);
            var finished = await Task.WhenAny(work, Task.Delay(Budget, cts.Token));
            if (finished != work) return ShortcutReadResult.Unavailable;
            return await work;
        }
        catch
        {
            return ShortcutReadResult.Unavailable;
        }
    }

    private static ShortcutReadResult Read(IntPtr hwnd, CancellationToken token)
    {
        var groups = new List<ShortcutGroup>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int total = 0;

        try
        {
            ReadWin32Menu(hwnd, groups, seen, ref total, token);
        }
        catch
        {
            // UIPI / detached menu: fall through to UIA
        }

        if (total == 0)
        {
            try
            {
                ReadAutomationMenu(hwnd, groups, seen, ref total, token);
            }
            catch
            {
                // automation unavailable for this window
            }
        }

        if (total == 0) return ShortcutReadResult.Unavailable;
        return new ShortcutReadResult(groups, ShortcutReadStatus.Live, total);
    }

    // ── Win32 menu bar ──

    private static void ReadWin32Menu(IntPtr hwnd, List<ShortcutGroup> groups, HashSet<string> seen, ref int total, CancellationToken token)
    {
        var menu = NativeMethods.GetMenu(hwnd);
        if (menu == IntPtr.Zero) return;
        int count = NativeMethods.GetMenuItemCount(menu);
        for (int i = 0; i < count && total < MaxTotalItems; i++)
        {
            token.ThrowIfCancellationRequested();
            var title = CleanTitle(MenuString(menu, i));
            var sub = NativeMethods.GetSubMenu(menu, i);
            if (title.Length == 0 || sub == IntPtr.Zero) continue;

            var items = new List<ShortcutItem>();
            CollectWin32(sub, items, seen, 1, ref total, token);
            if (items.Count == 0) continue;
            groups.Add(new ShortcutGroup($"live-{groups.Count}-{title}", ShortcutChineseTranslator.GroupTitle(title), items));
        }
    }

    private static void CollectWin32(IntPtr menu, List<ShortcutItem> items, HashSet<string> seen, int depth, ref int total, CancellationToken token)
    {
        if (depth > MaxDepth) return;
        int count = NativeMethods.GetMenuItemCount(menu);
        for (int i = 0; i < count; i++)
        {
            if (items.Count >= MaxItemsPerMenu || total >= MaxTotalItems) return;
            token.ThrowIfCancellationRequested();
            var sub = NativeMethods.GetSubMenu(menu, i);
            var raw = MenuString(menu, i);
            if (sub != IntPtr.Zero)
            {
                CollectWin32(sub, items, seen, depth + 1, ref total, token);
                continue;
            }
            int tab = raw.IndexOf('\t');
            if (tab < 0) continue;
            var title = CleanTitle(raw[..tab]);
            var keys = NormalizeKeys(raw[(tab + 1)..].Trim());
            if (title.Length == 0 || keys.Length == 0) continue;
            var action = ShortcutChineseTranslator.ActionTitle(title);
            if (!seen.Add($"{keys}|{action}")) continue;
            items.Add(new ShortcutItem($"live-{total}", keys, action));
            total++;
        }
    }

    private static string MenuString(IntPtr menu, int index)
    {
        var buffer = new StringBuilder(256);
        int length = NativeMethods.GetMenuString(menu, (uint)index, buffer, buffer.Capacity, NativeMethods.MF_BYPOSITION);
        return length > 0 ? buffer.ToString(0, Math.Min(length, buffer.Length)) : "";
    }

    // ── UI Automation menu bar (WPF / WinUI / Electron) ──

    private static void ReadAutomationMenu(IntPtr hwnd, List<ShortcutGroup> groups, HashSet<string> seen, ref int total, CancellationToken token)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var bars = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuBar));
        foreach (AutomationElement bar in bars)
        {
            token.ThrowIfCancellationRequested();
            var topItems = bar.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
            foreach (AutomationElement top in topItems)
            {
                if (total >= MaxTotalItems) return;
                var title = CleanTitle(top.Current.Name ?? "");
                if (title.Length == 0) continue;
                var items = new List<ShortcutItem>();
                CollectAutomation(top, items, seen, 1, ref total, token);
                if (items.Count == 0) continue;
                groups.Add(new ShortcutGroup($"live-{groups.Count}-{title}", ShortcutChineseTranslator.GroupTitle(title), items));
            }
        }

        // menu items directly under the window (ribbon-less apps expose accelerators on buttons too)
        if (total == 0)
        {
            var items = new List<ShortcutItem>();
            var all = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
            foreach (AutomationElement element in all)
            {
                if (items.Count >= MaxItemsPerMenu || total >= MaxTotalItems) break;
                token.ThrowIfCancellationRequested();
                AddAutomationItem(element, items, seen, ref total);
            }
            if (items.Count > 0)
                groups.Add(new ShortcutGroup("live-0-menu", "应用", items));
        }
    }

    private static void CollectAutomation(AutomationElement parent, List<ShortcutItem> items, HashSet<string> seen, int depth, ref int total, CancellationToken token)
    {
        if (depth > MaxDepth) return;
        var children = parent.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
        foreach (AutomationElement child in children)
        {
            if (items.Count >= MaxItemsPerMenu || total >= MaxTotalItems) return;
            token.ThrowIfCancellationRequested();
            AddAutomationItem(child, items, seen, ref total);
            CollectAutomation(child, items, seen, depth + 1, ref total, token);
        }
    }

    private static void AddAutomationItem(AutomationElement element, List<ShortcutItem> items, HashSet<string> seen, ref int total)
    {
        string keys;
        string title;
        try
        {
            keys = NormalizeKeys(element.Current.AcceleratorKey ?? "");
            title = CleanTitle(element.Current.Name ?? "");
        }
        catch
        {
            return;
        }
        if (keys.Length == 0 || title.Length == 0) return;
        var action = ShortcutChineseTranslator.ActionTitle(title);
        if (!seen.Add($"{keys}|{action}")) return;
        items.Add(new ShortcutItem($"live-{total}", keys, action));
        total++;
    }

    // ── helpers ──

    private static string CleanTitle(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '&' && i + 1 < raw.Length && raw[i + 1] != '&') continue;
            if (raw[i] == '&' && i + 1 < raw.Length && raw[i + 1] == '&') { sb.Append('&'); i++; continue; }
            sb.Append(raw[i]);
        }
        // strip trailing "(&F)" style mnemonics common in CJK apps
        var s = sb.ToString().Trim();
        int paren = s.LastIndexOf('(');
        if (paren > 0 && s.EndsWith(')') && s.Length - paren <= 3) s = s[..paren].Trim();
        return s.Replace("…", "").Replace("...", "").Trim();
    }

    /// <summary>Normalises "ctrl + shift + s" / "Ctrl+Shift+S" to mac-like ordering Ctrl, Alt, Shift, Win + key.</summary>
    private static string NormalizeKeys(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var parts = raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "";
        bool ctrl = false, alt = false, shift = false, win = false;
        var keys = new List<string>();
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control" or "strg": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift" or "umschalt": shift = true; break;
                case "win" or "windows": win = true; break;
                default:
                    keys.Add(ShortcutChineseTranslator.KeyName(NormalizeKey(part)));
                    break;
            }
        }
        if (keys.Count == 0) return "";
        var sb = new StringBuilder();
        if (ctrl) sb.Append("Ctrl+");
        if (alt) sb.Append("Alt+");
        if (shift) sb.Append("Shift+");
        if (win) sb.Append("Win+");
        sb.Append(string.Join("+", keys));
        return sb.ToString();
    }

    private static string NormalizeKey(string key) => key.ToLowerInvariant() switch
    {
        "del" or "delete" => "Delete",
        "ins" or "insert" => "Insert",
        "pgup" or "pageup" or "page up" => "PgUp",
        "pgdn" or "pagedown" or "page down" => "PgDn",
        "backspace" or "bksp" => "Backspace",
        "esc" or "escape" => "Esc",
        "enter" or "return" => "Enter",
        "home" => "Home",
        "end" => "End",
        "up" or "up arrow" => "↑",
        "down" or "down arrow" => "↓",
        "left" or "left arrow" => "←",
        "right" or "right arrow" => "→",
        _ => key.Length == 1 ? key.ToUpperInvariant() : key
    };
}

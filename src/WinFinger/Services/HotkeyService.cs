using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WinFinger.Interop;

namespace WinFinger.Services;

/// <summary>
/// 全局热键（RegisterHotKey）。与剪贴板监听共用同一个 HwndSource 钩子。
/// id 由调用方分配：1 = 剪贴板，2/3 预留给截图。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    /// <summary>剪贴板面板热键的固定 id。</summary>
    public const int HotkeyClipboard = 1;

    /// <summary>区域截图热键的固定 id。</summary>
    public const int HotkeyScreenshot = 2;

    /// <summary>截图识字热键的固定 id。</summary>
    public const int HotkeyScreenshotOcr = 3;

    /// <summary>id → 当前生效的注册（修饰键、虚拟键、回调），用于失败回滚。</summary>
    private readonly Dictionary<int, Binding> _handlers = new();

    private readonly record struct Binding(uint Modifiers, uint Vk, Action Handler);
    private HwndSource? _source;
    private IntPtr _hwnd;

    /// <summary>挂到已有窗口的消息循环上。</summary>
    public void Attach(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    public void Detach()
    {
        foreach (var id in _handlers.Keys.ToList())
            Unregister(id);
        _source?.RemoveHook(WndProc);
        _source = null;
        _hwnd = IntPtr.Zero;
    }

    /// <summary>注册一个热键；被占用或未 Attach 时返回 false。</summary>
    public bool Register(int id, uint modifiers, uint vk, Action handler)
    {
        if (_hwnd == IntPtr.Zero || vk == 0) return false;
        Unregister(id);
        if (!NativeMethods.RegisterHotKey(_hwnd, id, modifiers | NativeMethods.MOD_NOREPEAT, vk)) return false;
        _handlers[id] = new Binding(modifiers, vk, handler);
        return true;
    }

    public void Unregister(int id)
    {
        if (_handlers.Remove(id) && _hwnd != IntPtr.Zero)
            NativeMethods.UnregisterHotKey(_hwnd, id);
    }

    /// <summary>
    /// 按手势字符串（如 "Ctrl+Shift+V"）重新绑定。
    /// 手势非法或新组合被占用时返回 false，并把原来的绑定原样恢复——
    /// 用户改错一次不该把已有热键弄丢。
    /// </summary>
    public bool Rebind(int id, string gesture, Action handler)
    {
        _handlers.TryGetValue(id, out var previous);
        bool hadPrevious = _handlers.ContainsKey(id);

        if (!TryParse(gesture, out uint mods, out uint vk)) return false;
        if (Register(id, mods, vk, handler)) return true;

        // 新组合注册失败：Register 已经把旧的解掉了，这里补回去
        if (hadPrevious) Register(id, previous.Modifiers, previous.Vk, previous.Handler);
        return false;
    }

    /// <summary>解析 "Ctrl+Shift+V" / "Alt+Space" / "Win+Shift+S" 这类手势。</summary>
    public static bool TryParse(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        for (int i = 0; i < parts.Length; i++)
        {
            var token = parts[i];
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= NativeMethods.MOD_CONTROL; continue;
                case "shift": modifiers |= NativeMethods.MOD_SHIFT; continue;
                case "alt" or "option": modifiers |= NativeMethods.MOD_ALT; continue;
                case "win" or "meta" or "cmd" or "command": modifiers |= NativeMethods.MOD_WIN; continue;
            }
            if (i != parts.Length - 1) return false; // 主键必须在最后
            vk = ParseKey(token);
        }
        return vk != 0;
    }

    private static uint ParseKey(string token)
    {
        var key = token.ToLowerInvariant() switch
        {
            "space" => Key.Space,
            "enter" or "return" => Key.Enter,
            "esc" or "escape" => Key.Escape,
            "tab" => Key.Tab,
            "backspace" or "back" => Key.Back,
            "delete" or "del" => Key.Delete,
            "insert" or "ins" => Key.Insert,
            "home" => Key.Home,
            "end" => Key.End,
            "pageup" or "pgup" => Key.PageUp,
            "pagedown" or "pgdn" => Key.PageDown,
            "up" => Key.Up,
            "down" => Key.Down,
            "left" => Key.Left,
            "right" => Key.Right,
            "`" or "tilde" or "grave" => Key.OemTilde,
            "," => Key.OemComma,
            "." => Key.OemPeriod,
            ";" => Key.OemSemicolon,
            "/" => Key.OemQuestion,
            "\\" => Key.OemBackslash,
            "-" => Key.OemMinus,
            "=" => Key.OemPlus,
            "[" => Key.OemOpenBrackets,
            "]" => Key.OemCloseBrackets,
            _ => ParseAlnum(token)
        };
        return key == Key.None ? 0u : (uint)KeyInterop.VirtualKeyFromKey(key);
    }

    private static Key ParseAlnum(string token)
    {
        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z') return Key.A + (c - 'A');
            if (c is >= '0' and <= '9') return Key.D0 + (c - '0');
            return Key.None;
        }
        if ((token[0] is 'f' or 'F') && int.TryParse(token.AsSpan(1), out int n) && n is >= 1 and <= 12)
            return Key.F1 + (n - 1);
        return Key.None;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _handlers.TryGetValue((int)wParam, out var binding))
        {
            handled = true;
            // 消息已经在 UI 线程上，异步派发避免在钩子里做重活
            _source?.Dispatcher.BeginInvoke(binding.Handler);
        }
        return IntPtr.Zero;
    }

    public void Dispose() => Detach();
}

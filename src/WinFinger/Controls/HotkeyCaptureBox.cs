using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinFinger.Services;

namespace WinFinger.Controls;

/// <summary>手势字符串的构造规则（纯函数，便于单测）。</summary>
public static class HotkeyGesture
{
    /// <summary>捕获框里的占位提示。</summary>
    public const string Placeholder = "点击后按下快捷键";

    /// <summary>
    /// 由修饰键 + 主键拼出 "Ctrl+Shift+V" 这样的手势；
    /// 主键是修饰键本身、主键不认识、或非 F 键却没带修饰键时返回 null。
    /// </summary>
    public static string? Build(ModifierKeys modifiers, Key key)
    {
        if (key == Key.System) key = Key.None; // 调用方应传 SystemKey，这里防御
        string? main = MainKeyToken(key);
        if (main is null) return null;

        bool isFunctionKey = key is >= Key.F1 and <= Key.F12;
        var parts = new List<string>(4);
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        if (parts.Count == 0 && !isFunctionKey) return null; // 单键会抢走全局输入

        parts.Add(main);
        string gesture = string.Join("+", parts);
        return HotkeyService.TryParse(gesture, out _, out _) ? gesture : null;
    }

    /// <summary>这个键是不是纯修饰键（按下它本身不构成手势）。</summary>
    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System or Key.None;

    /// <summary>主键 → HotkeyService 能解析回来的 token；不支持的键返回 null。</summary>
    private static string? MainKeyToken(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return ((char)('0' + (key - Key.NumPad0))).ToString();
        if (key is >= Key.F1 and <= Key.F12) return "F" + (key - Key.F1 + 1);
        return key switch
        {
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.OemTilde => "`",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemSemicolon => ";",
            Key.OemQuestion => "/",
            Key.OemBackslash => "\\",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            _ => null
        };
    }
}

/// <summary>
/// 只读文本框：聚焦后按下组合键即录入手势。
/// Backspace/Delete 清空，Esc 还原为进入时的值。
/// </summary>
public sealed class HotkeyCaptureBox : TextBox
{
    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
        nameof(Gesture), typeof(string), typeof(HotkeyCaptureBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnGestureChanged));

    private string _valueOnFocus = "";

    public HotkeyCaptureBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        Cursor = Cursors.Hand;
        ContextMenu = null;
        Text = HotkeyGesture.Placeholder;
    }

    /// <summary>当前手势（空字符串 = 未设置）。</summary>
    public string Gesture
    {
        get => (string)GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    /// <summary>用户录入了新手势（含清空）。</summary>
    public event EventHandler? GestureChanged;

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _valueOnFocus = Gesture ?? "";
        SelectionLength = 0;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            SetGesture(_valueOnFocus, notify: false);
            return;
        }
        if (key is Key.Back or Key.Delete)
        {
            SetGesture("", notify: true);
            return;
        }
        if (HotkeyGesture.IsModifierKey(key)) return;

        var gesture = HotkeyGesture.Build(Keyboard.Modifiers, key);
        if (gesture is null) return; // 非法组合：保留原值，不打扰用户
        SetGesture(gesture, notify: true);
    }

    private void SetGesture(string gesture, bool notify)
    {
        bool changed = !string.Equals(Gesture ?? "", gesture, StringComparison.Ordinal);
        Gesture = gesture;
        UpdateText();
        if (notify && changed) GestureChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateText()
    {
        Text = string.IsNullOrEmpty(Gesture) ? HotkeyGesture.Placeholder : Gesture;
        CaretIndex = Text.Length;
    }

    private static void OnGestureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HotkeyCaptureBox)d).UpdateText();
}

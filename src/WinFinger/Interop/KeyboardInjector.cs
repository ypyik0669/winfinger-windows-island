using System.Runtime.InteropServices;

namespace WinFinger.Interop;

/// <summary>
/// SendInput 键盘注入。统一走 SCANCODE + wVk，兼容那些只看扫描码的应用（游戏/远程终端）。
/// </summary>
public static class KeyboardInjector
{
    /// <summary>需要检查的修饰键，逐个左右分开；扩展键（右 Ctrl/右 Alt/两个 Win）必须带 EXTENDEDKEY。</summary>
    private static readonly (int Vk, bool Extended)[] Modifiers =
    {
        (NativeMethods.VK_LSHIFT, false),
        (NativeMethods.VK_RSHIFT, false),
        (NativeMethods.VK_LCONTROL, false),
        (NativeMethods.VK_RCONTROL, true),
        (NativeMethods.VK_LMENU, false),
        (NativeMethods.VK_RMENU, true),
        (NativeMethods.VK_LWIN, true),
        (NativeMethods.VK_RWIN, true)
    };

    /// <summary>
    /// 释放仍被物理按住的修饰键。用户按着热键松手前我们就注入 Ctrl+V，
    /// 残留的 Shift/Win 会把粘贴变成别的命令，先统一抬起。
    /// </summary>
    public static void ReleaseStuckModifiers()
    {
        var ups = new List<NativeMethods.INPUT>();
        foreach (var (vk, extended) in Modifiers)
        {
            if (NativeMethods.GetAsyncKeyState(vk) < 0) ups.Add(Key(vk, up: true, extended));
        }
        if (ups.Count > 0) Send(ups.ToArray());
    }

    /// <summary>一次 SendInput 发出 Ctrl↓ V↓ V↑ Ctrl↑，中间不给别的输入插队。</summary>
    public static void SendCtrlV()
    {
        var inputs = new[]
        {
            Key(NativeMethods.VK_CONTROL, up: false),
            Key(NativeMethods.VK_V, up: false),
            Key(NativeMethods.VK_V, up: true),
            Key(NativeMethods.VK_CONTROL, up: true)
        };
        Send(inputs);
    }

    /// <summary>轻点一次 ALT：解除前台锁定的经典技巧，配合 SetForegroundWindow 使用。</summary>
    public static void SendAltTap()
    {
        var inputs = new[]
        {
            Key(NativeMethods.VK_MENU, up: false),
            Key(NativeMethods.VK_MENU, up: true)
        };
        Send(inputs);
    }

    private static NativeMethods.INPUT Key(int vk, bool up, bool extended = false)
    {
        uint flags = NativeMethods.KEYEVENTF_SCANCODE
                     | (up ? NativeMethods.KEYEVENTF_KEYUP : 0)
                     | (extended ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0);
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = (ushort)NativeMethods.MapVirtualKey((uint)vk, 0),
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private static void Send(NativeMethods.INPUT[] inputs)
    {
        try
        {
            NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        }
        catch
        {
            // 注入被安全软件拦截时静默降级
        }
    }
}

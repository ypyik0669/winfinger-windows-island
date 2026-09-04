using System.Runtime.InteropServices;

namespace WinFinger.Interop;

/// <summary>Centralised P/Invoke declarations.</summary>
internal static class NativeMethods
{
    // ── Window styles ──
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x0000_0080;
    public const int WS_EX_NOACTIVATE = 0x0800_0000;
    public const int WS_EX_TRANSPARENT = 0x0000_0020;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    // ── Acrylic blur backdrop (undocumented but long-stable composition API) ──
    public const int WCA_ACCENT_POLICY = 19;
    public const int ACCENT_ENABLE_BLURBEHIND = 3;
    public const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor; // ABGR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowCompositionAttribData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttribData data);

    // ── Live glass capture (self-made frosted glass) ──
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    public const int HALFTONE = 4;
    public const uint SRCCOPY = 0x00CC0020;

    [DllImport("gdi32.dll")]
    public static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage,
        out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    // ── Win11 system backdrop (documented DWM API) ──
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMSBT_TRANSIENTWINDOW = 3; // acrylic
    public const int DWMWCP_ROUND = 2;           // ~8px system rounding
    public const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRoundRectRgn(int nLeft, int nTop, int nRight, int nBottom, int nWidthEllipse, int nHeightEllipse);

    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const int WM_NCHITTEST = 0x0084;
    public const int HTTRANSPARENT = -1;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // ── Clipboard listener ──
    public const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    // ── Memory ──
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ── 网卡流量：GetIfTable2 一次拿全部接口的字节计数（微秒级）。
    //    NetworkInterface.GetAllNetworkInterfaces() 每次要走 GetAdaptersAddresses + 大量托管分配，
    //    机器上虚拟网卡多的时候单次 100ms 以上，每秒调一次等于常驻吃掉一成 CPU 还卡 UI 线程。
    [DllImport("iphlpapi.dll")]
    public static extern int GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    public static extern void FreeMibTable(IntPtr table);

    /// <summary>MIB_IF_TABLE2 / MIB_IF_ROW2 的布局常量（x64，sizeof(MIB_IF_ROW2) = 1352）。</summary>
    public static class MibIfRow2
    {
        public const int TableHeaderSize = 8;      // ULONG NumEntries + 对齐到 8
        public const int Size = 1352;
        public const int TypeOffset = 1128;        // IFTYPE
        public const int FlagsOffset = 1152;       // InterfaceAndOperStatusFlags（位域，1 字节）
        public const int OperStatusOffset = 1156;  // IF_OPER_STATUS
        public const int InOctetsOffset = 1208;
        public const int OutOctetsOffset = 1280;
        public const int IfTypeSoftwareLoopback = 24;
        public const int IfOperStatusUp = 1;
        public const byte FlagFilterInterface = 0x02; // 过滤驱动的镜像接口，会把同一份流量再算一遍
    }

    // ── Foreground window / WinEvent hook ──
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ── Low-level mouse hook (click-outside detection) ──
    public const int WH_MOUSE_LL = 14;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_RBUTTONDOWN = 0x0204;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

    // ── Win32 menu bar (live shortcut reading) ──
    public const uint MF_BYPOSITION = 0x00000400;

    [DllImport("user32.dll")]
    public static extern IntPtr GetMenu(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetSubMenu(IntPtr hMenu, int nPos);

    [DllImport("user32.dll")]
    public static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMenuStringW")]
    public static extern int GetMenuString(IntPtr hMenu, uint uIDItem, System.Text.StringBuilder lpString, int cchMax, uint flags);

    // ── Monitors (multi-display floating / snap-to-top) ──
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>All monitor rectangles in device pixels.</summary>
    public static List<RECT> MonitorRects()
    {
        var list = new List<RECT>();
        MonitorEnumProc proc = (IntPtr _, IntPtr _, ref RECT r, IntPtr _) =>
        {
            list.Add(r);
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        return list;
    }

    // ── Global hotkey ──
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ── Foreground window restore ──
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    public const uint GA_ROOT = 2;

    // ── SendInput（合成键盘/鼠标事件，用于 Ctrl+V 注入）──
    public const int INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_SCANCODE = 0x0008;
    public const int VK_CONTROL = 0x11;
    public const int VK_SHIFT = 0x10;
    public const int VK_MENU = 0x12;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_V = 0x56;
    // 左右分身：GetAsyncKeyState 要按具体键查，抬起时也必须发对应的键
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_LMENU = 0xA4;
    public const int VK_RMENU = 0xA5;

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    /// <summary>x64 下 union 各分支互相覆盖，靠 LayoutKind.Explicit 保证与原生 INPUT 布局一致。</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    // ── 剪贴板格式 ──
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    // ── 点击穿透判定（click-outside hit-testing，复用上面已有的 POINT）──
    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    // ── 文件/文件夹图标 ──
    public const uint SHGFI_ICON = 0x100;
    public const uint SHGFI_LARGEICON = 0x0;
    public const uint SHGFI_SMALLICON = 0x1;
    public const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ── 虚拟屏幕范围 / 区域截图 ──
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    // SRCCOPY 已在文件上方声明（Live glass capture 区块），此处仅补充截图叠加光标标志。
    public const uint CAPTUREBLT = 0x40000000;
}

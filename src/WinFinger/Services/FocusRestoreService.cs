using WinFinger.Interop;

namespace WinFinger.Services;

/// <summary>
/// 记住展开前的前台窗口，收起/粘贴时把焦点还给它。
/// Windows 的前台切换有诸多限制（UAC 提权窗口、远程桌面、其它进程正在抢焦点），
/// 因此所有方法都不抛异常，失败就老实返回 false，由调用方降级提示"请手动 Ctrl+V"。
/// </summary>
public sealed class FocusRestoreService
{
    /// <summary>上一次 Remember 记下的顶层窗口（0 表示没有可还原目标）。</summary>
    public IntPtr Remembered { get; private set; }

    /// <summary>
    /// 记住当前前台窗口。必须在岛激活之前调用，否则记到的是我们自己。
    /// 前台窗口属于本进程时退回 fallback（通常是 ForegroundAppService.Hwnd）。
    /// </summary>
    public void Remember(IntPtr fallback)
    {
        try
        {
            var h = NativeMethods.GetForegroundWindow();
            if (h == IntPtr.Zero || IsOwnProcess(h)) h = fallback;
            Remembered = h == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(h, NativeMethods.GA_ROOT);
        }
        catch
        {
            Remembered = IntPtr.Zero;
        }
    }

    /// <summary>清掉记忆（例如目标窗口已经关闭）。</summary>
    public void Forget() => Remembered = IntPtr.Zero;

    /// <summary>
    /// 尝试把焦点还给记住的窗口：最小化则先还原 → SetForegroundWindow →
    /// 失败则 AttachThreadInput + BringWindowToTop → 再失败用 ALT 轻点解锁前台。
    /// </summary>
    public bool Restore()
    {
        try
        {
            var h = Remembered;
            if (h == IntPtr.Zero || !NativeMethods.IsWindow(h) || !NativeMethods.IsWindowVisible(h)) return false;

            if (NativeMethods.IsIconic(h)) NativeMethods.ShowWindow(h, NativeMethods.SW_RESTORE);
            NativeMethods.SetForegroundWindow(h);
            if (NativeMethods.GetForegroundWindow() == h) return true;

            // 第二招：把输入队列挂到目标线程上，Windows 才允许我们改前台
            uint me = NativeMethods.GetCurrentThreadId();
            uint target = NativeMethods.GetWindowThreadProcessId(h, out _);
            if (target != 0 && target != me)
            {
                bool attached = NativeMethods.AttachThreadInput(me, target, true);
                NativeMethods.BringWindowToTop(h);
                NativeMethods.SetForegroundWindow(h);
                if (attached) NativeMethods.AttachThreadInput(me, target, false);
                if (NativeMethods.GetForegroundWindow() == h) return true;
            }

            // 第三招：合成一次 ALT 轻点，解除前台锁定后再试一次
            KeyboardInjector.SendAltTap();
            NativeMethods.SetForegroundWindow(h);
            return NativeMethods.GetForegroundWindow() == h;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Restore() 之后轮询确认前台真的切过去了；最多等待 timeoutMs。</summary>
    public async Task<bool> RestoreAndWaitAsync(int timeoutMs = 400)
    {
        var h = Remembered;
        if (h == IntPtr.Zero) return false;
        if (Restore()) return true;

        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(25).ConfigureAwait(true);
            try
            {
                if (NativeMethods.GetForegroundWindow() == h) return true;
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private static bool IsOwnProcess(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }
}

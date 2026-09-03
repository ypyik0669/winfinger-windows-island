using System.Windows;

namespace WinFinger.Views;

/// <summary>
/// 岛窗口是 WS_EX_NOACTIVATE、托盘菜单宿主也是瞬时窗口，
/// 弹出文件对话框 / MessageBox 时需要一个真正可激活的 owner，否则拿不到焦点。
/// </summary>
public static class DialogOwner
{
    /// <summary>建一个 1×1 透明置顶窗口并激活它，用完必须 Close()。</summary>
    public static Window Create()
    {
        var owner = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Opacity = 0,
            ShowInTaskbar = false,
            Width = 1,
            Height = 1,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        owner.Show();
        owner.Activate();
        return owner;
    }

    /// <summary>在临时 owner 下执行一段需要焦点的 UI 操作，结束后关闭 owner。</summary>
    public static T WithOwner<T>(Func<Window, T> action)
    {
        var owner = Create();
        try
        {
            return action(owner);
        }
        finally
        {
            owner.Close();
        }
    }
}

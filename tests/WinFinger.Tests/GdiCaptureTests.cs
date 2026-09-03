using System.IO;
using System.Windows.Media.Imaging;
using System.Windows;
using WinFinger.Interop;
using Xunit;

namespace WinFinger.Tests;

public class GdiCaptureTests
{
    /// <summary>没有交互式桌面（无会话 / 服务账户）时抓屏必然失败，跳过而不是判错。</summary>
    private static bool HasDesktop => Environment.UserInteractive && SystemParameters.VirtualScreenWidth > 0;

    [Fact]
    public void CapturePng_ReturnsDecodable8x8Png()
    {
        if (!HasDesktop) return;

        var png = GdiCapture.CapturePng(0, 0, 8, 8);
        Assert.NotNull(png);

        var frame = BitmapFrame.Create(new MemoryStream(png!), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Equal(8, frame.PixelWidth);
        Assert.Equal(8, frame.PixelHeight);
    }
}

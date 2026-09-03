using WinFinger.Services;
using Xunit;

namespace WinFinger.Tests;

public class QrServiceTests
{
    [Fact]
    public void EncodeThenDecode_RoundTripsUnicode()
    {
        var png = QrService.EncodePng("hello 中文");
        Assert.Equal("hello 中文", QrService.Decode(png));
    }

    [Fact]
    public void EncodeThenDecode_RoundTripsUrl()
    {
        const string url = "https://example.com/a?b=1&c=2";
        Assert.Equal(url, QrService.Decode(QrService.EncodePng(url)));
    }

    [Fact]
    public void Decode_SolidImage_ReturnsNull()
    {
        var png = TestImages.SolidPng(120, 120, 200, 30, 30);
        Assert.Null(QrService.Decode(png));
    }

    [Fact]
    public void Decode_EmptyOrGarbage_ReturnsNull()
    {
        Assert.Null(QrService.Decode(Array.Empty<byte>()));
        Assert.Null(QrService.Decode(new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void Encode_ProducesSquareFrozenBitmap()
    {
        var bmp = QrService.Encode("abc", 256);
        Assert.True(bmp.IsFrozen);
        Assert.Equal(bmp.PixelWidth, bmp.PixelHeight);
        Assert.True(bmp.PixelWidth > 0);
    }

    [Fact]
    public void Encode_TooLongContent_ThrowsFriendlyError()
    {
        // QR byte 模式上限约 2953 字节，4000 字符必定超限
        var text = new string('a', 4000);
        var ex = Assert.Throws<InvalidOperationException>(() => QrService.Encode(text));
        Assert.Equal("内容过长，无法生成二维码", ex.Message);
    }

    [Fact]
    public void Encode_EmptyContent_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => QrService.Encode(""));
    }

    [Fact]
    public async Task Decode_LargeImage_DownscalesAndStillDecodes()
    {
        // 2000px > 1600px 上限，走 TransformedBitmap 缩小分支；同时验证脱离 UI 线程可用
        var png = QrService.EncodePng("big-qr-payload", 2000);
        var source = new System.IO.MemoryStream(png);
        var frame = System.Windows.Media.Imaging.BitmapFrame.Create(source,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        Assert.True(Math.Max(frame.PixelWidth, frame.PixelHeight) > 1600);

        var text = await Task.Run(() => QrService.Decode(png));
        Assert.Equal("big-qr-payload", text);
    }

    [Fact]
    public async Task Decode_WorksOffTheUiThread()
    {
        var png = QrService.EncodePng("off-thread");
        var text = await Task.Run(() => QrService.Decode(png));
        Assert.Equal("off-thread", text);
    }
}

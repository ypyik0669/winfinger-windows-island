using WinFinger.Services;
using Xunit;

namespace WinFinger.Tests;

/// <summary>
/// OcrService 的无引擎/无副作用路径测试。
/// 识别质量依赖构建机上是否安装了语言包，所以这里只断言不抛异常与降级行为。
/// </summary>
public class OcrServiceTests
{
    [Fact]
    public void UnavailableMessage_MentionsLanguagePack()
    {
        Assert.Contains("OCR 语言包", OcrService.UnavailableMessage);
        Assert.Equal("ms-settings:regionlanguage", OcrService.LanguageSettingsUri);
    }

    [Fact]
    public void AvailableLanguages_NeverNull()
    {
        var svc = new OcrService();
        Assert.NotNull(svc.AvailableLanguages);
    }

    [Fact]
    public async Task RecognizeAsync_EmptyBytes_ReturnsNull()
    {
        var svc = new OcrService();
        var result = await svc.RecognizeAsync(Array.Empty<byte>(), null, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task RecognizeAsync_GarbageBytes_ReturnsNullWithoutThrowing()
    {
        var svc = new OcrService();
        var result = await svc.RecognizeAsync(new byte[] { 1, 2, 3, 4, 5 }, "en-US", CancellationToken.None);
        Assert.Null(result);
        Assert.True(svc.LastStatus is OcrStatus.Failed or OcrStatus.NoEngine);
    }

    [Fact]
    public async Task RecognizeAsync_SolidImage_DoesNotThrow()
    {
        var svc = new OcrService();
        var png = TestImages.SolidPng(64, 64, 255, 255, 255);
        var result = await svc.RecognizeAsync(png, null, CancellationToken.None);
        // 有引擎 → 空文本结果；无引擎 → null。两者都不应抛异常。
        if (result is not null)
        {
            Assert.NotNull(result.Lines);
            Assert.Equal(OcrStatus.Done, svc.LastStatus);
        }
        else
        {
            Assert.Equal(OcrStatus.NoEngine, svc.LastStatus);
        }
    }
}

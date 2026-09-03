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
    public async Task RecognizeAsync_UninstalledExplicitLanguage_ReturnsNoEngineWithoutSubstituting()
    {
        var svc = new OcrService();
        var png = TestImages.SolidPng(48, 48, 255, 255, 255);
        // 一个几乎不可能装了 OCR 语言包的标签：绝不能悄悄退回用户配置语言
        var result = await svc.RecognizeAsync(png, "xx-ZZ", CancellationToken.None);
        Assert.Null(result);
        Assert.Equal(OcrStatus.NoEngine, svc.LastStatus);

        // 负缓存后再调一次仍是同样结果（不会因缓存到别的引擎而变成 Done）
        var again = await svc.RecognizeAsync(png, "xx-ZZ", CancellationToken.None);
        Assert.Null(again);
        Assert.Equal(OcrStatus.NoEngine, svc.LastStatus);
    }

    [Fact]
    public async Task RecognizeAsync_ConcurrentCalls_AreSerializedAndDoNotThrow()
    {
        var svc = new OcrService();
        var png = TestImages.SolidPng(64, 64, 255, 255, 255);
        var tasks = Enumerable.Range(0, 6)
            .Select(_ => svc.RecognizeAsync(png, null, CancellationToken.None))
            .ToArray();
        await Task.WhenAll(tasks);
        Assert.True(svc.LastStatus is OcrStatus.Done or OcrStatus.NoEngine);
    }

    [Fact]
    public void IsAvailable_IsStableAcrossCalls()
    {
        var svc = new OcrService();
        Assert.Equal(svc.IsAvailable, svc.IsAvailable);
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

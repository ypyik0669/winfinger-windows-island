using System.IO;
using System.Text.Json;
using WinFinger.Services;
using Xunit;

namespace WinFinger.Tests;

public class AtomicJsonTests
{
    [Fact]
    public void IsCorruptionError_true_for_malformed_json()
    {
        Assert.True(AtomicJson.IsCorruptionError(new JsonException("bad json")));
    }

    [Fact]
    public void IsCorruptionError_true_for_unsupported_shape()
    {
        Assert.True(AtomicJson.IsCorruptionError(new NotSupportedException("unsupported")));
    }

    [Fact]
    public void IsCorruptionError_false_for_transient_io_failure()
    {
        // 文件被杀软/备份等占用时抛出的是 IOException（及其子类 UnauthorizedAccessException 等），
        // 这类瞬时失败不该被当成"内容损坏"，不能触发 .corrupt 改名。
        Assert.False(AtomicJson.IsCorruptionError(new IOException("locked")));
        Assert.False(AtomicJson.IsCorruptionError(new UnauthorizedAccessException("denied")));
    }
}

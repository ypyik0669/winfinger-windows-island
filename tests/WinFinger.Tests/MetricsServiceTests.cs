using System.Net.NetworkInformation;
using WinFinger.Services;
using Xunit;

namespace WinFinger.Tests;

/// <summary>GetIfTable2 口径必须跟原来的 NetworkInterface 枚举一致，否则网速会翻倍或漏算。</summary>
public class MetricsServiceTests
{
    [Fact]
    public void TryReadNetworkTotals_MatchesNetworkInterfaceEnumeration()
    {
        long refReceived = 0, refSent = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var stats = nic.GetIPStatistics();
            refReceived += stats.BytesReceived;
            refSent += stats.BytesSent;
        }

        Assert.True(MetricsService.TryReadNetworkTotals(out long received, out long sent));

        // 两次读取之间有流量经过，允许 2MB 的漂移；差一倍就是重复计数 / 漏算
        Assert.InRange(received, refReceived - 2_000_000, refReceived + 2_000_000);
        Assert.InRange(sent, refSent - 2_000_000, refSent + 2_000_000);
    }

    [Fact]
    public void TryReadNetworkTotals_IsFast()
    {
        MetricsService.TryReadNetworkTotals(out _, out _); // 预热
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 50; i++) MetricsService.TryReadNetworkTotals(out _, out _);
        Assert.True(sw.Elapsed.TotalMilliseconds / 50 < 20, $"每次 {sw.Elapsed.TotalMilliseconds / 50:F2} ms");
    }
}

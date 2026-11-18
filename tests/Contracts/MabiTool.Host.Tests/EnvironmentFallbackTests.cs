using System.Net;
using MabiTool.Host.Rooms;
using MabiTool.Host.Updates;
using MabiTool.Infrastructure.Updates;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>局域网邀请地址的选择，以及更新替换器未就绪时的重启一次。</summary>
public sealed class EnvironmentFallbackTests
{
    [Fact]
    public void LanAddressesSkipLinkLocalAndPutGatewayAdaptersFirst()
    {
        var addresses = RoomServerListenerFactory.LanAddresses([
            (IPAddress.Parse("172.20.0.1"), false), (IPAddress.Parse("169.254.10.2"), true), (IPAddress.Parse("192.168.1.20"), true),
            (IPAddress.Parse("10.8.0.3"), false), (IPAddress.Parse("127.0.0.1"), false), (IPAddress.Parse("fe80::1"), true),
            (IPAddress.Parse("192.168.1.20"), false)]);
        Assert.Equal(["192.168.1.20", "10.8.0.3", "172.20.0.1"], addresses.Select(address => address.ToString()));
        Assert.Empty(RoomServerListenerFactory.LanAddresses([(IPAddress.Parse("169.254.1.1"), false), (IPAddress.Parse("::1"), false)]));
    }

    [Fact]
    public async Task ReplacerThatIsNotReadyIsRestartedOnceAndSecondSuccessHandsOver()
    {
        var starts = 0; var abandoned = 0;
        await PortableUpdateService.StartReplacerAsync(() => starts++, () => starts == 2, () => false,
            () => { abandoned++; return Task.CompletedTask; }, TimeSpan.FromMilliseconds(50), CancellationToken.None, TimeSpan.FromMilliseconds(5));
        Assert.Equal((2, 1), (starts, abandoned));
    }

    [Fact]
    public async Task ReplacerThatExitsTwiceReportsUpdaterUnavailableAfterTheRetry()
    {
        var starts = 0; var abandoned = 0;
        var error = await Assert.ThrowsAsync<UpdatePackageException>(() => PortableUpdateService.StartReplacerAsync(() => starts++, () => false, () => true,
            () => { abandoned++; return Task.CompletedTask; }, TimeSpan.FromSeconds(30), CancellationToken.None, TimeSpan.FromMilliseconds(5)));
        Assert.Equal("update.updaterUnavailable", error.Code);
        Assert.Equal((2, 2), (starts, abandoned));
    }
}

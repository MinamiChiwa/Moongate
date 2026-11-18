using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.RoomServer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace MabiTool.Host.Rooms;

public interface IRoomServerListener : IAsyncDisposable
{
    int Port { get; }
    string Epoch { get; }
    string LocalEndpoint { get; }
    IReadOnlyList<string> LanEndpoints { get; }
}
public interface IRoomServerListenerFactory
{
    Task<IRoomServerListener> StartAsync(RoomServerMode mode, CancellationToken ct);
}

public sealed class RoomServerListenerFactory(TimeProvider? time = null) : IRoomServerListenerFactory
{
    public async Task<IRoomServerListener> StartAsync(RoomServerMode mode, CancellationToken ct)
    {
        var addresses = mode == RoomServerMode.Lan ? LanAddresses(NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(adapter => adapter.GetIPProperties())
            .SelectMany(properties => properties.UnicastAddresses.Select(address => (address.Address,
                Gateway: properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)))))) : [];
        if (mode == RoomServerMode.Lan && addresses.Length == 0)
            throw new OperationException("room.lanUnavailable", "本机没有可用的 IPv4 局域网地址（只有 IPv6 或未连接网络）");
        var application = RoomServerApplication.Build(new(mode == RoomServerMode.Lan ? "0.0.0.0" : "127.0.0.1", 0), time);
        try
        {
            await application.StartAsync(ct);
            var port = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
                .Addresses.Select(address => new Uri(address).Port).Distinct().Single();
            return new Listener(application, port, addresses);
        }
        catch { await application.DisposeAsync(); throw; }
    }
    /// <summary>
    /// 邀请里列出的局域网地址：IPv4、非回环、非链路本地（169.254/16，未取得地址时的自动地址，别的电脑连不上）；
    /// 有默认网关的网卡（实际接入局域网的那张）排在前面，虚拟网卡、VPN 等排在后面，同组内按地址排序，最多 16 个。
    /// </summary>
    internal static IPAddress[] LanAddresses(IEnumerable<(IPAddress Address, bool Gateway)> candidates) => candidates
        .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(item.Address) &&
            !item.Address.Equals(IPAddress.Any) && !item.Address.Equals(IPAddress.Broadcast) &&
            !(item.Address.GetAddressBytes() is [169, 254, ..]))
        .GroupBy(item => item.Address).Select(group => (group.Key, Gateway: group.Any(item => item.Gateway)))
        .OrderBy(item => item.Gateway ? 0 : 1).ThenBy(item => item.Key.ToString(), StringComparer.Ordinal)
        .Select(item => item.Key).Take(16).ToArray();

    private sealed class Listener(WebApplication application, int port, IReadOnlyList<IPAddress> addresses) : IRoomServerListener
    {
        private readonly object gate = new();
        private Task? disposal;
        public int Port { get; } = port;
        public string Epoch { get; } = RoomServerApplication.ServerEpoch(application);
        public string LocalEndpoint { get; } = $"ws://127.0.0.1:{port}/ws";
        public IReadOnlyList<string> LanEndpoints { get; } = Array.AsReadOnly(addresses.Select(address => $"ws://{address}:{port}/ws").ToArray());
        public ValueTask DisposeAsync() { lock (gate) return new(disposal ??= CloseAsync()); }
        private async Task CloseAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await application.StopAsync(deadline.Token); }
            finally { await application.DisposeAsync(); }
        }
    }
}

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
        var addresses = mode == RoomServerMode.Lan ? NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses).Select(address => address.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) &&
                !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.Broadcast))
            .Distinct().OrderBy(address => address.ToString(), StringComparer.Ordinal).Take(16).ToArray() : [];
        if (mode == RoomServerMode.Lan && addresses.Length == 0)
            throw new OperationException("room.lanUnavailable", "本机没有可用的 IPv4 局域网接口");
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

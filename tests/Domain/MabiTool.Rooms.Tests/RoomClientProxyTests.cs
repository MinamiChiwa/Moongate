using MabiTool.Modules.Rooms.Client;
using Xunit;

namespace MabiTool.Rooms.Tests;

/// <summary>本机与局域网房间直连不经系统代理，公网地址沿用系统代理。</summary>
public sealed class RoomClientProxyTests
{
    [Theory]
    [InlineData("ws://127.0.0.1:5000/ws", true)]
    [InlineData("ws://localhost:5000/ws", true)]
    [InlineData("ws://[::1]:5000/ws", true)]
    [InlineData("ws://192.168.1.20:5000/ws", true)]
    [InlineData("ws://10.0.0.5:5000/ws", true)]
    [InlineData("ws://172.16.0.9:5000/ws", true)]
    [InlineData("ws://172.31.255.1:5000/ws", true)]
    [InlineData("ws://169.254.3.4:5000/ws", true)]
    [InlineData("ws://[fe80::1]:5000/ws", true)]
    [InlineData("ws://[fd12:3456::1]:5000/ws", true)]
    [InlineData("ws://172.32.0.1:5000/ws", false)]
    [InlineData("ws://8.8.8.8:5000/ws", false)]
    [InlineData("wss://example.trycloudflare.com/ws", false)]
    [InlineData("ws://[2001:db8::1]:5000/ws", false)]
    public void LocalAndPrivateEndpointsBypassTheSystemProxy(string endpoint, bool bypass) =>
        Assert.Equal(bypass, RoomClient.BypassesProxy(new Uri(endpoint)));
}

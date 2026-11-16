using System.Text.Json;
using Xunit;

namespace MabiTool.Rooms.Tests;

public sealed class RoomProtocolFixtureTests
{
    [Fact]
    public void FixtureContainsVersionOneHandshakeAndClockExchange()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "room-protocol.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var messages = root.GetProperty("messages").EnumerateArray()
            .Select(item => item.GetProperty("message").GetProperty("type").GetString()!)
            .ToArray();

        Assert.Equal(1, root.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(["hello", "hello", "hello-accepted", "sync", "sync"], messages);
        Assert.DoesNotContain("token", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }
}

using MabiTool.ModuleSdk;

namespace MabiTool.Host.Rooms;

/// <summary>房间会话连接及本机房间服务状态；不输出房间码、邀请、恢复令牌或监听地址。</summary>
internal sealed class RoomDiagnosticProbe(RoomSessionManager sessions, RoomServerOperations? server) : IDiagnosticProbe
{
    public string Id => "rooms";
    public string Module => "rooms";
    public DiagnosticProbeReading Capture()
    {
        var (count, connected) = sessions.DiagnosticCounts();
        var reading = new DiagnosticProbeReading(connected > 0 ? "connected" : count > 0 ? "disconnected" : "idle")
            .Count("sessions", count).Count("sessions.connected", connected);
        if (server?.Get() is { } snapshot)
            reading.Value("server.state", snapshot.State.RawValue).Value("server.mode", snapshot.Mode?.RawValue)
                .Value("server.tunnel", snapshot.Tunnel.State.RawValue).Value("server.problem", snapshot.Problem?.Code)
                .Value("server.tunnelProblem", snapshot.Tunnel.Problem?.Code)
                .Flag("server.localEndpoint", snapshot.LocalEndpoint is not null).Count("server.lanEndpoints", snapshot.LanEndpoints.Count)
                .Count("server.revision", snapshot.Revision.Value);
        return reading;
    }
}

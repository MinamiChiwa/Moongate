using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

public enum RoomServerMode { Loopback, Lan, Public }
public enum RoomServerState { Stopped, Starting, Running, Stopping, Failed }
public enum RoomTunnelState { NotRequested, Starting, Available, Failed }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoomServerStartRequest(OpenEnum<RoomServerMode> Mode);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoomServerStopRequest;
public sealed record RoomTunnelSnapshot(OpenEnum<RoomTunnelState> State, string? Endpoint, ApiProblem? Problem);
public sealed record RoomServerSnapshot(string BackendEpoch, UInt64String Revision, OpenEnum<RoomServerState> State,
    OpenEnum<RoomServerMode>? Mode, string? Owner, string? OperationId, string? ServerEpoch,
    string? LocalEndpoint, IReadOnlyList<string> LanEndpoints, RoomTunnelSnapshot Tunnel, ApiProblem? Problem);

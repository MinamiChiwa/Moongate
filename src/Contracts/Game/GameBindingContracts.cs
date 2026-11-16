using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

public sealed record GameWindowCandidate(string Id, string Name, string ProcessName, int ProcessId,
    DateTimeOffset ProcessStartTime, string Region, UInt64String WindowIdentity, DateTimeOffset ExpiresAt);
public sealed record GameBindingSnapshot(string State, GameWindowBinding? Binding, bool Simulation, string BackendEpoch, UInt64String Revision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GameBindingRequest(string CandidateId);

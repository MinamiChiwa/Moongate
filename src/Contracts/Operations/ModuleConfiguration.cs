namespace MabiTool.Contracts;

public sealed record ModuleConfiguration(string Id, bool Enabled, UInt64String Revision);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record ModuleConfigurationRequest(bool Enabled);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record CancelOperationRequest(string? Reason);

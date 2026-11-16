namespace MabiTool.Contracts;

public static class ApiPermissions
{
    public const string MusicEdit = "music.edit";
    public const string DesktopCapture = "capture.desktop";
    public const string DesktopControl = "desktop.control";
    public const string DiagnosticsExport = "diagnostics.export";
    public const string DiagnosticsDebug = "diagnostics.debug";
    public const string FilesTransfer = "files.transfer";
    public const string StorageManage = "storage.manage";
    public const string UpdatesManage = "updates.manage";
    public const string ColorEdit = "color.edit";
    public const string ColorControl = "color.control";
    public const string RoomsCoordinate = "rooms.coordinate";
    public const string ReplayControl = "replay.control";
    public const string CombatControl = "combat.control";
    public const string InventoryControl = "inventory.control";
    public const string InventoryEdit = "inventory.edit";
    public const string GameObserve = "game.observe";
    public const string GameControl = "game.control";
    public const string SystemRead = "system.read";
    public const string AuthManage = "auth.manage";
    public const string ModulesConfigure = "modules.configure";
}

public sealed record AuthSession(string ClientId, string ClientName, IReadOnlyList<string> Permissions,
    string? Origin, DateTimeOffset? ExpiresAt, bool Owner);
public sealed record PairingChallengeRequest(string ClientName, string? Origin, IReadOnlyList<string> Permissions);
public sealed record PairingChallenge(string ChallengeId, string Code, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"PairingChallenge {{ ChallengeId = {ChallengeId}, ExpiresAt = {ExpiresAt}, Code = [redacted] }}";
}
public sealed record PairingRequest(string ChallengeId, string Code)
{
    public override string ToString() => $"PairingRequest {{ ChallengeId = {ChallengeId}, Code = [redacted] }}";
}
public sealed record AccessGrant(string Token, AuthSession Session)
{
    public override string ToString() => $"AccessGrant {{ Session = {Session}, Token = [redacted] }}";
}
public sealed record ClientRevocation(string ClientId, bool Revoked);
public sealed record LocalDiscoveryInfo(int ProtocolVersion, string InstanceId, string ApiBaseUri,
    string PipeName, int ProcessId, DateTimeOffset ProcessStartTime, DateTimeOffset PublishedAt);
public sealed record BootstrapRequest(int ProtocolVersion, string ClientName);
public sealed record BootstrapResponse(int ProtocolVersion, string InstanceId, string ApiBaseUri, AccessGrant Grant);

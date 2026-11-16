namespace MabiTool.Contracts;

public enum ObservationQuality { Confirmed, Estimated, Stale, Conflict, Unknown, Partial }
public sealed record ObservationSource(string Provider, string ProviderVersion, string Region, string? GameBuild);
public sealed record Observation<T>(T Value, ObservationSource Source, DateTimeOffset ObservedAt,
    string HostClockEpoch, double MonotonicMs, double ValidForMs, UInt64String Generation,
    UInt64String Sequence, OpenEnum<ObservationQuality> Quality) where T : notnull;
public sealed record GameSession(string Id, string Region, int ProcessId, DateTimeOffset ProcessStartTime,
    UInt64String WindowIdentity, UInt64String BindingRevision, string? GameBuild);
public sealed record PhysicalWindowRect(int X, int Y, int Width, int Height);
public sealed record GameWindowBinding(GameSession Session, PhysicalWindowRect Bounds, double DpiX, double DpiY,
    bool IsMinimized, bool CanAcceptInput);
public sealed record CharacterIdentity(string Region, string RealmId, UInt64String CharacterId,
    string DisplayName, ObservationSource Source);
public sealed record EnvironmentSnapshot(string? RawTimeText, double? GameTimeMinutes, string? Weather,
    string? Channel, string? Area, OpenEnum<ObservationQuality> Quality, IReadOnlyList<string> UnavailableFields);

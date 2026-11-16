namespace MabiTool.Contracts;

public sealed record ShortcutChordSnapshot(string Key, bool Control, bool Alt, bool Shift, bool Windows);
public sealed record GlobalShortcutSnapshot(string ActionId, ShortcutChordSnapshot Configured,
    ShortcutChordSnapshot? Registered, string Status, string? FailureCode, bool UsedFallback);
public sealed record ShortcutActionSnapshot(string ActionId, string ModuleId);
public sealed record ShortcutBindingSetting(string ActionId, ShortcutChordSnapshot Chord);
public sealed record ShortcutTriggered(string ActionId, ShortcutChordSnapshot Chord,
    DateTimeOffset TriggeredAt, DateTimeOffset ExpiresAt);
public sealed record ShortcutSettingsRequest(IReadOnlyList<ShortcutBindingSetting> Bindings);
public sealed record ShortcutSettingsSnapshot(UInt64String Revision, IReadOnlyList<ShortcutActionSnapshot> Actions,
    IReadOnlyList<ShortcutBindingSetting> Bindings, IReadOnlyList<GlobalShortcutSnapshot> Registrations);

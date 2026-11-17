namespace MabiTool.Contracts;

public sealed record ShortcutChordSnapshot(string Key, bool Control, bool Alt, bool Shift, bool Windows);
public sealed record GlobalShortcutSnapshot(string ActionId, ShortcutChordSnapshot Configured,
    ShortcutChordSnapshot? Registered, string Status, string? FailureCode, bool UsedFallback);
/// <summary>
/// 可配置的快捷键动作。内建动作只有标识与模块；扩展命令另带 <see cref="Names"/>（以语言标记为键，含扩展名称）、
/// <see cref="Source"/>（<c>extension:&lt;标识&gt;</c>）与 <see cref="UnavailableReason"/>（扩展未运行、未授予 ui.shortcut 等，此时绑定保留但不注册）。
/// </summary>
public sealed record ShortcutActionSnapshot(string ActionId, string ModuleId, IReadOnlyDictionary<string, string>? Names = null,
    string? Source = null, string? UnavailableReason = null);
public sealed record ShortcutBindingSetting(string ActionId, ShortcutChordSnapshot Chord);
public sealed record ShortcutTriggered(string ActionId, ShortcutChordSnapshot Chord,
    DateTimeOffset TriggeredAt, DateTimeOffset ExpiresAt);
public sealed record ShortcutSettingsRequest(IReadOnlyList<ShortcutBindingSetting> Bindings);
public sealed record ShortcutSettingsSnapshot(UInt64String Revision, IReadOnlyList<ShortcutActionSnapshot> Actions,
    IReadOnlyList<ShortcutBindingSetting> Bindings, IReadOnlyList<GlobalShortcutSnapshot> Registrations);

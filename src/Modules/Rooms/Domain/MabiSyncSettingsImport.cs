using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using static MabiTool.Infrastructure.LegacyJsonFiles;

namespace MabiTool.Modules.Rooms.Domain;

public sealed record MabiSyncSettingsPlan(RoomPreferences Preferences, IReadOnlyList<ImportIssue> Issues, bool Conflict);

public static class MabiSyncSettingsImport
{
    public static IReadOnlySet<string> Files { get; } = new HashSet<string>(StringComparer.Ordinal) { "settings.json" };
    private static readonly string[] Modes = ["emote", "independent", "ensemble"];
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
        { "emote.start", "independent.start", "independent.stop", "ensemble.start", "ensemble.stop" };

    public static MabiSyncSettingsPlan Plan(LegacyJsonSource source, SettingsImportRequest request,
        IReadOnlyList<GlobalShortcutSnapshot> shortcuts)
    {
        if (request.ProfileNames is { Count: > 0 }) throw Invalid("settings.json", "MabiSync 迁移不接受方案名称映射");
        var root = source.Documents["settings.json"];
        if (root.ValueKind != JsonValueKind.Object) throw Invalid("settings.json", "设置须为对象");
        var issues = new List<ImportIssue>();
        void Issue(string key, string message) => issues.Add(new("import.settingAdjusted", "settings.json", key, message));
        long Number(string key, long fallback, long minimum, long maximum)
        {
            var value = Field(root, key);
            if (value.ValueKind == JsonValueKind.Undefined) return fallback;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= minimum && number <= maximum) return number;
            Issue(key, "沿用原范围恢复默认值"); return fallback;
        }
        string String(string key, string fallback, int maximum)
        {
            var value = Field(root, key);
            return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? fallback : Text(value, "settings.json", maximum);
        }
        string Endpoint(string key)
        {
            var value = String(key, "", 2048);
            if (value.Length == 0 || Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "ws" or "wss" &&
                uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0) return value;
            issues.Add(new("import.endpointNeedsReview", "settings.json", key, "连接地址需重新填写")); return "";
        }
        var opacity = .9;
        var alpha = Field(root, "overlayOpacity");
        if (alpha.ValueKind == JsonValueKind.Number && alpha.TryGetDouble(out var number) && double.IsFinite(number))
        {
            opacity = Math.Clamp(number, .35, 1);
            if (opacity != number) Issue("overlayOpacity", "沿用原范围裁剪透明度");
        }
        else if (alpha.ValueKind != JsonValueKind.Undefined) Issue("overlayOpacity", "恢复原默认透明度");
        var capture = Field(root, "hideFromCapture");
        var hide = capture.ValueKind == JsonValueKind.True;
        if (capture.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Undefined)) Issue("hideFromCapture", "恢复原默认捕获偏好");
        var saved = Field(root, "bindings");
        if (saved.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined)) throw Invalid("settings.json", "模式绑定须为对象");
        var bindings = new Dictionary<string, ModeInputPreferences>(StringComparer.Ordinal);
        foreach (var mode in Modes) bindings[mode] = ReadMode(Field(saved, mode));
        if (bindings["ensemble"].Start is null && bindings["ensemble"].Stop is null)
            bindings["ensemble"] = bindings["independent"];
        if (request.BindingOverrides is not null)
        {
            if (request.BindingOverrides.Count > Actions.Count) throw Invalid("settings.json", "绑定覆盖数量超限");
            foreach (var pair in request.BindingOverrides)
            {
                if (!Actions.Contains(pair.Key)) throw Invalid("settings.json", "绑定覆盖动作无效");
                ValidateBinding(pair.Value);
                var parts = pair.Key.Split('.'); var binding = bindings[parts[0]];
                bindings[parts[0]] = parts[1] == "start" ? binding with { Start = pair.Value } : binding with { Stop = pair.Value };
            }
        }
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var conflict = false;
        foreach (var mode in Modes)
        {
            var row = bindings[mode];
            row = mode == "emote" ? new(row.Start, null, false) : row with
                { StopIsToggle = row.StopIsToggle || row.Start is not null && row.Start == row.Stop };
            bindings[mode] = row;
            foreach (var action in mode == "emote" ? new[] { "start" } : new[] { "start", "stop" })
            {
                var binding = action == "start" ? row.Start : row.Stop;
                if (binding is null) continue;
                if (binding.Kind.KnownValue == LocalBindingKind.ChatDraft && mode != "emote")
                { issues.Add(new("import.bindingConflict", "settings.json", mode + "." + action, "聊天草稿仅用于表情开始，请明确覆盖此绑定")); conflict = true; }
                if (Conflicts(binding, shortcuts))
                { issues.Add(new("import.shortcutConflict", "settings.json", mode + "." + action, "按键与中央快捷键冲突，请明确覆盖此绑定")); conflict = true; }
            }
            kinds[mode] = row.Start?.Kind.KnownValue switch { LocalBindingKind.Button => 1, LocalBindingKind.ChatDraft => 2, _ => 0 };
        }
        var startKinds = Field(root, "startInputKinds");
        if (startKinds.ValueKind == JsonValueKind.Object)
            foreach (var mode in Modes)
            {
                var kind = Field(startKinds, mode);
                if (kind.ValueKind == JsonValueKind.Number && kind.TryGetInt32(out var index) && index >= 0 && index <= (mode == "emote" ? 2 : 1)) kinds[mode] = index;
                else if (kind.ValueKind != JsonValueKind.Undefined) Issue("startInputKinds." + mode, "恢复由绑定推导的输入种类");
            }
        var rule = Field(root, "musicStopRule");
        var name = rule.ValueKind == JsonValueKind.String ? rule.GetString() : null;
        var legacy = Field(root, "musicStopChoice");
        int? choice = legacy.ValueKind == JsonValueKind.Number && legacy.TryGetInt32(out var indexValue) ? indexValue : null;
        var stop = name switch { "manual" => MusicStopRule.Manual, "duration" => MusicStopRule.Duration, "first-cycle" => MusicStopRule.FirstCycle,
            _ => name is null && choice is null ? MusicStopRule.FirstCycle : choice == 2 ? MusicStopRule.Duration : MusicStopRule.Manual };
        foreach (var field in root.EnumerateObject().Where(p => p.Name is "window" or "selectedWindow" or "gameWindow" or "processId" or "pid" or "handle" or "restoreToken" or "roomToken" or "calibration"))
            issues.Add(new("import.bindingInvalidated", "settings.json", field.Name, "旧运行身份已失效，当前游戏须重新绑定"));
        var provider = String("publicProvider", "community", 32);
        if (provider is not ("lan" or "cloudflare" or "community")) { Issue("publicProvider", "沿用原恢复规则选择社区直连"); provider = "community"; }
        return new(new(new(0), Endpoint("endpoint"), provider, Endpoint("lanEndpoint"), String("nickname", "", 200),
            (int)Number("countdownMs", 5000, 1000, 60000), (int)Number("timingOffsetMs", 0, -2000, 2000),
            (int)Number("musicDurationMs", 30000, 1, 86400000), (int)Number("musicTailHoldMs", 0, 0, 10000), stop, hide, opacity,
            bindings, kinds, true, "emergency-stop"), issues, conflict);
    }

    private static JsonElement BindingField(JsonElement value, string name)
    {
        var upper = Field(value, name); var lower = Field(value, char.ToLowerInvariant(name[0]) + name[1..]);
        if (upper.ValueKind != JsonValueKind.Undefined && lower.ValueKind != JsonValueKind.Undefined)
            throw Invalid("settings.json", "绑定属性的大小写表示重复");
        return upper.ValueKind == JsonValueKind.Undefined ? lower : upper;
    }
    private static ModeInputPreferences ReadMode(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new(null, null, false);
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("settings.json", "模式绑定无效");
        var toggle = BindingField(value, "StopIsToggle");
        return new(ReadBinding(BindingField(value, "Start")), ReadBinding(BindingField(value, "Stop")), Boolean(toggle));
    }
    private static bool Boolean(JsonElement value) => value.ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False or JsonValueKind.Undefined => false, _ => throw Invalid("settings.json", "绑定布尔属性无效") };
    private static InputBindingPreference? ReadBinding(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("settings.json", "输入绑定须为对象");
        int Integer(string name)
        {
            var field = BindingField(value, name);
            return field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var number) ? number : throw Invalid("settings.json", "输入绑定数值无效");
        }
        var kind = Text(Field(value, "type"), "settings.json", 32);
        InputBindingPreference result = kind switch
        {
            "key" => new(LocalBindingKind.Key, checked((ushort)Integer("Key")), Boolean(BindingField(value, "Control")), Boolean(BindingField(value, "Alt")), Boolean(BindingField(value, "Shift"))),
            "button" => new(LocalBindingKind.Button, X: Integer("X"), Y: Integer("Y"), ClientWidth: Integer("ClientWidth"), ClientHeight: Integer("ClientHeight"), Dpi: checked((uint)Integer("Dpi"))),
            "chat-draft" => new(LocalBindingKind.ChatDraft),
            _ => throw Invalid("settings.json", "输入绑定类型无效")
        };
        ValidateBinding(result); return result;
    }
    public static void ValidateBinding(InputBindingPreference? binding)
    {
        if (binding is null || binding.Kind.KnownValue is null) throw Invalid("settings.json", "输入绑定无效");
        var geometry = binding.X is not null || binding.Y is not null || binding.ClientWidth is not null || binding.ClientHeight is not null || binding.Dpi is not null;
        var valid = binding.Kind.KnownValue switch
        {
            LocalBindingKind.Key => binding.Key is > 0 and <= 255 && !geometry,
            LocalBindingKind.Button => binding.Key is null && !binding.Control && !binding.Alt && !binding.Shift &&
                binding.ClientWidth is > 0 and <= 32768 && binding.ClientHeight is > 0 and <= 32768 && binding.Dpi is > 0 and <= 960 &&
                binding.X is >= 0 && binding.X < binding.ClientWidth && binding.Y is >= 0 && binding.Y < binding.ClientHeight,
            LocalBindingKind.ChatDraft => binding.Key is null && !binding.Control && !binding.Alt && !binding.Shift && !geometry,
            _ => false
        };
        if (!valid) throw Invalid("settings.json", "输入绑定字段或客户区几何无效");
    }
    private static bool Conflicts(InputBindingPreference binding, IReadOnlyList<GlobalShortcutSnapshot> shortcuts)
    {
        if (binding.Kind.KnownValue != LocalBindingKind.Key) return false;
        var chords = shortcuts.Select(x => x.Registered).OfType<ShortcutChordSnapshot>().Append(new("F9", false, false, false, false));
        return chords.Any(chord => !chord.Windows && Enum.TryParse<ShortcutKey>(chord.Key, out var key) &&
            binding.Key == (key <= ShortcutKey.F12 ? 0x70 + (int)key : key switch { ShortcutKey.Insert => 0x2D, ShortcutKey.Home => 0x24, ShortcutKey.End => 0x23, ShortcutKey.Pause => 0x13, _ => -1 }) &&
            binding.Control == chord.Control && binding.Alt == chord.Alt && binding.Shift == chord.Shift);
    }
}

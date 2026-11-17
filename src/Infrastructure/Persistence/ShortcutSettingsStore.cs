using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

public sealed class ShortcutSettingsStore(SqliteStore database, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    public static DatabaseMigration Migration { get; } = new("platform-shortcuts", 1, """
        CREATE TABLE platform_shortcut_settings(id INTEGER PRIMARY KEY CHECK(id=1),snapshot TEXT NOT NULL) STRICT;
        CREATE TABLE platform_shortcut_receipts(principal TEXT NOT NULL,key TEXT NOT NULL,hash TEXT NOT NULL,
            snapshot TEXT NOT NULL,expires INTEGER NOT NULL,PRIMARY KEY(principal,key)) STRICT;
        """);
    public static string ETag(UInt64String revision) => "\"shortcut-settings-" + revision + "\"";
    /// <summary>由前端执行的动作：后端只注册组合键并发布 shortcut.triggered。定时测量的开始／取消默认不分配按键。</summary>
    public static bool IsFrontendAction(string? action) => action is "overlay-toggle" or "overlay-click-through-toggle" or "combat-measurement-toggle";
    /// <summary>
    /// 发布 shortcut.triggered 的动作：前端执行的动作，以及后端已执行完的紧急停止（通知前端取消进行中的定时测量并在界面说明）。
    /// </summary>
    public static bool IsNotifiedAction(string? action) => IsFrontendAction(action) || action == "emergency-stop";
    /// <summary>
    /// 扩展清单声明的命令：<c>extension.&lt;发布者&gt;.&lt;名称&gt;.&lt;命令&gt;</c>。绑定按标识保存，扩展停用、卸载或命令移除后绑定仍保留，
    /// 重新可用时恢复；按下时由宿主通知扩展，不发布 shortcut.triggered。
    /// </summary>
    public static bool IsExtensionAction(string? action) => action is { Length: <= 120 } &&
        Regex.IsMatch(action, @"\Aextension\.[a-z][a-z0-9-]{0,31}\.[a-z][a-z0-9-]{0,31}\.[a-z][a-z0-9-]{0,31}\z", RegexOptions.CultureInvariant);
    /// <summary>保留给紧急停止及其备用键、浮窗显隐与上锁的单键（F7–F10、Pause），扩展命令不能使用。</summary>
    public static bool IsReservedChord(ShortcutChordSnapshot chord) => !chord.Control && !chord.Alt && !chord.Shift && !chord.Windows &&
        ShortcutKeys.TryParse(chord.Key, out var key) && key is ShortcutKey.F7 or ShortcutKey.F8 or ShortcutKey.F9 or ShortcutKey.F10 or ShortcutKey.Pause;
    public static ShortcutSettingsSnapshot Default => new(new(0),
        [new("emergency-stop", "core"), new("overlay-toggle", "presentation"), new("overlay-click-through-toggle", "presentation"),
            new("combat-measurement-toggle", "combat-observation")],
        [new("emergency-stop", new("F9", false, false, false, false))], []);
    // 已保存的配置沿用保存时的动作表；读取时换成当前动作表，新增的动作在旧配置下也能分配按键。
    public ShortcutSettingsSnapshot Read() => database.ReadScalar("SELECT snapshot FROM platform_shortcut_settings WHERE id=1") is string json
        ? JsonSerializer.Deserialize<ShortcutSettingsSnapshot>(json, ContractJson.Options)! with { Actions = Default.Actions } : Default;

    public static ShortcutSettingsRequest Normalize(ShortcutSettingsRequest request)
    {
        if (request?.Bindings is null || request.Bindings.Count > 64 ||
            request.Bindings.Any(value => value is null || value.ActionId != "emergency-stop" && !IsFrontendAction(value.ActionId) &&
                !IsExtensionAction(value.ActionId) || value.Chord is null || !ShortcutKeys.TryParse(value.Chord.Key, out _)) ||
            request.Bindings.Select(value => value.ActionId).Distinct(StringComparer.Ordinal).Count() != request.Bindings.Count)
            throw new OperationException("request.invalid", "快捷键动作或组合无效");
        if (request.Bindings.Any(value => IsExtensionAction(value.ActionId) && IsReservedChord(value.Chord)))
            throw new OperationException("request.invalid", "F7–F10 与 Pause 保留给内建动作，扩展命令不能使用");
        var values = request.Bindings.Select(value =>
        {
            ShortcutKeys.TryParse(value.Chord.Key, out var key);
            return value with { Chord = value.Chord with { Key = key.ToString() } };
        }).ToArray();
        return new(values.Any(value => value.ActionId == "emergency-stop") ? values : [.. Default.Bindings, .. values]);
    }

    public static void ValidateHeaders(string? key, string? condition)
    {
        if (key is null) throw new OperationException("idempotency.required", "快捷键配置需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z")) throw new OperationException("request.invalid", "快捷键幂等键无效");
        if (condition is null) throw new OperationException("revision.required", "快捷键配置需要 If-Match");
        if (!Regex.IsMatch(condition, "\\A\"shortcut-settings-(0|[1-9][0-9]{0,19})\"\\z"))
            throw new OperationException("request.invalid", "快捷键 ETag 无效");
    }

    public ShortcutSettingsSnapshot? Receipt(string principal, ShortcutSettingsRequest request, string? key, string? condition)
    {
        ValidateHeaders(key, condition);
        var hash = Hash(Normalize(request), condition!);
        return database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT hash,snapshot FROM platform_shortcut_receipts WHERE principal=$p AND key=$key AND expires>$now";
            command.Parameters.AddWithValue("$p", principal); command.Parameters.AddWithValue("$key", key!);
            command.Parameters.AddWithValue("$now", time.GetUtcNow().ToUnixTimeMilliseconds());
            using var rows = command.ExecuteReader();
            if (!rows.Read()) return null;
            if (rows.GetString(0) != hash) throw new OperationException("idempotency.conflict", "幂等键已用于其他快捷键配置");
            return JsonSerializer.Deserialize<ShortcutSettingsSnapshot>(rows.GetString(1), ContractJson.Options);
        });
    }

    public Task<ShortcutSettingsSnapshot> UpdateAsync(string principal, ShortcutSettingsRequest request,
        IReadOnlyList<GlobalShortcutSnapshot> registrations, string? key, string? condition, CancellationToken token = default)
    {
        request = Normalize(request); ValidateHeaders(key, condition);
        var hash = Hash(request, condition!);
        var frozen = JsonSerializer.Deserialize<GlobalShortcutSnapshot[]>(JsonSerializer.Serialize(registrations, ContractJson.Options), ContractJson.Options)!;
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        return database.WriteAsync(tx =>
        {
            tx.Execute("DELETE FROM platform_shortcut_receipts WHERE expires <= $now", ("$now", now));
            if (tx.Scalar("SELECT hash FROM platform_shortcut_receipts WHERE principal=$p AND key=$key", ("$p", principal), ("$key", key)) is string savedHash)
            {
                if (savedHash != hash) throw new OperationException("idempotency.conflict", "幂等键已用于其他快捷键配置");
                return JsonSerializer.Deserialize<ShortcutSettingsSnapshot>((string)tx.Scalar("SELECT snapshot FROM platform_shortcut_receipts WHERE principal=$p AND key=$key",
                    ("$p", principal), ("$key", key))!, ContractJson.Options)!;
            }
            var previous = tx.Scalar("SELECT snapshot FROM platform_shortcut_settings WHERE id=1") is string saved
                ? JsonSerializer.Deserialize<ShortcutSettingsSnapshot>(saved, ContractJson.Options)! : Default;
            if (condition != ETag(previous.Revision)) throw new OperationException("revision.stale", "快捷键配置已变化");
            if (previous.Revision.Value == ulong.MaxValue || Convert.ToInt64(tx.Scalar("SELECT COUNT(*) FROM platform_shortcut_receipts")) >= 1000)
                throw new OperationException("resource.capacityExceeded", "快捷键修订或回执已达到上限");
            var result = new ShortcutSettingsSnapshot(new(previous.Revision.Value + 1), Default.Actions, request.Bindings, frozen);
            var json = JsonSerializer.Serialize(result, ContractJson.Options);
            tx.Execute("INSERT INTO platform_shortcut_settings VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET snapshot=excluded.snapshot", ("$json", json));
            tx.Execute("INSERT INTO platform_shortcut_receipts VALUES($p,$key,$hash,$snapshot,$expires)",
                ("$p", principal), ("$key", key), ("$hash", hash), ("$snapshot", json), ("$expires", now + 86400000L));
            return result;
        }, token);
    }

    private static string Hash(ShortcutSettingsRequest request, string condition) => Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { request, condition }, ContractJson.Options)));
}

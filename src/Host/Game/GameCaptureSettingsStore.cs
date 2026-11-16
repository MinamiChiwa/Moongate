using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Game;

/// <summary>
/// 全局抓包设置：单行快照加 24 小时幂等回执（至多 1000 条），修订与 If-Match 规则同房间偏好。
/// 没有数据根时（无界面测试、模拟宿主）保存在内存，规则相同。
/// </summary>
public sealed class GameCaptureSettingsStore(SqliteStore? database, TimeProvider? time = null)
{
    private const int MaximumReceipts = 1000;
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, (string Hash, GameCaptureSettings Value, long Created)> memoryReceipts = new(StringComparer.Ordinal);
    private GameCaptureSettings memory = GameCaptureSettings.Default;
    private GameCaptureSettings? cached;
    public event Action? Changed;

    public static DatabaseMigration Migration { get; } = new("game-capture-settings", 1, """
        CREATE TABLE game_capture_settings(id INTEGER PRIMARY KEY CHECK(id=1), revision TEXT NOT NULL, snapshot TEXT NOT NULL) STRICT;
        CREATE TABLE game_capture_settings_receipts(key TEXT PRIMARY KEY, hash TEXT NOT NULL, snapshot TEXT NOT NULL, created INTEGER NOT NULL) STRICT;
        """);
    public static string ETag(UInt64String revision) => "\"game-capture-settings-" + revision + "\"";

    public GameCaptureSettings Read()
    {
        lock (gate)
        {
            if (database is null) return memory;
            if (cached is not null) return cached;
            return cached = database.ReadScalar("SELECT snapshot FROM game_capture_settings WHERE id=1") is string json
                ? JsonSerializer.Deserialize<GameCaptureSettings>(json, ContractJson.Options)! : GameCaptureSettings.Default;
        }
    }

    public async Task<GameCaptureSettings> UpdateAsync(GameCaptureSettings value, string? key, string? condition, CancellationToken ct = default)
    {
        if (key is null) throw new OperationException("idempotency.required", "保存抓包设置需要 Idempotency-Key");
        if (key.Length is < 8 or > 128 || key.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('.' or '_' or '-')))
            throw new OperationException("request.invalid", "幂等键无效");
        if (condition is null) throw new OperationException("revision.required", "保存抓包设置需要 If-Match");
        Validate(value);
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { value, condition }, ContractJson.Options)));
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        GameCaptureSettings result;
        if (database is null)
        {
            lock (gate)
            {
                foreach (var old in memoryReceipts.Where(x => x.Value.Created < now - 86400000L).Select(x => x.Key).ToArray()) memoryReceipts.Remove(old);
                if (memoryReceipts.TryGetValue(key, out var saved))
                    return saved.Hash == hash ? saved.Value : throw new OperationException("idempotency.conflict", "幂等键已用于其他设置修改");
                if (condition != ETag(memory.Revision) || value.Revision != memory.Revision) throw new OperationException("revision.stale", "抓包设置已变化");
                if (memoryReceipts.Count >= MaximumReceipts) throw new OperationException("resource.capacityExceeded", "抓包设置修改回执达到上限");
                result = memory = Next(value, memory.Revision);
                memoryReceipts[key] = (hash, result, now);
            }
        }
        else
        {
            result = await database.WriteAsync(tx =>
            {
                tx.Execute("DELETE FROM game_capture_settings_receipts WHERE created < $expired", ("$expired", now - 86400000L));
                if (tx.Scalar("SELECT hash FROM game_capture_settings_receipts WHERE key=$key", ("$key", key)) is string savedHash)
                {
                    if (savedHash != hash) throw new OperationException("idempotency.conflict", "幂等键已用于其他设置修改");
                    return JsonSerializer.Deserialize<GameCaptureSettings>((string)tx.Scalar(
                        "SELECT snapshot FROM game_capture_settings_receipts WHERE key=$key", ("$key", key))!, ContractJson.Options)!;
                }
                var revision = UInt64String.Parse(tx.Scalar("SELECT revision FROM game_capture_settings WHERE id=1") as string ?? "0");
                if (condition != ETag(revision) || value.Revision != revision) throw new OperationException("revision.stale", "抓包设置已变化");
                if (Convert.ToInt64(tx.Scalar("SELECT COUNT(*) FROM game_capture_settings_receipts")) >= MaximumReceipts)
                    throw new OperationException("resource.capacityExceeded", "抓包设置修改回执达到上限");
                var next = Next(value, revision);
                var json = JsonSerializer.Serialize(next, ContractJson.Options);
                tx.Execute("INSERT INTO game_capture_settings VALUES(1,$revision,$snapshot) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,snapshot=excluded.snapshot",
                    ("$revision", next.Revision.ToString()), ("$snapshot", json));
                tx.Execute("INSERT INTO game_capture_settings_receipts VALUES($key,$hash,$snapshot,$created)",
                    ("$key", key), ("$hash", hash), ("$snapshot", json), ("$created", now));
                return next;
            }, ct);
            lock (gate) cached = result;
        }
        Changed?.Invoke();
        return result;
    }

    private static GameCaptureSettings Next(GameCaptureSettings value, UInt64String revision)
    {
        if (revision.Value == ulong.MaxValue) throw new OperationException("resource.capacityExceeded", "抓包设置修订已耗尽");
        return value with { Revision = new(revision.Value + 1) };
    }

    private static void Validate(GameCaptureSettings value)
    {
        if (value.ChannelMode is not (GameCaptureSettings.AutomaticChannel or GameCaptureSettings.ManualChannel) ||
            value.ChannelId is not null && !MabiTool.Infrastructure.Workers.WorkerProtocol.IsId(value.ChannelId) ||
            value.ChannelMode == GameCaptureSettings.ManualChannel && value.ChannelId is null ||
            value.ChannelDescription is { } text && (text.Length > 512 || text.Any(char.IsControl)) ||
            value.ChannelId is null && value.ChannelDescription is not null)
            throw new OperationException("request.invalid", "抓包设置字段无效");
    }
}

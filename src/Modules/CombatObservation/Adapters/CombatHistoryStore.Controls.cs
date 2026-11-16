using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Modules.CombatObservation.Adapters;

public sealed record CombatMeasurementPreferences(CombatMeasurementSettings Settings, UInt64String Revision);
public sealed record CombatAutoObservationPreferences(bool Enabled, UInt64String Revision);

public sealed partial class CombatHistoryStore
{
    private const string ControlResource = "preferences", MeasurementStartRoute = "measurement-start",
        MeasurementCancelRoute = "measurement-cancel", MeasurementSettingsRoute = "measurement-settings", AutoObservationRoute = "auto-observation";

    public static string MeasurementSettingsETag(UInt64String revision) => $"\"combat-measurement-settings-{revision}\"";
    public static string AutoObservationETag(UInt64String revision) => $"\"combat-auto-observation-{revision}\"";

    /// <summary>定时测量上次使用的设定与独立修订；首次读取为 1 分钟、预备 5 秒和修订 0。</summary>
    public CombatMeasurementPreferences MeasurementPreferences() => ReadMeasurementPreferences(database.ReadScalar(
        "SELECT json_object('settings',json(settings),'revision',revision) FROM combat_preferences WHERE id=1") as string);
    public CombatMeasurementSettings MeasurementSettings() => MeasurementPreferences().Settings;

    /// <summary>常驻自动观测偏好与独立修订；首次读取为开启和修订 0。</summary>
    public CombatAutoObservationPreferences AutoObservationPreferences() => ReadAutoObservationPreferences(database.ReadScalar(
        "SELECT json_object('enabled',json(CASE enabled WHEN 1 THEN 'true' ELSE 'false' END),'revision',revision) FROM combat_auto_observation WHERE id=1") as string);
    public bool AutoObservationEnabled() => AutoObservationPreferences().Enabled;

    private static CombatMeasurementPreferences ReadMeasurementPreferences(string? json) => json is null
        ? new(new(), new(0)) : JsonSerializer.Deserialize<CombatMeasurementPreferences>(json, ContractJson.Options)!;
    private static CombatAutoObservationPreferences ReadAutoObservationPreferences(string? json) => json is null
        ? new(true, new(0)) : JsonSerializer.Deserialize<CombatAutoObservationPreferences>(json, ContractJson.Options)!;
    private static CombatMeasurementPreferences MeasurementPreferences(DatabaseTransaction tx) => ReadMeasurementPreferences(tx.Scalar(
        "SELECT json_object('settings',json(settings),'revision',revision) FROM combat_preferences WHERE id=1") as string);
    private static CombatAutoObservationPreferences AutoObservationPreferences(DatabaseTransaction tx) => ReadAutoObservationPreferences(tx.Scalar(
        "SELECT json_object('enabled',json(CASE enabled WHEN 1 THEN 'true' ELSE 'false' END),'revision',revision) FROM combat_auto_observation WHERE id=1") as string);

    private static void ValidateControlKey(string? key)
    {
        if (key is null) throw new OperationException("idempotency.required", "操作需要 Idempotency-Key");
        if (key.Length is < 8 or > 128 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new OperationException("request.invalid", "幂等键格式无效");
    }

    private static void ValidateControlHeaders(string? key, string? etag, string prefix)
    {
        ValidateControlKey(key);
        if (etag is null) throw new OperationException("revision.required", "偏好保存需要 If-Match");
        var start = "\"" + prefix + "-";
        if (!etag.StartsWith(start, StringComparison.Ordinal) || !etag.EndsWith('"') || etag.Length <= start.Length)
            throw new OperationException("request.invalid", "偏好 ETag 无效");
        try { _ = UInt64String.Parse(etag[start.Length..^1]); }
        catch (FormatException) { throw new OperationException("request.invalid", "偏好 ETag 无效"); }
    }

    // 首次查重只用读连接，避免等待观察检查点的写队列时占住测量锁；提交事务仍再次查重。
    private T? ReadControlReceipt<T>(string principal, string route, string? key, string hash) where T : class
    {
        ValidateControlKey(key);
        var json = database.ReadScalar(
            "SELECT json_object('hash',hash,'result',json(result)) FROM combat_receipts WHERE principal=$p AND route=$route AND resource=$resource AND key=$key AND expires_at>$now",
            ("$p", principal), ("$route", route), ("$resource", ControlResource), ("$key", key), ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds())) as string;
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.GetProperty("hash").GetString() != hash)
            throw new OperationException("idempotency.conflict", "幂等键已用于其他内容或修订");
        return document.RootElement.GetProperty("result").Deserialize<T>(ContractJson.Options)!;
    }

    private Task<(T Value, bool Applied)> WriteControlAsync<T>(string principal, string route, string? key, string hash,
        Func<DatabaseTransaction, T> create, CancellationToken ct) where T : class => database.WriteAsync(tx =>
    {
        if (Receipt<T>(tx, principal, route, ControlResource, key, hash) is { } retry) return (retry, false);
        var result = create(tx);
        SaveReceipt(tx, principal, route, ControlResource, key!, hash, result);
        return (result, true);
    }, ct);

    private static string Hash<T>(T value) => JsonSerializer.Serialize(value, ContractJson.Options);
    private static void WriteMeasurementPreferences(DatabaseTransaction tx, CombatMeasurementPreferences value) => tx.Execute(
        "INSERT INTO combat_preferences(id,settings,revision) VALUES(1,$json,$r) ON CONFLICT(id) DO UPDATE SET settings=excluded.settings,revision=excluded.revision",
        ("$json", Hash(value.Settings)), ("$r", value.Revision.ToString()));

    public CombatMeasurementState? MeasurementStartReceipt(string principal, CombatMeasurementStartRequest request, string? key) =>
        ReadControlReceipt<CombatMeasurementState>(principal, MeasurementStartRoute, key, Hash(request));
    public CombatMeasurementState? MeasurementCancelReceipt(string principal, CombatMeasurementCancelRequest request, string? key) =>
        ReadControlReceipt<CombatMeasurementState>(principal, MeasurementCancelRoute, key, Hash(request));

    public Task<(CombatMeasurementState Value, bool Applied)> CommitMeasurementStartAsync(string principal, CombatMeasurementStartRequest request,
        string? key, Func<CombatMeasurementPreferences, CombatMeasurementState> snapshot, CancellationToken ct = default) =>
        WriteControlAsync(principal, MeasurementStartRoute, key, Hash(request), tx =>
        {
            var current = MeasurementPreferences(tx);
            var next = new CombatMeasurementPreferences(new(request.DurationMs, 0), new(checked(current.Revision.Value + 1)));
            WriteMeasurementPreferences(tx, next);
            return snapshot(next);
        }, ct);

    public Task<(CombatMeasurementState Value, bool Applied)> CommitMeasurementCancelAsync(string principal, CombatMeasurementCancelRequest request,
        string? key, CombatMeasurementState snapshot, CancellationToken ct = default) =>
        WriteControlAsync(principal, MeasurementCancelRoute, key, Hash(request), _ => snapshot, ct);

    public Task<(CombatMeasurementState Value, bool Applied)> SaveMeasurementSettingsAsync(string principal, CombatMeasurementSettings request,
        string? key, string? etag, Func<CombatMeasurementPreferences, CombatMeasurementState> snapshot, CancellationToken ct = default)
    {
        ValidateControlHeaders(key, etag, "combat-measurement-settings");
        return WriteControlAsync(principal, MeasurementSettingsRoute, key, Hash(new { request, etag }), tx =>
        {
            var current = MeasurementPreferences(tx);
            if (MeasurementSettingsETag(current.Revision) != etag) throw new OperationException("revision.stale", "测量设定已变化");
            var next = current with { Settings = request, Revision = new(checked(current.Revision.Value + 1)) };
            WriteMeasurementPreferences(tx, next);
            return snapshot(next);
        }, ct);
    }

    public Task<(CombatAutoObservationState Value, bool Applied)> SaveAutoObservationAsync(string principal, CombatAutoObservationRequest request,
        string? key, string? etag, Func<CombatAutoObservationPreferences, CombatAutoObservationState> snapshot, CancellationToken ct = default)
    {
        ValidateControlHeaders(key, etag, "combat-auto-observation");
        return WriteControlAsync(principal, AutoObservationRoute, key, Hash(new { request, etag }), tx =>
        {
            var current = AutoObservationPreferences(tx);
            if (AutoObservationETag(current.Revision) != etag) throw new OperationException("revision.stale", "自动观测偏好已变化");
            var next = current with { Enabled = request.Enabled, Revision = new(checked(current.Revision.Value + 1)) };
            tx.Execute("INSERT INTO combat_auto_observation(id,enabled,revision) VALUES(1,$enabled,$r) ON CONFLICT(id) DO UPDATE SET enabled=excluded.enabled,revision=excluded.revision",
                ("$enabled", next.Enabled ? 1 : 0), ("$r", next.Revision.ToString()));
            return snapshot(next);
        }, ct);
    }

    public Task SaveMeasurementSettingsAsync(CombatMeasurementSettings value, CancellationToken ct = default) => database.WriteAsync(tx =>
    {
        var current = MeasurementPreferences(tx);
        WriteMeasurementPreferences(tx, current with { Settings = value, Revision = new(checked(current.Revision.Value + 1)) });
        return true;
    }, ct);
}

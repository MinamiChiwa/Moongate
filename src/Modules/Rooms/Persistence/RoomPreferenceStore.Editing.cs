using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.Rooms.Domain;

namespace MabiTool.Modules.Rooms.Persistence;

public sealed partial class RoomPreferenceStore
{
    public static DatabaseMigration EditingMigration { get; } = new("room-preferences", 2, """
        CREATE TABLE rooms_preference_receipts(owner TEXT NOT NULL, key TEXT NOT NULL, hash TEXT NOT NULL,
            snapshot TEXT NOT NULL, created INTEGER NOT NULL, PRIMARY KEY(owner,key)) STRICT;
        """);
    public static string ETag(UInt64String revision) => "\"room-preferences-" + revision + "\"";

    public async Task<RoomPreferences> UpdateAsync(string owner, RoomPreferences value, string? key, string? condition, CancellationToken token = default)
    {
        if (key is null) throw new OperationException("idempotency.required", "保存房间偏好需要 Idempotency-Key");
        if (key.Length is < 8 or > 128 || key.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('.' or '_' or '-')))
            throw new OperationException("request.invalid", "幂等键无效");
        if (condition is null) throw new OperationException("revision.required", "保存房间偏好需要 If-Match");
        value = JsonSerializer.Deserialize<RoomPreferences>(JsonSerializer.Serialize(value, ContractJson.Options), ContractJson.Options)!;
        Validate(value);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { value, condition }, ContractJson.Options);
        if (bytes.Length > 65536) throw new OperationException("resource.capacityExceeded", "房间偏好超过 64 KiB");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        return await database.WriteAsync(tx =>
        {
            tx.Execute("DELETE FROM rooms_preference_receipts WHERE created < $expired", ("$expired", now - 86400000L));
            if (tx.Scalar("SELECT hash FROM rooms_preference_receipts WHERE owner=$owner AND key=$key", ("$owner", owner), ("$key", key)) is string savedHash)
            {
                if (hash != savedHash) throw new OperationException("idempotency.conflict", "幂等键已用于其他偏好修改");
                return JsonSerializer.Deserialize<RoomPreferences>((string)tx.Scalar("SELECT snapshot FROM rooms_preference_receipts WHERE owner=$owner AND key=$key",
                    ("$owner", owner), ("$key", key))!, ContractJson.Options)!;
            }
            var revision = ImportRevision(tx, owner);
            if (condition != ETag(revision) || value.Revision != revision) throw new OperationException("revision.stale", "房间偏好已变化");
            if (Convert.ToInt64(tx.Scalar("SELECT COUNT(*) FROM rooms_preference_receipts WHERE owner=$owner", ("$owner", owner))) >= 1000)
                throw new OperationException("resource.capacityExceeded", "房间偏好修改回执达到上限");
            var import = PrepareImport(owner, revision, value);
            CommitImport(tx, import);
            tx.Execute("INSERT INTO rooms_preference_receipts VALUES($owner,$key,$hash,$snapshot,$created)",
                ("$owner", owner), ("$key", key), ("$hash", hash), ("$snapshot", import.Json), ("$created", now));
            return import.Value;
        }, token);
    }

    private static void Validate(RoomPreferences value)
    {
        static bool Endpoint(string text) => text == "" || Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            uri.Scheme is "ws" or "wss" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
        if (!Endpoint(value.Endpoint) || !Endpoint(value.LanEndpoint) || value.PublicProvider is not ("lan" or "cloudflare" or "community") ||
            value.Nickname.Length > 200 || value.Nickname.Any(char.IsControl) || value.CountdownMs is < 1000 or > 60000 ||
            value.TimingOffsetMs is < -2000 or > 2000 || value.MusicDurationMs is < 1 or > 86400000 || value.MusicTailHoldMs is < 0 or > 10000 ||
            value.MusicStopRule.KnownValue is null || !double.IsFinite(value.OverlayOpacity) || value.OverlayOpacity is < 0.15 or > 1 ||
            !value.RequiresGameBinding || value.LocalStopActionId != "emergency-stop" || value.Bindings.Count != 3 || value.StartInputKinds.Count != 3)
            throw new OperationException("request.invalid", "房间偏好字段无效");
        foreach (var mode in new[] { "emote", "independent", "ensemble" })
        {
            if (!value.Bindings.TryGetValue(mode, out var binding) || !value.StartInputKinds.TryGetValue(mode, out var kind) || kind is < 0 or > 2)
                throw new OperationException("request.invalid", "房间模式配置无效");
            try
            {
                if (binding.Start is not null) MabiSyncSettingsImport.ValidateBinding(binding.Start);
                if (binding.Stop is not null) MabiSyncSettingsImport.ValidateBinding(binding.Stop);
            }
            catch (OperationException) { throw new OperationException("request.invalid", "本地输入绑定字段无效"); }
            if (mode != "emote" && (binding.Start?.Kind.KnownValue == LocalBindingKind.ChatDraft || binding.Stop?.Kind.KnownValue == LocalBindingKind.ChatDraft || kind == 2))
                throw new OperationException("request.invalid", "聊天草稿只适用于表情开始");
        }
    }
}

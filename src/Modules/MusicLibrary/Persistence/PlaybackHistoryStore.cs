using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Modules.MusicLibrary.Persistence;

public sealed class PlaybackHistoryStore(SqliteStore database, TimeProvider? timeProvider = null)
{
    public const int MaximumRecordsPerOwner = 2_000;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public static DatabaseMigration Migration { get; } = new("music-library", 3, """
        CREATE TABLE music_playback_history (
            owner TEXT NOT NULL, id TEXT NOT NULL, idempotency_key TEXT NOT NULL, revision INTEGER NOT NULL,
            request_hash TEXT NOT NULL, snapshot TEXT NOT NULL, recorded_at INTEGER NOT NULL, started_at INTEGER NOT NULL,
            PRIMARY KEY(owner,id), UNIQUE(owner,idempotency_key), UNIQUE(owner,revision)
        ) STRICT;
        CREATE INDEX music_playback_history_recent ON music_playback_history(owner,started_at DESC);
        """);

    public IReadOnlyList<MusicPlaybackHistoryItem> Recent(string owner, int limit = 100)
    {
        ValidateOwner(owner);
        if (limit is < 1 or > 200) throw new OperationException("request.invalid", "播放历史页大小须在 1 到 200 之间");
        var json = database.ReadScalar("SELECT json_group_array(snapshot) FROM (SELECT snapshot FROM music_playback_history WHERE owner=$owner ORDER BY started_at DESC, revision DESC LIMIT $limit)",
            ("$owner", owner), ("$limit", limit)) as string ?? "[]";
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Select(x => JsonSerializer.Deserialize<MusicPlaybackHistoryItem>(x.GetString()!, ContractJson.Options)!)
            .ToArray();
    }

    public Task<MusicPlaybackHistoryItem> ImportAsync(string owner, MusicPlaybackHistoryInput input, string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ValidateOwner(owner);
        ValidateInput(input, clock.GetUtcNow());
        if (!Regex.IsMatch(idempotencyKey ?? "", @"\A[A-Za-z0-9._:-]{1,128}\z"))
            throw new OperationException("idempotency.required", "播放历史导入需要有效 Idempotency-Key");
        var body = JsonSerializer.SerializeToUtf8Bytes(input, ContractJson.Options);
        var hash = Convert.ToHexString(SHA256.HashData(body));
        var item = new MusicPlaybackHistoryItem(input.Id, input.ScoreId, input.PartId, input.InstrumentId,
            input.StartedAt, input.DurationMs, TimelineEvidenceKind.ImportedHistoryEstimate,
            ObservationQuality.Estimated, "client-import");
        var snapshot = JsonSerializer.Serialize(item, ContractJson.Options);
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        return database.WriteAsync(tx =>
        {
            var existing = tx.Scalar("SELECT request_hash FROM music_playback_history WHERE owner=$owner AND id=$id",
                ("$owner", owner), ("$id", input.Id)) as string;
            if (existing is not null)
            {
                if (existing != hash) throw new OperationException("idempotency.conflict", "播放记录 ID 已对应其他内容");
                var saved = tx.Scalar("SELECT snapshot FROM music_playback_history WHERE owner=$owner AND id=$id",
                    ("$owner", owner), ("$id", input.Id)) as string;
                return JsonSerializer.Deserialize<MusicPlaybackHistoryItem>(saved!, ContractJson.Options)!;
            }
            var keyedHash = tx.Scalar("SELECT request_hash FROM music_playback_history WHERE owner=$owner AND idempotency_key=$key",
                ("$owner", owner), ("$key", idempotencyKey)) as string;
            if (keyedHash is not null)
            {
                if (keyedHash != hash) throw new OperationException("idempotency.conflict", "Idempotency-Key 已用于其他播放记录");
                var saved = tx.Scalar("SELECT snapshot FROM music_playback_history WHERE owner=$owner AND idempotency_key=$key",
                    ("$owner", owner), ("$key", idempotencyKey)) as string;
                return JsonSerializer.Deserialize<MusicPlaybackHistoryItem>(saved!, ContractJson.Options)!;
            }
            var count = (long)tx.Scalar("SELECT count(*) FROM music_playback_history WHERE owner=$owner", ("$owner", owner))!;
            if (count >= MaximumRecordsPerOwner) throw new OperationException("resource.capacityExceeded", "播放历史达到每客户端 2,000 条上限");
            var revision = checked((long)(tx.Scalar("SELECT max(coalesce((SELECT max(revision) FROM music_playback_history WHERE owner=$owner),0),coalesce((SELECT revision FROM music_playback_history_state WHERE owner=$owner),0))+1",
                ("$owner", owner))!));
            tx.Execute("INSERT INTO music_playback_history(owner,id,idempotency_key,revision,request_hash,snapshot,recorded_at,started_at) VALUES($owner,$id,$key,$revision,$hash,$snapshot,$now,$started)",
                ("$owner", owner), ("$id", input.Id), ("$key", idempotencyKey), ("$revision", revision), ("$hash", hash),
                ("$snapshot", snapshot), ("$now", now), ("$started", input.StartedAt.ToUnixTimeMilliseconds()));
            var changed = new MusicPlaybackHistoryChanged(owner, new((ulong)revision), item);
            SqliteStore.AppendEvent(tx, new("music-history-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner)))[..16] + "-" + revision,
                "music-history", owner, new((ulong)revision), "music.playback-history.changed",
                JsonSerializer.SerializeToElement(changed, ContractJson.Options)));
            return item;
        }, cancellationToken);
    }

    private static void ValidateInput(MusicPlaybackHistoryInput input, DateTimeOffset now)
    {
        if (input is null || !Regex.IsMatch(input.Id ?? "", @"\A[A-Za-z0-9._-]{1,128}\z") ||
            !Regex.IsMatch(input.ScoreId ?? "", @"\A[A-Za-z0-9._:-]{1,128}\z") ||
            input.PartId is { Length: > 128 } || input.InstrumentId is { Length: > 64 } ||
            input.StartedAt.Offset != TimeSpan.Zero || input.StartedAt < DateTimeOffset.UnixEpoch ||
            input.StartedAt > now.AddDays(1) ||
            input.DurationMs is { } duration && (!double.IsFinite(duration) || duration is <= 0 or > 86_400_000))
            throw new OperationException("request.invalid", "播放记录格式或时长无效");
    }

    private static void ValidateOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128)
            throw new OperationException("request.invalid", "播放历史归属无效");
    }
}

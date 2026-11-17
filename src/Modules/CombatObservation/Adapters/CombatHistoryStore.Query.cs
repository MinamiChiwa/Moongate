using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.CombatObservation.Domain;
using Microsoft.Data.Sqlite;

namespace MabiTool.Modules.CombatObservation.Adapters;

public sealed partial class CombatHistoryStore
{
    private sealed record CursorData(string Purpose, string Epoch, string Principal, bool Owner, string Filter, UInt64String Revision, string Position);
    private static string FilterHash<T>(T query) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(query, ContractJson.Options)));
    private string Cursor(string purpose, string principal, bool owner, string filter, UInt64String revision, string position)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new CursorData(purpose, epoch, principal, owner, filter, revision, position), ContractJson.Options);
        return Convert.ToBase64String(json) + "." + Convert.ToHexString(HMACSHA256.HashData(cursorKey, json));
    }
    private string? Position(string? cursor, string purpose, string principal, bool owner, string filter, UInt64String revision)
    {
        if (cursor is null) return null;
        try
        {
            if (cursor.Length > 2048) throw new FormatException();
            var parts = cursor.Split('.'); if (parts.Length != 2) throw new FormatException();
            var json = Convert.FromBase64String(parts[0]); var signature = Convert.FromHexString(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(cursorKey, json))) throw new FormatException();
            var value = JsonSerializer.Deserialize<CursorData>(json, ContractJson.Options)!;
            if (value.Purpose != purpose || value.Epoch != epoch || value.Principal != principal || value.Owner != owner || value.Filter != filter || value.Revision != revision)
                throw new FormatException();
            return value.Position;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        { throw new OperationException("combat.planStale", "分页游标已失效，请重新取得第一页"); }
    }
    public CombatEncounterPage Query(string principal, bool owner, CombatEncounterQuery query)
    {
        if (query is null || query.PageSize is < 1 or > 100 || query.GameSessionId?.Length > 128)
            throw new OperationException("request.invalid", "历史每页为 1–100 场");
        var filter = FilterHash(query with { Cursor = null });
        var key = (principal, owner, filter, query.Cursor);
        return database.Read(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT revision,(SELECT count(*)||':'||coalesce(max(rowid),0)||':'||coalesce(sum(favorite),0) FROM combat_encounters WHERE closed=1) FROM combat_history_state WHERE id=1";
            UInt64String revision; string stamp;
            using (var head = command.ExecuteReader()) { head.Read(); revision = UInt64String.Parse(head.GetString(0)); stamp = head.GetString(1); }
            // 历史页只随历史修订变化（关闭、收藏、清理、保存范围都推进修订）；数据整体删除或恢复时条数与行号也随之变化。
            lock (pageCache)
                if (pageCache.TryGetValue(key, out var cached) && cached.Revision == revision && cached.Stamp == stamp) return cached.Page;
            var position = Position(query.Cursor, "encounters", principal, owner, filter, revision); long? afterTime = null; string? afterId = null;
            if (position is not null)
            {
                var fields = position.Split('|');
                if (fields.Length != 2 || !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var at))
                    throw new OperationException("combat.planStale", "历史游标位置无效");
                afterTime = at; afterId = fields[1];
            }
            command.CommandText = "SELECT document FROM combat_encounters WHERE closed=1 AND ($owner=1 OR owner=$p) AND ($favorites=0 OR favorite=1) AND ($game IS NULL OR json_extract(document,'$.gameSessionId')=$game) AND ($after IS NULL OR started_at<$after OR started_at=$after AND id<$last) ORDER BY started_at DESC,id DESC LIMIT $limit";
            command.Parameters.AddWithValue("$owner", owner ? 1 : 0); command.Parameters.AddWithValue("$p", principal);
            command.Parameters.AddWithValue("$favorites", query.FavoritesOnly ? 1 : 0); command.Parameters.AddWithValue("$game", (object?)query.GameSessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$after", (object?)afterTime ?? DBNull.Value); command.Parameters.AddWithValue("$last", (object?)afterId ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", query.PageSize + 1);
            using var rows = command.ExecuteReader(); var values = new List<CombatEncounter>();
            while (rows.Read()) values.Add(JsonSerializer.Deserialize<CombatEncounter>(rows.GetString(0), ContractJson.Options)!);
            var page = values.Take(query.PageSize).ToArray();
            var next = values.Count > query.PageSize ? Cursor("encounters", principal, owner, filter, revision,
                page[^1].StartedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "|" + page[^1].Id) : null;
            var result = new CombatEncounterPage(page, next, revision);
            lock (pageCache)
            {
                if (pageCache.Count >= MaximumPageCacheEntries) pageCache.Clear();
                pageCache[key] = (revision, stamp, result);
            }
            return result;
        });
    }
    private const int MaximumPageCacheEntries = 16;
    private readonly Dictionary<(string Principal, bool Owner, string Filter, string? Cursor), (UInt64String Revision, string Stamp, CombatEncounterPage Page)> pageCache = [];

    /// <summary>
    /// 时间线只追加：按记录缓存已读取的条目，之后只读取序号更大的新页。上限 2 条记录、合计 16 MiB 存储字节，最久未用先淘汰。
    /// </summary>
    private sealed record TimelineEntry(CombatTimelineItem[] Items, ulong LastIndex, int Bytes, long Used);
    private const int MaximumTimelineCacheEntries = 2;
    private const int MaximumTimelineCacheBytes = 16 * 1024 * 1024;
    private readonly Dictionary<string, TimelineEntry> timelineCache = new(StringComparer.Ordinal);
    private long timelineUse;
    private CombatTimelineItem[] TimelineItems(SqliteCommand command, string id)
    {
        TimelineEntry? cached;
        lock (timelineCache) timelineCache.TryGetValue(id, out cached);
        command.CommandText = "SELECT document,bytes,last_index FROM combat_timeline_pages WHERE encounter_id=$id AND CAST(last_index AS INTEGER)>$last ORDER BY length(first_index),first_index";
        command.Parameters.AddWithValue("$last", (long)(cached?.LastIndex ?? 0));
        var bytes = cached?.Bytes ?? 0; var lastIndex = cached?.LastIndex ?? 0; var added = new List<CombatTimelineItem>();
        using (var rows = command.ExecuteReader())
            while (rows.Read())
            {
                bytes += rows.GetInt32(1);
                if (bytes > 17 * 1024 * 1024) throw new OperationException("resource.capacityExceeded", "时间线读取超过预算");
                added.AddRange(JsonSerializer.Deserialize<CombatTimelineItem[]>(rows.GetString(0), ContractJson.Options)!);
                if ((cached?.Items.Length ?? 0) + added.Count > 10000) throw new OperationException("resource.capacityExceeded", "时间线条目超过预算");
                lastIndex = Math.Max(lastIndex, UInt64String.Parse(rows.GetString(2)).Value);
            }
        var items = cached is null ? added.ToArray() : added.Count == 0 ? cached.Items : [.. cached.Items, .. added];
        lock (timelineCache)
        {
            timelineCache[id] = new(items, lastIndex, bytes, ++timelineUse);
            while (timelineCache.Count > MaximumTimelineCacheEntries || timelineCache.Count > 1 && timelineCache.Values.Sum(x => (long)x.Bytes) > MaximumTimelineCacheBytes)
                timelineCache.Remove(timelineCache.MinBy(x => x.Value.Used).Key);
            if (bytes > MaximumTimelineCacheBytes) timelineCache.Remove(id);
        }
        return items;
    }
    private static readonly UInt64String TimelineCursorRevision = new(0);
    public CombatTimelinePage Timeline(string principal, bool owner, string id, CombatTimelineQuery query)
    {
        if (query is null || query.PageSize is < 1 or > 1000 || query.FromMs is { } fromValue && (!double.IsFinite(fromValue) || fromValue < 0) ||
            query.ToMs is { } toValue && (!double.IsFinite(toValue) || toValue < (query.FromMs ?? 0)) ||
            query.BucketWidthMs is { } width && width is < 10 or > 60000 || query.EntityId?.Length > 128)
            throw new OperationException("request.invalid", "时间线页大小、窗口或桶宽无效");
        var filter = FilterHash(new { id, query = query with { Cursor = null } });
        return database.Read(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT document FROM combat_encounters WHERE id=$id AND ($owner=1 OR owner=$p)";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$owner", owner ? 1 : 0); command.Parameters.AddWithValue("$p", principal);
            var value = command.ExecuteScalar() is string document ? JsonSerializer.Deserialize<CombatEncounter>(document, ContractJson.Options)!
                : throw new OperationException("resource.notFound", "战斗记录不存在");
            // 时间线只追加，游标位置是条目序号：续页不绑定记录修订，进行中的分段写入新检查点后游标照常有效。
            var position = Position(query.Cursor, "timeline", principal, owner, filter, TimelineCursorRevision);
            var after = position is null ? 0UL : UInt64String.Parse(position).Value;
            var timeline = TimelineItems(command, id);
            var to = Math.Min(query.ToMs ?? value.Summary.DurationMs, value.Summary.DurationMs);
            var from = Math.Min(query.FromMs ?? 0, to);
            if (query.EntityId is { } entityId && entityId != CombatSession.EntityId(value.Summary.LocalEntity) &&
                !value.Targets.Any(x => x.Key == entityId) && !timeline.Any(x => x.Entity?.Id == entityId || x.Effect is { } effect && CombatSession.EntityId(effect.EntityKey) == entityId))
                throw new OperationException("combat.entityOutOfScope", "实体不属于该战斗");
            bool Matches(CombatTimelineItem item) => item.OffsetMs >= from && item.OffsetMs <= to &&
                (query.EntityId is null || item.Gap is not null || item.Entity?.Id == query.EntityId ||
                    item.Damage is { } damage && (CombatSession.EntityId(damage.SourceEntity) == query.EntityId || CombatSession.EntityId(damage.TargetEntity) == query.EntityId) ||
                    item.Effect is { } effect && CombatSession.EntityId(effect.EntityKey) == query.EntityId);
            var selected = timeline.Where(x => x.Index.Value > after && Matches(x)).OrderBy(x => x.Index.Value).Take(query.PageSize + 1).ToArray();
            var page = selected.Take(query.PageSize).ToArray();
            var next = selected.Length > query.PageSize ? Cursor("timeline", principal, owner, filter, TimelineCursorRevision, page[^1].Index.ToString()) : null;
            var buckets = query.BucketWidthMs is { } bucketWidth ? CombatTimelineProjection.DamageBuckets(value, timeline, from, to, bucketWidth, query.EntityId) : [];
            var coverage = CombatTimelineProjection.Coverage(value, timeline, from, to, query.EntityId);
            return new CombatTimelinePage(id, value.Revision, page, next, buckets, coverage, value.Gaps, value.Resources.TruncatedTimelineItems.Value > 0);
        });
    }
}

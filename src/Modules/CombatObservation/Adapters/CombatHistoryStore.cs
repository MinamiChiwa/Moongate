using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.CombatObservation.Domain;

namespace MabiTool.Modules.CombatObservation.Adapters;

public sealed record CombatRetentionLimits(int MaximumEncounters = 100, long SoftBytes = 200 * 1024 * 1024, int MaximumObservations = 1000);

public sealed partial class CombatHistoryStore(SqliteStore database, OperationStore operations, string epoch,
    TimeProvider? timeProvider = null, CombatRetentionLimits? retention = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly CombatRetentionLimits limits = CheckLimits(retention ?? new());
    private readonly byte[] cursorKey = RandomNumberGenerator.GetBytes(32);
    private sealed record TimelinePage(string Encounter, string First, string Last, string Json, int Bytes);
    public static DatabaseMigration Migration { get; } = new("combat-observation", 1, """
        CREATE TABLE combat_observations (
            id TEXT PRIMARY KEY, owner TEXT NOT NULL, game_session TEXT NOT NULL, revision TEXT NOT NULL,
            workspace TEXT NOT NULL, terminal INTEGER NOT NULL CHECK(terminal IN (0,1)), updated_at INTEGER NOT NULL
        ) STRICT;
        CREATE INDEX combat_observations_owner ON combat_observations(owner,updated_at);
        CREATE TABLE combat_encounters (
            id TEXT PRIMARY KEY, owner TEXT NOT NULL, observation_id TEXT NOT NULL REFERENCES combat_observations(id),
            revision TEXT NOT NULL, closed INTEGER NOT NULL CHECK(closed IN (0,1)), quality TEXT NOT NULL,
            favorite INTEGER NOT NULL CHECK(favorite IN (0,1)), started_at INTEGER NOT NULL,
            document TEXT NOT NULL, bytes INTEGER NOT NULL, timeline_bytes INTEGER NOT NULL DEFAULT 0
        ) STRICT;
        CREATE INDEX combat_encounters_history ON combat_encounters(closed,owner,started_at,id);
        CREATE TABLE combat_timeline_pages (
            encounter_id TEXT NOT NULL REFERENCES combat_encounters(id) ON DELETE CASCADE,
            first_index TEXT NOT NULL, last_index TEXT NOT NULL, document TEXT NOT NULL, bytes INTEGER NOT NULL,
            PRIMARY KEY(encounter_id,first_index)
        ) STRICT;
        CREATE TABLE combat_entities (
            id TEXT NOT NULL, observation_id TEXT NOT NULL REFERENCES combat_observations(id) ON DELETE CASCADE,
            owner TEXT NOT NULL, document TEXT NOT NULL, PRIMARY KEY(id,observation_id)
        ) STRICT;
        CREATE TABLE combat_history_state (id INTEGER PRIMARY KEY CHECK(id=1), revision TEXT NOT NULL) STRICT;
        INSERT INTO combat_history_state VALUES(1,'0');
        CREATE TABLE combat_receipts (
            principal TEXT NOT NULL, route TEXT NOT NULL, resource TEXT NOT NULL, key TEXT NOT NULL,
            hash TEXT NOT NULL, result TEXT NOT NULL, expires_at INTEGER NOT NULL,
            PRIMARY KEY(principal,route,resource,key)
        ) STRICT;
        """);
    // 冗余保存工作区当前战斗 ID，清理与容量统计不再对最大 4 MiB 的工作区执行 json_extract；旧数据在迁移时回填。
    public static DatabaseMigration EncounterReferenceMigration { get; } = new("combat-observation", 2, """
        ALTER TABLE combat_observations ADD COLUMN encounter_id TEXT;
        UPDATE combat_observations SET encounter_id=json_extract(workspace,'$.encounter.id');
        CREATE INDEX combat_observations_encounter ON combat_observations(encounter_id,terminal);
        """);
    // 定时测量上次使用的设定与常驻自动观测开关（后端偏好，各一行）。
    public static DatabaseMigration PreferencesMigration { get; } = new("combat-observation", 3, """
        CREATE TABLE combat_preferences (id INTEGER PRIMARY KEY CHECK(id=1), settings TEXT NOT NULL) STRICT;
        CREATE TABLE combat_auto_observation (id INTEGER PRIMARY KEY CHECK(id=1), enabled INTEGER NOT NULL CHECK(enabled IN (0,1))) STRICT;
        """);
    public static DatabaseMigration ControlRevisionsMigration { get; } = new("combat-observation", 4, """
        ALTER TABLE combat_preferences ADD COLUMN revision TEXT NOT NULL DEFAULT '0';
        ALTER TABLE combat_auto_observation ADD COLUMN revision TEXT NOT NULL DEFAULT '0';
        """);
    public static IReadOnlyList<DatabaseMigration> Migrations { get; } = Array.AsReadOnly([Migration, EncounterReferenceMigration, PreferencesMigration, ControlRevisionsMigration]);
    private const string ActiveWorkspaceReference = "EXISTS (SELECT 1 FROM combat_observations o WHERE o.encounter_id=combat_encounters.id AND o.terminal=0)";
    private static CombatRetentionLimits CheckLimits(CombatRetentionLimits value)
    {
        if (value.MaximumEncounters is < 1 or > 100 || value.SoftBytes is < 1024 or > 200 * 1024 * 1024 ||
            value.MaximumObservations is < 1 or > 1000) throw new ArgumentException("战斗历史限制超出登记上限");
        return value;
    }
    public static string ETag(CombatEncounter value) => $"\"combat-boundary-{value.Id}-{value.BoundaryRevision ?? value.Revision}\"";
    public static string WorkspaceETag(CombatWorkspace value) => $"\"combat-boundary-{value.Observation.Id}-{value.BoundaryRevision ?? value.Revision}\"";
    /// <summary>
    /// 活动观察的最新捕获：宿主每次捕获后登记，读取优先使用它（修订不低于已保存的检查点时）。只有时间推进的捕获不落库，
    /// 读取方仍得到当前的观测时刻、单调时点与效果剩余时间；观察结束时移除，此后读取已保存的终态。
    /// </summary>
    private sealed record LiveObservation(string Owner, CombatWorkspace Workspace, Func<string, CombatEntityEffects?> Effects);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, LiveObservation> live = new(StringComparer.Ordinal);
    public void PublishLive(CombatWorkspace workspace, Func<string, CombatEntityEffects?> effects)
    {
        var id = workspace.Observation.Id;
        live.AddOrUpdate(id, new LiveObservation(workspace.Observation.Owner, workspace, effects),
            (_, previous) => previous.Workspace.Revision.Value > workspace.Revision.Value ? previous : new(workspace.Observation.Owner, workspace, effects));
    }
    public void EndLive(string observationId) => live.TryRemove(observationId, out _);

    public CombatWorkspace? Workspace(string principal, bool owner, string? id = null)
    {
        var (selected, revision) = database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,revision FROM combat_observations WHERE ($owner=1 OR owner=$p) AND ($id IS NULL OR id=$id) ORDER BY updated_at DESC,id DESC LIMIT 1";
            command.Parameters.AddWithValue("$owner", owner ? 1 : 0); command.Parameters.AddWithValue("$p", principal); command.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
            using var row = command.ExecuteReader();
            return row.Read() ? (row.GetString(0), UInt64String.Parse(row.GetString(1))) : ((string?)null, default(UInt64String));
        });
        if (selected is null) return null;
        if (live.TryGetValue(selected, out var current) && current.Workspace.Revision.Value >= revision.Value)
        {
            // 已结束的当前分段以历史中保存的记录为准（收藏等修改只写入历史记录）。
            if (current.Workspace.Encounter is { Summary.Final: true } final &&
                database.ReadScalar("SELECT document FROM combat_encounters WHERE id=$id AND closed=1", ("$id", final.Id)) is string saved)
                return current.Workspace with { Encounter = JsonSerializer.Deserialize<CombatEncounter>(saved, ContractJson.Options) };
            return current.Workspace;
        }
        return PersistedWorkspace(principal, owner, selected);
    }
    private CombatWorkspace? PersistedWorkspace(string principal, bool owner, string? id) => database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT workspace FROM combat_observations WHERE ($owner=1 OR owner=$p) AND ($id IS NULL OR id=$id) ORDER BY updated_at DESC,id DESC LIMIT 1";
        command.Parameters.AddWithValue("$owner", owner ? 1 : 0); command.Parameters.AddWithValue("$p", principal); command.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<CombatWorkspace>(json, ContractJson.Options) : null;
    });
    public CombatEncounter Encounter(string id, string principal, bool owner) => database.Read(connection =>
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT document FROM combat_encounters WHERE id=$id AND ($owner=1 OR owner=$p)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$owner", owner ? 1 : 0); command.Parameters.AddWithValue("$p", principal);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<CombatEncounter>(json, ContractJson.Options)!
            : throw new OperationException("resource.notFound", "战斗记录不存在");
    });
    public CombatEntityEffects EntityEffects(string id, string principal, bool owner)
    {
        foreach (var current in live.Values)
            if ((owner || current.Owner == principal) && current.Effects(id) is { } value) return value;
        return PersistedEntityEffects(id, principal, owner);
    }
    private CombatEntityEffects PersistedEntityEffects(string id, string principal, bool owner) => database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT e.document FROM combat_entities e JOIN combat_observations o ON o.id=e.observation_id WHERE e.id=$id AND ($owner=1 OR e.owner=$p) ORDER BY o.updated_at DESC LIMIT 1";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$owner", owner ? 1 : 0); command.Parameters.AddWithValue("$p", principal);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<CombatEntityEffects>(json, ContractJson.Options)!
            : throw new OperationException("combat.entityOutOfScope", "实体不在当前观测范围");
    });
    public UInt64String HistoryRevision() => UInt64String.Parse((string)database.ReadScalar("SELECT revision FROM combat_history_state WHERE id=1")!);
    private static UInt64String AdvanceHistory(DatabaseTransaction tx)
    {
        var next = new UInt64String(checked(UInt64String.Parse((string)tx.Scalar("SELECT revision FROM combat_history_state WHERE id=1")!).Value + 1));
        tx.Execute("UPDATE combat_history_state SET revision=$r WHERE id=1", ("$r", next.ToString())); return next;
    }
    public async Task<bool> SaveAsync(CombatCheckpoint checkpoint, OperationState? terminal = null, ApiProblem? problem = null, CancellationToken ct = default,
        CombatBoundaryReceipt? boundary = null)
    {
        var workspaceJson = JsonSerializer.Serialize(checkpoint.Workspace, ContractJson.Options);
        if (Encoding.UTF8.GetByteCount(workspaceJson) > 4 * 1024 * 1024) throw new OperationException("resource.capacityExceeded", "战斗快照超过 4 MiB");
        var documents = checkpoint.Encounters.Select(value => (Value: value, Json: JsonSerializer.Serialize(value, ContractJson.Options))).ToArray();
        var entityDocuments = checkpoint.Effects.Select(value => (Value: value, Json: JsonSerializer.Serialize(value, ContractJson.Options))).ToArray();
        var pages = EncodePages(checkpoint.Timeline);
        var historyClosed = false;
        var committed = await operations.CommitCombatAsync(checkpoint.Workspace.Observation.Id, checkpoint.Workspace.Observation, tx =>
        {
            historyClosed = false;
            var observation = checkpoint.Workspace.Observation;
            if (boundary is not null) SaveBoundary(tx, boundary);
            var newObservation = tx.Scalar("SELECT id FROM combat_observations WHERE id=$id", ("$id", observation.Id)) is null;
            if (newObservation)
            {
                tx.Execute("DELETE FROM combat_entities WHERE observation_id IN (SELECT id FROM combat_observations WHERE terminal=1)");
                if ((long)tx.Scalar("SELECT count(*) FROM combat_observations")! >= limits.MaximumObservations)
                    throw new OperationException("resource.capacityExceeded", "观察记录达到上限；请先清理未收藏历史");
            }
            var existingFinal = checkpoint.Workspace.Encounter is { Summary.Final: true } current
                ? tx.Scalar("SELECT document FROM combat_encounters WHERE id=$id AND closed=1", ("$id", current.Id)) as string : null;
            var persistedWorkspace = existingFinal is null ? workspaceJson : JsonSerializer.Serialize(checkpoint.Workspace with
                { Encounter = JsonSerializer.Deserialize<CombatEncounter>(existingFinal, ContractJson.Options) }, ContractJson.Options);
            tx.Execute("INSERT INTO combat_observations(id,owner,game_session,revision,workspace,terminal,updated_at,encounter_id) VALUES($id,$owner,$game,$revision,$workspace,$terminal,$at,$encounter) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,workspace=excluded.workspace,terminal=excluded.terminal,updated_at=excluded.updated_at,encounter_id=excluded.encounter_id",
                ("$id", observation.Id), ("$owner", observation.Owner), ("$game", observation.GameSessionId), ("$revision", checkpoint.Workspace.Revision.ToString()),
                ("$workspace", persistedWorkspace), ("$terminal", terminal is null ? 0 : 1), ("$at", clock.GetUtcNow().ToUnixTimeMilliseconds()),
                ("$encounter", checkpoint.Workspace.Encounter?.Id));
            var historyChanged = false;
            // 取消的定时测量不保存：删除其未结束的记录，时间线页随外键级联删除。
            foreach (var deleted in checkpoint.DeletedEncounters)
                tx.Execute("DELETE FROM combat_encounters WHERE id=$id AND closed=0 AND owner=$owner", ("$id", deleted), ("$owner", observation.Owner));
            foreach (var (value, json) in documents)
            {
                var previousClosed = tx.Scalar("SELECT closed FROM combat_encounters WHERE id=$id", ("$id", value.Id));
                if (previousClosed is long isClosed && isClosed == 1) continue;
                tx.Execute("INSERT INTO combat_encounters(id,owner,observation_id,revision,closed,quality,favorite,started_at,document,bytes) VALUES($id,$owner,$observation,$r,$closed,$quality,0,$at,$json,$bytes) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,closed=excluded.closed,quality=excluded.quality,document=excluded.document,bytes=excluded.bytes",
                    ("$id", value.Id), ("$owner", value.Owner), ("$observation", value.ObservationId), ("$r", value.Revision.ToString()),
                    ("$closed", value.Summary.Final ? 1 : 0), ("$quality", value.Summary.Quality.ToString()), ("$at", value.StartedAt.ToUnixTimeMilliseconds()), ("$json", json), ("$bytes", Encoding.UTF8.GetByteCount(json)));
                if (value.Summary.Final)
                {
                    historyChanged = true;
                    Append(tx, "combat-closed", value.Id, value.Revision, "combat.encounter.closed",
                        new CombatEncounterClosed(value.Owner, value.ObservationId, value.Id, value.Revision, value.Summary, value.Summary.BoundaryReason));
                }
            }
            foreach (var page in pages)
            {
                tx.Execute("INSERT INTO combat_timeline_pages VALUES($id,$first,$last,$json,$bytes)",
                    ("$id", page.Encounter), ("$first", page.First), ("$last", page.Last), ("$json", page.Json), ("$bytes", page.Bytes));
                tx.Execute("UPDATE combat_encounters SET timeline_bytes=timeline_bytes+$bytes WHERE id=$id", ("$bytes", page.Bytes), ("$id", page.Encounter));
            }
            foreach (var (value, json) in entityDocuments)
                tx.Execute("INSERT INTO combat_entities VALUES($id,$observation,$owner,$json) ON CONFLICT(id,observation_id) DO UPDATE SET document=excluded.document",
                    ("$id", value.Id), ("$observation", value.ObservationId), ("$owner", value.Owner), ("$json", json));
            if (existingFinal is null && checkpoint.Workspace.Encounter is { } currentEncounter)
                Append(tx, "combat-summary", observation.Id, checkpoint.Workspace.Revision, "combat.summary.changed", currentEncounter.Summary);
            if (checkpoint.Effects.Count > 0)
            {
                var primary = checkpoint.Workspace.SelfEffects ?? checkpoint.Effects[0];
                var notification = primary.Snapshot with { Complete = false, Effects = [], SnapshotRequired = true,
                    ChangedEntityIds = checkpoint.Effects.Select(x => x.Id).Take(256).ToArray() };
                Append(tx, "combat-effects", observation.Id, checkpoint.Workspace.Revision, "combat.effects.changed", notification);
            }
            // 切图后旧代次实体不再出现在当前工作区：只保留当前实体的效果文档，避免长时间观察中逐图累积。
            if (entityDocuments.Length > 0 && (long)tx.Scalar("SELECT count(*) FROM combat_entities WHERE observation_id=$id", ("$id", observation.Id))! > checkpoint.Workspace.Entities.Count)
                tx.Execute("DELETE FROM combat_entities WHERE observation_id=$id AND id NOT IN (SELECT value FROM json_each($ids))",
                    ("$id", observation.Id), ("$ids", JsonSerializer.Serialize(checkpoint.Workspace.Entities.Select(value => value.Id))));
            if (checkpoint.Gaps.Count > 0 && checkpoint.GapsChanged)
                Append(tx, "combat-quality", observation.Id, checkpoint.Workspace.Revision, "combat.observation.degraded",
                    new CombatObservationDegraded(observation.Owner, observation.Id, observation.GameSessionId, observation.LocalEntity.ObservationGeneration,
                        checkpoint.Workspace.Revision, checkpoint.Gaps.Take(64).ToArray(), observation.DroppedEvents, SnapshotRequired: true));
            if (historyChanged) { AdvanceHistory(tx); historyClosed = true; }
        }, terminal, problem, ct);
        if (committed && terminal is not null) EndLive(checkpoint.Workspace.Observation.Id);
        // 预算清理放在检查点事务之后单独提交：检查点写事务只含本次观测数据，不随历史条数增长；清理失败时留到下一次关闭或手动清理。
        if (committed && historyClosed)
            try { await database.WriteAsync(tx => Clean(tx), ct); }
            catch (OperationException) { }
        return committed;
    }
    private static List<TimelinePage> EncodePages(IReadOnlyList<CombatTimelineRecord> timeline)
    {
        var pages = new List<TimelinePage>();
        foreach (var group in timeline.GroupBy(x => x.EncounterId))
        {
            var entries = new List<(CombatTimelineItem Item, string Json, int Bytes)>(); var bytes = 2;
            foreach (var record in group.OrderBy(x => x.Item.Index.Value))
            {
                var json = JsonSerializer.Serialize(record.Item, ContractJson.Options); var length = Encoding.UTF8.GetByteCount(json);
                if (length > 64 * 1024 - 2) throw new OperationException("resource.capacityExceeded", "单项战斗时间线超过 64 KiB");
                if (entries.Count >= 64 || bytes + length + 1 > 64 * 1024) Flush();
                entries.Add((record.Item, json, length)); bytes += length + (entries.Count > 1 ? 1 : 0);
            }
            Flush();
            void Flush()
            {
                if (entries.Count == 0) return;
                var json = "[" + string.Join(',', entries.Select(x => x.Json)) + "]";
                pages.Add(new(group.Key, entries[0].Item.Index.ToString(), entries[^1].Item.Index.ToString(), json, Encoding.UTF8.GetByteCount(json)));
                entries.Clear(); bytes = 2;
            }
        }
        return pages;
    }
    private static void Append<T>(DatabaseTransaction tx, string resourceType, string id, UInt64String revision, string type, T value) =>
        SqliteStore.AppendEvent(tx, new(resourceType + "-" + id + "-" + revision, resourceType, id, revision, type,
            JsonSerializer.SerializeToElement(value, ContractJson.Options)));

    public CombatStorageSummary Storage(string principal, bool owner) => database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*),coalesce(sum(bytes+timeline_bytes),0),coalesce(sum(favorite),0),coalesce(sum(CASE WHEN favorite=1 OR closed=0 OR " + ActiveWorkspaceReference + " THEN bytes+timeline_bytes ELSE 0 END),0) FROM combat_encounters WHERE ($owner=1 OR owner=$p)";
        command.Parameters.AddWithValue("$owner", owner ? 1 : 0); command.Parameters.AddWithValue("$p", principal);
        using var row = command.ExecuteReader(); row.Read();
        return new CombatStorageSummary(row.GetInt32(0), new((ulong)row.GetInt64(1)), row.GetInt32(2), new((ulong)row.GetInt64(3)),
            row.GetInt64(0) > limits.MaximumEncounters || row.GetInt64(1) > limits.SoftBytes, 0, new(0));
    });
    private CombatStorageSummary Clean(DatabaseTransaction tx, string? reportedPrincipal = null)
    {
        var count = (long)tx.Scalar("SELECT count(*) FROM combat_encounters")!;
        var bytes = (long)tx.Scalar("SELECT coalesce(sum(bytes+timeline_bytes),0) FROM combat_encounters")!;
        var removed = 0; long removedBytes = 0; var historyRemoved = false;
        while (count > limits.MaximumEncounters || bytes > limits.SoftBytes)
        {
            var json = tx.Scalar("SELECT document FROM combat_encounters WHERE closed=1 AND favorite=0 AND NOT " + ActiveWorkspaceReference + " ORDER BY CASE quality WHEN 'confirmed' THEN 0 ELSE 1 END,started_at,rowid LIMIT 1") as string;
            if (json is null) break;
            var value = JsonSerializer.Deserialize<CombatEncounter>(json, ContractJson.Options)!;
            var size = (long)tx.Scalar("SELECT bytes+timeline_bytes FROM combat_encounters WHERE id=$id", ("$id", value.Id))!;
            var revision = new UInt64String(checked(value.Revision.Value + 1));
            Append(tx, "combat-closed", value.Id, revision, "combat.encounter.closed", new CombatEncounterClosed(value.Owner, value.ObservationId, value.Id,
                revision, value.Summary with { Revision = revision }, value.Summary.BoundaryReason, Deleted: true));
            tx.Execute("DELETE FROM combat_encounters WHERE id=$id", ("$id", value.Id)); count--; bytes -= size; historyRemoved = true;
            if (reportedPrincipal is null || value.Owner == reportedPrincipal) { removed++; removedBytes += size; }
            var workspaceJson = tx.Scalar("SELECT workspace FROM combat_observations WHERE id=$id AND terminal=1 AND encounter_id=$encounter",
                ("$id", value.ObservationId), ("$encounter", value.Id)) as string;
            if (workspaceJson is not null)
            {
                var workspace = JsonSerializer.Deserialize<CombatWorkspace>(workspaceJson, ContractJson.Options)!;
                var nextRevision = new UInt64String(checked(workspace.Revision.Value + 1));
                var next = workspace with { Encounter = null, Revision = nextRevision, BoundaryRevision = new(checked((workspace.BoundaryRevision ?? workspace.Revision).Value + 1)),
                    Observation = workspace.Observation with { Revision = nextRevision } };
                tx.Execute("UPDATE combat_observations SET revision=$r,workspace=$json,encounter_id=NULL WHERE id=$id",
                    ("$r", nextRevision.ToString()), ("$json", JsonSerializer.Serialize(next, ContractJson.Options)), ("$id", value.ObservationId));
            }
        }
        if (historyRemoved) AdvanceHistory(tx);
        tx.Execute("DELETE FROM combat_observations WHERE terminal=1 AND id NOT IN (SELECT observation_id FROM combat_encounters) AND id NOT IN (SELECT id FROM combat_observations ORDER BY updated_at DESC LIMIT 1)");
        if (reportedPrincipal is not null)
        {
            count = (long)tx.Scalar("SELECT count(*) FROM combat_encounters WHERE owner=$p", ("$p", reportedPrincipal))!;
            bytes = (long)tx.Scalar("SELECT coalesce(sum(bytes+timeline_bytes),0) FROM combat_encounters WHERE owner=$p", ("$p", reportedPrincipal))!;
        }
        var favorites = (long)tx.Scalar("SELECT coalesce(sum(favorite),0) FROM combat_encounters WHERE ($p IS NULL OR owner=$p)", ("$p", reportedPrincipal))!;
        var protectedBytes = (long)tx.Scalar("SELECT coalesce(sum(bytes+timeline_bytes),0) FROM combat_encounters WHERE ($p IS NULL OR owner=$p) AND (favorite=1 OR closed=0 OR " + ActiveWorkspaceReference + ")", ("$p", reportedPrincipal))!;
        return new((int)count, new((ulong)bytes), (int)favorites, new((ulong)protectedBytes), count > limits.MaximumEncounters || bytes > limits.SoftBytes,
            removed, new((ulong)removedBytes));
    }
    public Task<CombatStorageSummary> CleanAsync(string principal, string? key, CancellationToken ct = default, bool owner = false) => database.WriteAsync(tx =>
    {
        var hash = owner ? "cleanup:owner" : "cleanup:scoped";
        var receipt = Receipt<CombatStorageSummary>(tx, principal, "cleanup", "history", key, hash);
        if (receipt is not null) return receipt;
        var result = Clean(tx, owner ? null : principal); SaveReceipt(tx, principal, "cleanup", "history", key!, hash, result); return result;
    }, ct);
    public async Task<CombatEncounter> FavoriteAsync(string principal, bool owner, string id, CombatFavoriteRequest request, string? key, string? etag, CancellationToken ct = default)
    {
        if (etag is null) throw new OperationException("revision.required", "收藏操作需要 If-Match");
        var hash = JsonSerializer.Serialize(new { request.Favorite, etag }, ContractJson.Options);
        var retry = await database.WriteAsync(tx => Receipt<CombatEncounter>(tx, principal, "favorite", id, key, hash), ct);
        if (retry is not null) return retry;
        var current = Encounter(id, principal, owner);
        var revision = new UInt64String(checked(current.Revision.Value + 1));
        var next = current with { Favorite = request.Favorite, Revision = revision, BoundaryRevision = new(checked((current.BoundaryRevision ?? current.Revision).Value + 1)),
            Summary = current.Summary with { Revision = revision } };
        var json = JsonSerializer.Serialize(next, ContractJson.Options);
        return await database.WriteAsync(tx =>
        {
            var receipt = Receipt<CombatEncounter>(tx, principal, "favorite", id, key, hash); if (receipt is not null) return receipt;
            if (!current.Summary.Final) throw new OperationException("resource.busy", "活动战斗已经受到清理保护，结束后可收藏");
            var saved = tx.Scalar("SELECT revision FROM combat_encounters WHERE id=$id", ("$id", id)) as string;
            if (saved != current.Revision.ToString() || ETag(current) != etag) throw new OperationException("revision.stale", "战斗历史已变化");
            tx.Execute("UPDATE combat_encounters SET favorite=$favorite,revision=$r,document=$json,bytes=$bytes WHERE id=$id",
                ("$favorite", request.Favorite ? 1 : 0), ("$r", revision.ToString()), ("$json", json), ("$bytes", Encoding.UTF8.GetByteCount(json)), ("$id", id));
            AdvanceHistory(tx); SaveReceipt(tx, principal, "favorite", id, key!, hash, next);
            Append(tx, "combat-closed", id, revision, "combat.encounter.closed", new CombatEncounterClosed(next.Owner, next.ObservationId, id, revision, next.Summary, next.Summary.BoundaryReason));
            return next;
        }, ct);
    }
    private T? Receipt<T>(DatabaseTransaction tx, string principal, string route, string resource, string? key, string hash) where T : class
    {
        if (key is null) throw new OperationException("idempotency.required", "操作需要 Idempotency-Key");
        if (key.Length is < 8 or > 128 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new OperationException("request.invalid", "幂等键格式无效");
        var json = tx.Scalar("SELECT json_object('hash',hash,'result',json(result)) FROM combat_receipts WHERE principal=$p AND route=$route AND resource=$resource AND key=$key AND expires_at>$now",
            ("$p", principal), ("$route", route), ("$resource", resource), ("$key", key), ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds())) as string;
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.GetProperty("hash").GetString() != hash) throw new OperationException("idempotency.conflict", "幂等键已用于其他内容或修订");
        return document.RootElement.GetProperty("result").Deserialize<T>(ContractJson.Options)!;
    }
    private void SaveReceipt<T>(DatabaseTransaction tx, string principal, string route, string resource, string key, string hash, T value)
    {
        tx.Execute("DELETE FROM combat_receipts WHERE expires_at<=$now", ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds()));
        if ((long)tx.Scalar("SELECT count(*) FROM combat_receipts")! >= 10000) throw new OperationException("resource.capacityExceeded", "战斗操作回执达到上限");
        var json = JsonSerializer.Serialize(value, ContractJson.Options);
        if ((long)tx.Scalar("SELECT coalesce(sum(length(CAST(result AS BLOB))),0) FROM combat_receipts")! + Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024)
            throw new OperationException("resource.capacityExceeded", "战斗回执超过 16 MiB");
        tx.Execute("INSERT INTO combat_receipts VALUES($p,$route,$resource,$key,$hash,$result,$expiry)",
            ("$p", principal), ("$route", route), ("$resource", resource), ("$key", key), ("$hash", hash),
            ("$result", json), ("$expiry", clock.GetUtcNow().AddHours(24).ToUnixTimeMilliseconds() + 1));
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO.Compression;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.MusicLibrary.Domain;

namespace MabiTool.Modules.MusicLibrary.Persistence;

public sealed class MusicImportStore(SqliteStore database, string hostEpoch, TimeProvider? timeProvider = null)
{
    public const int MaximumPendingPerOwner = 4;
    public const int MaximumPending = 16;
    public const int MaximumStoredBytes = 64 * 1024 * 1024;
    public const int MaximumPreviewBytes = 16 * 1024 * 1024;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim parsing = new(2, 2);
    public static DatabaseMigration Migration { get; } = new("music-library", 5, """
        CREATE TABLE music_imports (
            owner TEXT NOT NULL, id TEXT NOT NULL, scope_key TEXT NOT NULL, source_hash TEXT NOT NULL,
            request_hash TEXT NOT NULL, expected_revision TEXT NOT NULL, expires_at INTEGER NOT NULL,
            host_epoch TEXT NOT NULL, preview TEXT NOT NULL, plan TEXT NOT NULL, receipt TEXT NULL,
            PRIMARY KEY(owner,id)
        ) STRICT;
        CREATE INDEX music_import_source ON music_imports(owner,scope_key,source_hash);
        CREATE TABLE music_import_keys (
            owner TEXT NOT NULL, key TEXT NOT NULL, kind TEXT NOT NULL, hash TEXT NOT NULL,
            import_id TEXT NOT NULL, expires_at INTEGER NOT NULL, PRIMARY KEY(owner,key,kind)
        ) STRICT;
        CREATE TABLE music_import_preferences(owner TEXT PRIMARY KEY, snapshot TEXT NOT NULL) STRICT;
        """);

    public MusicPreferences Preferences(string owner)
    {
        MusicLibraryEditor.Text(owner, 128);
        var json = database.ReadScalar("SELECT snapshot FROM music_import_preferences WHERE owner=$owner", ("$owner", owner)) as string;
        return json is null ? MusicPreferences.Default : Decode<MusicPreferences>(json);
    }
    public MusicImportPreview Get(string owner, string id)
    {
        var row = Read(owner, id);
        var preview = Decode<MusicImportPreview>(Unpack(row.Preview));
        return row.Receipt is null ? preview : preview with { State = "committed" };
    }
    public MusicImportReceipt? Receipt(string owner, string id) => Read(owner, id).Receipt is { } json ? Decode<MusicImportReceipt>(json) : null;

    public async Task<MusicImportPreview> PreviewAsync(string owner, MusicImportRequest request, string? key, CancellationToken ct = default)
    {
        ValidateKey(key); MusicLibraryEditor.Text(owner, 128);
        if (!await parsing.WaitAsync(0, ct)) throw new OperationException("resource.capacityExceeded", "导入解析并发达到上限");
        try
        {
            using var activity = database.Activities.EnterActivity(owner, "music.import.preview");
            ct.ThrowIfCancellationRequested();
            var source = MobiFolioImport.Read(request, owner);
            var now = clock.GetUtcNow();
            var keyed = KeyedPreview(owner, key!, source.RequestHash, now);
            if (keyed is not null) return keyed;
            var committed = FindCommitted(owner, source.Scope.Key, source.SourceHash);
            var current = new MusicLibraryStore(database, hostEpoch, clock).Read();
            var historyRevision = HistoryRevision(owner);
            (MusicImportPreview preview, MobiFolioImportPlan? plan) = committed is null
                ? MobiFolioImport.Plan(source, current, KnownScores(current), historyRevision, hostEpoch, now, ct)
                : (Get(owner, Decode<MusicImportPreview>(Unpack(committed.Preview)).Id), null);
            var previewJson = plan is null ? "" : Pack(Encode(preview, MaximumPreviewBytes));
            var planJson = plan is null ? "{}" : Pack(Encode(plan, MaximumPreviewBytes));
            if (plan is not null)
            {
                _ = Encode(plan.Library, MusicLibraryStore.MaximumSnapshotBytes);
                _ = Encode(plan.Catalog, ScoreCatalogStore.MaximumSnapshotBytes);
            }
            var resultId = await database.WriteAsync(tx =>
            {
                Cleanup(tx, now);
                var previous = KeyedPreview(tx, owner, key!, source.RequestHash, now);
                if (previous is not null) return previous;
                CheckKeyCapacity(tx);
                var saved = tx.Scalar("SELECT id FROM music_imports WHERE owner=$owner AND scope_key=$scope AND source_hash=$hash AND receipt IS NOT NULL LIMIT 1",
                    ("$owner", owner), ("$scope", source.Scope.Key), ("$hash", source.SourceHash)) as string;
                if (saved is not null)
                {
                    SaveKey(tx, owner, key!, "preview", source.RequestHash, saved, now.AddDays(1));
                    return saved;
                }
                if (LibraryRevision(tx) != current.Revision.ToString() || HistoryRevision(tx, owner) != historyRevision) throw Stale();
                if (Count(tx, "WHERE receipt IS NULL") >= MaximumPending ||
                    (long)tx.Scalar("SELECT count(*) FROM music_imports WHERE owner=$owner AND receipt IS NULL", ("$owner", owner))! >= MaximumPendingPerOwner)
                    throw new OperationException("resource.capacityExceeded", "待提交导入预览达到上限");
                var occupied = (long)tx.Scalar("SELECT coalesce(sum(length(CAST(preview AS BLOB))+length(CAST(plan AS BLOB))),0) FROM music_imports")!;
                if (occupied + Encoding.UTF8.GetByteCount(previewJson) + Encoding.UTF8.GetByteCount(planJson) > MaximumStoredBytes)
                    throw new OperationException("resource.capacityExceeded", "导入记录超过 64 MiB 存储预算");
                tx.Execute("INSERT INTO music_imports VALUES($owner,$id,$scope,$source,$request,$revision,$expiry,$epoch,$preview,$plan,NULL)",
                    ("$owner", owner), ("$id", preview.Id), ("$scope", preview.ScopeKey), ("$source", preview.SourceHash),
                    ("$request", source.RequestHash), ("$revision", preview.ExpectedLibraryRevision.ToString()),
                    ("$expiry", preview.ExpiresAt.ToUnixTimeMilliseconds()), ("$epoch", hostEpoch), ("$preview", previewJson), ("$plan", planJson));
                SaveKey(tx, owner, key!, "preview", source.RequestHash, preview.Id, preview.ExpiresAt);
                return preview.Id;
            }, ct);
            return resultId == preview.Id ? preview : Get(owner, resultId);
        }
        finally { parsing.Release(); }
    }

    public void ValidateCommit(string owner, string id, string? ifMatch)
    {
        var row = Read(owner, id);
        var preview = Decode<MusicImportPreview>(Unpack(row.Preview));
        ValidateETag(ifMatch);
        if (row.Receipt is not null) return;
        ValidatePreview(preview, row, ifMatch!, clock.GetUtcNow());
        if (new MusicLibraryStore(database, hostEpoch, clock).Read().Revision != preview.ExpectedLibraryRevision ||
            HistoryRevision(owner) != Decode<MobiFolioImportPlan>(Unpack(row.Plan)).ExpectedHistoryRevision) throw Stale();
    }

    public async Task<MusicImportReceipt> CommitAsync(string owner, string id, string? key, string? ifMatch, CancellationToken ct = default)
    {
        ValidateKey(key); ValidateETag(ifMatch);
        using var activity = database.Activities.EnterActivity(owner, "music.import.commit");
        var row = Read(owner, id);
        var preview = Decode<MusicImportPreview>(Unpack(row.Preview));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + "\n" + ifMatch)));
        var now = clock.GetUtcNow();
        MobiFolioImportPlan? plan = null;
        string? libraryJson = null, catalogJson = null, preferencesJson = null, historyJson = null, durationsJson = null;
        if (row.Receipt is null)
        {
            ValidatePreview(preview, row, ifMatch!, now);
            plan = Decode<MobiFolioImportPlan>(Unpack(row.Plan));
            libraryJson = Encode(plan.Library, MusicLibraryStore.MaximumSnapshotBytes);
            catalogJson = Encode(plan.Catalog, ScoreCatalogStore.MaximumSnapshotBytes);
            preferencesJson = preview.Preferences is null ? null : Encode(preview.Preferences, 64 * 1024);
            // 大数组预编码后通过一条 SQL 写入，保留 250 ms 短事务预算。
            historyJson = JsonSerializer.Serialize(plan.History.Select((item, index) => new
            {
                item.Id, Key = "legacy-" + id + "-" + index,
                Hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(item, ContractJson.Options))),
                Snapshot = JsonSerializer.Serialize(item, ContractJson.Options), Started = item.StartedAt.ToUnixTimeMilliseconds(), Index = index + 1
            }), ContractJson.Options);
            durationsJson = JsonSerializer.Serialize(plan.Durations.Select(item => new
            { item.ScoreId, Kind = item.EvidenceKind.ToString(), Snapshot = JsonSerializer.Serialize(item, ContractJson.Options) }), ContractJson.Options);
        }
        var expectedPlan = plan;
        var receipt = new MusicImportReceipt(id, preview.SourceHash, preview.ScopeKey, expectedPlan?.Library.Revision ?? preview.ExpectedLibraryRevision,
            preview.Counts, now);
        var receiptJson = JsonSerializer.Serialize(receipt, ContractJson.Options);
        return await database.WriteAsync(tx =>
        {
            var keyed = tx.Scalar("SELECT hash FROM music_import_keys WHERE owner=$owner AND key=$key AND kind='commit' AND expires_at>$now",
                ("$owner", owner), ("$key", key), ("$now", now.ToUnixTimeMilliseconds())) as string;
            if (keyed is not null && keyed != hash) throw new OperationException("idempotency.conflict", "幂等键已用于其他导入或修订");
            var saved = tx.Scalar("SELECT receipt FROM music_imports WHERE owner=$owner AND id=$id", ("$owner", owner), ("$id", id)) as string;
            if (saved is not null)
            {
                if (keyed is null) { Cleanup(tx, now); CheckKeyCapacity(tx); SaveKey(tx, owner, key!, "commit", hash, id, now.AddDays(1)); }
                return Decode<MusicImportReceipt>(saved);
            }
            if (tx.Scalar("SELECT id FROM music_imports WHERE owner=$owner AND id=$id", ("$owner", owner), ("$id", id)) is null)
                throw new OperationException("resource.notFound", "导入预览不存在");
            ValidatePreview(preview, row, ifMatch!, clock.GetUtcNow());
            Cleanup(tx, now);
            if (expectedPlan is null || LibraryRevision(tx) != preview.ExpectedLibraryRevision.ToString() ||
                HistoryRevision(tx, owner) != expectedPlan.ExpectedHistoryRevision) throw Stale();
            if (tx.Scalar("SELECT scope_key FROM music_library_catalogs WHERE scope_key=$scope", ("$scope", preview.ScopeKey)) is not null)
                throw new OperationException("import.conflict", "目标导入来源已存在，请重新预览");
            if ((long)tx.Scalar("SELECT count(*) FROM music_library_catalogs")! >= ScoreCatalogStore.MaximumScopes)
                throw new OperationException("resource.capacityExceeded", "乐谱来源达到 256 个上限");
            if ((long)tx.Scalar("SELECT count(*) FROM music_playback_history WHERE owner=$owner", ("$owner", owner))! + expectedPlan.History.Count > PlaybackHistoryStore.MaximumRecordsPerOwner)
                throw new OperationException("resource.capacityExceeded", "导入后播放历史超过 2,000 条");
            CheckKeyCapacity(tx);
            tx.Execute("INSERT INTO music_library_catalogs VALUES($scope,'1',$snapshot)", ("$scope", preview.ScopeKey), ("$snapshot", catalogJson));
            tx.Execute("INSERT INTO music_library_workspace VALUES(1,$revision,$snapshot) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,snapshot=excluded.snapshot",
                ("$revision", expectedPlan.Library.Revision.ToString()), ("$snapshot", libraryJson));
            if (preferencesJson is not null)
                tx.Execute("INSERT INTO music_import_preferences VALUES($owner,$snapshot) ON CONFLICT(owner) DO UPDATE SET snapshot=excluded.snapshot",
                    ("$owner", owner), ("$snapshot", preferencesJson));
            tx.Execute("""
                INSERT INTO music_playback_history(owner,id,idempotency_key,revision,request_hash,snapshot,recorded_at,started_at)
                SELECT $owner,json_extract(value,'$.id'),json_extract(value,'$.key'),$revision+json_extract(value,'$.index'),
                    json_extract(value,'$.hash'),json_extract(value,'$.snapshot'),$now,json_extract(value,'$.started') FROM json_each($rows)
                """, ("$owner", owner), ("$revision", expectedPlan.ExpectedHistoryRevision), ("$now", now.ToUnixTimeMilliseconds()), ("$rows", historyJson));
            tx.Execute("""
                INSERT INTO music_score_timeline_evidence(owner,score_id,part_id,recorded_at,evidence_kind,snapshot)
                SELECT $owner,json_extract(value,'$.scoreId'),NULL,$now,json_extract(value,'$.kind'),json_extract(value,'$.snapshot') FROM json_each($rows)
                """, ("$owner", owner), ("$now", now.ToUnixTimeMilliseconds()), ("$rows", durationsJson));
            tx.Execute("UPDATE music_imports SET receipt=$receipt,plan='{}' WHERE owner=$owner AND id=$id",
                ("$receipt", receiptJson), ("$owner", owner), ("$id", id));
            SaveKey(tx, owner, key!, "commit", hash, id, now.AddDays(1));
            SqliteStore.AppendEvent(tx, new("music-library-" + expectedPlan.Library.Revision, "music-library", "music-library", expectedPlan.Library.Revision,
                "music.library.changed", JsonSerializer.SerializeToElement(expectedPlan.LibraryChange, ContractJson.Options)));
            SqliteStore.AppendEvent(tx, new("music-catalog-" + preview.ScopeKey + "-1", "music-catalog", preview.ScopeKey, new(1), "music.catalog.changed",
                JsonSerializer.SerializeToElement(new MusicCatalogChanged(preview.ScopeKey, new(1), "complete", expectedPlan.Catalog.Entries.Count), ContractJson.Options)));
            SqliteStore.AppendEvent(tx, new("music-import-" + id, "music-import", id, new(1), "music.import.committed",
                JsonSerializer.SerializeToElement(new MusicImportChanged(owner, receipt), ContractJson.Options)));
            if (expectedPlan.History.Count > 0)
            {
                var revision = expectedPlan.ExpectedHistoryRevision + expectedPlan.History.Count;
                var changed = new MusicPlaybackHistoryChanged(owner, new((ulong)revision), expectedPlan.History[^1]);
                SqliteStore.AppendEvent(tx, new("music-history-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner)))[..16] + "-" + revision,
                    "music-history", owner, changed.Revision, "music.playback-history.changed", JsonSerializer.SerializeToElement(changed, ContractJson.Options)));
            }
            return receipt;
        }, ct);
    }

    private void ValidatePreview(MusicImportPreview preview, ImportRow row, string etag, DateTimeOffset now)
    {
        if (row.Epoch != hostEpoch || preview.ExpiresAt <= now) throw new OperationException("import.previewExpired", "导入预览已过期，请重新生成");
        if (preview.State != "ready") throw new OperationException("import.conflict", "导入仍有冲突，请处理后重新预览");
        if (MusicLibraryStore.ETag(preview.ExpectedLibraryRevision) != etag) throw Stale();
    }
    private static void ValidateETag(string? etag)
    {
        if (etag is null) throw new OperationException("revision.required", "导入提交需要 If-Match");
        if (!Regex.IsMatch(etag, "\\A\"music-library-(0|[1-9][0-9]{0,19})\"\\z")) throw new OperationException("request.invalid", "曲库 ETag 格式无效");
    }
    private static void ValidateKey(string? key)
    {
        if (key is null) throw new OperationException("idempotency.required", "导入需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z")) throw new OperationException("request.invalid", "导入幂等键格式无效");
    }
    private sealed record ImportRow(string Preview, string Plan, string? Receipt, string Epoch);
    private ImportRow Read(string owner, string id)
    {
        MusicLibraryEditor.Text(owner, 128);
        if (!Regex.IsMatch(id ?? "", @"\A[a-f0-9]{32}\z")) throw new OperationException("request.invalid", "导入 ID 无效");
        var json = database.ReadScalar("SELECT json_object('preview',preview,'plan',plan,'receipt',receipt,'epoch',host_epoch) FROM music_imports WHERE owner=$owner AND id=$id",
            ("$owner", owner), ("$id", id)) as string;
        return json is null ? throw new OperationException("resource.notFound", "导入预览不存在") : Decode<ImportRow>(json);
    }
    private ImportRow? FindCommitted(string owner, string scope, string hash)
    {
        var id = database.ReadScalar("SELECT id FROM music_imports WHERE owner=$owner AND scope_key=$scope AND source_hash=$hash AND receipt IS NOT NULL LIMIT 1",
            ("$owner", owner), ("$scope", scope), ("$hash", hash)) as string;
        return id is null ? null : Read(owner, id);
    }
    private MusicImportPreview? KeyedPreview(string owner, string key, string hash, DateTimeOffset now)
    {
        var json = database.ReadScalar("SELECT json_object('hash',k.hash,'id',i.id) FROM music_import_keys k JOIN music_imports i ON i.owner=k.owner AND i.id=k.import_id WHERE k.owner=$owner AND k.key=$key AND k.kind='preview' AND k.expires_at>$now",
            ("$owner", owner), ("$key", key), ("$now", now.ToUnixTimeMilliseconds())) as string;
        return json is null ? null : Get(owner, DecodeKeyed(json, hash));
    }
    private static string? KeyedPreview(DatabaseTransaction tx, string owner, string key, string hash, DateTimeOffset now)
    {
        var json = tx.Scalar("SELECT json_object('hash',k.hash,'id',i.id) FROM music_import_keys k JOIN music_imports i ON i.owner=k.owner AND i.id=k.import_id WHERE k.owner=$owner AND k.key=$key AND k.kind='preview' AND k.expires_at>$now",
            ("$owner", owner), ("$key", key), ("$now", now.ToUnixTimeMilliseconds())) as string;
        return json is null ? null : DecodeKeyed(json, hash);
    }
    private static string DecodeKeyed(string json, string hash)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.GetProperty("hash").GetString() != hash) throw new OperationException("idempotency.conflict", "幂等键已用于其他导入内容或映射");
        return document.RootElement.GetProperty("id").GetString()!;
    }
    private IReadOnlySet<string> KnownScores(MusicLibraryState current)
    {
        var references = current.Playlists.SelectMany(x => x.Items).Select(x => x.ScoreId).Concat(current.NameSettings.Assignments.Keys).Distinct(StringComparer.Ordinal).ToArray();
        var json = database.ReadScalar("SELECT json_group_array(DISTINCT json_extract(e.value,'$.id')) FROM music_library_catalogs c, json_each(c.snapshot,'$.entries') e WHERE json_extract(e.value,'$.id') IN (SELECT value FROM json_each($references))",
            ("$references", JsonSerializer.Serialize(references))) as string ?? "[]";
        return JsonSerializer.Deserialize<string[]>(json)!.ToHashSet(StringComparer.Ordinal);
    }
    private const string HistoryRevisionSql = "SELECT max(coalesce((SELECT max(revision) FROM music_playback_history WHERE owner=$owner),0),coalesce((SELECT revision FROM music_playback_history_state WHERE owner=$owner),0))";
    private long HistoryRevision(string owner) => (long)database.ReadScalar(HistoryRevisionSql, ("$owner", owner))!;
    private static long HistoryRevision(DatabaseTransaction tx, string owner) => (long)tx.Scalar(HistoryRevisionSql, ("$owner", owner))!;
    private static string LibraryRevision(DatabaseTransaction tx) => tx.Scalar("SELECT revision FROM music_library_workspace WHERE id=1") as string ?? "0";
    private static long Count(DatabaseTransaction tx, string where) => (long)tx.Scalar("SELECT count(*) FROM music_imports " + where)!;
    private static void Cleanup(DatabaseTransaction tx, DateTimeOffset now)
    {
        tx.Execute("DELETE FROM music_imports WHERE receipt IS NULL AND expires_at<=$now", ("$now", now.ToUnixTimeMilliseconds()));
        tx.Execute("DELETE FROM music_import_keys WHERE expires_at<=$now OR NOT EXISTS(SELECT 1 FROM music_imports i WHERE i.owner=music_import_keys.owner AND i.id=music_import_keys.import_id)", ("$now", now.ToUnixTimeMilliseconds()));
    }
    private static void CheckKeyCapacity(DatabaseTransaction tx)
    {
        if ((long)tx.Scalar("SELECT count(*) FROM music_import_keys")! >= MusicLibraryStore.MaximumReceipts)
            throw new OperationException("resource.capacityExceeded", "导入幂等记录达到上限");
    }
    private static void SaveKey(DatabaseTransaction tx, string owner, string key, string kind, string hash, string id, DateTimeOffset expiry)
        => tx.Execute("INSERT INTO music_import_keys VALUES($owner,$key,$kind,$hash,$id,$expiry) ON CONFLICT(owner,key,kind) DO UPDATE SET hash=excluded.hash,import_id=excluded.import_id,expires_at=excluded.expires_at",
            ("$owner", owner), ("$key", key), ("$kind", kind), ("$hash", hash), ("$id", id), ("$expiry", expiry.ToUnixTimeMilliseconds() + 1));
    private static string Encode<T>(T value, int maximum)
    {
        var json = JsonSerializer.Serialize(value, ContractJson.Options);
        if (Encoding.UTF8.GetByteCount(json) > maximum) throw new OperationException("resource.capacityExceeded", "导入快照超过字节预算");
        return json;
    }
    private static T Decode<T>(string json) => JsonSerializer.Deserialize<T>(json, ContractJson.Options)!;
    private static string Pack(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        using var output = new MemoryStream();
        using (var compressor = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true)) compressor.Write(bytes);
        return "br1:" + Convert.ToHexString(SHA256.HashData(bytes)) + ":" + Convert.ToBase64String(output.ToArray());
    }
    private static string Unpack(string packed)
    {
        // 压缩仅用于模块私有迁移快照；公共响应及乐谱查询仍使用原契约 JSON。
        if (!packed.StartsWith("br1:", StringComparison.Ordinal))
        {
            if (packed.AsSpan().TrimStart().StartsWith("{") && Encoding.UTF8.GetByteCount(packed) <= MaximumPreviewBytes) return packed;
            throw new StorageException("storage.assetDamaged", "迁移快照格式无效");
        }
        try
        {
            if (packed.Length > 24 * 1024 * 1024 || packed.Length < 69 || packed[68] != ':') throw new InvalidDataException();
            using var input = new MemoryStream(Convert.FromBase64String(packed[69..]));
            using var decompressor = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var block = new byte[64 * 1024]; int read;
            while ((read = decompressor.Read(block)) > 0)
            {
                if (output.Length + read > MaximumPreviewBytes) throw new InvalidDataException();
                output.Write(block, 0, read);
            }
            var bytes = output.ToArray();
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(packed[4..68]))) throw new InvalidDataException();
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (Exception e) when (e is InvalidDataException or FormatException or DecoderFallbackException)
        { throw new StorageException("storage.assetDamaged", "迁移快照已损坏"); }
    }
    private static OperationException Stale() => new("revision.stale", "曲库或历史已更新，请重新生成导入预览");
}

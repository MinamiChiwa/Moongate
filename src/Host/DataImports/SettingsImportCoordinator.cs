using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Host.Shortcuts;
using MabiTool.Infrastructure;
using MabiTool.Modules.Color.Domain;
using MabiTool.Modules.Color.Persistence;
using MabiTool.Modules.Rooms.Domain;
using MabiTool.Modules.Rooms.Persistence;

namespace MabiTool.Host.DataImports;

public sealed record SettingsImportPlan(UInt64String RoomRevision, UInt64String ColorRevision, UInt64String ColorPreferencesRevision,
    string ShortcutRevision, RoomPreferenceImport? Room, ColorLibraryImport? Color, ColorPreferenceImport? ColorPreferences);

public sealed class SettingsImportCoordinator(SqliteStore database, RoomPreferenceStore rooms,
    ColorLibraryStore colors, ColorPreferenceStore colorPreferences, ShortcutService shortcuts,
    SystemInfo system, TimeProvider clock)
{
    public static void ForgetModuleData(DatabaseTransaction transaction, string moduleId)
    {
        var project = moduleId == "color" ? "color-studio" : moduleId == "rooms" ? "mabisync" : null;
        if (project is null) return;
        transaction.Execute("DELETE FROM settings_import_keys WHERE project=$project", ("$project", project));
        transaction.Execute("DELETE FROM settings_imports WHERE project=$project", ("$project", project));
    }
    private sealed record Row(string Project, string SourceId, string SourceHash, string Epoch, long ExpiresAt, string Preview, string Plan, string? Receipt);
    public const int MaximumSnapshotBytes = 8 * 1024 * 1024;
    public const int MaximumPreviewBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim parsing = new(2, 2);
    public static DatabaseMigration Migration { get; } = new("settings-imports", 1, """
        CREATE TABLE settings_imports(
            owner TEXT NOT NULL, id TEXT NOT NULL, project TEXT NOT NULL, source_id TEXT NOT NULL, source_hash TEXT NOT NULL,
            request_hash TEXT NOT NULL, host_epoch TEXT NOT NULL, expires_at INTEGER NOT NULL, preview TEXT NOT NULL,
            plan TEXT NOT NULL, receipt TEXT NULL, PRIMARY KEY(owner,id)
        ) STRICT;
        CREATE INDEX settings_import_sources ON settings_imports(owner,project,source_id,source_hash);
        CREATE UNIQUE INDEX settings_import_committed_source ON settings_imports(owner,project,source_id,source_hash) WHERE receipt IS NOT NULL;
        CREATE TABLE settings_import_keys(
            owner TEXT NOT NULL, project TEXT NOT NULL, kind TEXT NOT NULL, key TEXT NOT NULL, hash TEXT NOT NULL,
            import_id TEXT NOT NULL, expires_at INTEGER NOT NULL, PRIMARY KEY(owner,project,kind,key)
        ) STRICT;
        """);

    public SettingsImportPreview Get(string project, string owner, string id)
    {
        var row = Read(project, owner, id);
        var preview = ImportSnapshotCodec.Read<SettingsImportPreview>(row.Preview, MaximumPreviewBytes);
        return preview with { State = row.Receipt is not null || FindReceipt(project, owner, row.SourceId, row.SourceHash) is not null
            ? "committed" : row.Epoch != system.InstanceId || row.ExpiresAt <= clock.GetUtcNow().ToUnixTimeMilliseconds() ? "expired" : preview.State };
    }

    public async Task<SettingsImportPreview> PreviewAsync(string project, string owner, SettingsImportRequest request, string? key, CancellationToken ct = default)
    {
        ValidateProject(project); ValidateOwner(owner); ValidateKey(key);
        if (!await parsing.WaitAsync(0, ct)) throw new OperationException("resource.capacityExceeded", "设置导入解析并发达到上限");
        try
        {
            using var activity = database.Activities.EnterActivity(owner, "settings.import.preview");
            if (request is null) throw LegacyJsonFiles.Invalid("", "设置导入请求不能为空");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request, ContractJson.Options);
            if (bytes.Length > LegacyJsonFiles.MaximumRequestBytes) throw new OperationException("request.bodyTooLarge", "设置导入请求超过 4 MiB");
            request = JsonSerializer.Deserialize<SettingsImportRequest>(bytes, ContractJson.Options)!;
            LegacyJsonSource source;
            try { source = LegacyJsonFiles.Read(request, project == "mabisync" ? MabiSyncSettingsImport.Files : ColorStudioImport.Files, ct); }
            catch (ArgumentException) { throw LegacyJsonFiles.Invalid("", "导入设置无效"); }
            var requestHash = Hash(JsonSerializer.SerializeToUtf8Bytes(new { project, source.SourceId, source.SourceLabel, source.SourceHash,
                bindings = request.BindingOverrides?.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray(),
                names = request.ProfileNames?.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray() }, ContractJson.Options));
            var now = clock.GetUtcNow();
            if (Keyed(project, owner, key!, requestHash, "preview", now) is { } previous) return Get(project, owner, previous);
            if (FindReceipt(project, owner, source.SourceId, source.SourceHash) is { } committed)
            {
                var id = JsonSerializer.Deserialize<SettingsImportReceipt>(committed, ContractJson.Options)!.ImportId;
                await database.WriteAsync(tx => { Cleanup(tx, now); CheckKeys(tx); SaveKey(tx, project, owner, key!, requestHash, "preview", id, now.AddDays(1)); return true; }, ct);
                return Get(project, owner, id);
            }
            var room = project == "mabisync" ? rooms.Read(owner) : RoomPreferences.Default;
            var library = project == "color-studio" ? colors.Read() : new ColorLibrary(new(0), ColorProfiles.Default, []);
            var color = project == "color-studio" ? colorPreferences.Read(owner) : ColorStudioPreferences.Default;
            var shortcut = shortcuts.Snapshot();
            var target = TargetETag(project, owner, room.Revision, library.Revision, color.Revision, shortcut.SnapshotRevision);
            RoomPreferenceImport? roomImport = null; ColorLibraryImport? colorImport = null; ColorPreferenceImport? preferenceImport = null;
            IReadOnlyList<ImportIssue> issues; IReadOnlyList<ImportIdMapping> mappings = [];
            IReadOnlyList<ColorProfile> profiles = []; IReadOnlyList<ImportedColorHistoryItem> history = []; ColorProfile? draft = null; bool conflict;
            try
            {
                if (project == "mabisync")
                {
                    var result = MabiSyncSettingsImport.Plan(source, request, shortcut.Items);
                    issues = result.Issues; conflict = result.Conflict;
                    roomImport = rooms.PrepareImport(owner, room.Revision, result.Preferences);
                    mappings = result.Preferences.Bindings.SelectMany(pair => new[] { ("start", pair.Value.Start), ("stop", pair.Value.Stop) }
                        .Where(value => value.Item2 is not null).Select(value => new ImportIdMapping("room-binding", pair.Key + "." + value.Item1,
                            pair.Key + "." + value.Item1, null, "requires-current-binding", []))).ToArray();
                }
                else
                {
                    var result = ColorStudioImport.Plan(source, request, library, color, now, ct);
                    issues = result.Issues; conflict = result.Conflict; mappings = result.Mappings; profiles = result.Profiles; history = result.History; draft = result.Draft;
                    preferenceImport = colorPreferences.PrepareImport(owner, color.Revision, result.Preferences, history);
                    if (!conflict) colorImport = colors.PrepareImport(library, draft, profiles);
                }
            }
            catch (Exception error) when (error is ArgumentException or OverflowException) { throw LegacyJsonFiles.Invalid("", "旧设置字段或范围无效"); }
            var preview = new SettingsImportPreview(Guid.NewGuid().ToString("N"), project, source.SourceId, source.SourceLabel, source.SourceHash,
                target, now + PreviewLifetime, conflict ? "conflict" : "ready", source.Files, mappings, issues, roomImport?.Value,
                preferenceImport?.Value, draft, profiles, history, shortcut.Items);
            var plan = new SettingsImportPlan(room.Revision, library.Revision, color.Revision, shortcut.SnapshotRevision, roomImport, colorImport, preferenceImport);
            var previewPacked = ImportSnapshotCodec.Pack(preview, MaximumPreviewBytes);
            var planPacked = ImportSnapshotCodec.Pack(plan, MaximumSnapshotBytes);
            var storedBytes = Encoding.UTF8.GetByteCount(previewPacked) + Encoding.UTF8.GetByteCount(planPacked);
            var idResult = await database.WriteAsync(tx =>
            {
                Cleanup(tx, now);
                if (Keyed(tx, project, owner, key!, requestHash, "preview", now) is { } retry) return retry;
                CheckKeys(tx);
                if (FindReceipt(tx, project, owner, source.SourceId, source.SourceHash) is { } receipt)
                {
                    var id = JsonSerializer.Deserialize<SettingsImportReceipt>(receipt, ContractJson.Options)!.ImportId;
                    SaveKey(tx, project, owner, key!, requestHash, "preview", id, now.AddDays(1)); return id;
                }
                if (CurrentETag(tx, project, owner) != target) throw Stale();
                if ((long)tx.Scalar("SELECT count(*) FROM settings_imports WHERE receipt IS NULL")! >= 16 ||
                    (long)tx.Scalar("SELECT count(*) FROM settings_imports WHERE owner=$owner AND receipt IS NULL", ("$owner", owner))! >= 4 ||
                    (long)tx.Scalar("SELECT coalesce(sum(length(CAST(preview AS BLOB))+length(CAST(plan AS BLOB))),0) FROM settings_imports")! + storedBytes > 64 * 1024 * 1024)
                    throw new OperationException("resource.capacityExceeded", "设置导入预览超过数量或 64 MiB 存储预算");
                tx.Execute("INSERT INTO settings_imports VALUES($owner,$id,$project,$source,$hash,$request,$epoch,$expiry,$preview,$plan,NULL)",
                    ("$owner", owner), ("$id", preview.Id), ("$project", project), ("$source", source.SourceId), ("$hash", source.SourceHash),
                    ("$request", requestHash), ("$epoch", system.InstanceId), ("$expiry", preview.ExpiresAt.ToUnixTimeMilliseconds()), ("$preview", previewPacked), ("$plan", planPacked));
                SaveKey(tx, project, owner, key!, requestHash, "preview", preview.Id, preview.ExpiresAt); return preview.Id;
            }, ct);
            return idResult == preview.Id ? preview : Get(project, owner, idResult);
        }
        finally { parsing.Release(); }
    }

    public void ValidateCommit(string project, string owner, string id, string? ifMatch)
    {
        ValidateETag(ifMatch);
        var row = Read(project, owner, id);
        if (row.Receipt is not null || FindReceipt(project, owner, row.SourceId, row.SourceHash) is not null) return;
        var preview = ImportSnapshotCodec.Read<SettingsImportPreview>(row.Preview, MaximumPreviewBytes);
        ValidatePreview(row, preview, ifMatch!);
        _ = ImportSnapshotCodec.Read<SettingsImportPlan>(row.Plan, MaximumSnapshotBytes);
        if (CurrentETag(project, owner) != preview.TargetETag) throw Stale();
    }

    public async Task<SettingsImportReceipt> CommitAsync(string project, string owner, string id, string? key, string? ifMatch, CancellationToken ct = default)
    {
        ValidateKey(key); ValidateETag(ifMatch);
        using var activity = database.Activities.EnterActivity(owner, "settings.import.commit");
        var row = Read(project, owner, id);
        var preview = ImportSnapshotCodec.Read<SettingsImportPreview>(row.Preview, MaximumPreviewBytes);
        SettingsImportPlan? plan = null;
        if (row.Receipt is null && FindReceipt(project, owner, row.SourceId, row.SourceHash) is null)
        { ValidatePreview(row, preview, ifMatch!); plan = ImportSnapshotCodec.Read<SettingsImportPlan>(row.Plan, MaximumSnapshotBytes); }
        var now = clock.GetUtcNow(); var hash = Hash(Encoding.UTF8.GetBytes(id + "\n" + ifMatch));
        var nextETag = plan is null ? preview.TargetETag : TargetETag(project, owner, plan.Room?.Value.Revision ?? plan.RoomRevision,
            plan.Color?.Value.Revision ?? plan.ColorRevision, plan.ColorPreferences?.Value.Revision ?? plan.ColorPreferencesRevision, plan.ShortcutRevision);
        var receipt = new SettingsImportReceipt(id, project, row.SourceId, row.SourceHash, nextETag, preview.Profiles.Count, preview.History.Count, now);
        var receiptJson = JsonSerializer.Serialize(receipt, ContractJson.Options);
        var changed = JsonSerializer.SerializeToElement(new SettingsImportChanged(owner, receipt), ContractJson.Options);
        return await database.WriteAsync(tx =>
        {
            var previousKey = Keyed(tx, project, owner, key!, hash, "commit", now);
            Cleanup(tx, now);
            if (previousKey is null) CheckKeys(tx);
            var saved = tx.Scalar("SELECT receipt FROM settings_imports WHERE owner=$owner AND id=$id AND project=$project",
                ("$owner", owner), ("$id", id), ("$project", project)) as string ?? FindReceipt(tx, project, owner, row.SourceId, row.SourceHash);
            if (saved is not null)
            { var previous = JsonSerializer.Deserialize<SettingsImportReceipt>(saved, ContractJson.Options)!; SaveKey(tx, project, owner, key!, hash, "commit", previous.ImportId, now.AddDays(1)); return previous; }
            if (plan is null || tx.Scalar("SELECT id FROM settings_imports WHERE owner=$owner AND id=$id", ("$owner", owner), ("$id", id)) is null)
                throw new OperationException("resource.notFound", "设置导入预览不存在");
            ValidatePreview(row, preview, ifMatch!);
            if (CurrentETag(tx, project, owner) != preview.TargetETag) throw Stale();
            if (plan.Room is not null) rooms.CommitImport(tx, plan.Room);
            if (plan.Color is not null) colors.CommitImport(tx, plan.Color);
            if (plan.ColorPreferences is not null) colorPreferences.CommitImport(tx, plan.ColorPreferences);
            tx.Execute("UPDATE settings_imports SET receipt=$receipt,plan='{}' WHERE owner=$owner AND id=$id",
                ("$receipt", receiptJson), ("$owner", owner), ("$id", id));
            SaveKey(tx, project, owner, key!, hash, "commit", id, now.AddDays(1));
            SqliteStore.AppendEvent(tx, new("settings-import-" + id, "settings-import", id, new(1),
                project == "mabisync" ? "room.import.committed" : "color.import.committed", changed));
            return receipt;
        }, ct);
    }

    private void ValidatePreview(Row row, SettingsImportPreview preview, string ifMatch)
    {
        if (row.Epoch != system.InstanceId || row.ExpiresAt <= clock.GetUtcNow().ToUnixTimeMilliseconds())
            throw new OperationException("import.previewExpired", "设置预览已过期，请重新生成");
        if (preview.State != "ready") throw new OperationException("import.conflict", "设置预览存在待处理冲突");
        if (preview.TargetETag != ifMatch) throw Stale();
    }
    private string CurrentETag(string project, string owner) => project == "mabisync"
        ? TargetETag(project, owner, rooms.Read(owner).Revision, new(0), new(0), shortcuts.Snapshot().SnapshotRevision)
        : TargetETag(project, owner, new(0), colors.Read().Revision, colorPreferences.Read(owner).Revision, shortcuts.Snapshot().SnapshotRevision);
    private string CurrentETag(DatabaseTransaction tx, string project, string owner) => project == "mabisync"
        ? TargetETag(project, owner, RoomPreferenceStore.ImportRevision(tx, owner), new(0), new(0), shortcuts.Snapshot().SnapshotRevision)
        : TargetETag(project, owner, new(0), ColorLibraryStore.ImportRevision(tx), ColorPreferenceStore.ImportRevision(tx, owner), shortcuts.Snapshot().SnapshotRevision);
    private static string TargetETag(string project, string owner, UInt64String room, UInt64String color, UInt64String preferences, string shortcut) =>
        "\"settings-" + Hash(Encoding.UTF8.GetBytes(project + "\n" + owner + "\n" + (project == "mabisync" ? room.ToString() + "\n" + shortcut : color + "\n" + preferences + "\n" + shortcut))) + "\"";
    private Row Read(string project, string owner, string id)
    {
        ValidateProject(project); ValidateOwner(owner);
        if (!Regex.IsMatch(id ?? "", @"\A[a-f0-9]{32}\z")) throw new OperationException("resource.notFound", "设置导入预览不存在");
        var json = database.ReadScalar("SELECT json_object('project',project,'sourceId',source_id,'sourceHash',source_hash,'epoch',host_epoch,'expiresAt',expires_at,'preview',preview,'plan',plan,'receipt',receipt) FROM settings_imports WHERE owner=$owner AND id=$id AND project=$project",
            ("$owner", owner), ("$id", id), ("$project", project)) as string;
        return json is null ? throw new OperationException("resource.notFound", "设置导入预览不存在") : JsonSerializer.Deserialize<Row>(json, ContractJson.Options)!;
    }
    private string? FindReceipt(string project, string owner, string source, string hash) => database.ReadScalar(
        "SELECT receipt FROM settings_imports WHERE owner=$owner AND project=$project AND source_id=$source AND source_hash=$hash AND receipt IS NOT NULL LIMIT 1",
        ("$owner", owner), ("$project", project), ("$source", source), ("$hash", hash)) as string;
    private static string? FindReceipt(DatabaseTransaction tx, string project, string owner, string source, string hash) => tx.Scalar(
        "SELECT receipt FROM settings_imports WHERE owner=$owner AND project=$project AND source_id=$source AND source_hash=$hash AND receipt IS NOT NULL LIMIT 1",
        ("$owner", owner), ("$project", project), ("$source", source), ("$hash", hash)) as string;
    private string? Keyed(string project, string owner, string key, string hash, string kind, DateTimeOffset now) =>
        ReadKey(database.ReadScalar("SELECT json_object('hash',hash,'id',import_id) FROM settings_import_keys WHERE owner=$owner AND project=$project AND key=$key AND kind=$kind AND expires_at>$now",
            ("$owner", owner), ("$project", project), ("$key", key), ("$kind", kind), ("$now", now.ToUnixTimeMilliseconds())) as string, hash);
    private static string? Keyed(DatabaseTransaction tx, string project, string owner, string key, string hash, string kind, DateTimeOffset now) =>
        ReadKey(tx.Scalar("SELECT json_object('hash',hash,'id',import_id) FROM settings_import_keys WHERE owner=$owner AND project=$project AND key=$key AND kind=$kind AND expires_at>$now",
            ("$owner", owner), ("$project", project), ("$key", key), ("$kind", kind), ("$now", now.ToUnixTimeMilliseconds())) as string, hash);
    private static string? ReadKey(string? json, string hash)
    {
        if (json is null) return null;
        using var value = JsonDocument.Parse(json);
        if (value.RootElement.GetProperty("hash").GetString() != hash) throw new OperationException("idempotency.conflict", "幂等键已用于其他设置导入内容或修订");
        return value.RootElement.GetProperty("id").GetString();
    }
    private static void SaveKey(DatabaseTransaction tx, string project, string owner, string key, string hash, string kind, string id, DateTimeOffset expiry) =>
        tx.Execute("INSERT INTO settings_import_keys VALUES($owner,$project,$kind,$key,$hash,$id,$expiry) ON CONFLICT(owner,project,kind,key) DO UPDATE SET hash=excluded.hash,import_id=excluded.import_id,expires_at=excluded.expires_at",
            ("$owner", owner), ("$project", project), ("$kind", kind), ("$key", key), ("$hash", hash), ("$id", id), ("$expiry", expiry.ToUnixTimeMilliseconds() + 1));
    private static void Cleanup(DatabaseTransaction tx, DateTimeOffset now)
    {
        tx.Execute("DELETE FROM settings_imports WHERE receipt IS NULL AND expires_at<=$now", ("$now", now.ToUnixTimeMilliseconds()));
        tx.Execute("DELETE FROM settings_import_keys WHERE expires_at<=$now OR NOT EXISTS(SELECT 1 FROM settings_imports i WHERE i.owner=settings_import_keys.owner AND i.id=settings_import_keys.import_id)",
            ("$now", now.ToUnixTimeMilliseconds()));
    }
    private static void CheckKeys(DatabaseTransaction tx)
    {
        if ((long)tx.Scalar("SELECT count(*) FROM settings_import_keys")! >= 1000) throw new OperationException("resource.capacityExceeded", "设置导入幂等记录达到上限");
    }
    private static void ValidateProject(string project)
    { if (project is not ("mabisync" or "color-studio")) throw new OperationException("request.invalid", "设置导入项目无效"); }
    private static void ValidateOwner(string owner)
    { if (owner.Length is < 1 or > 128) throw new OperationException("request.invalid", "导入主体无效"); }
    private static void ValidateKey(string? key)
    {
        if (key is null) throw new OperationException("idempotency.required", "设置导入需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z")) throw new OperationException("request.invalid", "设置导入幂等键无效");
    }
    private static void ValidateETag(string? tag)
    {
        if (tag is null) throw new OperationException("revision.required", "设置导入需要 If-Match");
        if (!Regex.IsMatch(tag, "\\A\"settings-[A-F0-9]{64}\"\\z")) throw new OperationException("request.invalid", "设置导入 ETag 无效");
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static OperationException Stale() => new("revision.stale", "目标偏好、方案或快捷键已变化，请重新预览");
}

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MabiTool.ModuleSdk;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

internal sealed record DiagnosticExportPlan(IReadOnlyList<DiagnosticSessionSnapshot> Sessions, DateTimeOffset CreatedAt);
internal sealed record SavedDiagnosticExport(DiagnosticExportArtifact Artifact, string Pin);

public sealed partial class DiagnosticStore(SqliteStore database, PathService paths, AssetStore assets,
    TimeProvider? clock = null)
{
    public static DatabaseMigration Migration { get; } = new("platform-diagnostics", 1, """
        CREATE TABLE platform_diagnostic_policy (id INTEGER PRIMARY KEY CHECK(id=1), snapshot TEXT NOT NULL) STRICT;
        CREATE TABLE platform_diagnostic_keys (principal TEXT NOT NULL,key TEXT NOT NULL,hash TEXT NOT NULL,
            receipt TEXT NOT NULL,expires INTEGER NOT NULL,PRIMARY KEY(principal,key)) STRICT;
        CREATE TABLE platform_diagnostic_sessions (id TEXT PRIMARY KEY,snapshot TEXT NOT NULL) STRICT;
        CREATE TABLE platform_diagnostic_sources (operation_id TEXT NOT NULL,session_id TEXT NOT NULL
            REFERENCES platform_diagnostic_sessions(id),PRIMARY KEY(operation_id,session_id)) STRICT;
        CREATE TABLE platform_diagnostic_exports (id TEXT PRIMARY KEY,owner TEXT NOT NULL,snapshot TEXT NOT NULL,
            expires INTEGER NOT NULL) STRICT;
        """);
    /// <summary>调试采集的采集信息及开始、结束时的运行状态总览；会话删除时级联删除。</summary>
    public static DatabaseMigration CaptureMigration { get; } = new("platform-diagnostics", 2, """
        CREATE TABLE platform_diagnostic_captures (id TEXT PRIMARY KEY,session_id TEXT NOT NULL UNIQUE
            REFERENCES platform_diagnostic_sessions(id) ON DELETE CASCADE,started INTEGER NOT NULL,snapshot TEXT NOT NULL,
            start_runtime TEXT,end_runtime TEXT) STRICT;
        """);
    public const int MaximumActiveSessions = 16, MaximumSessions = 128, MaximumExports = 16;
    public const int MaximumRecordBytes = 1024, MaximumSessionBytes = 1024 * 1024;
    /// <summary>调试采集会话使用独立模块目录与上限：单条记录 64 KiB（含文字详情，按 JSON 转义后的字节计），单次采集文件 8 MiB。</summary>
    public const string CaptureModule = "capture";
    public const int MaximumCaptureRecordBytes = 64 * 1024, MaximumCaptureBytes = 8 * 1024 * 1024;
    public const int MaximumQueryRecords = 500, MaximumQueryBytes = 3 * 1024 * 1024;
    private static bool IsCapture(DiagnosticSessionSnapshot session) => session.Module == CaptureModule;
    private static int RecordLimit(DiagnosticSessionSnapshot session) => IsCapture(session) ? MaximumCaptureRecordBytes : MaximumRecordBytes;
    private static ulong SessionLimit(DiagnosticSessionSnapshot session) => IsCapture(session) ? (ulong)MaximumCaptureBytes : MaximumSessionBytes;
    // 进行中的采集会话保留增量哈希，追加时不再整文件重读；其余会话沿用每批整文件复验。
    private readonly Dictionary<string, (IncrementalHash Hash, ulong Length)> liveHashes = new(StringComparer.Ordinal);
    public const int MaximumExportBytes = 16 * 1024 * 1024;
    public static readonly TimeSpan ExportLifetime = TimeSpan.FromMinutes(15);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    public RollingDiagnosticLog Logs { get; } = new(paths, clock);
    public static string PolicyETag(UInt64String revision) => "\"diagnostic-retention-" + revision + "\"";
    public static DiagnosticRetentionPolicy Normalize(DiagnosticRetentionPolicy policy) => policy with
    {
        SessionBudgetBytes = policy.SessionBudgetBytes ?? new(1024 * 1024 * 1024),
        LogBudgetBytes = policy.LogBudgetBytes ?? new(50 * 1024 * 1024)
    };
    public DiagnosticPolicySnapshot Policy() => database.ReadScalar("SELECT snapshot FROM platform_diagnostic_policy WHERE id=1") is string json
        ? JsonSerializer.Deserialize<DiagnosticPolicySnapshot>(json, ContractJson.Options)! : new(Normalize(new()), new(0));

    public Task<DiagnosticPolicySnapshot> ChangePolicyAsync(string principal, DiagnosticRetentionPolicy policy, string? key,
        string? ifMatch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy = Normalize(policy);
        if (policy.CompletedSessions is < 0 or > 100 || policy.RecentProtectionSeconds is < 300 or > 3600 ||
            policy.SessionBudgetBytes!.Value.Value is < 1024 or > 4UL * 1024 * 1024 * 1024 ||
            policy.LogBudgetBytes!.Value.Value is < 1024 or > 200UL * 1024 * 1024)
            throw new OperationException("request.invalid", "诊断保留策略超出数量、空间或近期保护范围");
        if (key is null) throw new OperationException("idempotency.required", "诊断配置需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z")) throw new OperationException("request.invalid", "诊断配置幂等键无效");
        if (ifMatch is null) throw new OperationException("revision.required", "诊断配置需要 If-Match");
        if (!Regex.IsMatch(ifMatch, @"\A""diagnostic-retention-(0|[1-9][0-9]{0,19})""\z"))
            throw new OperationException("request.invalid", "诊断配置 ETag 无效");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(policy, ContractJson.Options) + "\n" + ifMatch)));
        return database.WriteAsync(tx =>
        {
            tx.Execute("DELETE FROM platform_diagnostic_keys WHERE expires<=$now", ("$now", time.GetUtcNow().ToUnixTimeMilliseconds()));
            var receipt = tx.Scalar("SELECT receipt FROM platform_diagnostic_keys WHERE principal=$p AND key=$key", ("$p", principal), ("$key", key)) as string;
            if (receipt is not null)
            {
                if ((string?)tx.Scalar("SELECT hash FROM platform_diagnostic_keys WHERE principal=$p AND key=$key", ("$p", principal), ("$key", key)) != hash)
                    throw new OperationException("idempotency.conflict", "幂等键已用于其他诊断配置");
                return JsonSerializer.Deserialize<DiagnosticPolicySnapshot>(receipt, ContractJson.Options)!;
            }
            var previous = tx.Scalar("SELECT snapshot FROM platform_diagnostic_policy WHERE id=1") is string saved
                ? JsonSerializer.Deserialize<DiagnosticPolicySnapshot>(saved, ContractJson.Options)! : new DiagnosticPolicySnapshot(Normalize(new()), new(0));
            if (ifMatch != PolicyETag(previous.Revision)) throw new OperationException("revision.stale", "诊断配置修订已变化");
            if (previous.Revision.Value == ulong.MaxValue || (long)tx.Scalar("SELECT count(*) FROM platform_diagnostic_keys")! >= 1000)
                throw new OperationException("resource.capacityExceeded", "诊断配置修订或幂等记录预算已耗尽");
            var result = new DiagnosticPolicySnapshot(policy, new(previous.Revision.Value + 1));
            var encoded = JsonSerializer.Serialize(result, ContractJson.Options);
            tx.Execute("INSERT INTO platform_diagnostic_policy VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET snapshot=excluded.snapshot", ("$json", encoded));
            tx.Execute("INSERT INTO platform_diagnostic_keys VALUES($p,$key,$hash,$receipt,$expires)", ("$p", principal), ("$key", key),
                ("$hash", hash), ("$receipt", encoded), ("$expires", time.GetUtcNow().AddHours(24).ToUnixTimeMilliseconds()));
            return result;
        }, ct);
    }

    public IReadOnlyList<DiagnosticSessionSnapshot> Sessions() => database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot FROM platform_diagnostic_sessions ORDER BY id LIMIT 129";
        using var rows = command.ExecuteReader(); var result = new List<DiagnosticSessionSnapshot>();
        while (rows.Read()) result.Add(JsonSerializer.Deserialize<DiagnosticSessionSnapshot>(rows.GetString(0), ContractJson.Options)!);
        if (result.Count > MaximumSessions) throw new StorageException("storage.assetInvalid", "诊断会话登记超出数量预算");
        return (IReadOnlyList<DiagnosticSessionSnapshot>)result;
    });
    public DiagnosticSessionSnapshot Get(string id, string principal, bool owner)
    {
        var snapshot = database.ReadScalar("SELECT snapshot FROM platform_diagnostic_sessions WHERE id=$id", ("$id", id)) is string json
            ? JsonSerializer.Deserialize<DiagnosticSessionSnapshot>(json, ContractJson.Options)! : null;
        if (snapshot is null || !owner && snapshot.Owner != principal || snapshot.State == "deleting")
            throw new OperationException("resource.notFound", "诊断会话不存在");
        return snapshot;
    }
    private string EventPath(DiagnosticSessionSnapshot session)
    {
        if (!Regex.IsMatch(session.Id, @"\A[0-9a-f]{32}\z") || !Regex.IsMatch(session.Module, @"\A[a-z][a-z0-9-]{0,39}\z"))
            throw new StorageException("storage.assetInvalid", "诊断会话路径身份无效");
        return paths.Resolve("diagnostics", session.Module, session.Id, "events.jsonl");
    }
    private Task SaveAsync(DiagnosticSessionSnapshot session, CancellationToken ct = default) => database.WriteAsync(tx =>
        tx.Execute("INSERT INTO platform_diagnostic_sessions VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET snapshot=excluded.snapshot",
            ("$id", session.Id), ("$json", JsonSerializer.Serialize(session, ContractJson.Options))), ct);

    public async Task<DiagnosticSessionSnapshot> BeginAsync(string owner, string module, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128 || !Regex.IsMatch(module, @"\A[a-z][a-z0-9-]{0,39}\z"))
            throw new OperationException("request.invalid", "诊断主体或模块无效");
        using var activity = database.Activities.EnterActivity("diagnostics", "diagnostic.write");
        await gate.WaitAsync(ct);
        try
        {
            await CleanupLockedAsync(ct);
            var sessions = Sessions();
            if (sessions.Count >= MaximumSessions || sessions.Count(x => x.State == "active") >= MaximumActiveSessions)
                throw new OperationException("resource.capacityExceeded", "诊断会话数量达到上限");
            var now = time.GetUtcNow();
            var session = new DiagnosticSessionSnapshot(Guid.NewGuid().ToString("N"), owner, module, "active", new(1), now, now, new(0), new(0), new(0),
                Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>())));
            var path = EventPath(session); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) file.Flush(true);
            await SaveAsync(session, ct);
            return session;
        }
        finally { gate.Release(); }
    }

    public async Task<DiagnosticSessionSnapshot?> AppendAsync(string id, IReadOnlyList<DiagnosticRecord> records, CancellationToken ct = default)
    {
        if (records.Count > 256) throw new OperationException("request.invalid", "单批诊断记录超出数量预算");
        using var activity = database.Activities.EnterActivity("diagnostics", "diagnostic.write");
        await gate.WaitAsync(ct);
        try
        {
            var session = Get(id, "owner", true);
            if (session.State != "active") return session;
            var path = EventPath(session); var capture = IsCapture(session);
            if (!File.Exists(path) || new FileInfo(path).Length != (long)session.EventBytes.Value)
                throw new StorageException("storage.assetDamaged", "诊断事件文件与写入水位不一致");
            (IncrementalHash Hash, ulong Length) live = default;
            if (!capture || !liveHashes.TryGetValue(id, out live) || live.Length != session.EventBytes.Value)
            {
                byte[] content;
                await using (var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous))
                { content = new byte[original.Length]; await original.ReadExactlyAsync(content, ct); }
                if (Convert.ToHexStringLower(SHA256.HashData(content)) != session.EventSha256)
                    throw new StorageException("storage.assetDamaged", "诊断事件内容不匹配当前会话哈希");
                if (capture)
                {
                    if (liveHashes.Remove(id, out var stale)) stale.Hash.Dispose();
                    var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); incremental.AppendData(content);
                    liveHashes[id] = live = (incremental, (ulong)content.Length);
                }
            }
            var cleaned = records.Select(x => DiagnosticSanitizer.Create(x.OccurredAt, x.Level, x.Module, x.Code, x.TraceId, x.OperationId,
                x.ProviderVersion, x.ExceptionType, x.Component, x.Event, x.Fields, x.Sequence, x.Detail, capture, x.HttpMethod, x.Route)).ToArray();
            // 采集记录可能带文字详情，只写入采集文件，不进入常驻日志段。
            if (!capture) await Logs.AppendAsync(cleaned, Policy().Policy, ct);
            var length = session.EventBytes.Value; var accepted = 0UL; var dropped = 0UL;
            await using (var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 8192, FileOptions.Asynchronous))
            {
                foreach (var record in cleaned)
                {
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, ContractJson.Options) + "\n");
                    if (bytes.Length > RecordLimit(session) || length + (ulong)bytes.Length > SessionLimit(session)) { dropped++; continue; }
                    await file.WriteAsync(bytes, ct); length += (ulong)bytes.Length; accepted++;
                    if (capture) live.Hash!.AppendData(bytes);
                }
                await file.FlushAsync(ct); file.Flush(true);
            }
            string hash;
            if (capture) { hash = Convert.ToHexStringLower(live.Hash!.GetCurrentHash()); liveHashes[id] = (live.Hash, length); }
            else
            {
                await using var verified = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(verified, ct));
            }
            var next = session with { Revision = new(checked(session.Revision.Value + 1)), LastWrittenAt = time.GetUtcNow(),
                EventBytes = new(length), Records = new(session.Records.Value + accepted), DroppedRecords = new(session.DroppedRecords.Value + dropped),
                EventSha256 = hash };
            await SaveAsync(next, ct);
            return next;
        }
        catch { if (liveHashes.Remove(id, out var failed)) failed.Hash.Dispose(); throw; }
        finally { gate.Release(); }
    }
    public async Task CompleteAsync(string id, bool interrupted = false, CancellationToken ct = default,
        ActivityBarrier.MaintenanceLease? maintenance = null)
    {
        using var activity = maintenance is null ? database.Activities.EnterActivity("diagnostics", "diagnostic.write") :
            database.Activities.EnterMaintenanceActivity(maintenance, "diagnostic.complete");
        await gate.WaitAsync(ct);
        try
        {
            if (liveHashes.Remove(id, out var live)) live.Hash.Dispose();
            var session = Get(id, "owner", true);
            if (session.State != "active") return;
            var completed = session with { State = interrupted ? "interrupted" : "completed", Revision = new(checked(session.Revision.Value + 1)) };
            if (maintenance is null) await SaveAsync(completed, ct);
            else await database.WriteMaintenanceAsync(maintenance, tx => tx.Execute(
                "UPDATE platform_diagnostic_sessions SET snapshot=$json WHERE id=$id", ("$id", id),
                ("$json", JsonSerializer.Serialize(completed, ContractJson.Options))), ct);
        }
        finally { gate.Release(); }
    }
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            foreach (var session in Sessions().Where(x => x.State == "active"))
                await SaveAsync(session with { State = "interrupted", Revision = new(checked(session.Revision.Value + 1)) }, ct);
            await RecoverCapturesAsync(ct);
            await database.WriteAsync(tx => tx.Execute("DELETE FROM platform_diagnostic_sources WHERE operation_id IN (SELECT id FROM core_operations WHERE terminal=1)"), ct);
            await database.WriteAsync(tx => tx.Execute("DELETE FROM platform_asset_pins WHERE owner LIKE 'diagnostic-read:%'"), ct);
            await CleanupLockedAsync(ct);
        }
        finally { gate.Release(); }
        await Logs.MeasureAsync(ct);
    }

    internal static DiagnosticExportPlan PinSources(DatabaseTransaction tx, string operationId, string principal, bool owner,
        DiagnosticExportRequest request, DateTimeOffset now)
    {
        var snapshots = new List<DiagnosticSessionSnapshot>();
        foreach (var version in request.Sessions)
        {
            var json = tx.Scalar("SELECT snapshot FROM platform_diagnostic_sessions WHERE id=$id", ("$id", version.Id)) as string;
            var session = json is null ? null : JsonSerializer.Deserialize<DiagnosticSessionSnapshot>(json, ContractJson.Options);
            if (session is null || !owner && session.Owner != principal || session.State is "deleting" or "damaged")
                throw new OperationException("resource.notFound", "诊断会话不存在");
            if (session.Revision != version.Revision) throw new OperationException("revision.stale", "诊断会话修订已变化");
            if (session.EventBytes.Value > SessionLimit(session)) throw new StorageException("storage.assetDamaged", "诊断事件水位超出预算");
            snapshots.Add(session);
            tx.Execute("INSERT INTO platform_diagnostic_sources VALUES($op,$id)", ("$op", operationId), ("$id", session.Id));
        }
        return new(snapshots, now);
    }
    internal Task ReleaseSourcesAsync(string operationId, CancellationToken ct = default) => database.WriteAsync(tx =>
        tx.Execute("DELETE FROM platform_diagnostic_sources WHERE operation_id=$op", ("$op", operationId)), ct);

    internal async Task<DiagnosticExportArtifact> ExportAsync(string principal, DiagnosticExportPlan plan, CancellationToken ct)
    {
        using var activity = database.Activities.EnterActivity("diagnostics", "diagnostic.export");
        var contents = new List<DiagnosticExportSession>();
        var captures = new List<DiagnosticCaptureExport>();
        foreach (var session in plan.Sessions)
        {
            contents.Add(new(session, await ReadRecordsAsync(session, ct)));
            if (IsCapture(session) && CaptureForSession(session.Id) is { } capture) captures.Add(capture);
        }
        using var payload = new MemoryStream();
        await using (var gzip = new GZipStream(payload, CompressionLevel.Fastest, true))
            await JsonSerializer.SerializeAsync(gzip, new DiagnosticExportDocument(2, plan.CreatedAt, contents, captures,
                captures.Any(x => x.Capture.IncludeDetail)), ContractJson.Options, ct);
        if (payload.Length > MaximumExportBytes) throw new StorageException("storage.assetInvalid", "诊断导出超过资产预算");
        await gate.WaitAsync(ct);
        AssetSnapshot? asset = null; string? pin = null;
        try
        {
            await CleanupExportsAsync(ct);
            if ((long)database.ReadScalar("SELECT count(*) FROM platform_diagnostic_exports")! >= MaximumExports)
                throw new OperationException("resource.capacityExceeded", "诊断导出资产数量达到上限");
            payload.Position = 0; var id = Guid.NewGuid().ToString("N");
            asset = await assets.PutAsync("diagnostics", "export:" + id, "application/gzip", payload, MaximumExportBytes, ct);
            pin = await assets.PinAsync(asset.Id, "diagnostic-export:" + id, ct);
            var result = new DiagnosticExportArtifact(id, principal, asset, time.GetUtcNow() + ExportLifetime);
            await database.WriteAsync(tx => tx.Execute("INSERT INTO platform_diagnostic_exports VALUES($id,$owner,$json,$expires)",
                ("$id", id), ("$owner", principal), ("$json", JsonSerializer.Serialize(new SavedDiagnosticExport(result, pin), ContractJson.Options)),
                ("$expires", result.ExpiresAt.ToUnixTimeMilliseconds())), ct);
            return result;
        }
        catch
        {
            if (pin is not null) await assets.UnpinAsync(pin);
            if (asset is not null) await assets.RemoveAsync(asset.Id);
            throw;
        }
        finally { gate.Release(); }
    }
    public DiagnosticExportArtifact GetExport(string id, string principal, bool owner)
    {
        var saved = database.ReadScalar("SELECT snapshot FROM platform_diagnostic_exports WHERE id=$id", ("$id", id)) is string json
            ? JsonSerializer.Deserialize<SavedDiagnosticExport>(json, ContractJson.Options) : null;
        if (saved is null || !owner && saved.Artifact.Owner != principal || saved.Artifact.ExpiresAt <= time.GetUtcNow())
            throw new OperationException("resource.notFound", "诊断导出不存在或已到期");
        return saved.Artifact;
    }
    public DiagnosticExportArtifact? FindExportForAsset(string assetId, string principal, bool owner)
    {
        var json = database.ReadScalar("SELECT snapshot FROM platform_diagnostic_exports WHERE json_extract(snapshot,'$.artifact.asset.id')=$id AND ($admin=1 OR owner=$owner) AND expires>$now",
            ("$id", assetId), ("$admin", owner ? 1 : 0), ("$owner", principal), ("$now", time.GetUtcNow().ToUnixTimeMilliseconds())) as string;
        return json is null ? null : JsonSerializer.Deserialize<SavedDiagnosticExport>(json, ContractJson.Options)!.Artifact;
    }
    public async Task<AssetContentChunk> ReadExportAsync(string id, string principal, bool owner, AssetContentRequest request, CancellationToken ct)
    {
        if (request.Offset < 0 || request.Count is < 1 or > 262144) throw new OperationException("request.invalid", "诊断资产读取范围无效");
        await gate.WaitAsync(ct);
        string? pin = null;
        try
        {
            var artifact = GetExport(id, principal, owner);
            pin = await assets.PinAsync(artifact.Asset.Id, "diagnostic-read:" + id, ct);
            var bytes = await assets.ReadRangeAsync(artifact.Asset.Id, request.Offset, request.Count, MaximumExportBytes, ct);
            return new(id, request.Offset, Convert.ToBase64String(bytes), artifact.Asset.SizeBytes, artifact.Asset.Sha256);
        }
        catch (StorageException error) when (error.Code is "storage.assetDamaged" or "storage.assetInvalid")
        { throw new OperationException(error.Code, error.Message); }
        finally
        {
            try { if (pin is not null) await assets.UnpinAsync(pin); }
            finally { gate.Release(); }
        }
    }
    private async Task<IReadOnlyList<DiagnosticRecord>> ReadRecordsAsync(DiagnosticSessionSnapshot session, CancellationToken ct)
    {
        try
        {
            var path = EventPath(session);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.Asynchronous);
            if (session.EventBytes.Value > SessionLimit(session) || (ulong)stream.Length < session.EventBytes.Value)
                throw new StorageException("storage.assetDamaged", "诊断源文件缺失或短于固定水位");
            var bytes = new byte[(int)session.EventBytes.Value]; await stream.ReadExactlyAsync(bytes, ct);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != session.EventSha256)
                throw new StorageException("storage.assetDamaged", "诊断源文件不匹配固定内容哈希");
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.Length > 0 && !text.EndsWith('\n')) throw new JsonException("诊断记录未完整结束");
            var result = new List<DiagnosticRecord>();
            foreach (var line in text.Split('\n').SkipLast(1))
            {
                ct.ThrowIfCancellationRequested();
                if (Encoding.UTF8.GetByteCount(line) > RecordLimit(session)) throw new JsonException("诊断记录超出预算");
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    document.RootElement.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != document.RootElement.EnumerateObject().Count())
                    throw new JsonException("诊断记录格式或属性无效");
                var record = document.RootElement.Deserialize<DiagnosticRecord>(ContractJson.Options)!;
                if (!DiagnosticSanitizer.IsValid(record, IsCapture(session))) throw new JsonException("诊断记录字段不匹配白名单");
                result.Add(record);
            }
            if ((ulong)result.Count != session.Records.Value) throw new JsonException("诊断记录数量与固定水位不匹配");
            return result;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or IOException or UnauthorizedAccessException)
        { throw new StorageException("storage.assetDamaged", "诊断源文件无法按固定记录水位校验"); }
    }

    public async Task<int> CleanupAsync(CancellationToken ct = default)
    {
        using var activity = database.Activities.EnterActivity("diagnostics", "diagnostic.cleanup");
        await gate.WaitAsync(ct);
        try { return await CleanupLockedAsync(ct); }
        finally { gate.Release(); }
    }
    internal async Task DiscardExportAsync(DiagnosticExportArtifact artifact, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var saved = database.ReadScalar("SELECT snapshot FROM platform_diagnostic_exports WHERE id=$id", ("$id", artifact.Id)) is string json
                ? JsonSerializer.Deserialize<SavedDiagnosticExport>(json, ContractJson.Options) : null;
            if (saved is null) return;
            await assets.UnpinAsync(saved.Pin, ct); await assets.RemoveAsync(saved.Artifact.Asset.Id, ct);
            await database.WriteAsync(tx => tx.Execute("DELETE FROM platform_diagnostic_exports WHERE id=$id", ("$id", artifact.Id)), ct);
        }
        finally { gate.Release(); }
    }
    private async Task<int> CleanupLockedAsync(CancellationToken ct)
    {
        await CleanupExportsAsync(ct);
        var policy = Policy().Policy; var all = Sessions(); var completed = all.Count(x => x.State != "active");
        var total = all.Sum(x => (long)x.EventBytes.Value); var removed = 0;
        foreach (var session in all.Where(x => x.State != "active").OrderBy(x => x.LastWrittenAt).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            if (session.State != "deleting" && completed <= policy.CompletedSessions && (ulong)total <= policy.SessionBudgetBytes!.Value.Value) break;
            if (session.State != "deleting" && time.GetUtcNow() - session.LastWrittenAt < TimeSpan.FromSeconds(policy.RecentProtectionSeconds)) continue;
            var marked = await database.WriteAsync(tx =>
            {
                if ((long)tx.Scalar("SELECT count(*) FROM platform_diagnostic_sources WHERE session_id=$id", ("$id", session.Id))! > 0) return false;
                var current = tx.Scalar("SELECT snapshot FROM platform_diagnostic_sessions WHERE id=$id", ("$id", session.Id)) as string;
                if (current is null) return false;
                var live = JsonSerializer.Deserialize<DiagnosticSessionSnapshot>(current, ContractJson.Options)!;
                if (live.State == "active" || live.Revision != session.Revision) return false;
                return tx.Execute("UPDATE platform_diagnostic_sessions SET snapshot=$json WHERE id=$id",
                    ("$id", session.Id), ("$json", JsonSerializer.Serialize(live with { State = "deleting" }, ContractJson.Options))) == 1;
            }, ct);
            if (!marked) continue;
            var path = EventPath(session); if (File.Exists(path)) File.Delete(path);
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            await database.WriteAsync(tx => tx.Execute("DELETE FROM platform_diagnostic_sessions WHERE id=$id", ("$id", session.Id)), ct);
            completed--; total -= (long)session.EventBytes.Value; removed++;
        }
        return removed;
    }
    private async Task CleanupExportsAsync(CancellationToken ct)
    {
        var expired = database.Read(connection =>
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT snapshot FROM platform_diagnostic_exports WHERE expires<=$now";
            command.Parameters.AddWithValue("$now", time.GetUtcNow().ToUnixTimeMilliseconds());
            using var rows = command.ExecuteReader(); var result = new List<SavedDiagnosticExport>();
            while (rows.Read()) result.Add(JsonSerializer.Deserialize<SavedDiagnosticExport>(rows.GetString(0), ContractJson.Options)!);
            return result;
        });
        foreach (var saved in expired)
        {
            await assets.UnpinAsync(saved.Pin, ct);
            try { await assets.RemoveAsync(saved.Artifact.Asset.Id, ct); }
            catch (StorageException error) when (error.Code == "storage.assetInUse") { continue; }
            await database.WriteAsync(tx => tx.Execute("DELETE FROM platform_diagnostic_exports WHERE id=$id", ("$id", saved.Artifact.Id)), ct);
        }
        if (expired.Count > 0) await assets.CollectGarbageAsync(ct);
    }
}

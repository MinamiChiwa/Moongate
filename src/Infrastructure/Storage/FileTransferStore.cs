using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

internal sealed record FileAssetAccess(AssetSnapshot Asset, string Name, string Category);
internal sealed record FileExportPlan(string Id, string Name, IReadOnlyList<FileAssetAccess> Sources,
    IReadOnlyList<string> Categories, DateTimeOffset CreatedAt, string OperationId = "");
internal sealed record SavedFileWrite(bool Completed, FileSaveReceipt? Receipt);
internal sealed record FileImportLink(string Id, string Project);

public sealed class FileTransferStore(SqliteStore database, AssetStore assets, TimeProvider? clock = null)
{
    public const int MaximumUploads = 32;
    public const long MaximumDeclaredBytes = 1024 * 1024 * 1024;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    public static DatabaseMigration Migration { get; } = new("platform-files", 1, """
        CREATE TABLE platform_file_uploads(id TEXT PRIMARY KEY,owner TEXT NOT NULL,snapshot TEXT NOT NULL,pin TEXT,expires INTEGER NOT NULL) STRICT;
        CREATE TABLE platform_file_keys(owner TEXT NOT NULL,kind TEXT NOT NULL,key TEXT NOT NULL,hash TEXT NOT NULL,
            receipt TEXT NOT NULL,expires INTEGER NOT NULL,PRIMARY KEY(owner,kind,key)) STRICT;
        CREATE TABLE platform_file_imports(id TEXT PRIMARY KEY,owner TEXT NOT NULL,project TEXT NOT NULL,parser TEXT NOT NULL,expires INTEGER NOT NULL) STRICT;
        CREATE TABLE platform_file_exports(id TEXT PRIMARY KEY,owner TEXT NOT NULL,snapshot TEXT NOT NULL,pin TEXT NOT NULL,expires INTEGER NOT NULL,
            operation_id TEXT NOT NULL REFERENCES core_operations(id)) STRICT;
        CREATE TABLE platform_file_export_sources(operation_id TEXT NOT NULL,asset_id TEXT NOT NULL,pin TEXT NOT NULL,
            PRIMARY KEY(operation_id,asset_id)) STRICT;
        """);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim uploadCapacity = new(2, 2);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> uploadGates = new(StringComparer.Ordinal);
    public static string ETag(FileUploadSnapshot value) => "\"upload-" + value.Id + "-" + value.Revision + "\"";
    public static string AssetETag(AssetSnapshot value) => "\"asset-" + value.Id + "-" + value.Revision + "\"";
    internal static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options)));
    internal static void Key(string? key)
    {
        if (key is null) throw new OperationException("idempotency.required", "文件操作需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z")) throw new OperationException("request.invalid", "文件操作幂等键无效");
    }
    internal static void Id(string id)
    { if (id is null || !Regex.IsMatch(id, @"\A[0-9a-f]{32}\z")) throw new OperationException("resource.notFound", "文件资源不存在"); }
    public async Task<FileUploadSnapshot> CreateAsync(string owner, FileUploadRequest request, string? key, CancellationToken ct)
    {
        Key(key); FileNames.Validate(request.Name);
        if (request.SizeBytes.Value > FilePackageReader.MaximumUploadBytes ||
            request.Sha256 is null || !Regex.IsMatch(request.Sha256, @"\A[0-9a-f]{64}\z") ||
            request.MediaType is not ("application/json" or "application/zip" or "application/gzip" or "text/plain" or "application/octet-stream"))
            throw new OperationException("request.invalid", "上传长度、哈希或媒体类型无效");
        var hash = Hash(request);
        await gate.WaitAsync(ct);
        try
        {
            await CleanupLockedAsync(ct);
            return await database.WriteAsync(tx =>
            {
                if (FindKey<FileUploadSnapshot>(tx, owner, "create", key!, hash, time.GetUtcNow()) is { } previous) return previous;
                var rows = tx.Scalar("SELECT count(*) FROM platform_file_uploads");
                var bytes = tx.Scalar("SELECT coalesce(sum(CAST(json_extract(snapshot,'$.sizeBytes') AS INTEGER)),0) FROM platform_file_uploads");
                if ((long)rows! >= MaximumUploads || (long)bytes! + (long)request.SizeBytes.Value > MaximumDeclaredBytes)
                    throw new OperationException("resource.capacityExceeded", "上传数量或声明空间达到上限");
                var result = new FileUploadSnapshot(Guid.NewGuid().ToString("N"), owner, request.Name, request.SizeBytes,
                    request.Sha256, request.MediaType, "created", new(1), time.GetUtcNow() + Lifetime, null);
                tx.Execute("INSERT INTO platform_file_uploads VALUES($id,$owner,$json,NULL,$expires)", ("$id", result.Id), ("$owner", owner),
                    ("$json", JsonSerializer.Serialize(result, ContractJson.Options)), ("$expires", result.ExpiresAt.ToUnixTimeMilliseconds()));
                SaveKey(tx, owner, "create", key!, hash, result, time.GetUtcNow());
                return result;
            }, ct);
        }
        finally { gate.Release(); }
    }
    public FileUploadSnapshot Get(string id, string owner)
    {
        Id(id);
        var json = database.ReadScalar("SELECT snapshot FROM platform_file_uploads WHERE id=$id AND owner=$owner", ("$id", id), ("$owner", owner)) as string;
        var value = json is null ? null : JsonSerializer.Deserialize<FileUploadSnapshot>(json, ContractJson.Options);
        return value is not null && value.ExpiresAt > time.GetUtcNow() ? value : throw new OperationException("resource.notFound", "上传不存在或已到期");
    }
    public async Task<FileUploadSnapshot> UploadAsync(string id, string owner, Stream content, string? key, string? etag,
        Func<bool> authorized, CancellationToken ct)
    {
        Key(key); _ = Get(id, owner);
        if (etag is null) throw new OperationException("revision.required", "上传内容需要 If-Match");
        if (!await uploadCapacity.WaitAsync(0, ct)) throw new OperationException("resource.capacityExceeded", "同时上传数量达到上限");
        var perUpload = uploadGates.GetOrAdd(id, _ => new(1, 1));
        var acquired = false;
        try
        {
            await perUpload.WaitAsync(ct); acquired = true;
            var upload = Get(id, owner);
            var hash = Hash(new { id, etag });
            var retry = database.Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT hash FROM platform_file_keys WHERE owner=$owner AND kind=$kind AND key=$key AND expires>$now";
                command.Parameters.AddWithValue("$owner", owner); command.Parameters.AddWithValue("$kind", "content:" + id);
                command.Parameters.AddWithValue("$key", key!); command.Parameters.AddWithValue("$now", time.GetUtcNow().ToUnixTimeMilliseconds());
                var found = command.ExecuteScalar() as string;
                if (found is not null && found != hash) throw new OperationException("idempotency.conflict", "幂等键已用于其他上传条件");
                return found is not null;
            });
            if (!retry && etag != ETag(upload)) throw new OperationException("revision.stale", "上传状态修订已变化");
            if (retry && upload.State != "ready") throw new OperationException("operation.interrupted", "原上传没有完整提交，请查询后使用新条件重试");
            using var guarded = new AuthorizedFileInput(content, authorized);
            if (upload.State == "ready")
            {
                await VerifyAsync(guarded, upload, ct);
                await database.WriteAsync(tx => { SaveKey(tx, owner, "content:" + id, key!, hash, upload, time.GetUtcNow()); return 0; }, ct);
                return upload;
            }
            await SaveUploadAsync(upload with { State = "uploading", Revision = new(upload.Revision.Value + 1) }, null, ct);
            AssetSnapshot? asset = null; string? pin = null; var committed = false;
            try
            {
                asset = await assets.PutAsync("platform-files", "upload:" + id, upload.MediaType, guarded, (long)upload.SizeBytes.Value, ct);
                if (asset.SizeBytes != upload.SizeBytes || asset.Sha256 != upload.Sha256)
                    throw new OperationException("storage.assetDamaged", "实收文件长度或哈希不匹配上传声明");
                pin = await assets.PinAsync(asset.Id, "file-upload:" + id, ct);
                var result = upload with { State = "ready", Revision = new(upload.Revision.Value + 2), Asset = asset, ExpiresAt = time.GetUtcNow() + Lifetime };
                await database.WriteAsync(tx =>
                {
                    tx.Execute("UPDATE platform_file_uploads SET snapshot=$json,pin=$pin,expires=$expires WHERE id=$id AND owner=$owner",
                        ("$json", JsonSerializer.Serialize(result, ContractJson.Options)), ("$pin", pin), ("$expires", result.ExpiresAt.ToUnixTimeMilliseconds()), ("$id", id), ("$owner", owner));
                    SaveKey(tx, owner, "content:" + id, key!, hash, result, time.GetUtcNow());
                    return 0;
                }, ct);
                committed = true; return result;
            }
            finally
            {
                if (!committed)
                {
                    try
                    {
                        if (pin is not null) await assets.UnpinAsync(pin);
                        if (asset is not null) await assets.RemoveAsync(asset.Id);
                    }
                    finally { await SaveUploadAsync(upload with { State = "failed", Revision = new(upload.Revision.Value + 2) }, null, default); }
                }
            }
        }
        finally { if (acquired) perUpload.Release(); uploadCapacity.Release(); }
    }
    private Task SaveUploadAsync(FileUploadSnapshot upload, string? pin, CancellationToken ct) => database.WriteAsync(tx =>
        tx.Execute("UPDATE platform_file_uploads SET snapshot=$json,pin=$pin WHERE id=$id", ("$json", JsonSerializer.Serialize(upload, ContractJson.Options)),
            ("$pin", pin), ("$id", upload.Id)), ct);
    private static async Task VerifyAsync(Stream input, FileUploadSnapshot upload, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var block = new byte[65536]; ulong bytes = 0; int count;
        while ((count = await input.ReadAsync(block, ct)) != 0)
        {
            bytes += (ulong)count; if (bytes > upload.SizeBytes.Value) throw new OperationException("storage.assetDamaged", "重复上传内容超出原声明");
            hash.AppendData(block, 0, count);
        }
        if (bytes != upload.SizeBytes.Value || Convert.ToHexStringLower(hash.GetHashAndReset()) != upload.Sha256)
            throw new OperationException("storage.assetDamaged", "重复上传内容与原文件不符");
    }
    internal FileAssetAccess? OwnedAsset(string id, string owner, bool admin)
    {
        var args = new (string, object?)[] { ("$id", id), ("$owner", owner), ("$admin", admin ? 1 : 0), ("$now", time.GetUtcNow().ToUnixTimeMilliseconds()) };
        var uploaded = database.ReadScalar("SELECT snapshot FROM platform_file_uploads WHERE json_extract(snapshot,'$.asset.id')=$id AND ($admin=1 OR owner=$owner) AND expires>$now", args) as string;
        if (uploaded is not null)
        {
            var upload = JsonSerializer.Deserialize<FileUploadSnapshot>(uploaded, ContractJson.Options)!;
            if (upload.State == "ready" && upload.Asset is not null) return new(upload.Asset, upload.Name, "selected-files");
        }
        var exported = database.ReadScalar("SELECT e.snapshot FROM platform_file_exports e JOIN core_operations o ON e.operation_id=o.id WHERE json_extract(e.snapshot,'$.asset.id')=$id AND ($admin=1 OR e.owner=$owner) AND e.expires>$now AND o.terminal=1 AND json_extract(o.snapshot,'$.state')='succeeded'", args) as string;
        if (exported is not null)
        {
            var value = JsonSerializer.Deserialize<FileExportArtifact>(exported, ContractJson.Options)!;
            return new(value.Asset, value.Name, value.ContentCategories.Contains("selected-files") ? "selected-files" : "export");
        }
        return null;
    }
    internal async Task RegisterImportAsync(FileImportPreview value, string owner, string key, string hash, CancellationToken ct)
    {
        await database.WriteAsync(tx =>
        {
            tx.Execute("DELETE FROM platform_file_imports WHERE expires<=$now", ("$now", time.GetUtcNow().ToUnixTimeMilliseconds()));
            if ((long)tx.Scalar("SELECT count(*) FROM platform_file_imports")! >= 128 &&
                tx.Scalar("SELECT id FROM platform_file_imports WHERE id=$id", ("$id", value.Id)) is null)
                throw new OperationException("resource.capacityExceeded", "通用导入预览登记达到上限");
            tx.Execute("INSERT INTO platform_file_imports VALUES($id,$owner,$project,$parser,$expires) ON CONFLICT(id) DO NOTHING",
                ("$id", value.Id), ("$owner", owner), ("$project", value.Project), ("$parser", value.ParserVersion), ("$expires", time.GetUtcNow().AddDays(1).ToUnixTimeMilliseconds()));
            SaveKey(tx, owner, "import", key, hash, new FileImportLink(value.Id, value.Project), time.GetUtcNow());
            return 0;
        }, ct);
    }
    internal T? LookupKey<T>(string owner, string kind, string key, string hash) where T : class
    {
        Key(key);
        var json = database.ReadScalar("SELECT receipt FROM platform_file_keys WHERE owner=$owner AND kind=$kind AND key=$key AND expires>$now",
            ("$owner", owner), ("$kind", kind), ("$key", key), ("$now", time.GetUtcNow().ToUnixTimeMilliseconds())) as string;
        if (json is null) return null;
        if ((string?)database.ReadScalar("SELECT hash FROM platform_file_keys WHERE owner=$owner AND kind=$kind AND key=$key",
            ("$owner", owner), ("$kind", kind), ("$key", key)) != hash)
            throw new OperationException("idempotency.conflict", "幂等键已用于其他文件内容或条件");
        return JsonSerializer.Deserialize<T>(json, ContractJson.Options);
    }
    internal Task RecordKeyAsync<T>(string owner, string kind, string key, string hash, T value, CancellationToken ct) where T : class => database.WriteAsync(tx =>
    {
        _ = FindKey<T>(tx, owner, kind, key, hash, time.GetUtcNow());
        SaveKey(tx, owner, kind, key, hash, value, time.GetUtcNow());
        return 0;
    }, ct);
    internal string ImportProject(string id, string owner)
    {
        Id(id);
        return database.ReadScalar("SELECT project FROM platform_file_imports WHERE id=$id AND owner=$owner", ("$id", id), ("$owner", owner)) as string
            ?? throw new OperationException("resource.notFound", "导入预览不存在");
    }
    internal Task ReleaseSourcesAsync(string operationId, CancellationToken ct = default) => database.WriteAsync(tx =>
    {
        tx.Execute("DELETE FROM platform_asset_pins WHERE pin_id IN (SELECT pin FROM platform_file_export_sources WHERE operation_id=$id)", ("$id", operationId));
        tx.Execute("DELETE FROM platform_file_export_sources WHERE operation_id=$id", ("$id", operationId));
        return 0;
    }, ct);
    internal async Task<FileSaveReceipt> SaveAsync(string owner, string exportId, string selectionId, string? key,
        Func<Task<FileSaveReceipt>> writer, CancellationToken ct)
    {
        Key(key);
        var hash = Hash(new { exportId, selectionId }); var kind = "save:" + exportId;
        await gate.WaitAsync(ct);
        try
        {
            var previous = await database.WriteAsync(tx =>
            {
                if (FindKey<SavedFileWrite>(tx, owner, kind, key!, hash, time.GetUtcNow()) is { } saved) return saved;
                SaveKey(tx, owner, kind, key!, hash, new SavedFileWrite(false, null), time.GetUtcNow());
                return null;
            }, ct);
            if (previous is { Completed: true, Receipt: { } receipt }) return receipt;
            if (previous is not null) throw new OperationException("operation.interrupted", "该保存请求未完成确认，请核对目标文件后重新选择");
            var result = await writer();
            await database.WriteAsync(tx => tx.Execute("UPDATE platform_file_keys SET receipt=$json WHERE owner=$owner AND kind=$kind AND key=$key",
                ("$json", JsonSerializer.Serialize(new SavedFileWrite(true, result), ContractJson.Options)), ("$owner", owner), ("$kind", kind), ("$key", key!)));
            return result;
        }
        finally { gate.Release(); }
    }
    internal async Task<FileExportArtifact> PublishExportAsync(string owner, FileExportPlan plan, Stream content, CancellationToken ct)
    {
        await gate.WaitAsync(ct); AssetSnapshot? asset = null; string? pin = null; var committed = false;
        try
        {
            await CleanupLockedAsync(ct);
            if ((long)database.ReadScalar("SELECT count(*) FROM platform_file_exports")! >= 16)
                throw new OperationException("resource.capacityExceeded", "文件导出资产达到上限");
            asset = await assets.PutAsync("platform-files", "export:" + plan.Id, "application/zip", content, FilePackageReader.MaximumUploadBytes, ct);
            pin = await assets.PinAsync(asset.Id, "file-export:" + plan.Id, ct);
            var result = new FileExportArtifact(plan.Id, owner, plan.Name, asset, time.GetUtcNow() + Lifetime, plan.Categories);
            await database.WriteAsync(tx => tx.Execute("INSERT INTO platform_file_exports VALUES($id,$owner,$json,$pin,$expires,$operation)",
                ("$id", plan.Id), ("$owner", owner), ("$json", JsonSerializer.Serialize(result, ContractJson.Options)),
                ("$pin", pin), ("$expires", result.ExpiresAt.ToUnixTimeMilliseconds()), ("$operation", plan.OperationId)), ct);
            committed = true; return result;
        }
        finally
        {
            try { if (!committed) { if (pin is not null) await assets.UnpinAsync(pin); if (asset is not null) await assets.RemoveAsync(asset.Id); } }
            finally { gate.Release(); }
        }
    }
    public FileExportArtifact Export(string id, string owner, bool admin)
    {
        Id(id);
        var json = database.ReadScalar("SELECT e.snapshot FROM platform_file_exports e JOIN core_operations o ON e.operation_id=o.id WHERE e.id=$id AND ($admin=1 OR e.owner=$owner) AND e.expires>$now AND o.terminal=1 AND json_extract(o.snapshot,'$.state')='succeeded'",
            ("$id", id), ("$owner", owner), ("$admin", admin ? 1 : 0), ("$now", time.GetUtcNow().ToUnixTimeMilliseconds())) as string;
        return json is null ? throw new OperationException("resource.notFound", "文件导出不存在或已到期") : JsonSerializer.Deserialize<FileExportArtifact>(json, ContractJson.Options)!;
    }
    internal async Task DiscardExportAsync(FileExportArtifact value, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var pin = database.ReadScalar("SELECT pin FROM platform_file_exports WHERE id=$id", ("$id", value.Id)) as string;
            if (pin is null) return;
            await assets.UnpinAsync(pin, ct); await assets.RemoveAsync(value.Asset.Id, ct);
            await database.WriteAsync(tx => tx.Execute("DELETE FROM platform_file_exports WHERE id=$id", ("$id", value.Id)), ct);
        }
        finally { gate.Release(); }
    }
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        await database.WriteAsync(tx =>
        {
            tx.Execute("UPDATE platform_file_uploads SET snapshot=json_set(snapshot,'$.state','failed','$.revision',CAST(CAST(json_extract(snapshot,'$.revision') AS INTEGER)+1 AS TEXT)) WHERE json_extract(snapshot,'$.state')='uploading'");
            tx.Execute("DELETE FROM platform_asset_pins WHERE pin_id IN (SELECT pin FROM platform_file_export_sources WHERE operation_id IN (SELECT id FROM core_operations WHERE terminal=1))");
            tx.Execute("DELETE FROM platform_file_export_sources WHERE operation_id IN (SELECT id FROM core_operations WHERE terminal=1)");
            tx.Execute("DELETE FROM platform_asset_pins WHERE owner LIKE 'file-read:%'");
            return 0;
        }, ct);
        var orphanIds = database.Read(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT id FROM platform_assets WHERE module_id='platform-files'
                AND NOT EXISTS(SELECT 1 FROM platform_file_uploads u WHERE json_extract(u.snapshot,'$.asset.id')=platform_assets.id)
                AND NOT EXISTS(SELECT 1 FROM platform_file_exports e WHERE json_extract(e.snapshot,'$.asset.id')=platform_assets.id)
                """;
            using var rows = cmd.ExecuteReader(); var ids = new List<string>(); while (rows.Read()) ids.Add(rows.GetString(0)); return ids;
        });
        foreach (var id in orphanIds) await assets.RemoveAsync(id, ct);
        await CleanupAsync(ct);
    }
    public async Task CleanupAsync(CancellationToken ct = default)
    {
        using var activity = database.Activities.EnterActivity("platform-files", "storage.cleanup");
        await gate.WaitAsync(ct);
        try { await CleanupLockedAsync(ct); }
        finally { gate.Release(); }
    }
    private async Task CleanupLockedAsync(CancellationToken ct)
    {
        var rows = database.Read(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id,snapshot,pin,'upload' FROM platform_file_uploads WHERE expires<=$now UNION ALL SELECT e.id,e.snapshot,e.pin,'export' FROM platform_file_exports e JOIN core_operations o ON e.operation_id=o.id WHERE e.expires<=$now OR (o.terminal=1 AND json_extract(o.snapshot,'$.state')<>'succeeded')";
            cmd.Parameters.AddWithValue("$now", time.GetUtcNow().ToUnixTimeMilliseconds());
            using var reader = cmd.ExecuteReader(); var result = new List<(string Id, string Json, string? Pin, string Kind)>();
            while (reader.Read()) result.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)));
            return result;
        });
        foreach (var row in rows)
        {
            var uploadGate = row.Kind == "upload" ? uploadGates.GetOrAdd(row.Id, _ => new(1, 1)) : null;
            if (uploadGate is not null && !await uploadGate.WaitAsync(0, ct)) continue;
            try
            {
                var asset = row.Kind == "upload" ? JsonSerializer.Deserialize<FileUploadSnapshot>(row.Json, ContractJson.Options)!.Asset
                    : JsonSerializer.Deserialize<FileExportArtifact>(row.Json, ContractJson.Options)!.Asset;
                if (row.Pin is not null) await assets.UnpinAsync(row.Pin, ct);
                if (asset is not null)
                {
                    try { await assets.RemoveAsync(asset.Id, ct); }
                    catch (StorageException error) when (error.Code == "storage.assetInUse") { continue; }
                }
                var table = row.Kind == "upload" ? "platform_file_uploads" : "platform_file_exports";
                await database.WriteAsync(tx => tx.Execute("DELETE FROM " + table + " WHERE id=$id", ("$id", row.Id)), ct);
                if (uploadGate is not null) uploadGates.TryRemove(row.Id, out _);
            }
            finally { uploadGate?.Release(); }
        }
        await database.WriteAsync(tx => tx.Execute("DELETE FROM platform_file_keys WHERE expires<=$now", ("$now", time.GetUtcNow().ToUnixTimeMilliseconds())), ct);
        if (rows.Count > 0) await assets.CollectGarbageAsync(ct);
    }
    internal static T? FindKey<T>(DatabaseTransaction tx, string owner, string kind, string key, string hash, DateTimeOffset now) where T : class
    {
        var saved = tx.Scalar("SELECT receipt FROM platform_file_keys WHERE owner=$owner AND kind=$kind AND key=$key AND expires>$now",
            ("$owner", owner), ("$kind", kind), ("$key", key), ("$now", now.ToUnixTimeMilliseconds())) as string;
        if (saved is null) return null;
        if ((string?)tx.Scalar("SELECT hash FROM platform_file_keys WHERE owner=$owner AND kind=$kind AND key=$key", ("$owner", owner), ("$kind", kind), ("$key", key)) != hash)
            throw new OperationException("idempotency.conflict", "幂等键已用于其他文件内容或条件");
        return JsonSerializer.Deserialize<T>(saved, ContractJson.Options);
    }
    internal static void SaveKey<T>(DatabaseTransaction tx, string owner, string kind, string key, string hash, T receipt, DateTimeOffset now)
    {
        if ((long)tx.Scalar("SELECT count(*) FROM platform_file_keys")! >= 1000 &&
            (long)tx.Scalar("SELECT count(*) FROM platform_file_keys WHERE owner=$owner AND kind=$kind AND key=$key", ("$owner", owner), ("$kind", kind), ("$key", key))! == 0)
            throw new OperationException("resource.capacityExceeded", "文件幂等回执达到上限");
        tx.Execute("INSERT INTO platform_file_keys VALUES($owner,$kind,$key,$hash,$json,$expires) ON CONFLICT(owner,kind,key) DO NOTHING",
            ("$owner", owner), ("$kind", kind), ("$key", key), ("$hash", hash), ("$json", JsonSerializer.Serialize(receipt, ContractJson.Options)),
            ("$expires", (now + TimeSpan.FromDays(1)).ToUnixTimeMilliseconds()));
    }
}

internal sealed class AuthorizedFileInput(Stream input, Func<bool> authorized) : Stream
{
    public override bool CanRead => input.CanRead;
    public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    private void Check() { if (!authorized()) throw new OperationException("permission.denied", "文件传输授权已结束"); }
    public override int Read(byte[] buffer, int offset, int count) { Check(); return input.Read(buffer, offset, count); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { Check(); return input.ReadAsync(buffer, ct); }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

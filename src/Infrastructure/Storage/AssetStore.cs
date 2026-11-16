using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using Microsoft.Data.Sqlite;

namespace MabiTool.Infrastructure;

internal enum AssetCommitPhase { Journaled, BlobPublished }

public sealed partial class AssetStore : IAsyncDisposable
{
    private sealed record PendingCommit(string CommitId, string AssetId, string ModuleId, string Scope,
        string MediaType, string Sha256, long SizeBytes, string CreatedAt);

    public static IReadOnlyList<DatabaseMigration> Migrations { get; } =
    [new DatabaseMigration("platform-assets", 1, """
        CREATE TABLE platform_asset_blobs (
            sha256 TEXT PRIMARY KEY,
            size_bytes INTEGER NOT NULL CHECK(size_bytes >= 0),
            created_at TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('ready','deleting'))
        ) STRICT;
        CREATE TABLE platform_assets (
            id TEXT PRIMARY KEY,
            module_id TEXT NOT NULL,
            scope TEXT NOT NULL,
            media_type TEXT NOT NULL,
            sha256 TEXT NOT NULL REFERENCES platform_asset_blobs(sha256),
            size_bytes INTEGER NOT NULL CHECK(size_bytes >= 0),
            revision TEXT NOT NULL,
            created_at TEXT NOT NULL
        ) STRICT;
        CREATE INDEX platform_assets_scope ON platform_assets(module_id, scope, id);
        CREATE TABLE platform_asset_pins (
            pin_id TEXT PRIMARY KEY,
            asset_id TEXT NOT NULL REFERENCES platform_assets(id) ON DELETE CASCADE,
            owner TEXT NOT NULL,
            created_at TEXT NOT NULL
        ) STRICT;
        CREATE INDEX platform_asset_pins_asset ON platform_asset_pins(asset_id);
        CREATE TABLE platform_asset_commits (
            commit_id TEXT PRIMARY KEY,
            asset_id TEXT NOT NULL,
            module_id TEXT NOT NULL,
            scope TEXT NOT NULL,
            media_type TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            size_bytes INTEGER NOT NULL CHECK(size_bytes >= 0),
            created_at TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('pending','committed','failed'))
        ) STRICT;
        CREATE INDEX platform_asset_commits_pending ON platform_asset_commits(state, created_at);
        """), new DatabaseMigration("platform-assets", 2, """
        CREATE TABLE platform_asset_backup_pins (
            backup_id TEXT NOT NULL,
            sha256 TEXT NOT NULL REFERENCES platform_asset_blobs(sha256),
            runtime_id TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY(backup_id,sha256)
        ) STRICT;
        CREATE INDEX platform_asset_backup_pins_hash ON platform_asset_backup_pins(sha256);
        """), new DatabaseMigration("platform-assets", 3, """
        CREATE TABLE platform_asset_references (
            owner_module TEXT NOT NULL, owner_resource TEXT NOT NULL,
            asset_id TEXT NOT NULL REFERENCES platform_assets(id),
            PRIMARY KEY(owner_module,owner_resource,asset_id)
        ) STRICT;
        CREATE INDEX platform_asset_references_asset ON platform_asset_references(asset_id);
        """)];

    private readonly SqliteStore database;
    private readonly PathService paths;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly Func<AssetCommitPhase, Task>? phaseHook;
    private bool disposed;

    public AssetStore(SqliteStore database, PathService paths, TimeProvider? timeProvider = null)
        : this(database, paths, timeProvider, null) { }

    internal AssetStore(SqliteStore database, PathService paths, TimeProvider? timeProvider,
        Func<AssetCommitPhase, Task>? phaseHook)
    {
        this.database = database;
        this.paths = paths;
        time = timeProvider ?? TimeProvider.System;
        this.phaseHook = phaseHook;
    }

    public async Task<AssetSnapshot> PutAsync(string moduleId, string scope, string mediaType, Stream content,
        long maximumBytes = 1_073_741_824, CancellationToken cancellationToken = default)
    {
        ValidateMetadata(moduleId, scope, mediaType);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead) throw new StorageException("storage.assetInvalid", "资产流不可读");
        if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var activity = database.Activities.EnterActivity("assets", "storage.assetMutation");
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var stagingDirectory = paths.Resolve("temp", "assets");
            Directory.CreateDirectory(stagingDirectory);
            var stagedPath = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N") + ".part");
            await using var temporary = await database.TemporaryFiles.TrackAsync(moduleId, stagedPath, cancellationToken);
            var expected = content.CanSeek ? Math.Min(maximumBytes, Math.Max(0, content.Length - content.Position)) : maximumBytes;
            using var space = database.Space.Reserve("asset:" + moduleId, new VolumeSpaceRequirement(stagingDirectory, checked(expected + 65536)));
            try
            {
                var (hash, length) = await StageAsync(content, stagedPath, maximumBytes, cancellationToken).ConfigureAwait(false);
                var createdAt = time.GetUtcNow().ToString("O");
                var commit = new PendingCommit(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                    moduleId, scope, mediaType, hash, length, createdAt);
                await database.WriteAsync(tx => tx.Execute("""
                    INSERT INTO platform_asset_commits(commit_id,asset_id,module_id,scope,media_type,sha256,size_bytes,created_at,state)
                    VALUES($commit,$asset,$module,$scope,$media,$hash,$size,$created,'pending')
                    """, ("$commit", commit.CommitId), ("$asset", commit.AssetId), ("$module", moduleId),
                    ("$scope", scope), ("$media", mediaType), ("$hash", hash), ("$size", length), ("$created", createdAt)),
                    cancellationToken).ConfigureAwait(false);
                if (phaseHook is not null) await phaseHook(AssetCommitPhase.Journaled).ConfigureAwait(false);

                var blobPath = BlobPath(hash);
                Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
                if (File.Exists(blobPath))
                {
                    if (await HashFileAsync(blobPath, cancellationToken).ConfigureAwait(false) != hash)
                        throw new StorageException("storage.assetDamaged", "已存在的内容寻址资产校验失败");
                    File.Delete(stagedPath);
                }
                else File.Move(stagedPath, blobPath);
                if (phaseHook is not null) await phaseHook(AssetCommitPhase.BlobPublished).ConfigureAwait(false);
                await FinalizeCommitAsync(commit, cancellationToken).ConfigureAwait(false);
                return new(commit.AssetId, moduleId, scope, mediaType, hash, new((ulong)length), new(1));
            }
            finally
            {
                if (File.Exists(stagedPath)) File.Delete(stagedPath);
            }
        }
        finally { mutationGate.Release(); }
    }

    public AssetSnapshot? Get(string assetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        var row = Metadata(assetId);
        if (row is not null)
        {
            var path = BlobPath(row.Sha256);
            if (!File.Exists(path) || HashFileAsync(path, CancellationToken.None).GetAwaiter().GetResult() != row.Sha256)
                throw new StorageException("storage.assetDamaged", "资产索引对应的内容缺失或校验失败");
        }
        return row;
    }

    private AssetSnapshot? Metadata(string assetId) => database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,module_id,scope,media_type,sha256,size_bytes,revision FROM platform_assets WHERE id=$id";
            command.Parameters.AddWithValue("$id", assetId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? new AssetSnapshot(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), new((ulong)reader.GetInt64(5)),
                new(ulong.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture))) : null;
        });
    public AssetSnapshot? GetMetadata(string assetId) => Metadata(assetId);

    /// <summary>调用方验证业务归属；固定文件句柄与 pin 覆盖整个流式读取。</summary>
    public async Task<AssetReadLease> OpenReadAsync(string assetId, string owner, long maximumBytes, CancellationToken ct = default)
    {
        if (owner.Length is < 1 or > 128 || maximumBytes < 0) throw new StorageException("storage.assetInvalid", "资产读取预算或保护身份无效");
        var activity = database.Activities.EnterActivity("assets", "storage.assetRead");
        var acquired = false; FileStream? stream = null; string? pin = null;
        try
        {
            await mutationGate.WaitAsync(ct); acquired = true;
            var asset = Metadata(assetId) ?? throw new StorageException("storage.assetMissing", "资产不存在");
            if (asset.SizeBytes.Value > (ulong)maximumBytes) throw new StorageException("storage.quotaExceeded", "资产超过流式读取预算");
            stream = new(BlobPath(asset.Sha256), FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != (long)asset.SizeBytes.Value ||
                Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) != asset.Sha256)
                throw new StorageException("storage.assetDamaged", "资产流不匹配固定长度或哈希");
            stream.Position = 0; pin = Guid.NewGuid().ToString("N");
            await database.WriteAsync(tx =>
            {
                if ((long)tx.Scalar("SELECT count(*) FROM platform_assets a JOIN platform_asset_blobs b ON a.sha256=b.sha256 WHERE a.id=$id AND b.state='ready'", ("$id", assetId))! != 1)
                    throw new StorageException("storage.assetMissing", "资产正在回收或已被删除");
                tx.Execute("INSERT INTO platform_asset_pins VALUES($pin,$asset,$owner,$at)", ("$pin", pin), ("$asset", assetId),
                    ("$owner", owner), ("$at", time.GetUtcNow().ToString("O")));
                return 0;
            }, ct);
            var fixedPin = pin;
            return new(asset, stream, async () => { await UnpinAsync(fixedPin); }, activity);
        }
        catch (Exception error)
        {
            try { if (stream is not null) await stream.DisposeAsync(); }
            finally { activity.Dispose(); }
            if (error is IOException or UnauthorizedAccessException)
                throw new StorageException("storage.assetDamaged", "资产文件不可读或缺失");
            throw;
        }
        finally { if (acquired) mutationGate.Release(); }
    }

    public AssetSnapshot? Find(string moduleId, string scope)
    {
        var id = database.ReadScalar("SELECT id FROM platform_assets WHERE module_id=$module AND scope=$scope ORDER BY created_at,id LIMIT 1",
            ("$module", moduleId), ("$scope", scope)) as string;
        return id is null ? null : Get(id);
    }

    /// <summary>按创建先后列出模块内某作用域前缀的资产元数据，不读取内容；供模块执行自身的缓存上限。</summary>
    public IReadOnlyList<AssetSnapshot> ListByScopePrefix(string moduleId, string prefix) => database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,module_id,scope,media_type,sha256,size_bytes,revision FROM platform_assets
            WHERE module_id=$module AND substr(scope,1,length($prefix))=$prefix ORDER BY created_at,id
            """;
        command.Parameters.AddWithValue("$module", moduleId);
        command.Parameters.AddWithValue("$prefix", prefix);
        using var reader = command.ExecuteReader();
        var rows = new List<AssetSnapshot>();
        while (reader.Read())
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                new((ulong)reader.GetInt64(5)), new(ulong.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture))));
        return (IReadOnlyList<AssetSnapshot>)rows;
    });

    /// <summary>调用方先校验业务归属；在同一资产锁内读取并验证固定文件内容。</summary>
    public async Task<byte[]> ReadRangeAsync(string assetId, int offset, int count, int maximumBytes,
        CancellationToken ct = default)
    {
        if (offset < 0 || count is < 1 or > 262144 || maximumBytes < 0)
            throw new StorageException("storage.assetInvalid", "资产读取范围无效");
        using var activity = database.Activities.EnterActivity("assets", "storage.assetRead");
        await mutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var size = database.ReadScalar("SELECT size_bytes FROM platform_assets WHERE id=$id", ("$id", assetId));
            if (size is null) throw new StorageException("storage.assetMissing", "资产不存在");
            if ((long)size > maximumBytes || offset > (long)size)
                throw new StorageException("storage.assetInvalid", "资产超过读取预算或范围越界");
            var asset = Get(assetId)!;
            var path = BlobPath(asset.Sha256);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != (long)size || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)) != asset.Sha256)
                throw new StorageException("storage.assetDamaged", "资产内容校验失败");
            stream.Position = offset;
            var bytes = new byte[(int)Math.Min(count, (long)size - offset)];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            return bytes;
        }
        finally { mutationGate.Release(); }
    }

    public async Task<string> PinAsync(string assetId, string owner, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (owner.Length > 128) throw new StorageException("storage.assetInvalid", "活动资产引用所有者无效");
        var pinId = Guid.NewGuid().ToString("N");
        using var activity = database.Activities.EnterActivity("assets", "storage.assetMutation");
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow().ToString("O");
            var inserted = await database.WriteAsync(tx =>
            {
                var ready = tx.Scalar("SELECT count(*) FROM platform_assets a JOIN platform_asset_blobs b ON b.sha256=a.sha256 WHERE a.id=$id AND b.state='ready'",
                    ("$id", assetId));
                if ((long)ready! == 0) throw new StorageException("storage.assetMissing", "待保护的资产不存在或不可用");
                return tx.Execute("INSERT INTO platform_asset_pins VALUES($pin,$asset,$owner,$created)",
                    ("$pin", pinId), ("$asset", assetId), ("$owner", owner), ("$created", now));
            }, cancellationToken).ConfigureAwait(false);
            return inserted == 1 ? pinId : throw new StorageException("storage.assetInvalid", "资产引用创建失败");
        }
        finally { mutationGate.Release(); }
    }

    public async Task<bool> UnpinAsync(string pinId, CancellationToken cancellationToken = default)
    {
        using var activity = database.Activities.EnterActivity("assets", "storage.assetMutation");
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await database.WriteAsync(tx => tx.Execute("DELETE FROM platform_asset_pins WHERE pin_id=$id", ("$id", pinId)) == 1,
                cancellationToken).ConfigureAwait(false);
        }
        finally { mutationGate.Release(); }
    }

    public async Task<bool> RemoveAsync(string assetId, CancellationToken cancellationToken = default)
    {
        using var activity = database.Activities.EnterActivity("assets", "storage.assetMutation");
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await database.WriteAsync(tx =>
            {
                if ((long)tx.Scalar("SELECT count(*) FROM platform_asset_pins WHERE asset_id=$id", ("$id", assetId))! > 0)
                    throw new StorageException("storage.assetInUse", "资产仍被活动任务引用");
                if ((long)tx.Scalar("SELECT count(*) FROM platform_asset_references WHERE asset_id=$id", ("$id", assetId))! > 0)
                    throw new StorageException("storage.assetInUse", "资产仍被模块业务引用");
                return tx.Execute("DELETE FROM platform_assets WHERE id=$id", ("$id", assetId)) == 1;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { mutationGate.Release(); }
    }

    public async Task<int> CollectGarbageAsync(CancellationToken cancellationToken = default)
    {
        using var activity = database.Activities.EnterActivity("assets", "storage.assetMutation");
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CollectGarbageLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { mutationGate.Release(); }
    }

    private async Task<int> CollectGarbageLockedAsync(CancellationToken cancellationToken)
    {
            var eligible = database.Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT b.sha256 FROM platform_asset_blobs b
                    WHERE NOT EXISTS(SELECT 1 FROM platform_assets a WHERE a.sha256=b.sha256)
                      AND NOT EXISTS(SELECT 1 FROM platform_asset_backup_pins p WHERE p.sha256=b.sha256)
                      AND b.state IN ('ready','deleting') ORDER BY b.sha256
                    """;
                using var reader = command.ExecuteReader();
                var hashes = new List<string>();
                while (reader.Read()) hashes.Add(reader.GetString(0));
                return hashes;
            });
            var removed = 0;
            foreach (var hash in eligible)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var marked = await database.WriteAsync(tx => tx.Execute("""
                    UPDATE platform_asset_blobs SET state='deleting' WHERE sha256=$hash
                      AND NOT EXISTS(SELECT 1 FROM platform_assets WHERE sha256=$hash)
                      AND NOT EXISTS(SELECT 1 FROM platform_asset_backup_pins WHERE sha256=$hash)
                    """, ("$hash", hash)) == 1, cancellationToken).ConfigureAwait(false);
                if (!marked) continue;
                var path = BlobPath(hash);
                if (File.Exists(path)) File.Delete(path);
                removed += await database.WriteAsync(tx => tx.Execute("""
                    DELETE FROM platform_asset_blobs WHERE sha256=$hash AND state='deleting'
                      AND NOT EXISTS(SELECT 1 FROM platform_assets WHERE sha256=$hash)
                      AND NOT EXISTS(SELECT 1 FROM platform_asset_backup_pins WHERE sha256=$hash)
                    """, ("$hash", hash)), cancellationToken).ConfigureAwait(false);
            }
            return removed;
    }

    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        using var activity = database.Activities.EnterActivity("assets", "storage.assetMutation");
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await database.WriteAsync(tx => tx.Execute("DELETE FROM platform_asset_backup_pins WHERE runtime_id<>$runtime",
                ("$runtime", database.RuntimeId)), cancellationToken).ConfigureAwait(false);
            var pending = database.Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT commit_id,asset_id,module_id,scope,media_type,sha256,size_bytes,created_at FROM platform_asset_commits WHERE state='pending' ORDER BY created_at";
                using var reader = command.ExecuteReader();
                var rows = new List<PendingCommit>();
                while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetString(7)));
                return rows;
            });
            foreach (var commit in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = BlobPath(commit.Sha256);
                if (File.Exists(path) && await HashFileAsync(path, cancellationToken).ConfigureAwait(false) == commit.Sha256)
                    await FinalizeCommitAsync(commit, cancellationToken).ConfigureAwait(false);
                else
                    await database.WriteAsync(tx => tx.Execute("UPDATE platform_asset_commits SET state='failed' WHERE commit_id=$id AND state='pending'",
                        ("$id", commit.CommitId)), cancellationToken).ConfigureAwait(false);
            }
            await CollectGarbageLockedAsync(cancellationToken).ConfigureAwait(false);
            CleanupStaging();
        }
        finally { mutationGate.Release(); }
    }

    private Task FinalizeCommitAsync(PendingCommit commit, CancellationToken cancellationToken) => database.WriteAsync(tx =>
    {
        var existingSize = tx.Scalar("SELECT size_bytes FROM platform_asset_blobs WHERE sha256=$hash", ("$hash", commit.Sha256));
        if (existingSize is not null && (long)existingSize != commit.SizeBytes)
            throw new StorageException("storage.assetDamaged", "同一内容哈希对应不同资产长度");
        if (existingSize is null)
            tx.Execute("INSERT INTO platform_asset_blobs VALUES($hash,$size,$created,'ready')",
                ("$hash", commit.Sha256), ("$size", commit.SizeBytes), ("$created", commit.CreatedAt));
        else if ((string?)tx.Scalar("SELECT state FROM platform_asset_blobs WHERE sha256=$hash", ("$hash", commit.Sha256)) != "ready")
            throw new StorageException("storage.assetInUse", "资产内容正在回收");
        tx.Execute("INSERT INTO platform_assets VALUES($id,$module,$scope,$media,$hash,$size,'1',$created)",
            ("$id", commit.AssetId), ("$module", commit.ModuleId), ("$scope", commit.Scope), ("$media", commit.MediaType),
            ("$hash", commit.Sha256), ("$size", commit.SizeBytes), ("$created", commit.CreatedAt));
        tx.Execute("UPDATE platform_asset_commits SET state='committed' WHERE commit_id=$id AND state='pending'", ("$id", commit.CommitId));
        return true;
    }, cancellationToken);

    private string BlobPath(string hash) => paths.Resolve("assets", "sha256", hash[..2], hash);

    private void CleanupStaging()
    {
        var directory = paths.Resolve("temp", "assets");
        if (Directory.Exists(directory))
            foreach (var file in Directory.EnumerateFiles(directory, "*.part")) File.Delete(file);
    }

    private static async Task<(string Hash, long Length)> StageAsync(Stream input, string path, long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        var buffer = new byte[81920];
        long length = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            length = checked(length + read);
            if (length > maximumBytes) throw new StorageException("storage.quotaExceeded", "资产超过单文件大小上限");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(true);
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void ValidateMetadata(string moduleId, string scope, string mediaType)
    {
        if (!Regex.IsMatch(moduleId ?? "", "\\A[a-z][a-z0-9-]{0,63}\\z") || string.IsNullOrWhiteSpace(scope) || scope.Length > 256 ||
            string.IsNullOrWhiteSpace(mediaType) || mediaType.Length > 128 || mediaType.Any(char.IsControl))
            throw new StorageException("storage.assetInvalid", "资产模块、范围或媒体类型无效");
    }

    public async ValueTask DisposeAsync()
    {
        await mutationGate.WaitAsync().ConfigureAwait(false);
        try { disposed = true; }
        finally { mutationGate.Release(); mutationGate.Dispose(); }
    }
}

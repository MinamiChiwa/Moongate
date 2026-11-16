using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Updates;

namespace MabiTool.Infrastructure;

public sealed partial class DataRootLease
{
    internal string? PendingInitialDatabaseId { get; private set; }
    internal void ClearPendingInitialDatabaseId() => PendingInitialDatabaseId = null;

    [SupportedOSPlatform("windows")]
    internal static DataRootLease OpenForInitialInstall(InitialInstallAuthorization authorization)
    {
        var record = authorization.Record;
        var current = InitialInstallRecord.ReadAsync(record.Request.InstallationRoot, record.InputSha256).GetAwaiter().GetResult();
        if (!InitialInstallFiles.Same(record, current) || record.Step is < 2 or >= 6) throw InitialInstallFiles.Invalid();
        var request = record.Request;
        EnsurePrivateDirectory(request.BootstrapDirectory, true);
        using (var bootstrapOwnership = Lock(UpdatePaths.Local(request.BootstrapDirectory, "bootstrap.lock"), "storage.bootstrapBusy"))
        {
            var referencePath = UpdatePaths.Local(request.BootstrapDirectory, InitialInstallReference.FileName);
            var reference = InitialInstallReference.For(record);
            var bootstrapConfiguration = new BootstrapConfiguration(1, "MabiTool", request.DataRoot, record.RootId, false);
            var referenceBytes = JsonSerializer.SerializeToUtf8Bytes(reference, ContractJson.Options);
            var bootstrapBytes = JsonSerializer.SerializeToUtf8Bytes(bootstrapConfiguration, ContractJson.Options);
            if (File.Exists(referencePath))
            {
                if (Read<InitialInstallReference>(referencePath) != reference) throw InitialInstallFiles.Invalid();
            }
            else
            {
                if (record.Step >= 3) throw new UpdatePlanException("update.initialInstallNotEmpty");
                RequireInitialMetadataPrelude(request.BootstrapDirectory, "bootstrap.lock", [referenceBytes]);
                Publish(referencePath, reference, false);
            }
            foreach (var entry in Directory.EnumerateFileSystemEntries(request.BootstrapDirectory))
            {
                var name = Path.GetFileName(entry);
                if (name is "bootstrap.lock" or "data-root.json" or InitialInstallReference.FileName) continue;
                if (!InitialInstallMetadata.IsAnonymousTemporary(name)) throw new UpdatePlanException("update.initialInstallNotEmpty");
                InitialInstallMetadata.RequirePrefix(entry, [referenceBytes, bootstrapBytes], maximumBytes: 16384);
            }
            EnsurePrivateDirectory(request.DataRoot, true);
            using var rootOwnership = Lock(UpdatePaths.Local(request.DataRoot, "writer.lock"), "storage.writerBusy");
            var markerPath = UpdatePaths.Local(request.DataRoot, MarkerName);
            DataRootIdentity marker;
            if (File.Exists(markerPath))
            {
                marker = Read<DataRootIdentity>(markerPath);
                if (marker.Version != 2 || marker.Product != "MabiTool" || marker.RootId != record.RootId ||
                    marker.DatabaseId is not null && marker.DatabaseId != record.DatabaseId || marker.State != "active" ||
                    marker.AuthorityBootstrap != request.BootstrapDirectory) throw new StorageException("storage.identityMismatch", "初装数据身份已改变");
            }
            else
            {
                if (record.Step >= 3) throw new UpdatePlanException("update.initialInstallNotEmpty");
                marker = new(2, "MabiTool", record.RootId, AuthorityBootstrap: request.BootstrapDirectory);
                RequireInitialMetadataPrelude(request.DataRoot, "writer.lock", [JsonSerializer.SerializeToUtf8Bytes(marker, ContractJson.Options)]);
                Publish(markerPath, marker, false);
            }
            var configurationPath = UpdatePaths.Local(request.BootstrapDirectory, "data-root.json");
            if (File.Exists(configurationPath))
            {
                if (Read<BootstrapConfiguration>(configurationPath) != bootstrapConfiguration)
                    throw new StorageException("storage.identityMismatch", "初装引导数据身份已改变");
            }
            else if (record.Step >= 3) throw new StorageException("storage.notInitialized", "初装数据引导记录丢失");
            var expectedGate = new UpdateInstallGate(SchemaVersion: 1, TransactionId: record.InstallationId,
                IntentSha256: record.InputSha256, InstallationRoot: request.InstallationRoot, RootId: record.RootId);
            EnsurePrivateDirectory(UpdatePaths.Local(request.DataRoot, "recovery"), true);
            var actualGate = UpdateInstallGate.Read(request.DataRoot);
            if (actualGate is null) expectedGate.Publish(new PathService(request.DataRoot, record.RootId, request.BootstrapDirectory, false));
            else if (actualGate != expectedGate) throw InitialInstallFiles.Invalid();
            var allowed = new HashSet<string>([MarkerName, "writer.lock", UpdateInstallGate.RelativePath,
                "store/database.lock", "store/main.sqlite", "store/main.sqlite-wal", "store/main.sqlite-shm", "store/main.sqlite-journal"], StringComparer.Ordinal);
            foreach (var (relative, path) in UpdatePaths.EnumerateFiles(request.DataRoot, CancellationToken.None))
            {
                if (InitialInstallMetadata.IsAnonymousTemporary(relative))
                    InitialInstallMetadata.RequirePrefix(path, [JsonSerializer.SerializeToUtf8Bytes(marker with { DatabaseId = null }, ContractJson.Options),
                        JsonSerializer.SerializeToUtf8Bytes(marker with { DatabaseId = record.DatabaseId }, ContractJson.Options)], maximumBytes: 16384);
                else if (InitialInstallMetadata.IsTemporary(relative, UpdateInstallGate.RelativePath))
                    InitialInstallMetadata.RequirePrefix(path, [JsonSerializer.SerializeToUtf8Bytes(expectedGate, UpdateHandoffProtocol.Json)], maximumBytes: 16384);
                else if (record.Step < 3 && !allowed.Contains(relative)) throw new UpdatePlanException("update.initialInstallNotEmpty");
            }
        }
        var lease = OpenForUpdate(request.Storage with { Initialize = true }, record.InstallationId, record.InputSha256);
        try
        {
            if (lease.Paths.RootId != record.RootId || lease.DatabaseId is not null && lease.DatabaseId != record.DatabaseId) throw InitialInstallFiles.Invalid();
            lease.PendingInitialDatabaseId = lease.DatabaseId is null ? record.DatabaseId : null;
            // 数据库提交后标记尚未刷盘时，仍按日志固定的数据库身份打开并复核已有库。
            if (lease.DatabaseId is null && File.Exists(lease.Paths.Database) && new FileInfo(lease.Paths.Database).Length > 0)
                lease.DatabaseId = record.DatabaseId;
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static void RequireInitialMetadataPrelude(string directory, string lockName, IReadOnlyList<byte[]> documents)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var name = Path.GetFileName(path);
            if (name == lockName) continue;
            if (!InitialInstallMetadata.IsAnonymousTemporary(name)) throw new UpdatePlanException("update.initialInstallNotEmpty");
            InitialInstallMetadata.RequirePrefix(path, documents, maximumBytes: 16384);
        }
    }
}

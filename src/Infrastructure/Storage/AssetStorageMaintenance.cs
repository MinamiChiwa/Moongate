using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

public sealed partial class AssetStore : IStorageMaintenanceParticipant
{
    string IStorageMaintenanceParticipant.Id => "assets";
    IReadOnlyList<string> IStorageMaintenanceParticipant.VolumeRoots => [paths.Assets];
    public Task<IReadOnlyList<StorageManagedItem>> InspectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT b.sha256,b.size_bytes,b.created_at,b.state,
                  (SELECT count(*) FROM platform_assets a WHERE a.sha256=b.sha256),
                  (SELECT count(*) FROM platform_asset_commits c WHERE c.sha256=b.sha256 AND c.state='pending'),
                  (SELECT group_concat(DISTINCT a.module_id) FROM platform_assets a WHERE a.sha256=b.sha256),
                  (SELECT count(*) FROM platform_asset_backup_pins p WHERE p.sha256=b.sha256)
                FROM platform_asset_blobs b ORDER BY b.sha256 LIMIT 100001
                """;
            using var rows = command.ExecuteReader(); var result = new List<StorageManagedItem>();
            while (rows.Read())
            {
                if (result.Count >= 100000) throw new StorageException("resource.capacityExceeded", "资产占用索引超过巡检预算");
                var at = DateTimeOffset.Parse(rows.GetString(2), System.Globalization.CultureInfo.InvariantCulture);
                var hasReferences = rows.GetInt64(4) > 0; var pending = rows.GetInt64(5) > 0;
                var modules = rows.IsDBNull(6) ? "platform" : rows.GetString(6);
                var protection = hasReferences ? "businessReference" : rows.GetInt64(7) > 0 ? "backupReference" : pending ? "pendingCommit" :
                    time.GetUtcNow() - at < TimeSpan.FromHours(24) ? "orphanRetention" : null;
                var hash = rows.GetString(0); var bytes = rows.GetInt64(1);
                result.Add(new(hash, hasReferences ? "user" : "orphans", modules.Contains(',') ? "shared" : modules,
                    bytes, hash + ":" + bytes + ":" + rows.GetString(3), at, protection is null,
                    "unreferencedBlob", protection is null ? [] : [protection], hash));
            }
            return (IReadOnlyList<StorageManagedItem>)result;
        }));
    }

    public async Task<StorageDeletion> DeleteAsync(StorageManagedItem expected, ActivityBarrier.MaintenanceLease maintenance,
        CancellationToken ct = default)
    {
        using var activity = database.Activities.EnterMaintenanceActivity(maintenance, "storage.assetCleanup");
        await mutationGate.WaitAsync(ct);
        try
        {
            var current = (await InspectAsync(ct)).SingleOrDefault(x => x.Key == expected.Key);
            if (current is null) return new(false, 0, "missing");
            if (current.Revision != expected.Revision) return new(false, 0, "changed");
            if (!current.Eligible || current.ProtectionReasons.Count > 0) return new(false, 0, current.ProtectionReasons.FirstOrDefault() ?? "retentionProtected");
            var claimed = await database.WriteMaintenanceAsync(maintenance, tx => tx.Execute("""
                UPDATE platform_asset_blobs SET state='deleting' WHERE sha256=$hash AND size_bytes=$size
                  AND NOT EXISTS(SELECT 1 FROM platform_assets WHERE sha256=$hash)
                  AND NOT EXISTS(SELECT 1 FROM platform_asset_commits WHERE sha256=$hash AND state='pending')
                  AND NOT EXISTS(SELECT 1 FROM platform_asset_backup_pins WHERE sha256=$hash)
                """, ("$hash", expected.Key), ("$size", expected.Bytes)) == 1, ct);
            if (!claimed) return new(false, 0, "businessReference");
            var path = BlobPath(expected.Key);
            StorageDeletion result;
            try
            {
                result = await StorageFileDeletion.DeleteAsync(path, expected.Bytes,
                    StorageFileDeletion.FileRevision(path, expected.Bytes), expected.ContentHash!, ct);
            }
            catch
            {
                await database.WriteMaintenanceAsync(maintenance, tx => tx.Execute(
                    "UPDATE platform_asset_blobs SET state='ready' WHERE sha256=$hash AND state='deleting'", ("$hash", expected.Key)));
                throw;
            }
            await database.WriteMaintenanceAsync(maintenance, tx => result.Deleted || result.Reason == "missing"
                ? tx.Execute("DELETE FROM platform_asset_blobs WHERE sha256=$hash AND state='deleting'", ("$hash", expected.Key))
                : tx.Execute("UPDATE platform_asset_blobs SET state='ready' WHERE sha256=$hash AND state='deleting'", ("$hash", expected.Key)));
            return result;
        }
        finally { mutationGate.Release(); }
    }
}

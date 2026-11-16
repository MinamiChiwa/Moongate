using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed partial class AssetStore
{
    public const int MaximumModuleReferences = 1000;
    public async Task AddModuleReferenceAsync(string moduleId, string resourceId, string assetId, CancellationToken ct = default)
    {
        ReferenceIdentity(moduleId, resourceId);
        using var activity = database.Activities.EnterActivity(moduleId, "storage.assetReference");
        await database.WriteAsync(tx =>
        {
            if (tx.Scalar("SELECT 1 FROM platform_assets WHERE id=$id", ("$id", assetId)) is null)
                throw new StorageException("storage.assetInvalid", "引用的资产不存在");
            if (tx.Scalar("SELECT 1 FROM platform_asset_references WHERE owner_module=$module AND owner_resource=$resource AND asset_id=$id",
                ("$module", moduleId), ("$resource", resourceId), ("$id", assetId)) is not null) return 0;
            if ((long)tx.Scalar("SELECT count(*) FROM platform_asset_references")! >= 100000)
                throw new StorageException("resource.capacityExceeded", "模块资产引用达到上限");
            return tx.Execute("INSERT INTO platform_asset_references VALUES($module,$resource,$id)",
                ("$module", moduleId), ("$resource", resourceId), ("$id", assetId));
        }, ct);
    }

    public Task RemoveModuleReferenceAsync(string moduleId, string resourceId, string assetId, CancellationToken ct = default)
    {
        ReferenceIdentity(moduleId, resourceId);
        return database.WriteAsync(tx => tx.Execute("DELETE FROM platform_asset_references WHERE owner_module=$module AND owner_resource=$resource AND asset_id=$id",
            ("$module", moduleId), ("$resource", resourceId), ("$id", assetId)), ct);
    }

    public (ulong Assets, ulong Bytes, IReadOnlyList<StorageModuleReference> References, bool ActivePins) ModuleData(string moduleId) => database.Read(connection =>
    {
        using var command = connection.CreateCommand(); command.Parameters.AddWithValue("$module", moduleId);
        command.CommandText = "SELECT count(*),coalesce(sum(size_bytes),0) FROM platform_assets WHERE module_id=$module";
        ulong count, bytes; using (var row = command.ExecuteReader()) { row.Read(); count = (ulong)row.GetInt64(0); bytes = (ulong)row.GetInt64(1); }
        command.CommandText = "SELECT r.owner_module,r.owner_resource,r.asset_id FROM platform_asset_references r JOIN platform_assets a ON a.id=r.asset_id WHERE a.module_id=$module AND r.owner_module<>$module ORDER BY r.owner_module,r.owner_resource,r.asset_id LIMIT 1001";
        var references = new List<StorageModuleReference>();
        using (var rows = command.ExecuteReader()) while (rows.Read()) references.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2)));
        if (references.Count > MaximumModuleReferences) throw new StorageException("resource.capacityExceeded", "跨模块引用超过单次预览 1000 条预算");
        command.CommandText = "SELECT count(*) FROM platform_asset_pins p JOIN platform_assets a ON a.id=p.asset_id WHERE a.module_id=$module";
        return (count, bytes, (IReadOnlyList<StorageModuleReference>)references, (long)command.ExecuteScalar()! > 0);
    });

    public void ApplyModuleDataDeletion(DatabaseTransaction tx, string moduleId)
    {
        if ((long)tx.Scalar("SELECT count(*) FROM platform_asset_references r JOIN platform_assets a ON a.id=r.asset_id WHERE a.module_id=$module AND r.owner_module<>$module", ("$module", moduleId))! > 0 ||
            (long)tx.Scalar("SELECT count(*) FROM platform_asset_pins p JOIN platform_assets a ON a.id=p.asset_id WHERE a.module_id=$module", ("$module", moduleId))! > 0 ||
            (long)tx.Scalar("SELECT count(*) FROM platform_asset_commits WHERE module_id=$module AND state='pending'", ("$module", moduleId))! > 0)
            throw new StorageException("storage.assetInUse", "模块资产仍被其他业务、活动或未完成提交引用");
        tx.Execute("DELETE FROM platform_asset_references WHERE owner_module=$module", ("$module", moduleId));
        tx.Execute("DELETE FROM platform_assets WHERE module_id=$module", ("$module", moduleId));
        tx.Execute("DELETE FROM platform_asset_commits WHERE module_id=$module AND state<>'pending'", ("$module", moduleId));
    }

    private static void ReferenceIdentity(string moduleId, string resourceId)
    {
        if (!Regex.IsMatch(moduleId, "^[a-z][a-z0-9-]{0,63}$") || string.IsNullOrWhiteSpace(resourceId) || resourceId.Length > 128 || resourceId.Any(char.IsControl))
            throw new StorageException("storage.assetInvalid", "模块引用身份无效");
    }
}

namespace MabiTool.Infrastructure;

public sealed partial class SqliteStore
{
    /// <summary>平台与给定模块迁移的完整目标历史（按模块与版本排序），供备份预检与健康检查比较。</summary>
    public static IReadOnlyList<DatabaseMigrationStamp> MigrationTargets(IReadOnlyList<DatabaseMigration> moduleMigrations)
    {
        var migrations = new[] { CoreMigrations.Initial, CoreMigrations.Operations, CoreMigrations.ProjectionMetadata, CoreMigrations.BackendMode }
            .Concat(moduleMigrations).ToArray();
        _ = MigrationCatalog(migrations);
        return Array.AsReadOnly(migrations.OrderBy(item => item.ModuleId, StringComparer.Ordinal).ThenBy(item => item.Version)
            .Select(item => new DatabaseMigrationStamp(item.ModuleId, item.Version, item.Checksum)).ToArray());
    }
}

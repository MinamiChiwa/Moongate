using System.Text.Json;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.PortableUpdate;

namespace MabiTool.Host.Updates;

/// <summary>
/// 宿主启动时与替换器的约定：回退时旧宿主先恢复安装前的数据库备份再打开数据库；
/// 替换器启动的版本就绪后写出健康标记；事务结束后的下一次成功启动清理上一版文件与暂存。
/// </summary>
internal static class PortableUpdateStartup
{
    /// <summary>日志登记了恢复请求、且本宿主就是回退后启动的旧版本时，在数据根独占锁下用离线恢复换回安装前的数据库。</summary>
    internal static async Task RestoreDatabaseIfRequestedAsync(PortableInstallation? installation, StorageOptions storage)
    {
        if (installation is null || UpdateJournal.Read(installation.ToolRoot) is not { DatabaseRestorePending: true } journal ||
            journal.FromVersion != installation.Identity.DisplayVersion || !journal.Launched(journal.FromVersion)) return;
        var descriptor = JsonSerializer.Deserialize<DatabaseBackupDescriptor>(
            await File.ReadAllBytesAsync(UpdateLayout.DatabaseBackup(installation.ToolRoot, journal.TransactionId)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new UpdatePackageException("update.recoveryRequired");
        using (var lease = DataRootLease.Open(storage))
            await SqliteStore.RestoreDatabaseBackupOfflineAsync(lease, journal.TransactionId, descriptor);
        journal.Append(UpdateJournal.DatabaseRestored);
        Console.WriteLine($"更新已回退：数据库已恢复到安装 {journal.ToVersion} 之前的备份 {journal.BackupId}");
    }

    /// <summary>宿主完成数据迁移并开始监听后调用。</summary>
    internal static void ReportReady(PortableInstallation? installation)
    {
        if (installation is null || UpdateJournal.Read(installation.ToolRoot) is not { } journal) return;
        var root = installation.ToolRoot;
        if (!journal.IsFinal)
        {
            var launched = journal.Entries.LastOrDefault(entry => entry[0] == UpdateJournal.Launch);
            if (launched is { Length: 2 } && launched[1] == installation.Identity.DisplayVersion)
            {
                Directory.CreateDirectory(UpdateLayout.Transaction(root, journal.TransactionId));
                File.WriteAllText(UpdateLayout.Healthy(root, journal.TransactionId, launched[1]), DateTimeOffset.UtcNow.ToString("O"));
            }
            return;
        }
        // 事务结束后的下一次成功启动：上一版文件、暂存与下载缓存不再需要，结果文件保留给设置页显示。
        foreach (var directory in new[] { "previous", "staging", "transactions", "downloads", "plans" })
            UpdatePaths.DeleteOwnedTree(Path.Combine(UpdateLayout.Root(root), directory));
        File.Delete(UpdateLayout.Journal(root));
    }
}

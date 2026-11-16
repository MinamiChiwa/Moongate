using System.Text.Json;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;

internal static class ManagedInstallProbe
{
    internal static async Task<int> BootstrapAsync(string installationRoot, string phase, string marker)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var intent = await UpdateInstallIntent.ReadAsync(installationRoot);
        var hash = UpdateInstallIntent.Hash(JsonSerializer.SerializeToUtf8Bytes(intent, UpdateHandoffProtocol.Json));
        using var installation = new InstallTransaction(installationRoot);
        var record = await UpdaterBootstrap.PrepareAsync(intent, hash, false);
        if (phase == "activated")
        {
            await UpdaterBootstrap.VerifyVersionAsync(intent, record);
            await UpdaterBootstrap.SaveAsync(intent, record with { Phase = phase });
        }
        await File.WriteAllTextAsync(marker, phase); await Task.Delay(Timeout.InfiniteTimeSpan); return 0;
    }

    internal static async Task<int> RunAsync(string installationRoot, string pauseAt, string marker)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var intent = await UpdateInstallIntent.ReadAsync(installationRoot);
        var hash = UpdateInstallIntent.Hash(JsonSerializer.SerializeToUtf8Bytes(intent, UpdateHandoffProtocol.Json));
        var rollback = pauseAt.StartsWith("rollback/", StringComparison.Ordinal);
        if (rollback) pauseAt = pauseAt["rollback/".Length..];
        await new UpdateInstallEngine(intent, hash, new(0, 1, 0), async point =>
        {
            if (rollback && point == InstallPersistencePoint.PointerSwitched)
            {
                using var root = DataRootLease.OpenForUpdate(intent.Storage, intent.TransactionId, hash);
                await using var database = SqliteStore.OpenUpdateControl(root, intent.PreviousMigrations, "live");
                await database.WriteAsync(tx =>
                {
                    tx.Execute("UPDATE core_migrations SET checksum=$checksum WHERE module_id='music-library' AND version=1", ("$checksum", new string('f', 64)));
                    tx.Execute("UPDATE core_module_configuration SET enabled=0 WHERE id='environment'");
                    return 0;
                });
                await File.WriteAllTextAsync(root.Paths.Resolve("settings", "install-value.json"), "candidate-settings");
            }
            if (point.ToString() != pauseAt) return;
            await File.WriteAllTextAsync(marker, pauseAt);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }).RunAsync(false);
        return 0;
    }
}

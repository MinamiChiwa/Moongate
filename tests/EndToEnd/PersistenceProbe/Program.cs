using MabiTool.Contracts;
using MabiTool.Infrastructure;

if (args.Length != 4) return 2;
if (args[0] == "managed-install") return await ManagedInstallProbe.RunAsync(args[1], args[2], args[3]);
if (args[0] == "managed-bootstrap") return await ManagedInstallProbe.BootstrapAsync(args[1], args[2], args[3]);
if (args[0].StartsWith("data-maintenance-", StringComparison.Ordinal))
    return await DataMaintenanceProbe.RunAsync(args[1], args[2], args[3], args[0]["data-maintenance-".Length..]);
if (args[0] == "update-handoff") return await UpdateHandoffProbe.RunAsync(args[1], args[2], args[3]);
if (args[0].StartsWith("data-snapshot-", StringComparison.Ordinal))
{
    using var snapshotRoot = DataRootLease.Open(new(args[1], false, args[2]));
    await using var snapshotStore = SqliteStore.Open(snapshotRoot, AssetStore.Migrations);
    using var maintenance = snapshotStore.Activities.TryEnterMaintenance("snapshot-probe")!;
    await snapshotStore.CreateDataSnapshotAsync(maintenance, default, async point =>
    {
        if (args[0] != "data-snapshot-" + point) return;
        File.WriteAllText(args[3], point.ToString());
        await Task.Delay(Timeout.InfiniteTimeSpan);
    });
    return 0;
}
if (args[0] == "install-transaction") return await InstallTransactionProbe.RunAsync(args[1], args[2], args[3]);
if (args[0] == "install-rollback") return await InstallTransactionProbe.RollbackAsync(args[1], args[2], args[3]);
if (args[0].StartsWith("restore-database-", StringComparison.Ordinal))
{
    using var restoreLease = DataRootLease.Open(new(args[1], false, args[2]));
    var expected = System.Text.Json.JsonSerializer.Deserialize<DatabaseBackupDescriptor>(
        File.ReadAllText(restoreLease.Paths.Resolve("recovery", "restore-probe.json")),
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    if (args[0] == "restore-database-SourceWal")
    {
        using var walOwnership = restoreLease.AcquireDatabaseOwnership();
        using var wal = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = restoreLease.Paths.Database, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        wal.Open();
        using var command = wal.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=0; UPDATE backup_values SET value='new-in-wal';";
        command.ExecuteNonQuery();
        File.WriteAllText(args[3], "SourceWal");
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }
    await SqliteStore.RestoreDatabaseBackupOfflineAsync(restoreLease, new string('1', 32), expected, persisted: async point =>
    {
        if (args[0] != "restore-database-" + point) return;
        File.WriteAllText(args[3], point.ToString());
        await Task.Delay(Timeout.InfiniteTimeSpan);
    });
    return 0;
}
var elapsed = System.Diagnostics.Stopwatch.StartNew();
void Stage(string name) => Console.Error.WriteLine($"probe.{name}: {elapsed.ElapsedMilliseconds} ms");
Stage("open-root");
var scenario = args[0];
using var lease = DataRootLease.Open(new(args[1], true, args[2]));
Stage("open-store");
await using var store = SqliteStore.Open(lease);
Stage("store-ready");
var operation = new OperationSnapshot("op-crash-fixture", "fixture", "owner", [], OperationState.Running, "input-dispatched",
    new(1), null, new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null), null, null,
    new(CancellationState.None, null), EffectStatus.InputDispatched);
if (scenario == "committed")
{
    await store.SaveOperationAsync(operation);
    File.WriteAllText(args[3], "committed");
    await Task.Delay(Timeout.InfiniteTimeSpan);
}
else if (scenario == "uncommitted")
{
    await store.SaveOperationAsync(operation);
    // 故障注入：在事务提交前通知测试进程并阻塞，由外部强制终止本进程。
    await store.WriteAsync(tx =>
    {
        tx.Execute("UPDATE core_operations SET terminal=1 WHERE id=$id", ("$id", operation.Id));
        SqliteStore.AppendEvent(tx, new("uncommitted-event", "fixture", "uncommitted", new(1), "fixture.changed",
            System.Text.Json.JsonSerializer.SerializeToElement(new { value = "uncommitted" })));
        File.WriteAllText(args[3], "uncommitted");
        Thread.Sleep(Timeout.Infinite);
        return true;
    });
}
else if (scenario is "operation-queued" or "operation-applying")
{
    var operations = new OperationStore(store);
    Stage("admit-operation");
    var admitted = await operations.AdmitConfigurationAsync("owner", "color", true, new(false), "crash-operation-key",
        OperationStore.ConfigurationETag(new("color", true, new(0))));
    Stage("operation-admitted");
    if (scenario == "operation-applying") await operations.BeginConfigurationAsync(admitted.Snapshot.Id);
    Stage("operation-ready");
    File.WriteAllText(args[3], admitted.Snapshot.Id);
    await Task.Delay(Timeout.InfiniteTimeSpan);
}
else return 2;
return 0;

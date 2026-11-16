using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Updates;

internal static class UpdateCandidateProgram
{
    internal static async Task RunAsync(string[] arguments, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (arguments.Length != 6 || arguments[0] is not ("--update-preflight" or "--update-candidate" or "--update-rollback-health"))
            throw new ArgumentException("更新候选参数无效");
        var mode = arguments[0]; var root = arguments[1]; var transactionId = arguments[2]; var hash = arguments[3];
        var intent = await UpdateInstallIntent.ReadAsync(root, hash, token);
        if (intent.TransactionId != transactionId) throw UpdateHandoffProtocol.Invalid();
        var peer = JsonSerializer.Deserialize<UpdatePeerIdentity>(arguments[5], UpdateHandoffProtocol.Json) ?? throw UpdateHandoffProtocol.Invalid();
        using var connection = await UpdateHandoffConnection.ConnectAsync(arguments[4], peer, transactionId, token);
        var record = await InstallTransaction.ReadJournalAsync(root, token);
        if (record.TransactionId != transactionId || record.PlanId != intent.Plan.PlanId) throw UpdateHandoffProtocol.Invalid();
        var versionRoot = mode == "--update-preflight" ? intent.PreflightRoot : mode == "--update-rollback-health" ? intent.PreviousRoot : intent.CandidateRoot;
        var expected = mode == "--update-rollback-health" ? intent.PreviousTarget : intent.CandidateTarget;
        expected.RequireSame(BackendApplication.InstallationTarget(intent.Validation.Runtime.RuntimeMode));
        var assembly = UpdatePaths.Local(versionRoot, "components/core/MabiTool.Host.dll");
        if (!UpdatePaths.SameLocalFile(typeof(BackendApplication).Assembly.Location, assembly))
            throw new UpdatePlanException("update.installTargetInvalid");
        using var lease = DataRootLease.OpenForUpdate(intent.Storage, transactionId, hash);
        if (lease.Paths.RootId != intent.RootId || lease.DatabaseId != intent.DatabaseId) throw new UpdatePlanException("update.installIntentInvalid");
        if (mode == "--update-preflight")
        {
            if (record.Phase != "dataBackedUp" || record.Snapshot is null) throw new UpdatePlanException("update.transactionPhaseInvalid");
            var request = await connection.ReceiveAsync<UpdatePreflightRequest>("preflight", token);
            if (request != new UpdatePreflightRequest(record.Snapshot.SnapshotId, record.Revision)) throw UpdateHandoffProtocol.Invalid();
            await using var control = SqliteStore.OpenUpdateControl(lease, intent.PreviousMigrations, intent.BackendMode);
            using var maintenance = control.Activities.TryEnterMaintenance(transactionId) ?? throw new UpdatePlanException("resource.busy");
            var result = await control.PreflightDatabaseMigrationsAsync(record.Snapshot.Database, BackendApplication.DatabaseMigrations, maintenance, token);
            if (!result.Migrations.SequenceEqual(expected.Migrations)) throw new UpdatePlanException("update.migrationInvalid");
            await connection.SendAsync("preflightResult", result, token);
            if (await connection.ReceiveAsync<UpdateIntentTransfer>("preflightCompleted", token) != new UpdateIntentTransfer(transactionId, hash))
                throw UpdateHandoffProtocol.Invalid();
            return;
        }

        if (mode == "--update-candidate" && record.Phase is not ("pointerSwitched" or "healthy") ||
            mode == "--update-rollback-health" && record.Phase != "previousPointerRestored")
            throw new UpdatePlanException("update.transactionPhaseInvalid");
        var options = LocalApiOptions.FromEnvironment() with { Port = 0, RuntimeDirectory = null };
        await using var application = BackendApplication.Build(options, storage: intent.Storage, updateCandidate: true, updateLease: lease);
        var barrier = application.Services.GetRequiredService<ActivityBarrier>();
        using var healthLease = barrier.TryEnterMaintenance(transactionId) ?? throw new UpdatePlanException("resource.busy");
        var operations = application.Services.GetRequiredService<OperationStore>();
        var modules = application.Services.GetRequiredService<ModuleRuntime>();
        var catalog = application.Services.GetRequiredService<ModuleCatalog>();
        foreach (var configuration in operations.SavedConfigurations())
            if (catalog.Registrations.TryGetValue(configuration.Id, out var registration) && (configuration.Enabled || !registration.Definition.Essential))
                await modules.SetEnabledAsync(configuration.Id, configuration.Enabled, token);
        await application.StartAsync(token);
        try
        {
            var system = application.Services.GetRequiredService<SystemInfo>();
            var health = application.Services.GetRequiredService<UpdateHostHealthCheck>();
            if (mode == "--update-candidate")
            {
                var notice = await UpdateHandoffHealthSession.RunCandidateAsync(connection, health, healthLease, system, token);
                await InstallTransaction.VerifyCommittedAsync(root, notice, token);
            }
            else
            {
                var request = await connection.ReceiveAsync<UpdateRollbackHealthRequest>("rollbackHealth", token);
                var target = expected.HealthTarget(intent.RootId, intent.DatabaseId, intent.BackendMode,
                    intent.HealthTarget.Modules.Select(module => module.Id).ToHashSet(StringComparer.Ordinal));
                if (request.Challenge is not { Length: 64 } || !request.Challenge.All(Uri.IsHexDigit) ||
                    JsonSerializer.Serialize(target, UpdateHandoffProtocol.Json) != JsonSerializer.Serialize(request.Target, UpdateHandoffProtocol.Json))
                    throw UpdateHandoffProtocol.Invalid();
                var result = await health.VerifyAsync(healthLease, new(transactionId, target.HostVersion, target.MinimumHostApi,
                    target.MaximumHostApiExclusive, target.RootId, target.DatabaseId, target.BackendMode, target.Migrations, target.Modules), token);
                await connection.SendAsync("rollbackHealthResult", new InstallHealthEvidence(transactionId, request.Challenge, result.InstanceId,
                    result.HostVersion, result.ApiVersion, result.Database, result.Modules), token);
                if (await connection.ReceiveAsync<UpdateIntentTransfer>("rollbackHealthCompleted", token) != new UpdateIntentTransfer(transactionId, hash))
                    throw UpdateHandoffProtocol.Invalid();
            }
        }
        finally { await application.StopAsync(CancellationToken.None); }
    }
}

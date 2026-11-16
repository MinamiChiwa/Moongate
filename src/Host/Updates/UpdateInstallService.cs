using System.Diagnostics;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Storage;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Updates;

public interface IUpdateInstallService
{
    Task<bool> TransferAsync(string operationId, UpdatePlan plan, ActivityBarrier.MaintenanceLease maintenance, CancellationToken token);
    void RequestShutdown();
}

internal sealed class UpdateInstallService(UpdateInstallationOptions? options, ReleaseUpdateCheckService checker,
    SqliteStore database, DataRootLease root, OperationStore operations, ModuleRuntime modules, ModuleCatalog catalog,
    IHostApplicationLifetime host, MaintenanceShutdownContext shutdown) : IUpdateInstallService
{
    public async Task<bool> TransferAsync(string operationId, UpdatePlan frozenPlan, ActivityBarrier.MaintenanceLease maintenance, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new UpdatePlanException("update.platformUnsupported");
        if (options is null) throw new UpdatePlanException("update.sourceNotConfigured");
        using var activity = database.Activities.EnterMaintenanceActivity(maintenance, "update.handoff");
        var (plan, release) = await checker.ReadValidatedPlanAsync(frozenPlan.PlanId, token, verifyAvailableSpace: false);
        if (JsonSerializer.Serialize(plan, ContractJson.Options) != JsonSerializer.Serialize(frozenPlan, ContractJson.Options))
            throw new UpdatePlanException("update.planStale");
        var stage = UpdatePaths.Local(options.StagingRoot, plan.PlanId);
        if (!Directory.Exists(stage)) throw new UpdatePlanException("update.stageRequired");
        var targetFiles = UpdateSignedPlanVerifier.TargetFiles(plan, release);
        await FileTreePlanner.VerifyAsync(stage, targetFiles, token);
        using var installation = new InstallTransaction(options.InstallationRoot);
        var installed = await installation.ReadActiveAsync(token);
        if (installed.Version.ReleaseId != options.InstalledReleaseId || installed.Version.ReleaseSequence != options.InstalledReleaseSequence ||
            installed.Version.ManifestSha256 != options.InstalledManifestSha256) throw new UpdatePlanException("update.planStale");
        var target = await InstallTargetDescriptor.ReadAsync(stage, options.ProductId, token);
        var previousTarget = await InstallTargetDescriptor.ReadAsync(options.CurrentVersionRoot!, options.ProductId, token);
        previousTarget.RequireSame(BackendApplication.InstallationTarget(options.Runtime.RuntimeMode));
        var required = (await modules.SnapshotAsync(token)).Items
            .Where(item => item.Enabled && catalog.Registrations[item.Id].Definition.Implemented)
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        required.Add("core");
        var transactionId = Guid.NewGuid().ToString("N");
        await UpdaterBootstrap.ClearCompletedRecordAsync(options.InstallationRoot, token);
        using var currentProcess = Process.GetCurrentProcess();
        var intent = new UpdateInstallIntent(1, transactionId, operationId, Path.GetFullPath(options.InstallationRoot),
            new(root.Paths.BootstrapDirectory), root.Paths.RootId, root.DatabaseId!, "live", UpdatePeerIdentity.Capture(currentProcess), options.Validation,
            plan, installed.Version, previousTarget, target, target.HealthTarget(root.Paths.RootId, root.DatabaseId!, "live", required),
            SqliteStore.MigrationTargets(BackendApplication.DatabaseMigrations),
            JsonSerializer.SerializeToUtf8Bytes(options with { BootstrapDirectory = root.Paths.BootstrapDirectory }, ContractJson.Options), targetFiles);
        var hash = await intent.SaveAsync(token);
        var gate = new UpdateInstallGate(1, transactionId, hash, intent.InstallationRoot, root.Paths.RootId);
        Process? child = null; var transferred = false;
        try
        {
            gate.Publish(root.Paths);
            using var listener = new UpdateHandoffListener(transactionId);
            child = UpdateProcess.Start(intent.PreviousRoot, "updater", "MabiTool.Updater", options.Runtime.RuntimeMode,
                ["--handoff", intent.InstallationRoot, transactionId, hash, listener.PipeName,
                    JsonSerializer.Serialize(intent.Parent, UpdateHandoffProtocol.Json)]);
            using var connection = await listener.AcceptAsync(UpdatePeerIdentity.Capture(child), token);
            var transfer = new UpdateIntentTransfer(transactionId, hash);
            await connection.SendAsync("intent", transfer, token);
            if (await connection.ReceiveAsync<UpdateIntentTransfer>("accepted", token) != transfer) throw UpdateHandoffProtocol.Invalid();
            await operations.ReportTransferredUpdateAsync(intent, maintenance, "handedOff", token);
            await connection.SendAsync("release", transfer, token);
            if (await connection.ReceiveAsync<UpdateIntentTransfer>("released", token) != transfer) throw UpdateHandoffProtocol.Invalid();
            transferred = true; shutdown.Publish(maintenance); return true;
        }
        finally
        {
            if (!transferred)
            {
                if (child is not null) { UpdateProcess.Terminate(child); await child.WaitForExitAsync(CancellationToken.None); }
                if (UpdateInstallGate.Read(root.Paths.DataRoot) == gate) gate.Remove(root.Paths);
                if (File.Exists(intent.PathValue)) File.Delete(intent.PathValue);
            }
            child?.Dispose();
        }
    }
    public void RequestShutdown() => host.StopApplication();
}

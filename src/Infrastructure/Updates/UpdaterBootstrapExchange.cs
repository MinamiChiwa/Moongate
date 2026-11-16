using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace MabiTool.Infrastructure.Updates;

[SupportedOSPlatform("windows")]
internal static class UpdaterBootstrapExchange
{
    internal static async Task HandOffAsync(UpdateInstallIntent intent, string hash, bool launch, CancellationToken token = default)
    {
        using var installation = new InstallTransaction(intent.InstallationRoot);
        var record = await UpdaterBootstrap.PrepareAsync(intent, hash, launch, token).ConfigureAwait(false);
        using var listener = new UpdateHandoffListener(intent.TransactionId);
        using var child = UpdateProcess.Start(UpdaterBootstrap.StableRoot(intent), "launcher", "MabiTool.Launcher",
            intent.Validation.Runtime.RuntimeMode, ["--continue-updater", intent.InstallationRoot, hash, listener.PipeName,
                JsonSerializer.Serialize(record.Predecessor, UpdateHandoffProtocol.Json)]);
        var transferred = false; var phase = "launcherIdentity";
        try
        {
            record = record with { Launcher = UpdatePeerIdentity.Capture(child) };
            await UpdaterBootstrap.SaveAsync(intent, record, token).ConfigureAwait(false);
            phase = "launcherConnect";
            using var connection = await listener.AcceptAsync(record.Launcher, token).ConfigureAwait(false);
            var transfer = new UpdateIntentTransfer(intent.TransactionId, hash);
            phase = "launcherReady";
            await connection.SendAsync("updaterReady", transfer, token).ConfigureAwait(false);
            phase = "launcherAccepted";
            if (await connection.ReceiveAsync<UpdateIntentTransfer>("updaterAccepted", token).ConfigureAwait(false) != transfer)
                throw UpdateHandoffProtocol.Invalid();
            phase = "launcherReleased";
            await connection.ConfirmPeerClosedAsync(token).ConfigureAwait(false);
            transferred = true;
        }
        catch (Exception error)
        {
            error.Data["updatePhase"] = phase; await UpdateFailureLog.RecordAsync(intent, error).ConfigureAwait(false); throw;
        }
        finally
        {
            if (!transferred) { UpdateProcess.Terminate(child); await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        }
    }

    internal static async Task RunLauncherAsync(string assemblyPath, string installationRoot, string hash, string pipe,
        UpdatePeerIdentity predecessor, CancellationToken token = default)
    {
        var intent = await UpdateInstallIntent.ReadAsync(installationRoot, hash, token).ConfigureAwait(false);
        var record = await UpdaterBootstrap.ReadAsync(intent, hash, token).ConfigureAwait(false) ?? throw UpdateHandoffProtocol.Invalid();
        if (record.Predecessor != predecessor) throw UpdateHandoffProtocol.Invalid();
        await UpdaterBootstrap.VerifyStableAsync(intent, token).ConfigureAwait(false);
        RequireAssembly(assemblyPath, UpdaterBootstrap.StableRoot(intent), "launcher", "MabiTool.Launcher");
        using var pinned = new PinnedUpdatePeer(predecessor);
        using var process = Process.GetProcessById(predecessor.ProcessId); _ = process.Handle;
        using var current = Process.GetCurrentProcess();
        var identity = UpdatePeerIdentity.Capture(current);
        try
        {
            using (var connection = await UpdateHandoffConnection.ConnectAsync(pipe, predecessor, intent.TransactionId, token).ConfigureAwait(false))
            {
                var transfer = new UpdateIntentTransfer(intent.TransactionId, hash);
                if (await connection.ReceiveAsync<UpdateIntentTransfer>("updaterReady", token).ConfigureAwait(false) != transfer) throw UpdateHandoffProtocol.Invalid();
                record = await UpdaterBootstrap.ReadAsync(intent, hash, token).ConfigureAwait(false) ?? throw UpdateHandoffProtocol.Invalid();
                if (record.Predecessor != predecessor || record.Launcher != identity) throw UpdateHandoffProtocol.Invalid();
                await UpdaterBootstrap.VerifyVersionAsync(intent, record, token).ConfigureAwait(false);
                await connection.SendAsync("updaterAccepted", transfer, token).ConfigureAwait(false);
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token); wait.CancelAfter(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            var fixedRecord = await UpdaterBootstrap.ReadAsync(intent, hash, token).ConfigureAwait(false);
            if (fixedRecord != record && JsonSerializer.Serialize(fixedRecord, UpdateHandoffProtocol.Json) != JsonSerializer.Serialize(record, UpdateHandoffProtocol.Json))
                throw UpdateHandoffProtocol.Invalid();
            await UpdaterBootstrap.VerifyVersionAsync(intent, record, token).ConfigureAwait(false);
            using var listener = new UpdateHandoffListener(intent.TransactionId);
            using var successor = UpdateProcess.Start(UpdaterBootstrap.VersionRoot(intent, record), "updater", "MabiTool.Updater",
                intent.Validation.Runtime.RuntimeMode, ["--resume", intent.InstallationRoot, hash, listener.PipeName,
                    JsonSerializer.Serialize(identity, UpdateHandoffProtocol.Json)]);
            try
            {
                using (var connection = await listener.AcceptAsync(UpdatePeerIdentity.Capture(successor), token).ConfigureAwait(false))
                {
                    var transfer = new UpdateIntentTransfer(intent.TransactionId, hash);
                    await connection.SendAsync("updaterResume", transfer, token).ConfigureAwait(false);
                    if (await connection.ReceiveAsync<UpdateIntentTransfer>("updaterResumed", token).ConfigureAwait(false) != transfer)
                        throw UpdateHandoffProtocol.Invalid();
                }
                using var completion = CancellationTokenSource.CreateLinkedTokenSource(token); completion.CancelAfter(TimeSpan.FromMinutes(5));
                await successor.WaitForExitAsync(completion.Token).ConfigureAwait(false);
                if (successor.ExitCode != 0) throw new UpdatePlanException("update.recoveryRequired");
            }
            finally
            {
                UpdateProcess.Terminate(successor); await successor.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is UpdatePlanException or IOException or OperationCanceledException or TimeoutException)
        {
            await UpdateFailureLog.RecordAsync(intent, error, "launcherResume").ConfigureAwait(false);
            if (File.Exists(intent.PathValue))
            {
                var saved = await UpdaterBootstrap.ReadAsync(intent, hash, CancellationToken.None).ConfigureAwait(false);
                if (saved is not null && saved.Launcher == identity)
                    await UpdaterBootstrap.SaveAsync(intent, saved with { Phase = "failed", FailureCode = error is UpdatePlanException e ? e.Code : "update.handoffTimeout" }, CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
    }

    internal static async Task<UpdaterBootstrapRecord> AcceptResumeAsync(string assemblyPath, Version actualVersion,
        UpdateInstallIntent intent, string hash, string pipe, UpdatePeerIdentity launcher, CancellationToken token = default)
    {
        var record = await UpdaterBootstrap.ReadAsync(intent, hash, token).ConfigureAwait(false) ?? throw UpdateHandoffProtocol.Invalid();
        if (record.Launcher != launcher) throw UpdateHandoffProtocol.Invalid();
        await UpdaterBootstrap.VerifyStableAsync(intent, token).ConfigureAwait(false);
        await UpdaterBootstrap.VerifyVersionAsync(intent, record, token).ConfigureAwait(false);
        RequireAssembly(assemblyPath, UpdaterBootstrap.VersionRoot(intent, record), "updater", "MabiTool.Updater");
        var release = await UpdaterBootstrap.VerifyPlanAsync(intent, token).ConfigureAwait(false);
        if (actualVersion.ToString(3) != Version.Parse(record.Version).ToString(3) || actualVersion < release.MinimumUpdaterVersion)
            throw new UpdatePlanException("update.updaterRequired");
        using var connection = await UpdateHandoffConnection.ConnectAsync(pipe, launcher, intent.TransactionId, token).ConfigureAwait(false);
        var transfer = new UpdateIntentTransfer(intent.TransactionId, hash);
        if (await connection.ReceiveAsync<UpdateIntentTransfer>("updaterResume", token).ConfigureAwait(false) != transfer) throw UpdateHandoffProtocol.Invalid();
        await UpdaterBootstrap.SaveAsync(intent, record with { Phase = "activated", FailureCode = null }, token).ConfigureAwait(false);
        await connection.SendAsync("updaterResumed", transfer, token).ConfigureAwait(false);
        return record;
    }

    private static void RequireAssembly(string actual, string root, string component, string assembly)
    {
        if (!UpdatePaths.SameLocalFile(actual, UpdatePaths.Local(root, $"components/{component}/{assembly}.dll")))
            throw new UpdatePlanException("update.updaterUnavailable");
    }
}

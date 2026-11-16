using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace MabiTool.Infrastructure.Updates;

internal sealed record InitialInstallModuleOffer(int SchemaVersion, IReadOnlyList<InstallHealthModule> Modules);
internal sealed record InitialInstallHealthRequest(int SchemaVersion, string Challenge, InstallHealthTarget Target);

[SupportedOSPlatform("windows")]
internal sealed class InitialInstallHealthExchange : IInitialInstallHealthExchange
{
    public async Task<InstallHealthEvidence> VerifyAsync(InitialInstallAuthorization authorization, CancellationToken token)
    {
        var record = authorization.Record;
        using var listener = new UpdateHandoffListener(record.InstallationId);
        using var currentProcess = Process.GetCurrentProcess();
        var parent = UpdatePeerIdentity.Capture(currentProcess);
        using var process = UpdateProcess.Start(record.VersionRoot, "core", "MabiTool.Host", record.Target!.RuntimeMode,
            ["--initial-install-health", record.Request.InstallationRoot, record.InputSha256, listener.PipeName,
                JsonSerializer.Serialize(parent, UpdateHandoffProtocol.Json)], captureOutput: true,
            environment: new Dictionary<string, string>
            {
                ["MABITOOL_BOOTSTRAP_DIRECTORY"] = record.Request.BootstrapDirectory,
                ["MABITOOL_UPDATE_CONFIGURATION"] = "", ["MABITOOL_OBSERVATION_EXECUTABLE"] = "",
                ["MABITOOL_COLOR_PYTHON"] = "", ["MABITOOL_COLOR_WORKER_DIRECTORY"] = ""
            });
        var output = UpdateProcess.ReadBoundedAsync(process.StandardOutput);
        var errors = UpdateProcess.ReadBoundedAsync(process.StandardError);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(UpdateHandoffProtocol.Timeout);
        try
        {
            using var connection = await listener.AcceptAsync(UpdatePeerIdentity.Capture(process), deadline.Token).ConfigureAwait(false);
            var transfer = new UpdateIntentTransfer(record.InstallationId, record.InputSha256);
            await connection.SendAsync("intent", transfer, deadline.Token).ConfigureAwait(false);
            if (await connection.ReceiveAsync<UpdateIntentTransfer>("accepted", deadline.Token).ConfigureAwait(false) != transfer)
                throw UpdateHandoffProtocol.Invalid();
            var offer = await connection.ReceiveAsync<InitialInstallModuleOffer>("offer", deadline.Token).ConfigureAwait(false);
            if (offer.SchemaVersion != 1 || offer.Modules is not { Count: > 0 and <= 128 } || offer.Modules.All(module => module.Id != "core") ||
                offer.Modules.Any(module => !record.Target.Modules.Contains(module)) ||
                offer.Modules.Select(module => module.Id).Distinct(StringComparer.Ordinal).Count() != offer.Modules.Count)
                throw new UpdatePlanException("update.initialInstallHealthMismatch");
            var target = record.Target.HealthTarget(record.RootId, record.DatabaseId, "live", offer.Modules.Select(module => module.Id).ToHashSet(StringComparer.Ordinal));
            var challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            await connection.SendAsync("healthCheck", new InitialInstallHealthRequest(1, challenge, target), deadline.Token).ConfigureAwait(false);
            var evidence = await connection.ReceiveAsync<InstallHealthEvidence>("healthResult", deadline.Token).ConfigureAwait(false);
            if (evidence.Challenge != challenge || !InitialInstallFiles.Same(evidence.Modules, target.Modules))
                throw new UpdatePlanException("update.initialInstallHealthMismatch");
            await connection.SendAsync("committed", transfer, deadline.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new UpdatePlanException("update.initialInstallHealthFailed");
            return evidence;
        }
        finally
        {
            UpdateProcess.Terminate(process); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            _ = await output.ConfigureAwait(false); _ = await errors.ConfigureAwait(false);
        }
    }
}

using System.Text.Json;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Updates;

internal static class InitialInstallProgram
{
    internal static async Task<int> RunAsync(string[] arguments, CancellationToken token = default)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            if (arguments.Length != 5 || arguments[0] != "--initial-install-health") throw new ArgumentException("初装健康参数无效");
            var authorization = await InitialInstallAuthorization.ReadAsync(arguments[1], arguments[2], token: token).ConfigureAwait(false);
            var record = authorization.Record;
            record.Target!.RequireSame(BackendApplication.InstallationTarget(record.Target.RuntimeMode));
            if (!UpdatePaths.SameLocalFile(typeof(BackendApplication).Assembly.Location,
                UpdatePaths.Local(record.VersionRoot, "components/core/MabiTool.Host.dll")))
                throw new UpdatePlanException("update.installTargetInvalid");
            var peer = JsonSerializer.Deserialize<UpdatePeerIdentity>(arguments[4], UpdateHandoffProtocol.Json) ?? throw UpdateHandoffProtocol.Invalid();
            using var connection = await UpdateHandoffConnection.ConnectAsync(arguments[3], peer, record.InstallationId, token).ConfigureAwait(false);
            var transfer = new UpdateIntentTransfer(record.InstallationId, record.InputSha256);
            if (await connection.ReceiveAsync<UpdateIntentTransfer>("intent", token).ConfigureAwait(false) != transfer) throw UpdateHandoffProtocol.Invalid();
            await connection.SendAsync("accepted", transfer, token).ConfigureAwait(false);
            using var lease = DataRootLease.OpenForInitialInstall(authorization);
            await using var application = BackendApplication.Build(LocalApiOptions.FromEnvironment() with { Port = 0, RuntimeDirectory = null },
                storage: record.Request.Storage, updateCandidate: true, updateLease: lease);
            _ = application.Services.GetRequiredService<SqliteStore>();
            if (lease.DatabaseId != record.DatabaseId) throw new UpdatePlanException("update.initialInstallHealthMismatch");
            var barrier = application.Services.GetRequiredService<ActivityBarrier>();
            using var maintenance = barrier.TryEnterMaintenance(record.InstallationId) ?? throw new UpdatePlanException("resource.busy");
            var catalog = application.Services.GetRequiredService<ModuleCatalog>();
            var modules = application.Services.GetRequiredService<ModuleRuntime>();
            var required = (await modules.SnapshotAsync(token).ConfigureAwait(false)).Items
                .Where(module => module.Enabled && catalog.Registrations[module.Id].Definition.Implemented)
                .Select(module => module.Id).ToHashSet(StringComparer.Ordinal);
            required.Add("core");
            var target = record.Target.HealthTarget(record.RootId, record.DatabaseId, "live", required);
            await connection.SendAsync("offer", new InitialInstallModuleOffer(1, target.Modules), token).ConfigureAwait(false);
            var request = await connection.ReceiveAsync<InitialInstallHealthRequest>("healthCheck", token).ConfigureAwait(false);
            if (request.SchemaVersion != 1 || request.Challenge is not { Length: 64 } || !request.Challenge.All(Uri.IsHexDigit) ||
                !InitialInstallFiles.Same(request.Target, target)) throw UpdateHandoffProtocol.Invalid();
            await application.StartAsync(token).ConfigureAwait(false);
            try
            {
                var result = await application.Services.GetRequiredService<UpdateHostHealthCheck>().VerifyAsync(maintenance,
                    new(record.InstallationId, target.HostVersion, target.MinimumHostApi, target.MaximumHostApiExclusive,
                        target.RootId, target.DatabaseId, target.BackendMode, target.Migrations, target.Modules), token).ConfigureAwait(false);
                await connection.SendAsync("healthResult", new InstallHealthEvidence(record.InstallationId, request.Challenge,
                    result.InstanceId, result.HostVersion, result.ApiVersion, result.Database, result.Modules), token).ConfigureAwait(false);
                if (await connection.ReceiveAsync<UpdateIntentTransfer>("committed", token).ConfigureAwait(false) != transfer) throw UpdateHandoffProtocol.Invalid();
            }
            finally { await application.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            return 0;
        }
        catch (Exception error) when (error is UpdatePlanException or StorageException or TrustValidationException or FileTreeException or
            UpdateHostHealthException or IOException or UnauthorizedAccessException or ArgumentException or JsonException or OperationCanceledException)
        {
            Console.Error.WriteLine(error switch { UpdatePlanException e => e.Code, StorageException e => e.Code,
                TrustValidationException e => e.Code, FileTreeException e => e.Code, UpdateHostHealthException e => e.Code,
                ArgumentException => "request.invalid", _ => "update.initialInstallIncomplete" });
            return 4;
        }
    }

    internal static void RequireCompleted(StorageOptions storage)
    {
        try { RequireCompletedCore(storage); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or TrustValidationException)
        { throw new UpdatePlanException("update.initialInstallStateInvalid"); }
    }

    private static void RequireCompletedCore(StorageOptions storage)
    {
        var referencePath = UpdatePaths.Local(StoragePaths.ValidateLocal(storage.BootstrapDirectory), InitialInstallReference.FileName);
        if (!File.Exists(referencePath)) return;
        var reference = InitialInstallFiles.ReadJsonAsync<InitialInstallReference>(referencePath, 16384).GetAwaiter().GetResult();
        var record = InitialInstallRecord.ReadAsync(reference.InstallationRoot, reference.InputSha256).GetAwaiter().GetResult();
        if (reference != InitialInstallReference.For(record) || record.Request.BootstrapDirectory != Path.GetFullPath(storage.BootstrapDirectory) ||
            storage.PortableRoot is not null || storage.DataRoot is not null && record.Request.DataRoot != StoragePaths.ValidateLocal(storage.DataRoot))
            throw new UpdatePlanException("update.initialInstallInputMismatch");
        if (record.Phase != "complete") throw new UpdatePlanException("update.initialInstallIncomplete");
    }
}

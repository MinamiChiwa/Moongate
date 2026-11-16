using System.Security.Cryptography;
using System.Text.Json;

namespace MabiTool.Infrastructure.Updates;

internal sealed record UpdateInstallIntent(int SchemaVersion, string TransactionId, string OperationId,
    string InstallationRoot, StorageOptions Storage, string RootId, string DatabaseId, string BackendMode,
    UpdatePeerIdentity Parent, UpdateInstallValidation Validation, UpdatePlan Plan, InstallVersion Previous,
    InstallTargetDescriptor PreviousTarget, InstallTargetDescriptor CandidateTarget, InstallHealthTarget HealthTarget,
    IReadOnlyList<DatabaseMigrationStamp> PreviousMigrations, byte[] InstallationConfiguration, IReadOnlyList<ManifestFile> CandidateFiles)
{
    internal const string FileName = "pending-install.json";
    internal const int MaximumBytes = 64 * 1024 * 1024;
    internal string StateRoot => UpdatePaths.Local(InstallationRoot, "update-state");
    internal string PathValue => UpdatePaths.Local(StateRoot, FileName);
    internal string PreviousRoot => UpdatePaths.Local(InstallationRoot, "versions/" + Previous.ReleaseId);
    internal string StageRoot => UpdatePaths.Local(InstallationRoot, "staging/" + Plan.PlanId);
    internal string CandidateRoot => UpdatePaths.Local(InstallationRoot, "versions/" + Plan.ReleaseId);
    internal string PreflightRoot => Directory.Exists(StageRoot) ? StageRoot : CandidateRoot;

    internal void Validate()
    {
        if (SchemaVersion != 1 || !UpdateHandoffProtocol.Id(TransactionId) || !OperationIdValid(OperationId) ||
            !UpdateHandoffProtocol.Id(RootId) || !UpdateHandoffProtocol.Id(DatabaseId) || BackendMode != "live" ||
            Storage is null || Storage.Initialize || Parent is null || Parent.ProcessId <= 0 || Parent.StartTimeUtcTicks <= 0 ||
            Validation is null || Plan is null || Previous is null || PreviousTarget is null || CandidateTarget is null ||
            PreviousMigrations is not { Count: >= 4 and <= 4096 } || HealthTarget is null || InstallationConfiguration is not { Length: > 0 and <= 262144 } || CandidateFiles is null)
            throw Invalid();
        StoragePaths.ValidateLocal(InstallationRoot); StoragePaths.ValidateLocal(Storage.BootstrapDirectory);
        StoragePaths.ValidateLocal(Parent.ExecutablePath);
        using var configuration = StrictJson.Parse(InstallationConfiguration, 262144);
        var saved = configuration.RootElement;
        if (saved.GetProperty("productId").GetString() != Validation.ProductId ||
            !Path.GetFullPath(saved.GetProperty("installationRoot").GetString()!).Equals(Path.GetFullPath(InstallationRoot), StringComparison.OrdinalIgnoreCase) ||
            saved.GetProperty("installedReleaseSequence").GetInt64() != Previous.ReleaseSequence ||
            saved.GetProperty("installedManifestSha256").GetString() != Previous.ManifestSha256 ||
            saved.GetProperty("source").GetProperty("channel").GetString() != Validation.Channel) throw Invalid();
        if (Validation.InstalledReleaseSequence != Previous.ReleaseSequence || Validation.InstalledManifestSha256 != Previous.ManifestSha256 ||
            Plan.ReleaseSequence <= Previous.ReleaseSequence || HealthTarget.RootId != RootId || HealthTarget.DatabaseId != DatabaseId ||
            HealthTarget.BackendMode != BackendMode || HealthTarget.HostVersion != CandidateTarget.HostVersion ||
            !HealthTarget.Migrations.SequenceEqual(CandidateTarget.Migrations)) throw Invalid();
        PreviousTarget.Validate(Validation.ProductId); CandidateTarget.Validate(Validation.ProductId);
        _ = FileTreePlanner.ValidateManifest(CandidateFiles);
        if (PreviousTarget.RuntimeMode != Validation.Runtime.RuntimeMode || CandidateTarget.RuntimeMode != Validation.Runtime.RuntimeMode ||
            Version.Parse(CandidateTarget.HostVersion) != Plan.Components.Single(item => item.ComponentId == "core").Version ||
            Version.Parse(PreviousTarget.HostVersion) != Validation.Context.HostVersion) throw Invalid();
        var expected = CandidateTarget.HealthTarget(RootId, DatabaseId, BackendMode,
            HealthTarget.Modules.Select(module => module.Id).ToHashSet(StringComparer.Ordinal));
        if (JsonSerializer.Serialize(expected, UpdateHandoffProtocol.Json) != JsonSerializer.Serialize(HealthTarget, UpdateHandoffProtocol.Json)) throw Invalid();
        _ = PreviousRoot; _ = StageRoot; _ = CandidateRoot;
    }

    internal async Task<string> SaveAsync(CancellationToken token = default)
    {
        Validate(); var bytes = JsonSerializer.SerializeToUtf8Bytes(this, UpdateHandoffProtocol.Json);
        if (bytes.Length > MaximumBytes) throw Invalid();
        Directory.CreateDirectory(StateRoot); UpdatePaths.RejectLinks(PathValue);
        if (File.Exists(PathValue) || Directory.Exists(PathValue)) throw new UpdatePlanException("resource.busy");
        var temporary = PathValue + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            { await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, PathValue, false);
            return Hash(bytes);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task<UpdateInstallIntent> ReadAsync(string installationRoot, string? expectedHash = null, CancellationToken token = default)
    {
        var root = StoragePaths.ValidateLocal(installationRoot); var path = UpdatePaths.Local(root, "update-state/" + FileName);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length is <= 0 or > MaximumBytes) throw Invalid();
        var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (expectedHash is not null && Hash(bytes) != expectedHash) throw Invalid();
        try
        {
            using var document = StrictJson.Parse(bytes, MaximumBytes);
            var value = document.RootElement.Deserialize<UpdateInstallIntent>(UpdateHandoffProtocol.Json) ?? throw Invalid();
            value.Validate();
            if (!Path.GetFullPath(value.InstallationRoot).Equals(root, StringComparison.OrdinalIgnoreCase)) throw Invalid();
            return value;
        }
        catch (Exception error) when (error is JsonException or TrustValidationException or InvalidOperationException or ArgumentException or NullReferenceException)
        { throw Invalid(); }
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static bool OperationIdValid(string? value) => value is { Length: 35 } && value.StartsWith("op-", StringComparison.Ordinal) && UpdateHandoffProtocol.Id(value[3..]);
    private static UpdatePlanException Invalid() => new("update.installIntentInvalid");
}

internal sealed record UpdateInstallGate(int SchemaVersion, string TransactionId, string IntentSha256, string InstallationRoot, string RootId)
{
    internal const string RelativePath = "recovery/update-install.json";
    internal static UpdateInstallGate? Read(string dataRoot)
    {
        var path = UpdatePaths.Local(dataRoot, RelativePath);
        if (!File.Exists(path)) { if (Directory.Exists(path)) throw Invalid(); return null; }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 16384) throw Invalid();
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        try
        {
            using var json = StrictJson.Parse(bytes, 16384);
            var gate = json.RootElement.Deserialize<UpdateInstallGate>(UpdateHandoffProtocol.Json) ?? throw Invalid();
            if (gate.SchemaVersion != 1 || !UpdateHandoffProtocol.Id(gate.TransactionId) || !UpdateHandoffProtocol.Id(gate.RootId) ||
                gate.IntentSha256 is not { Length: 64 } || !gate.IntentSha256.All(Uri.IsHexDigit)) throw Invalid();
            StoragePaths.ValidateLocal(gate.InstallationRoot); return gate;
        }
        catch (Exception error) when (error is JsonException or TrustValidationException or ArgumentException) { throw Invalid(); }
    }

    internal static UpdateInstallGate? Find(StorageOptions storage)
    {
        var bootstrap = StoragePaths.ValidateLocal(storage.BootstrapDirectory);
        var config = UpdatePaths.Local(bootstrap, "data-root.json");
        if (!File.Exists(config)) return null;
        using var input = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > 16384) throw Invalid();
        using var json = JsonDocument.Parse(input);
        var root = json.RootElement.GetProperty("dataRoot").GetString() ?? throw Invalid();
        return Read(StoragePaths.ValidateLocal(root));
    }

    internal static void RequireOpen(string root, string? transactionId, string? intentHash, string? rootId = null)
    {
        var gate = Read(root);
        if (gate is not null && (gate.TransactionId != transactionId || gate.IntentSha256 != intentHash || rootId is not null && gate.RootId != rootId))
            throw new StorageException("storage.maintenanceRequired", "安装维护尚未完成，须由匹配更新事务恢复");
    }

    internal void Publish(PathService paths)
    {
        if (RootId != paths.RootId || Read(paths.DataRoot) is not null) throw Invalid();
        var path = UpdatePaths.Local(paths.DataRoot, RelativePath);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(output, this, UpdateHandoffProtocol.Json); output.Flush(true); }
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal void Remove(PathService paths)
    {
        if (Read(paths.DataRoot) != this || RootId != paths.RootId) throw Invalid();
        File.Delete(UpdatePaths.Local(paths.DataRoot, RelativePath));
    }
    private static UpdatePlanException Invalid() => new("update.installIntentInvalid");
}

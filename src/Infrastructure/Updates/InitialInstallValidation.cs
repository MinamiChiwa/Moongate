using System.Runtime.InteropServices;
using System.Text.Json;

namespace MabiTool.Infrastructure.Updates;

internal sealed record InitialInstallSelection(InstallTargetDescriptor Target, InstallVersion Version,
    IReadOnlyList<ManifestFile> StableFiles, byte[] Configuration, long PeakBytes);

// 只有重新验签且复核实际版本树后才能取得数据初始化授权。
internal sealed class InitialInstallAuthorization
{
    internal InitialInstallRecord Record { get; }
    private InitialInstallAuthorization(InitialInstallRecord record) => Record = record;

    internal static async Task<InitialInstallAuthorization> ReadAsync(string installationRoot, string inputHash,
        TimeProvider? time = null, CancellationToken token = default)
    {
        var record = await InitialInstallRecord.ReadAsync(installationRoot, inputHash, token).ConfigureAwait(false);
        if (record.Step < 2 || record.Step >= 6) throw InitialInstallFiles.Invalid();
        var updaterVersion = JsonSerializer.Deserialize<JsonElement>(record.Configuration!, UpdateHandoffProtocol.Json)
            .GetProperty("runtime").GetProperty("updaterVersion").Deserialize<Version>(UpdateHandoffProtocol.Json)!;
        var selected = await InitialInstallValidation.SelectAsync(record, updaterVersion, time, token).ConfigureAwait(false);
        InitialInstallValidation.RequireSelection(record, selected);
        InitialInstallCopy.RequireDirectories(record.VersionRoot, record.Version!.Files);
        InitialInstallCopy.RequireDirectories(record.StableRoot, record.StableFiles!);
        await FileTreePlanner.VerifyAsync(record.VersionRoot, record.Version!.Files, token).ConfigureAwait(false);
        await FileTreePlanner.VerifyAsync(record.StableRoot, record.StableFiles!, token).ConfigureAwait(false);
        await InitialInstallValidation.RequireDataSnapshotAsync(record, token).ConfigureAwait(false);
        return new(record);
    }
}

internal static class InitialInstallValidation
{
    internal const string ProductId = "mabitool";
    internal static readonly string[] BaseComponents = ["core", "dotnet-runtime", "launcher", "room-server", "updater"];

    internal static async Task<InitialInstallSelection> SelectAsync(InitialInstallRecord record, Version updaterVersion,
        TimeProvider? time = null, CancellationToken token = default)
    {
        record.Validate(); var clock = time ?? TimeProvider.System; var request = record.Request;
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new UpdatePlanException("update.incompatibleComponents");
        using var rootJson = StrictJson.Parse(record.TrustedRoot);
        var root = rootJson.RootElement.Deserialize<TrustRoot>(UpdateHandoffProtocol.Json) ?? throw InitialInstallFiles.Invalid();
        var trust = new UpdateTrustStore(UpdatePaths.Local(request.StateRoot, "trust.json"), root, clock.GetUtcNow());
        var index = trust.VerifyChannelIndex(SignedPayloadEnvelope.Parse(record.SignedIndex), ProductId, request.Channel, clock.GetUtcNow());
        var release = trust.VerifyReleaseManifest(record.SignedManifest, index, "win-x64", clock.GetUtcNow());
        if (BaseComponents.Any(id => !release.Components.TryGetValue(id, out var item) || item.Metadata.IsOptional) ||
            request.OptionalComponents.Any(id => !release.Components.TryGetValue(id, out var item) || !item.Metadata.IsOptional) ||
            release.Components["updater"].Metadata.Version != updaterVersion)
            throw new UpdatePlanException("update.incompatibleComponents");
        var core = release.Components["core"];
        await FileTreePlanner.VerifyAsync(UpdatePaths.Local(request.PackageRoot, "components/core"), core.Artifacts.Files.Select(file => file.File).ToArray(), token).ConfigureAwait(false);
        var target = await InstallTargetDescriptor.ReadAsync(request.PackageRoot, ProductId, token).ConfigureAwait(false);
        if (target.HostVersion != core.Metadata.Version.ToString(3) || target.RuntimeMode != release.RuntimeMode)
            throw new UpdatePlanException("update.installTargetInvalid");
        var context = new UpdatePlannerContext(Version.Parse(target.HostVersion), 1, target.DataSchema, "win-x64",
            Environment.OSVersion.Version, long.MaxValue, []);
        var runtime = new ReleaseRuntimeContext(updaterVersion, Version.Parse(target.HostApiVersion).Major, target.RuntimeMode);
        var plan = new UpdatePlanner(clock).CreatePlan(release, context, runtime, request.OptionalComponents.ToHashSet(StringComparer.Ordinal));
        var files = UpdateSignedPlanVerifier.TargetFiles(plan, release);
        if (target.RuntimeMode == "framework-dependent" && !files.Any(file => file.RelativePath == "components/dotnet-runtime/dotnet.exe") ||
            !files.Any(file => file.RelativePath == "components/launcher/MabiTool.Launcher.dll"))
            throw new UpdatePlanException("update.incompatibleComponents");
        foreach (var component in plan.Components)
            await FileTreePlanner.VerifyAsync(UpdatePaths.Local(request.PackageRoot, "components/" + component.ComponentId),
                release.Components[component.ComponentId].Artifacts.Files.Select(file => file.File).ToArray(), token).ConfigureAwait(false);
        var version = new InstallVersion(plan.ReleaseId, plan.ReleaseSequence, plan.ManifestSha256, files);
        var stable = files.Where(file => file.RelativePath.StartsWith("components/launcher/", StringComparison.Ordinal)).ToArray();
        var configuration = JsonSerializer.SerializeToUtf8Bytes(new
        {
            productId = ProductId, request.InstallationRoot,
            source = new { request.GithubRepository, request.Channel, apiBaseUri = (string?)null, relayBaseUri = (string?)null },
            initialRoot = root, installedReleaseSequence = plan.ReleaseSequence, installedManifestSha256 = plan.ManifestSha256,
            plannerContext = context with { InstalledComponents = plan.Components.Select(component =>
                new InstalledUpdateComponent(component.ComponentId, component.Version, component.InstalledBytes, component.Sha256)).ToArray() },
            runtime, installedReleaseId = plan.ReleaseId, request.BootstrapDirectory
        }, UpdateHandoffProtocol.Json);
        return new(target, version, stable, configuration, checked(plan.PeakBytes + stable.Sum(file => file.SizeBytes)));
    }

    internal static void RequireSelection(InitialInstallRecord record, InitialInstallSelection selected)
    {
        if (!InitialInstallFiles.Same(record.Target, selected.Target) || !InitialInstallFiles.Same(record.Version, selected.Version) ||
            !InitialInstallFiles.Same(record.StableFiles, selected.StableFiles) || !record.Configuration!.AsSpan().SequenceEqual(selected.Configuration))
            throw new UpdatePlanException("update.initialInstallInputMismatch");
    }

    internal static async Task RequireDataSnapshotAsync(InitialInstallRecord record, CancellationToken token)
    {
        if (record.Step < 3) return;
        var data = await SnapshotAsync(record.Request.DataRoot, false, token).ConfigureAwait(false);
        var bootstrap = await SnapshotAsync(record.Request.BootstrapDirectory, true, token).ConfigureAwait(false);
        if (!InitialInstallFiles.Same(data, record.DataFiles) || !InitialInstallFiles.Same(bootstrap, record.BootstrapFiles))
            throw new UpdatePlanException("update.initialInstallDataChanged");
    }

    internal static async Task<IReadOnlyList<ManifestFile>> SnapshotAsync(string root, bool bootstrap, CancellationToken token)
    {
        var files = await FileTreePlanner.ReadCurrentAsync(root, new UpdateIoBudget(), token).ConfigureAwait(false);
        return files.Values.Where(file => !InitialInstallMetadata.IsStorageTemporary(file.RelativePath, bootstrap) &&
            (bootstrap ? file.RelativePath != "bootstrap.lock" :
            file.RelativePath != "writer.lock" && file.RelativePath != UpdateInstallGate.RelativePath))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }
}

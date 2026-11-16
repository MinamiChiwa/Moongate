using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Updates;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;
using MabiTool.Updates.Tests;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace MabiTool.Host.Tests;

[SupportedOSPlatform("windows")]
public sealed class ManagedInstallImages : IAsyncLifetime
{
    private readonly IReadOnlyDictionary<string, string>? packagedImages;
    public ManagedInstallImages() { }
    internal ManagedInstallImages(IReadOnlyDictionary<string, string> images, string initialVersion = "0.1.0")
    { packagedImages = images; InitialVersion = initialVersion; }
    internal string InitialVersion { get; } = "0.1.0";
    internal bool IsPackaged => packagedImages is not null;
    internal static string Repository { get; } = FindRepository();
    internal string Root { get; } = Path.Combine(Repository, "artifacts", "tmp", "managed-install-builds", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, Task<FileTreeArtifacts>> artifacts = new(StringComparer.Ordinal);
    internal string Image(string version, bool bootstrap = false) => packagedImages is not null ? packagedImages[version] : Path.Combine(Root, "images", version + (bootstrap ? "-bootstrap" : ""));
    internal Task<FileTreeArtifacts> ArtifactsAsync(string version, string component, bool bootstrap = false)
    {
        var key = version + (bootstrap ? "-bootstrap" : "") + "/" + component;
        if (!artifacts.TryGetValue(key, out var task))
            artifacts[key] = task = new FileTreeArtifactBuilder().BuildAsync(Path.Combine(Image(version, bootstrap), "components", component), Path.Combine(Root, "packages", key));
        return task;
    }

    public async Task InitializeAsync()
    {
        if (packagedImages is not null) return;
        foreach (var version in new[] { "0.1.0", "0.2.0", "0.3.0" })
        {
            await PublishAsync("src/Host/MabiTool.Host.csproj", version, Path.Combine(Image(version), "components", "core"));
            var output = await CommandAsync([Path.Combine(Image(version), "components", "core", "MabiTool.Host.dll"),
                "--print-install-target", "framework-dependent"]);
            var target = JsonSerializer.Deserialize<InstallTargetDescriptor>(output, UpdateHandoffProtocol.Json)!;
            Assert.Equal(version, target.HostVersion);
            await File.WriteAllTextAsync(Path.Combine(Image(version), InstallTargetDescriptor.RelativePath), output);
        }
        await PublishAsync("src/Updater/MabiTool.Updater.csproj", "0.1.0", Path.Combine(Root, "shared", "updater"));
        await PublishAsync("src/Updater/MabiTool.Updater.csproj", "0.2.0", Path.Combine(Root, "shared", "updater-new"));
        await PublishAsync("src/Launcher/MabiTool.Launcher.csproj", "0.1.0", Path.Combine(Root, "shared", "launcher"));
        foreach (var version in new[] { "0.1.0", "0.2.0", "0.3.0" })
            foreach (var component in new[] { "updater", "launcher" })
                CopyTree(Path.Combine(Root, "shared", component), Path.Combine(Image(version), "components", component));
        CopyTree(Image("0.2.0"), Image("0.2.0", bootstrap: true));
        UpdatePaths.DeleteOwnedTree(Path.Combine(Image("0.2.0", bootstrap: true), "components", "updater"));
        CopyTree(Path.Combine(Root, "shared", "updater-new"), Path.Combine(Image("0.2.0", bootstrap: true), "components", "updater"));
    }

    private async Task PublishAsync(string project, string version, string output) =>
        _ = await CommandAsync(["publish", Path.Combine(Repository, project), "-c", "Release", "--nologo", "--self-contained", "false",
            "-p:UseAppHost=false", "-p:RestoreLockedMode=true", "-p:Version=" + version,
            "-p:ArtifactsPath=" + Path.Combine(Root, "build", Path.GetFileNameWithoutExtension(project) + "-" + version), "-o", output]);

    internal static async Task<string> CommandAsync(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(UpdateProcess.Dotnet())
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Repository, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = UpdateProcess.ReadBoundedAsync(process.StandardOutput, 262144);
        var errors = UpdateProcess.ReadBoundedAsync(process.StandardError, 262144);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { UpdateProcess.Terminate(process); await process.WaitForExitAsync(); }
        var result = await output; var problem = await errors;
        if (process.ExitCode != 0) throw new InvalidOperationException($"测试发行命令退出 {process.ExitCode}: {result}\n{problem}");
        return result.Trim();
    }

    internal static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), false);
    }

    private static string FindRepository()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MabiTool.slnx"))) root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("找不到测试解决方案");
    }

    public Task DisposeAsync() { if (Directory.Exists(Root)) Directory.Delete(Root, true); return Task.CompletedTask; }
}

[SupportedOSPlatform("windows")]
internal sealed class ManagedInstallFixture(ManagedInstallImages images, bool usePackageMigrationHistory = false) : IAsyncDisposable
{
    internal string? EvidenceDirectory { get; set; }
    internal string EvidencePrefix { get; set; } = "managed-install";
    private readonly SignedReleaseFixture signing = new();
    private readonly List<ManagedInstallProcess> processes = [];
    internal string Root => signing.Path("managed");
    internal string Installation => Path.Combine(Root, "installation");
    internal string State => Path.Combine(Installation, "update-state");
    internal string Runtime => Path.Combine(Root, "runtime");
    internal string Discovery => Path.Combine(Runtime, "instance.json");
    internal StorageOptions Storage => new(Path.Combine(Root, "bootstrap"), DataRoot: Path.Combine(Root, "data"));
    internal UpdateInstallationOptions Options { get; private set; } = null!;
    internal UpdatePlan Plan { get; private set; } = null!;
    internal IReadOnlyList<DatabaseMigration> DatabaseMigrations { get; private set; } = BackendApplication.DatabaseMigrations;
    internal SignedReleaseFixture Signing => signing;
    private IReadOnlyList<ManifestFile> targetFiles = [];

    internal async Task InitializeAsync()
    {
        var initialVersion = images.InitialVersion;
        var image = images.Image(initialVersion);
        var packagedInput = Path.Combine(image, "release-input.json");
        var releaseId = "release-" + initialVersion;
        long sequence = 1;
        var channel = "stable";
        if (File.Exists(packagedInput))
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(packagedInput));
            var input = document.RootElement;
            Assert.Equal(initialVersion, input.GetProperty("version").GetString());
            Assert.Equal("mabitool", input.GetProperty("productId").GetString());
            releaseId = input.GetProperty("releaseId").GetString()!;
            sequence = input.GetProperty("releaseSequence").GetInt64();
            channel = input.GetProperty("channel").GetString()!;
        }
        var previous = Path.Combine(Installation, "versions", releaseId);
        if (File.Exists(packagedInput))
            ManagedInstallImages.CopyTree(Path.Combine(image, "components"), Path.Combine(previous, "components"));
        else ManagedInstallImages.CopyTree(image, previous);
        ManagedInstallImages.CopyTree(Path.Combine(previous, "components", "launcher"), Path.Combine(Installation, "launcher", "stable", "components", "launcher"));
        var files = await Inventory(previous);
        var target = await InstallTargetDescriptor.ReadAsync(previous, "mabitool");
        Assert.Equal(initialVersion, target.HostVersion);
        SelectDatabaseMigrations(target);
        var components = await InstalledComponents(previous, initialVersion, packagedInput);
        var manifestHash = FileTreePlanner.TreeHash(files);
        using (var transaction = new InstallTransaction(Installation))
            await transaction.InitializePointerAsync(new(releaseId, sequence, manifestHash, files));
        using (var root = DataRootLease.Open(Storage with { Initialize = true }))
        {
            await using var database = SqliteStore.Open(root, DatabaseMigrations);
            var policy = new UpdatePolicyStore(database);
            await policy.SaveAsync(new(AutomaticChecks: false), UpdatePolicyStore.ETag(policy.Get()));
        }
        Options = new("mabitool", Installation, new("managed/tests", channel, null, null), signing.Root, sequence, manifestHash,
            new(Version.Parse(target.HostVersion), 1, target.DataSchema, "win-x64", Environment.OSVersion.Version, 2_000_000_000, components),
            new(components.Single(component => component.ComponentId == "updater").Version, 1, "framework-dependent"), releaseId, Storage.BootstrapDirectory);
        await SaveOptions();
    }

    internal async Task<UpdatePlan> StageAsync(string version, bool bootstrap = false)
    {
        var targetRoot = images.Image(version, bootstrap); var now = DateTimeOffset.UtcNow;
        var sequence = version == "0.2.0" ? 2 : 3;
        var components = new List<JsonNode>();
        foreach (var id in new[] { "core", "updater", "launcher" })
        {
            var artifacts = await images.ArtifactsAsync(version, id, bootstrap);
            components.Add(Component(id, id == "core" ? version : id == "updater" && bootstrap ? "0.2.0" : "0.1.0", artifacts));
        }
        var payload = signing.Payload(); payload["releaseId"] = "release-" + version; payload["version"] = version;
        payload["releaseSequence"] = sequence; payload["publishedAt"] = JsonValue.Create(now); payload["data"] = new JsonArray();
        if (bootstrap) payload["minUpdaterVersion"] = "0.2.0";
        payload["platforms"] = new JsonArray(new JsonObject
        {
            ["os"] = "windows", ["architecture"] = "x64", ["minimumOsBuild"] = "10.0.19041", ["runtimeMode"] = "framework-dependent",
            ["components"] = new JsonArray(components.ToArray())
        });
        var manifest = signing.Release(payload);
        var envelope = signing.Index(manifest, sequence, sequence, node =>
        { node["issuedAt"] = JsonValue.Create(now); node["expiresAt"] = JsonValue.Create(now.AddHours(4)); });
        var indexBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, SignedReleaseFixture.Options);
        var trust = new UpdateTrustStore(Path.Combine(State, "trust.json"), signing.Root, now);
        var index = trust.VerifyChannelIndex(envelope, "mabitool", "stable", now);
        var release = trust.VerifyReleaseManifest(manifest, index, "win-x64", now);
        Plan = bootstrap ? new UpdatePlanner().CreateUpdaterBootstrapPlan(release, Options.PlannerContext, Options.Runtime)
            : new UpdatePlanner().CreatePlan(release, Options.PlannerContext, Options.Runtime);
        targetFiles = UpdateSignedPlanVerifier.TargetFiles(Plan, release);
        await new UpdatePlanRepository(State).SaveAsync(new(1, Plan, indexBytes, manifest));
        ManagedInstallImages.CopyTree(targetRoot, Path.Combine(Installation, "staging", Plan.PlanId));
        await FileTreePlanner.VerifyAsync(Path.Combine(Installation, "staging", Plan.PlanId), UpdateSignedPlanVerifier.TargetFiles(Plan, release));
        return Plan;
    }

    internal async Task<UpdatePlan> PlanPackagedReleaseAsync(byte[] manifest, long? releaseSequence = null)
    {
        var now = DateTimeOffset.UtcNow;
        var sequence = releaseSequence ?? Options.Validation.InstalledReleaseSequence + 1;
        var envelope = signing.Index(manifest, sequence, sequence, node =>
        { node["issuedAt"] = JsonValue.Create(now); node["expiresAt"] = JsonValue.Create(now.AddHours(4)); node["channel"] = Options.Source.Channel; });
        var trust = new UpdateTrustStore(Path.Combine(State, "trust.json"), signing.Root, now);
        var index = trust.VerifyChannelIndex(envelope, "mabitool", Options.Source.Channel, now);
        var release = trust.VerifyReleaseManifest(manifest, index, "win-x64", now);
        var planner = new UpdatePlanner();
        Plan = Options.Runtime.UpdaterVersion < release.MinimumUpdaterVersion
            ? planner.CreateUpdaterBootstrapPlan(release, Options.PlannerContext, Options.Runtime, new HashSet<string>())
            : planner.CreatePlan(release, Options.PlannerContext, Options.Runtime, new HashSet<string>());
        targetFiles = UpdateSignedPlanVerifier.TargetFiles(Plan, release);
        await new UpdatePlanRepository(State).SaveAsync(new(1, Plan, JsonSerializer.SerializeToUtf8Bytes(envelope, SignedReleaseFixture.Options), manifest));
        return Plan;
    }

    internal async Task AcceptPackagedStageAsync(string stage)
    {
        Directory.CreateDirectory(Path.Combine(Installation, "staging"));
        var destination = Path.Combine(Installation, "staging", Plan.PlanId);
        await UpdatePaths.PublishOwnedDirectoryAsync(stage, destination, CancellationToken.None);
        await FileTreePlanner.VerifyAsync(destination, targetFiles);
    }

    internal async Task<(UpdateInstallIntent Intent, string Hash)> TransferIntentAsync()
    {
        using var installation = new InstallTransaction(Installation);
        var previous = await installation.ReadActiveAsync();
        var stage = Path.Combine(Installation, "staging", Plan.PlanId);
        var candidate = await InstallTargetDescriptor.ReadAsync(stage, "mabitool");
        var previousTarget = await InstallTargetDescriptor.ReadAsync(Path.Combine(Installation, "versions", previous.Version.ReleaseId), "mabitool");
        SelectDatabaseMigrations(previousTarget);
        using var root = DataRootLease.Open(Storage);
        await using var database = SqliteStore.Open(root, DatabaseMigrations);
        var operations = new OperationStore(database);
        var admission = await operations.AdmitUpdateAsync("test-owner", UpdateAction.Apply, Plan, "apply-" + Plan.PlanId, OperationStore.UpdatePlanETag(Plan));
        await operations.BeginUpdateAsync(admission.Snapshot.Id);
        var leases = new ResourceLeaseCoordinator(activityBarrier: database.Activities);
        Assert.True(await operations.TryBeginUpdateMaintenanceAsync(admission.Snapshot.Id, leases));
        using var process = Process.GetCurrentProcess();
        var exitedParent = UpdatePeerIdentity.Capture(process) with { ProcessId = int.MaxValue };
        var intent = new UpdateInstallIntent(1, Guid.NewGuid().ToString("N"), admission.Snapshot.Id, Installation, Storage,
            root.Paths.RootId, root.DatabaseId!, "live", exitedParent, Options.Validation, Plan, previous.Version, previousTarget, candidate,
            candidate.HealthTarget(root.Paths.RootId, root.DatabaseId!, "live", new HashSet<string> { "core" }),
            previousTarget.Migrations, JsonSerializer.SerializeToUtf8Bytes(Options, ContractJson.Options), targetFiles);
        var hash = await intent.SaveAsync();
        new UpdateInstallGate(1, intent.TransactionId, hash, Installation, root.Paths.RootId).Publish(root.Paths);
        return (intent, hash);
    }

    internal async Task ReloadOptionsAsync()
    {
        Options = JsonSerializer.Deserialize<UpdateInstallationOptions>(await File.ReadAllTextAsync(UpdateInstalledConfiguration.PathFor(Installation)), ContractJson.Options)!;
        Options.Validate();
        if (usePackageMigrationHistory)
            SelectDatabaseMigrations(await InstallTargetDescriptor.ReadAsync(
                Path.Combine(Installation, "versions", Options.InstalledReleaseId!), "mabitool"));
    }

    private void SelectDatabaseMigrations(InstallTargetDescriptor target)
    {
        if (!usePackageMigrationHistory) return;
        var history = target.Migrations.ToDictionary(item => (item.ModuleId, item.Version));
        var migrations = BackendApplication.DatabaseMigrations.Where(item => history.ContainsKey((item.ModuleId, item.Version))).ToArray();
        foreach (var migration in migrations)
            Assert.Equal(history[(migration.ModuleId, migration.Version)].Checksum, migration.Checksum);
        Assert.Equal(target.Migrations, SqliteStore.MigrationTargets(migrations));
        DatabaseMigrations = migrations;
    }

    private Task SaveOptions() => File.WriteAllTextAsync(UpdateInstalledConfiguration.PathFor(Installation), JsonSerializer.Serialize(Options, ContractJson.Options));
    private static async Task<IReadOnlyList<InstalledUpdateComponent>> InstalledComponents(string root, string hostVersion, string? inputPath = null)
    {
        var components = new List<InstalledUpdateComponent>();
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        var releaseInput = inputPath ?? Path.Combine(root, "release-input.json");
        if (File.Exists(releaseInput))
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(releaseInput));
            foreach (var component in document.RootElement.GetProperty("components").EnumerateArray())
                versions.Add(component.GetProperty("componentId").GetString()!, component.GetProperty("version").GetString()!);
        }
        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(root, "components")))
        {
            var id = Path.GetFileName(directory);
            var files = await Inventory(Path.Combine(root, "components", id));
            components.Add(new(id, Version.Parse(versions.GetValueOrDefault(id, id == "core" ? hostVersion : "0.1.0")), files.Sum(item => item.SizeBytes), FileTreePlanner.TreeHash(files)));
        }
        return components;
    }

    private static async Task<IReadOnlyList<ManifestFile>> Inventory(string root) =>
        (await FileTreePlanner.ReadCurrentAsync(root, new UpdateIoBudget(), default)).Values.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();

    private static JsonNode Component(string id, string version, FileTreeArtifacts artifacts) => JsonSerializer.SerializeToNode(new
    {
        componentId = id, version, optional = false,
        moduleApiRange = new { minimum = 1, maximum = 1 }, hostVersionRange = new { minimum = "0.1.0", maximum = "9.0.0" },
        workerProtocolRange = new { minimum = 1, maximum = 1 }, dataSchemaRange = new { minimum = 0, maximum = 100 }, dependencies = Array.Empty<object>(),
        installedSize = artifacts.Files.Sum(item => item.File.SizeBytes), targetTreeHash = artifacts.TargetTreeHash,
        files = artifacts.Files.Select(item => new { path = item.File.RelativePath, size = item.File.SizeBytes, sha256 = item.File.Sha256,
            componentId = id, executable = item.File.RelativePath.EndsWith(".dll", StringComparison.Ordinal),
            encoding = item.Encoding == UpdateBlobEncoding.Gzip ? "gzip" : "identity", asset = Asset(item.DownloadLength, item.DownloadSha256) }),
        fullPackage = Asset(artifacts.FullPackage!.DownloadLength, artifacts.FullPackage.DownloadSha256)
    }, SignedReleaseFixture.Options)!;

    private static object Asset(long length, string sha256) => new { assetName = sha256 + ".blob", length, sha256 };

    internal ManagedInstallProcess StartProbe(string point)
    {
        var buildRoot = Path.GetFullPath("../../..", AppContext.BaseDirectory);
        var dll = Path.Combine(buildRoot, "bin", "MabiTool.PersistenceProbe", "release", "MabiTool.PersistenceProbe.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException("缺少与本次测试匹配的安装探针", dll);
        return Start(dll, ["managed-install", Installation, point, Path.Combine(Root, "pause")]);
    }

    internal ManagedInstallProcess StartHost()
    {
        var dll = Path.Combine(Installation, "versions", Options.InstalledReleaseId!, "components", "core", "MabiTool.Host.dll");
        return Start(dll, []);
    }

    internal ManagedInstallProcess StartUpdaterRecovery() => Start(Path.Combine(Installation, "versions", Options.InstalledReleaseId!,
        "components", "updater", "MabiTool.Updater.dll"), ["--recover", Installation, "--no-launch"]);

    internal ManagedInstallProcess StartBootstrapProbe(string phase)
    {
        var buildRoot = Path.GetFullPath("../../..", AppContext.BaseDirectory);
        return Start(Path.Combine(buildRoot, "bin", "MabiTool.PersistenceProbe", "release", "MabiTool.PersistenceProbe.dll"),
            ["managed-bootstrap", Installation, phase, Path.Combine(Root, "pause")]);
    }

    private ManagedInstallProcess Start(string dll, IEnumerable<string> arguments)
    {
        string? bundledRuntime = null;
        if (images.IsPackaged)
        {
            var parent = new DirectoryInfo(Path.GetDirectoryName(dll)!);
            while (parent is not null && parent.Name != "components") parent = parent.Parent;
            if (parent is null) throw new InvalidOperationException("实际包进程入口不在组件目录内");
            bundledRuntime = Path.Combine(parent.FullName, "dotnet-runtime");
            if (!File.Exists(Path.Combine(bundledRuntime, "dotnet.exe"))) throw new InvalidOperationException("实际包缺少随包运行时");
        }
        var start = new ProcessStartInfo(bundledRuntime is null ? UpdateProcess.Dotnet() : Path.Combine(bundledRuntime, "dotnet.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Root };
        start.ArgumentList.Add(dll); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["MABITOOL_BOOTSTRAP_DIRECTORY"] = Storage.BootstrapDirectory;
        start.Environment["MABITOOL_UPDATE_CONFIGURATION"] = UpdateInstalledConfiguration.PathFor(Installation);
        start.Environment["MABITOOL_API_PORT"] = "0"; start.Environment["MABITOOL_RUNTIME_DIRECTORY"] = Runtime;
        start.Environment.Remove("MABITOOL_API_TOKEN"); start.Environment["MABITOOL_ALLOWED_ORIGINS"] = "";
        if (bundledRuntime is not null)
        {
            start.Environment["DOTNET_ROOT"] = bundledRuntime;
            start.Environment["DOTNET_ROOT_X64"] = bundledRuntime;
            start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        }
        var process = new ManagedInstallProcess(start); processes.Add(process); return process;
    }

    internal async Task<BootstrapResponse> ConnectAsync(ManagedInstallProcess original, string? previousEpoch = null, CancellationToken token = default,
        TimeSpan? maximumWait = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(maximumWait ?? TimeSpan.FromSeconds(90));
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (File.Exists(Discovery))
                {
                    try
                    {
                        var discovery = JsonSerializer.Deserialize<LocalDiscoveryInfo>(await File.ReadAllTextAsync(Discovery, deadline.Token), ContractJson.Options)!;
                        if (previousEpoch is null ? discovery.ProcessId == original.Process.Id : discovery.InstanceId != previousEpoch)
                        {
                            var response = await LocalBootstrapClient.ConnectAsync(Discovery, "managed-install-test", cancellationToken: deadline.Token);
                            if (previousEpoch is null || response.InstanceId != previousEpoch) return response;
                        }
                    }
                    catch (Exception error) when (error is IOException or JsonException or HttpRequestException) { }
                }
                if (previousEpoch is null && original.Process.HasExited) throw new InvalidOperationException(await original.Errors);
                if (previousEpoch is not null && original.Process.HasExited && original.ActiveProcesses == 0)
                {
                    await SaveEvidenceAsync();
                    throw new InvalidOperationException("升级进程树已全部退出，未发布新的宿主发现记录");
                }
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException error)
        {
            string diagnostic;
            try { diagnostic = JsonSerializer.Serialize(await ManagedInstallEvidence.ReadAsync(Installation)); }
            catch (Exception) { diagnostic = "evidence-unavailable"; }
            await SaveEvidenceAsync();
            try { await original.KillAsync(); } catch (Exception) { }
            throw new TimeoutException($"宿主发现超时，previousEpoch={previousEpoch}; state={diagnostic}; processes={JsonSerializer.Serialize(original.ProcessHistory())}", error);
        }
    }

    internal async Task SaveEvidenceAsync()
    {
        if (EvidenceDirectory is null) return;
        try
        {
            await ManagedInstallEvidence.TrySaveAsync(Installation, EvidenceDirectory, EvidencePrefix,
                processes.SelectMany(process => process.ProcessHistory()).Take(64).ToArray(),
                processes.Select(process => process.OutputEvidence()).Take(64).ToArray(), Storage.DataRoot);
        }
        catch (Exception) { /* Evidence collection must preserve the installation failure. */ }
    }

    public async ValueTask DisposeAsync()
    {
        await SaveEvidenceAsync();
        foreach (var process in processes) await process.DisposeAsync();
        await SaveEvidenceAsync();
        signing.Dispose();
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ManagedInstallProcess : IAsyncDisposable
{
    private readonly SafeFileHandle job;
    private readonly CancellationTokenSource historyStop = new();
    private readonly Task historyTask;
    private readonly object historyGate = new();
    private readonly Dictionary<int, (Process Process, ManagedInstallProcessHistory Info)> history = [];
    internal Process Process { get; }
    internal Task<string> Output { get; }
    internal Task<string> Errors { get; }
    internal uint ActiveProcesses
    {
        get
        {
            if (!QueryInformationJobObject(job, 1, out JobAccounting accounting, (uint)Marshal.SizeOf<JobAccounting>(), IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception();
            return accounting.ActiveProcesses;
        }
    }

    internal IReadOnlyList<ManagedInstallProcessInfo> ActiveProcessList()
    {
        const int capacity = 1024;
        var length = 8 + capacity * IntPtr.Size;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!QueryProcessList(job, 3, buffer, (uint)length, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception();
            var count = Marshal.ReadInt32(buffer, 4);
            if (count is < 0 or > capacity) throw new InvalidOperationException("测试进程列表超过容量");
            var entries = new List<ManagedInstallProcessInfo>();
            for (var index = 0; index < count; index++)
            {
                var id = checked((int)Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size).ToInt64());
                Process process;
                try
                {
                    process = Process.GetProcessById(id);
                }
                catch (ArgumentException) { continue; }
                using (process)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        var name = process.ProcessName;
                        var executable = process.MainModule?.FileName;
                        if (process.HasExited) continue;
                        if (string.IsNullOrWhiteSpace(executable))
                            throw new InvalidOperationException("存活进程没有可读映像路径");
                        entries.Add(new(id, name, executable));
                    }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
                }
            }
            return entries;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal bool HasOnlyHostAndConsole(int hostId)
    {
        var processes = ActiveProcessList();
        var console = Path.Combine(Environment.SystemDirectory, "conhost.exe");
        return processes.Count is 1 or 2 && processes.Any(item => item.Id == hostId) &&
            processes.All(item => item.Id == hostId || item.Executable.Equals(console, StringComparison.OrdinalIgnoreCase));
    }

    internal string DescribeActiveProcesses() => $"active={ActiveProcesses}; processes=" +
        string.Join(",", ActiveProcessList().Select(item => $"{item.Id}:{item.Name}:{item.Executable}"));

    internal ManagedInstallProcess(ProcessStartInfo start)
    {
        job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new System.ComponentModel.Win32Exception();
        var limits = new JobLimits { Basic = new() { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>())) throw new System.ComponentModel.Win32Exception();
        Process = Process.Start(start)!;
        if (!AssignProcessToJobObject(job, Process.SafeHandle)) { UpdateProcess.Terminate(Process); throw new System.ComponentModel.Win32Exception(); }
        Output = UpdateProcess.ReadBoundedAsync(Process.StandardOutput); Errors = UpdateProcess.ReadBoundedAsync(Process.StandardError);
        Observe(Process.Id, start.ArgumentList.FirstOrDefault(argument => argument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));
        historyTask = ObserveHistoryAsync();
    }

    private void Observe(int id, string? managedEntry = null)
    {
        lock (historyGate)
        {
            if (history.ContainsKey(id) || history.Count >= 64) return;
            Process? process = null;
            try
            {
                process = Process.GetProcessById(id);
                // Opening the handle retains exit information after a short-lived updater disappears.
                _ = process.Handle;
                var identity = UpdatePeerIdentity.Capture(process);
                history.Add(id, (process, new(id, identity.ExecutablePath, managedEntry, identity.StartTimeUtcTicks, null)));
                process = null;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UpdatePlanException) { }
            finally { process?.Dispose(); }
        }
    }

    private async Task ObserveHistoryAsync()
    {
        while (!historyStop.IsCancellationRequested)
        {
            try { foreach (var process in ActiveProcessList()) Observe(process.Id); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException) { }
            _ = ProcessHistory();
            try { await Task.Delay(25, historyStop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal IReadOnlyList<ManagedInstallProcessHistory> ProcessHistory()
    {
        lock (historyGate)
        {
            foreach (var pair in history.ToArray())
            {
                try
                {
                    if (pair.Value.Process.HasExited)
                        history[pair.Key] = (pair.Value.Process, pair.Value.Info with { ExitCode = pair.Value.Process.ExitCode });
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            return history.Values.Select(value => value.Info).ToArray();
        }
    }

    internal InstallOutputEvidence OutputEvidence() => new(Process.Id, Output.IsCompletedSuccessfully && Errors.IsCompletedSuccessfully,
        Output.IsCompletedSuccessfully ? ManagedInstallEvidence.SafeOutput(Output.Result) : null,
        Errors.IsCompletedSuccessfully ? ManagedInstallEvidence.SafeOutput(Errors.Result) : null);

    internal async Task KillAsync()
    {
        historyStop.Cancel();
        try { await historyTask; }
        finally
        {
            job.Dispose(); UpdateProcess.Terminate(Process); await Process.WaitForExitAsync();
        }
        _ = ProcessHistory();
        _ = await Output; _ = await Errors;
    }
    public async ValueTask DisposeAsync()
    {
        try { await KillAsync(); }
        finally
        {
            Process.Dispose(); historyStop.Dispose();
            lock (historyGate) foreach (var value in history.Values) value.Process.Dispose();
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long PerProcessUserTime, PerJobUserTime; public uint LimitFlags; public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits
    { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential)] private struct JobAccounting
    { public long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out JobAccounting accounting, uint length, IntPtr returnedLength);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryProcessList(SafeFileHandle job, int informationClass, IntPtr information, uint length, IntPtr returnedLength);
}

internal sealed record ManagedInstallProcessInfo(int Id, string Name, string Executable);
internal sealed record ManagedInstallProcessHistory(int ProcessId, string ExecutablePath, string? ManagedEntry, long StartTimeUtcTicks, int? ExitCode);

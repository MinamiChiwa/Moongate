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
using Xunit;

namespace MabiTool.Host.Tests;

[Trait("Category", "Slow")]
[Collection("Persistent backend")]
[SupportedOSPlatform("windows")]
public sealed class PackagedInitialInstallTests(InitialInstallImages images) : IClassFixture<InitialInstallImages>
{
    [Fact]
    public async Task RealInstallAndAbsoluteWrapperStartTheSameHostAndDataFromAnotherDirectory()
    {
        await using var fixture = await PackageFixture.CreateAsync(images);
        var installed = await fixture.InstallAsync(); Assert.Equal(0, installed.ExitCode);
        var record = await InitialInstallRecord.ReadAsync(fixture.Request.InstallationRoot);
        Assert.Equal("complete", record.Phase); Assert.NotNull(record.Health);
        Assert.Equal(BackendApplication.HostVersion, record.Health.HostVersion);
        Assert.Equal("1.0", record.Health.ApiVersion);
        Assert.Contains(record.Health.Modules, module => module.Id == "core");
        Assert.False(Directory.Exists(Path.Combine(record.VersionRoot, "components", "color-engine")));
        Assert.False(Directory.Exists(Path.Combine(record.VersionRoot, "components", "observation-worker")));
        using (var lease = DataRootLease.Open(record.Request.Storage))
        {
            await using var database = SqliteStore.Open(lease, BackendApplication.DatabaseMigrations);
            var policy = new UpdatePolicyStore(database);
            await policy.SaveAsync(new(AutomaticChecks: false), UpdatePolicyStore.ETag(policy.Get()));
        }
        var sentinel = Path.Combine(record.Request.DataRoot, "settings", "initial-install-sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "same-data-root");
        var launches = new List<object>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var launchDirectory = Path.Combine(fixture.WorkingDirectory, "launch-" + attempt);
            Directory.CreateDirectory(launchDirectory);
            var launched = await fixture.LaunchAsync(launchDirectory); Assert.Equal(0, launched.ExitCode);
            var bootstrap = await fixture.ConnectAsync();
            using var client = new MabiToolClient(new(bootstrap.ApiBaseUri), _ => ValueTask.FromResult(bootstrap.Grant.Token));
            var system = await client.GetSystemInfoAsync(); var ready = await client.GetReadyAsync();
            var moduleList = await client.GetModulesAsync(); var observationRuntime = await client.GetGameObservationRuntimeAsync();
            Assert.Contains(moduleList.Items, module => module.Id == "core");
            Assert.False(observationRuntime.Installed); Assert.Equal(0, observationRuntime.SubscriberCount); Assert.Null(observationRuntime.WorkerProcessId);
            Assert.Equal(record.Target!.HostVersion, system.Version); Assert.Equal(record.Target.HostApiVersion, system.ApiVersion);
            Assert.Equal(bootstrap.InstanceId, system.InstanceId);
            Assert.Equal("ready", ready.Status);
            Assert.Equal("same-data-root", await File.ReadAllTextAsync(sentinel));
            var configuration = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(record.Request.BootstrapDirectory, "data-root.json")))!;
            Assert.Equal(record.Request.DataRoot, configuration["dataRoot"]!.GetValue<string>());
            Assert.Equal(record.RootId, configuration["rootId"]!.GetValue<string>());
            var discovery = await InitialInstallFiles.ReadJsonAsync<LocalDiscoveryInfo>(fixture.Discovery, 16384);
            using var host = Process.GetProcessById(discovery.ProcessId);
            Assert.False(host.HasExited);
            var identity = UpdatePeerIdentity.Capture(host);
            Assert.Equal(UpdatePaths.Local(record.VersionRoot, "components/dotnet-runtime/dotnet.exe"), identity.ExecutablePath, StringComparer.OrdinalIgnoreCase);
            await fixture.StopHostAsync(host);
            using var reopened = DataRootLease.Open(record.Request.Storage);
            Assert.Equal(record.DatabaseId, reopened.DatabaseId);
            launches.Add(new { system.Version, system.ApiVersion, ready = ready.Status, processId = identity.ProcessId,
                exited = host.HasExited, workingDirectory = launchDirectory, modules = moduleList.Items.Select(module => module.Id).ToArray(),
                observation = new { observationRuntime.Installed, observationRuntime.SubscriberCount, observationRuntime.WorkerProcessId } });
        }
        await fixture.EvidenceAsync("install-and-relaunch", new { result = "passed", record.Phase, record.RootId, record.DatabaseId,
            sourceRuntime = images.RuntimeVersion, launches, sameDataRoot = record.Request.DataRoot, workingDirectory = fixture.WorkingDirectory });
    }

    [Fact]
    public async Task TerminatedRealUpdaterResumesFixedInputAndWrapperBlocksIncompleteInstallation()
    {
        await using var fixture = await PackageFixture.CreateAsync(images);
        using (var installer = fixture.StartInstall())
        {
            var output = UpdateProcess.ReadBoundedAsync(installer.StandardOutput); var errors = UpdateProcess.ReadBoundedAsync(installer.StandardError);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (installer.HasExited) throw new InvalidOperationException("初装未到终止边界: " + await errors);
                if (File.Exists(fixture.Request.JournalPath))
                {
                    var current = await InitialInstallRecord.ReadAsync(fixture.Request.InstallationRoot, token: deadline.Token);
                    if (current.Phase == "filesPublished") break;
                    if (current.Step > 2) throw new InvalidOperationException("错过初装终止边界");
                }
                await Task.Delay(25, deadline.Token);
            }
            UpdateProcess.Terminate(installer); await installer.WaitForExitAsync(deadline.Token); _ = await output; _ = await errors;
        }
        var interrupted = await InitialInstallRecord.ReadAsync(fixture.Request.InstallationRoot);
        Assert.Equal("filesPublished", interrupted.Phase);
        var blocked = await fixture.LaunchAsync(); Assert.NotEqual(0, blocked.ExitCode);
        var resumed = await fixture.InstallAsync(); Assert.Equal(0, resumed.ExitCode);
        var completed = await InitialInstallRecord.ReadAsync(fixture.Request.InstallationRoot);
        Assert.Equal(interrupted.InputSha256, completed.InputSha256); Assert.Equal(interrupted.RootId, completed.RootId);
        Assert.Equal(interrupted.DatabaseId, completed.DatabaseId); Assert.Equal("complete", completed.Phase);
        Assert.Null(UpdateInstallGate.Read(completed.Request.DataRoot));
        using var lease = DataRootLease.Open(completed.Request.Storage);
        Assert.Equal(completed.DatabaseId, lease.DatabaseId);
        await fixture.EvidenceAsync("terminated-and-resumed", new { result = "passed", boundary = interrupted.Phase,
            completed.Phase, completed.InputSha256, completed.RootId, completed.DatabaseId, blockedExitCode = blocked.ExitCode });
    }

    [Fact]
    public async Task RealInstallerRejectsExpiredAuthorizationBeforeCreatingData()
    {
        await using var fixture = await PackageFixture.CreateAsync(images, expired: true);
        var result = await fixture.InstallAsync(); Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("update.metadataExpired", result.Errors);
        Assert.False(Directory.Exists(fixture.Request.DataRoot));
        Assert.False(File.Exists(UpdateInstalledConfiguration.PathFor(fixture.Request.InstallationRoot)));
        Assert.False(File.Exists(Path.Combine(fixture.Request.InstallationRoot, "launcher", "active-version.json")));
        var record = await InitialInstallRecord.ReadAsync(fixture.Request.InstallationRoot);
        Assert.Equal("captured", record.Phase); Assert.Equal("update.metadataExpired", record.FailureCode);
        await fixture.EvidenceAsync("expired-index", new { result = "passed", exitCode = result.ExitCode, record.Phase, record.FailureCode, dataCreated = false });
    }

    [Fact]
    public async Task NormalHostEntryRefusesIncompleteMissingOrDamagedInitialLog()
    {
        await using var fixture = await PackageFixture.CreateAsync(images);
        Directory.CreateDirectory(fixture.Request.InstallationRoot);
        Directory.CreateDirectory(fixture.Request.StateRoot);
        var root = await File.ReadAllBytesAsync(fixture.Request.TrustRootPath);
        var index = await File.ReadAllBytesAsync(fixture.Request.ChannelIndexPath);
        var manifest = await File.ReadAllBytesAsync(fixture.Request.ReleaseManifestPath);
        var record = new InitialInstallRecord(1, fixture.Request.Normalize(), InitialInstallRecord.InputHash(fixture.Request.Normalize(), root, index, manifest),
            root, index, manifest, "captured");
        await InitialInstallFiles.WriteAsync(fixture.Request.JournalPath, JsonSerializer.SerializeToUtf8Bytes(record, UpdateHandoffProtocol.Json), false, default);
        Directory.CreateDirectory(fixture.Request.BootstrapDirectory);
        var reference = Path.Combine(fixture.Request.BootstrapDirectory, InitialInstallReference.FileName);
        await File.WriteAllTextAsync(reference, JsonSerializer.Serialize(InitialInstallReference.For(record), UpdateHandoffProtocol.Json));
        Assert.Equal("update.initialInstallIncomplete", Assert.Throws<UpdatePlanException>(() => InitialInstallProgram.RequireCompleted(fixture.Request.Storage)).Code);
        File.Move(fixture.Request.JournalPath, fixture.Request.JournalPath + ".retained");
        Assert.Equal("update.initialInstallStateInvalid", Assert.Throws<UpdatePlanException>(() => InitialInstallProgram.RequireCompleted(fixture.Request.Storage)).Code);
        await File.WriteAllTextAsync(fixture.Request.JournalPath, "{");
        Assert.Equal("update.initialInstallStateInvalid", Assert.Throws<UpdatePlanException>(() => InitialInstallProgram.RequireCompleted(fixture.Request.Storage)).Code);
        Assert.False(Directory.Exists(fixture.Request.DataRoot));
    }

    private sealed record CommandResult(int ExitCode, string Output, string Errors);

    private sealed class PackageFixture : IAsyncDisposable
    {
        private readonly SignedReleaseFixture signing = new();
        private readonly List<UpdatePeerIdentity> activeHosts = [];
        private PackageFixture() { }
        internal InitialInstallRequest Request { get; private set; } = null!;
        internal string RequestPath => signing.Path("initial-request.json");
        internal string WorkingDirectory => signing.Path("another-working-directory");
        internal string Runtime => signing.Path("runtime");
        internal string Discovery => Path.Combine(Runtime, "instance.json");

        internal static async Task<PackageFixture> CreateAsync(InitialInstallImages images, bool expired = false)
        {
            var fixture = new PackageFixture(); var now = DateTimeOffset.UtcNow;
            var payload = fixture.signing.Payload(); var template = payload["platforms"]![0]!["components"]![0]!;
            var components = new JsonArray();
            foreach (var id in InitialInstallValidation.BaseComponents)
            {
                var files = images.Files[id]; var component = template.DeepClone(); component["componentId"] = id;
                component["version"] = BackendApplication.HostVersion; component["optional"] = false;
                component["dataSchemaRange"] = JsonSerializer.SerializeToNode(new { minimum = 0, maximum = int.MaxValue });
                component["installedSize"] = files.Sum(file => file.SizeBytes); component["targetTreeHash"] = FileTreePlanner.TreeHash(files);
                component["files"] = JsonSerializer.SerializeToNode(files.Select(file => new { path = file.RelativePath, size = file.SizeBytes, sha256 = file.Sha256,
                    componentId = id, executable = file.RelativePath.EndsWith(".dll", StringComparison.Ordinal) || file.RelativePath.EndsWith(".exe", StringComparison.Ordinal),
                    encoding = "identity", asset = new { assetName = file.Sha256 + ".blob", length = file.SizeBytes, sha256 = file.Sha256 } }), SignedReleaseFixture.Options);
                components.Add(component);
            }
            payload["platforms"]![0]!["components"] = components; payload["platforms"]![0]!["runtimeMode"] = "framework-dependent";
            payload["minUpdaterVersion"] = BackendApplication.HostVersion; payload["publishedAt"] = JsonValue.Create(now); payload["data"] = new JsonArray();
            var manifest = fixture.signing.Release(payload);
            await File.WriteAllBytesAsync(fixture.signing.Path("manifest.json"), manifest);
            var index = fixture.signing.Index(manifest, mutate: node =>
            {
                node["issuedAt"] = JsonValue.Create(now.AddMinutes(expired ? -10 : -1));
                node["expiresAt"] = JsonValue.Create(now.AddMinutes(expired ? -1 : 120));
            });
            await File.WriteAllTextAsync(fixture.signing.Path("index.json"), JsonSerializer.Serialize(index, SignedReleaseFixture.Options));
            await File.WriteAllTextAsync(fixture.signing.Path("root.json"), JsonSerializer.Serialize(fixture.signing.Root, SignedReleaseFixture.Options));
            fixture.Request = new(1, images.Image, fixture.signing.Path("manifest.json"), fixture.signing.Path("index.json"), fixture.signing.Path("root.json"),
                fixture.signing.Path("installation"), fixture.signing.Path("bootstrap"), fixture.signing.Path("data"), "initial/tests", "stable", []);
            Directory.CreateDirectory(fixture.WorkingDirectory); return fixture;
        }

        internal Process StartInstall() => StartScript("install-backend.ps1", ["-PackageRoot", Request.PackageRoot, "-ReleaseManifest", Request.ReleaseManifestPath,
            "-ChannelIndex", Request.ChannelIndexPath, "-TrustRoot", Request.TrustRootPath, "-InstallationRoot", Request.InstallationRoot,
            "-BootstrapDirectory", Request.BootstrapDirectory, "-DataRoot", Request.DataRoot, "-GithubRepository", Request.GithubRepository, "-RequestFile", RequestPath]);
        internal Task<CommandResult> InstallAsync() => CompleteAsync(StartInstall());
        internal Task<CommandResult> LaunchAsync(string? workingDirectory = null) => CompleteAsync(StartScript("launch-installed-backend.ps1", ["-InstallationRoot", Request.InstallationRoot], workingDirectory));
        private Process StartScript(string name, IEnumerable<string> arguments, string? workingDirectory = null)
        {
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = workingDirectory ?? WorkingDirectory };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(InitialInstallImages.Repository, "packaging", name) }.Concat(arguments))
                start.ArgumentList.Add(arg);
            start.Environment["MABITOOL_RUNTIME_DIRECTORY"] = Runtime; start.Environment["MABITOOL_API_PORT"] = "0";
            start.Environment["MABITOOL_ALLOWED_ORIGINS"] = ""; start.Environment.Remove("MABITOOL_API_TOKEN");
            start.Environment.Remove("MABITOOL_OBSERVATION_EXECUTABLE"); start.Environment.Remove("MABITOOL_COLOR_PYTHON");
            start.Environment.Remove("MABITOOL_COLOR_WORKER_DIRECTORY");
            return Process.Start(start)!;
        }
        private static async Task<CommandResult> CompleteAsync(Process process)
        {
            using (process)
            {
                var output = UpdateProcess.ReadBoundedAsync(process.StandardOutput); var errors = UpdateProcess.ReadBoundedAsync(process.StandardError);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
                try { await process.WaitForExitAsync(deadline.Token); }
                finally { if (!process.HasExited) { UpdateProcess.Terminate(process); await process.WaitForExitAsync(); } }
                // 启动脚本会拉起长期运行的宿主，宿主继承脚本的输出管道，管道在宿主退出前不会 EOF；
                // 进程退出时脚本已写入的内容都在管道中，读取只等待有限时间。
                return new(process.ExitCode, await DrainAsync(output), await DrainAsync(errors));
            }
        }
        private static async Task<string> DrainAsync(Task<string> reader)
        {
            var finished = await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(3)));
            if (finished == reader) return await reader;
            _ = reader.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return "";
        }
        internal async Task<BootstrapResponse> ConnectAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (File.Exists(Discovery))
                {
                    try
                    {
                        var response = await LocalBootstrapClient.ConnectAsync(Discovery, "initial-install-test", cancellationToken: deadline.Token);
                        var discovery = await InitialInstallFiles.ReadJsonAsync<LocalDiscoveryInfo>(Discovery, 16384, deadline.Token);
                        using var process = Process.GetProcessById(discovery.ProcessId);
                        var identity = UpdatePeerIdentity.Capture(process);
                        if (identity.StartTimeUtcTicks != discovery.ProcessStartTime.UtcDateTime.Ticks) throw new InvalidOperationException("宿主创建身份不匹配");
                        activeHosts.Add(identity); return response;
                    }
                    catch (Exception error) when (error is IOException or JsonException or HttpRequestException or ArgumentException) { }
                }
                await Task.Delay(100, deadline.Token);
            }
        }
        internal async Task StopHostAsync(Process process)
        {
            var identity = UpdatePeerIdentity.Capture(process);
            Assert.Contains(identity, activeHosts); UpdateProcess.Terminate(process);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await process.WaitForExitAsync(deadline.Token);
            activeHosts.Remove(identity);
        }
        internal async Task EvidenceAsync(string name, object value)
        {
            var directory = Path.Combine(InitialInstallImages.Repository, "artifacts", "test-results", "initial-install-implementation", "packaged");
            Directory.CreateDirectory(directory); await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(value, SignedReleaseFixture.Options));
        }
        public async ValueTask DisposeAsync()
        {
            if (File.Exists(Discovery))
            {
                try
                {
                    var discovery = await InitialInstallFiles.ReadJsonAsync<LocalDiscoveryInfo>(Discovery, 16384);
                    using var host = Process.GetProcessById(discovery.ProcessId);
                    var identity = UpdatePeerIdentity.Capture(host);
                    if (identity.StartTimeUtcTicks == discovery.ProcessStartTime.UtcDateTime.Ticks &&
                        StoragePaths.IsWithin(identity.ExecutablePath, Request.InstallationRoot) && !activeHosts.Contains(identity)) activeHosts.Add(identity);
                }
                catch (Exception error) when (error is ArgumentException or IOException or JsonException or UpdatePlanException) { }
            }
            foreach (var identity in activeHosts.ToArray())
            {
                try { using var host = Process.GetProcessById(identity.ProcessId); if (UpdatePeerIdentity.Capture(host) == identity) { UpdateProcess.Terminate(host); await host.WaitForExitAsync(); } }
                catch (Exception error) when (error is ArgumentException or UpdatePlanException) { }
            }
            signing.RootSigner.Dispose(); signing.IndexSigner.Dispose(); signing.ReleaseSigner.Dispose();
            InitialInstallImages.DeleteWorkspace(signing.Path(""), Path.Combine(InitialInstallImages.Repository, "artifacts", "tmp", "update-tests"));
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class InitialInstallImages : IAsyncLifetime
{
    internal static string Repository => ManagedInstallImages.Repository;
    // 安装镜像是可再生成的工作区，放在 test-data 并在夹具释放时删除；test-results 只留报告。
    private static string ImagesParent => Path.Combine(Repository, "artifacts", "test-data", "initial-install-images");
    internal string Root { get; } = Path.Combine(ImagesParent, Guid.NewGuid().ToString("N"));
    internal string Image => Path.Combine(Root, "image");
    internal string RuntimeVersion { get; private set; } = "";
    internal Dictionary<string, IReadOnlyList<ManifestFile>> Files { get; } = new(StringComparer.Ordinal);

    public async Task InitializeAsync()
    {
        var build = Path.GetFullPath("../../..", AppContext.BaseDirectory);
        foreach (var (component, project) in new[] { ("core", "MabiTool.Host"), ("updater", "MabiTool.Updater"), ("launcher", "MabiTool.Launcher"), ("room-server", "MabiTool.RoomServer") })
        {
            var source = Path.Combine(build, "bin", project, "release");
            Assert.True(Directory.Exists(source), "隔离 build 缺少 " + project);
            ManagedInstallImages.CopyTree(source, Path.Combine(Image, "components", component));
        }
        await File.WriteAllTextAsync(Path.Combine(Image, InstallTargetDescriptor.RelativePath),
            JsonSerializer.Serialize(BackendApplication.InstallationTarget("framework-dependent"), UpdateHandoffProtocol.Json));
        var runtimeDirectory = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        RuntimeVersion = Path.GetFileName(runtimeDirectory); var runtimeRoot = Path.GetDirectoryName(UpdateProcess.Dotnet())!;
        var runtime = Path.Combine(Image, "components", "dotnet-runtime"); Directory.CreateDirectory(runtime);
        File.Copy(UpdateProcess.Dotnet(), Path.Combine(runtime, "dotnet.exe"));
        ManagedInstallImages.CopyTree(runtimeDirectory, Path.Combine(runtime, "shared", "Microsoft.NETCore.App", RuntimeVersion));
        var aspnet = Directory.EnumerateDirectories(Path.Combine(runtimeRoot, "shared", "Microsoft.AspNetCore.App"))
            .Where(path => Version.TryParse(Path.GetFileName(path), out var version) && version.Major == Version.Parse(RuntimeVersion).Major)
            .OrderByDescending(path => Version.Parse(Path.GetFileName(path))).First();
        ManagedInstallImages.CopyTree(aspnet, Path.Combine(runtime, "shared", "Microsoft.AspNetCore.App", Path.GetFileName(aspnet)));
        var fxr = Directory.EnumerateDirectories(Path.Combine(runtimeRoot, "host", "fxr"))
            .Where(path => Version.TryParse(Path.GetFileName(path), out var version) && version.Major == Version.Parse(RuntimeVersion).Major)
            .OrderByDescending(path => Version.Parse(Path.GetFileName(path))).First();
        ManagedInstallImages.CopyTree(fxr, Path.Combine(runtime, "host", "fxr", Path.GetFileName(fxr)));
        foreach (var id in InitialInstallValidation.BaseComponents)
            Files[id] = (await FileTreePlanner.ReadCurrentAsync(Path.Combine(Image, "components", id), new(), default)).Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }
    public Task DisposeAsync()
    {
        var root = Path.GetFullPath(Root);
        if (!StoragePaths.IsWithin(root, ImagesParent) || root.Equals(Path.GetFullPath(ImagesParent), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException();
        UpdatePaths.DeleteOwnedTree(root);
        return Task.CompletedTask;
    }
    internal static void DeleteWorkspace(string path, string expectedParent)
    {
        var source = Path.GetFullPath(path);
        if (!StoragePaths.IsWithin(source, expectedParent) || source.Equals(expectedParent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        UpdatePaths.DeleteOwnedTree(source);
    }
}

using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Updates;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;
using MabiTool.PortableUpdate;
using MabiTool.Providers.GitHubReleases;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>
/// 解压版整包更新：版本比较与禁止降级、SHA-256 与 zip 结构校验、替换器的成功、等待超时、健康失败回退并恢复数据库、
/// 中断后恢复与文件夹搬移后恢复。替换器核心逻辑直接调用，启动程序由测试钩子代替，不启动真实前端，不访问网络。
/// </summary>
[Collection("Persistent backend")]
public sealed class PortableUpdateTests : IDisposable
{
    private const string Repository = "MinamiChiwa/Moongate";
    private readonly string root = Path.Combine(StorageTestWorkspace.Repository, "artifacts", "tmp", "portable-update-" + Guid.NewGuid().ToString("N"));

    public PortableUpdateTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [Fact]
    public async Task CheckOffersOnlyNewerReleasesInTheChannel()
    {
        Assert.True(ReleaseVersion.Parse("0.2.0-preview").CompareTo(ReleaseVersion.Parse("0.2.0")) < 0);
        Assert.True(ReleaseVersion.Parse("0.2.0").CompareTo(ReleaseVersion.Parse("0.2.1-preview")) < 0);
        Assert.True(ReleaseVersion.Parse("0.10.0").CompareTo(ReleaseVersion.Parse("0.9.9")) > 0);
        Assert.False(ReleaseVersion.TryParse("0.02.0", out _));

        var package = Package("installed", "0.2.0-preview", "old");
        var installation = new PortableInstallation(package, ToolRoot(package), ReleaseIdentity.Read(package)!);
        var releases = new List<string> { Release("v0.1.9", false), Release("v0.2.0-preview", true) };
        var handler = new FakeHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://api.github.com/repos/MinamiChiwa/Moongate/releases?per_page=30" => Json("[" + string.Join(",", releases) + "]"),
            var uri when uri.EndsWith(".zip.sha256", StringComparison.Ordinal) =>
                Text(new string('a', 64) + "  " + uri[(uri.LastIndexOf('/') + 1)..^".sha256".Length] + "\n"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var source = new GitHubReleaseSource(handler, retryCount: 0);
        var service = Service(installation, source);

        // 仓库里只有更旧或相同的发行：不提供降级，也不生成计划。
        var current = await service.CheckAsync(CancellationToken.None);
        Assert.False(current.UpdateAvailable);
        Assert.False(Directory.Exists(UpdateLayout.Plans(installation.ToolRoot)));

        releases.Add(Release("v0.3.0-preview", true));
        var offer = await service.CheckAsync(CancellationToken.None);
        Assert.True(offer.UpdateAvailable);
        Assert.Equal("0.3.0-preview", offer.ReleaseId);
        var plan = await service.ReadPlanAsync(offer.PlanId!);
        Assert.Equal(("Moongate-v0.3.0-preview-Windows-x64.zip", new string('a', 64)), (plan.AssetName, plan.Sha256));
        Assert.Equal("https://github.com/MinamiChiwa/Moongate/releases/download/v0.3.0-preview/Moongate-v0.3.0-preview-Windows-x64.zip",
            plan.Sources[0].Uri.AbsoluteUri);
        // 请求只带固定的 User-Agent，不带设备或个人标识。
        Assert.All(handler.Requests, request => Assert.Equal("Moongate-Update/1", request.Headers.UserAgent.ToString()));
        Assert.All(handler.Requests, request => Assert.Null(request.Headers.Authorization));

        // .sha256 的文件名与 zip 不一致时拒绝。
        Assert.Equal("update.assetMetadataInvalid", Assert.Throws<UpdateSourceException>(() =>
            GitHubReleaseSource.ParseChecksum(new string('a', 64) + "  other.zip\n", plan.AssetName)).Code);
    }

    [Fact]
    public async Task DownloadRejectsHashMismatchAndArchivesWithInvalidLayout()
    {
        var package = Package("installed", "0.2.0-preview", "old");
        var installation = new PortableInstallation(package, ToolRoot(package), ReleaseIdentity.Read(package)!);
        var archive = Archive("0.3.0-preview", "new");
        var bytes = await File.ReadAllBytesAsync(archive);
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        using var source = new GitHubReleaseSource(handler, retryCount: 0);
        var service = Service(installation, source, () => new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var plan = Plan("0.3.0-preview", bytes.Length, new string('b', 64));
        await UpdatePlanStore.SaveAsync(installation.ToolRoot, plan);
        var error = await Assert.ThrowsAsync<AssetDownloadException>(() => service.DownloadAsync(plan, (_, _, _) => Task.CompletedTask));
        Assert.Equal("update.assetHashMismatch", error.Code);
        Assert.False(Directory.Exists(UpdateLayout.Staging(installation.ToolRoot, plan.PlanId)));

        // 哈希正确时解压到暂存目录并核对包内发行身份。
        var good = Plan("0.3.0-preview", bytes.Length, ReleaseIdentity.FileSha256(archive));
        await UpdatePlanStore.SaveAsync(installation.ToolRoot, good);
        await service.DownloadAsync(good, (_, _, _) => Task.CompletedTask);
        Assert.Equal("0.3.0-preview", ReleaseIdentity.Read(UpdateLayout.StagedPackage(installation.ToolRoot, good.PlanId))!.DisplayVersion);

        var transaction = Guid.NewGuid().ToString("N");
        async Task<string> Rejected(string zip, string version = "0.3.0-preview") =>
            (await Assert.ThrowsAsync<UpdatePackageException>(() => PortableUpdatePackage.ExtractAsync(zip, installation.ToolRoot, transaction, version))).Code;
        Assert.Equal("update.releaseIdentityMismatch", await Rejected(archive, "0.4.0-preview"));
        Assert.Equal("update.packageInvalid", await Rejected(WithEntry(archive, "Other/readme.txt")));
        Assert.Equal("update.packageInvalid", await Rejected(WithEntry(archive, "Moongate/../escape.txt")));
        Assert.Equal("update.packageInvalid", await Rejected(WithEntry(archive, "Moongate/extra.txt")));
        Assert.Equal("update.packageInvalid", await Rejected(WithEntry(archive, "Moongate/components/core/MabiTool.Host.dll", replace: true)));
        Assert.False(Directory.Exists(UpdateLayout.Staging(installation.ToolRoot, transaction)));
        Assert.False(Directory.Exists(UpdateLayout.Staging(installation.ToolRoot, transaction) + ".work"));
    }

    [Fact]
    public void ReplacerSwapsInNewVersionAndCommitsAfterHealthMarker()
    {
        var (package, toolRoot, transaction) = Prepared();
        File.WriteAllText(UpdateLayout.Restarting(toolRoot), transaction);
        var environment = new FakeEnvironment(launched =>
        {
            if (ReleaseIdentity.Read(launched)!.DisplayVersion != "0.3.0-preview") return;
            Directory.CreateDirectory(UpdateLayout.Transaction(toolRoot, transaction));
            File.WriteAllText(UpdateLayout.Healthy(toolRoot, transaction, "0.3.0-preview"), "ok");
        });
        Assert.Equal(ReplacerOutcome.Committed, new UpdateReplacer(package, toolRoot, environment).Apply(transaction));
        Assert.Equal("exe-new", File.ReadAllText(Path.Combine(package, "Moongate.exe")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(package, "components", "core", "MabiTool.Host.dll")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(UpdateLayout.Previous(toolRoot, transaction), "components", "core", "MabiTool.Host.dll")));
        Assert.False(File.Exists(UpdateLayout.Restarting(toolRoot)));
        Assert.Equal(UpdateJournal.Committed, UpdateJournal.Read(toolRoot)!.Phase);
        var result = UpdateResult.Read(toolRoot)!;
        Assert.Equal((UpdateResult.Committed, "0.2.0-preview", "0.3.0-preview"), (result.Outcome, result.FromVersion, result.ToVersion));
        Assert.Equal(new[] { 4242 }, environment.Waited);
    }

    [Fact]
    public void ReplacerLeavesFilesUntouchedWhenOldProcessesDoNotExit()
    {
        var (package, toolRoot, transaction) = Prepared();
        var environment = new FakeEnvironment(_ => { }) { Exits = false };
        Assert.Equal(ReplacerOutcome.Aborted, new UpdateReplacer(package, toolRoot, environment).Apply(transaction));
        Assert.Equal("old", File.ReadAllText(Path.Combine(package, "components", "core", "MabiTool.Host.dll")));
        Assert.True(Directory.Exists(Path.Combine(UpdateLayout.StagedPackage(toolRoot, transaction), "components")));
        Assert.Equal("update.processTimeout", UpdateResult.Read(toolRoot)!.Reason);
        Assert.Equal(new[] { package }, environment.Launches);
    }

    [Fact]
    public async Task HealthFailureRollsBackFilesAndOldHostRestoresTheDatabase()
    {
        var (package, toolRoot, transaction) = Prepared(withBackup: true, out var storage);
        var environment = new FakeEnvironment(launched =>
        {
            var identity = ReleaseIdentity.Read(launched)!;
            if (identity.DisplayVersion != "0.2.0-preview") return;
            // 回退后启动的旧宿主：先恢复安装前的数据库，再就绪写健康标记。
            var old = new PortableInstallation(launched, toolRoot, identity);
            PortableUpdateStartup.RestoreDatabaseIfRequestedAsync(old, storage).GetAwaiter().GetResult();
            PortableUpdateStartup.ReportReady(old);
        });
        Assert.Equal(ReplacerOutcome.RolledBack, new UpdateReplacer(package, toolRoot, environment, Fast()).Apply(transaction));
        Assert.Equal("old", File.ReadAllText(Path.Combine(package, "components", "core", "MabiTool.Host.dll")));
        Assert.Equal("exe-old", File.ReadAllText(Path.Combine(package, "Moongate.exe")));
        Assert.Equal(1, environment.Stops);
        var journal = UpdateJournal.Read(toolRoot)!;
        Assert.True(journal.Has(UpdateJournal.DatabaseRestored));
        Assert.Equal(UpdateJournal.RolledBack, journal.Phase);
        Assert.Equal((UpdateResult.RolledBack, "update.healthTimeout"), (UpdateResult.Read(toolRoot)!.Outcome, UpdateResult.Read(toolRoot)!.Reason));
        using var lease = DataRootLease.Open(storage);
        await using var database = SqliteStore.Open(lease, [Migration]);
        Assert.Equal("before-update", database.ReadScalar("SELECT value FROM update_values"));
    }

    [Fact]
    public void InterruptedSwapIsRolledBackOnNextStartEvenAfterTheFolderMoved()
    {
        var (package, toolRoot, transaction) = Prepared();
        // 断电发生在换入 components 之后、换入其余条目之前。
        var journal = UpdateJournal.Read(toolRoot)!;
        journal.Append(UpdateJournal.Swap);
        Directory.CreateDirectory(UpdateLayout.Previous(toolRoot, transaction));
        Directory.Move(Path.Combine(package, "components"), Path.Combine(UpdateLayout.Previous(toolRoot, transaction), "components"));
        Directory.Move(Path.Combine(UpdateLayout.StagedPackage(toolRoot, transaction), "components"), Path.Combine(package, "components"));

        // 整个程序文件夹被搬到别处后再启动：日志只记相对位置，照样能恢复。
        var moved = Path.Combine(root, "moved", "Moongate");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(package, moved);
        var movedToolRoot = ToolRoot(moved);
        var environment = new FakeEnvironment(launched =>
            PortableUpdateStartup.ReportReady(new PortableInstallation(launched, movedToolRoot, ReleaseIdentity.Read(launched)!)));
        Assert.Equal(ReplacerOutcome.RolledBack, new UpdateReplacer(moved, movedToolRoot, environment).Recover());
        Assert.Equal("old", File.ReadAllText(Path.Combine(moved, "components", "core", "MabiTool.Host.dll")));
        Assert.Null(ReleaseIdentity.Read(moved)!.Verify(moved));
        Assert.Equal("new", File.ReadAllText(Path.Combine(UpdateLayout.StagedPackage(movedToolRoot, transaction), "components", "core", "MabiTool.Host.dll")));
        Assert.False(UpdateJournal.Read(movedToolRoot)!.Has(UpdateJournal.RestoreDatabase));
        Assert.Equal("update.interrupted", UpdateResult.Read(movedToolRoot)!.Reason);

        // 事务结束后的下一次成功启动清理暂存与上一版文件。
        PortableUpdateStartup.ReportReady(new PortableInstallation(moved, movedToolRoot, ReleaseIdentity.Read(moved)!));
        Assert.Null(UpdateJournal.Read(movedToolRoot));
        Assert.False(Directory.Exists(Path.Combine(UpdateLayout.Root(movedToolRoot), "staging")));
        Assert.NotNull(UpdateResult.Read(movedToolRoot));
    }

    [Fact]
    public void ReleaseIdentityTreeHashMatchesThePackagingScript()
    {
        // 与 tests/EndToEnd/Repository/test_package_inventory.py 使用同一组向量。
        var vector = Path.Combine(root, "vector");
        Directory.CreateDirectory(Path.Combine(vector, "b"));
        File.WriteAllText(Path.Combine(vector, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(vector, "B.txt"), "beta");
        File.WriteAllText(Path.Combine(vector, "b", "c.txt"), "gamma");
        Assert.Equal("0c4cc6beb8fd7294852602c7d566e726e38a9eed3f4a8c8da0f674cf5b14d4f5", ReleaseIdentity.TreeSha256(vector, out var files));
        Assert.Equal(3, files);
    }

    private static readonly DatabaseMigration Migration = new("update-test", 1,
        "CREATE TABLE update_values (id INTEGER PRIMARY KEY, value TEXT NOT NULL) STRICT;");

    private (string Package, string ToolRoot, string Transaction) Prepared() => Prepared(false, out _);

    private (string Package, string ToolRoot, string Transaction) Prepared(bool withBackup, out StorageOptions storage)
    {
        var package = Package("Moongate", "0.2.0-preview", "old");
        var toolRoot = ToolRoot(package);
        var transaction = Guid.NewGuid().ToString("N");
        Package(Path.Combine(UpdateLayout.Staging(toolRoot, transaction), "Moongate"), "0.3.0-preview", "new");
        storage = new StorageOptions(Path.Combine(toolRoot, "bootstrap"), true, DataRoot: Path.Combine(toolRoot, "data"));
        var backupId = "none";
        if (withBackup)
        {
            using var lease = DataRootLease.Open(storage);
            var database = SqliteStore.Open(lease, [Migration]);
            database.WriteAsync(tx => tx.Execute("INSERT INTO update_values VALUES(1, 'before-update')")).GetAwaiter().GetResult();
            using (var maintenance = database.Activities.TryEnterMaintenance("update-test")!)
            {
                var backup = database.CreateDatabaseBackupAsync(maintenance).GetAwaiter().GetResult();
                backupId = backup.BackupId;
                Directory.CreateDirectory(UpdateLayout.Transaction(toolRoot, transaction));
                File.WriteAllBytes(UpdateLayout.DatabaseBackup(toolRoot, transaction),
                    JsonSerializer.SerializeToUtf8Bytes(backup, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            // 新版本启动后改写了数据（相当于迁移），回退须恢复到备份。
            database.WriteAsync(tx => tx.Execute("UPDATE update_values SET value='written-by-new-version'")).GetAwaiter().GetResult();
            database.DisposeAsync().AsTask().GetAwaiter().GetResult();
            storage = storage with { Initialize = false };
        }
        UpdateJournal.Create(toolRoot, transaction, "0.2.0-preview", "0.3.0-preview", "op-test", backupId,
            [new KeyValuePair<int, long>(4242, 1)]);
        return (package, toolRoot, transaction);
    }

    private string Package(string name, string displayVersion, string marker)
    {
        var package = Path.IsPathRooted(name) ? name : Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(package, "components", "core"));
        Directory.CreateDirectory(Path.Combine(package, "licenses"));
        File.WriteAllText(Path.Combine(package, "components", "core", "MabiTool.Host.dll"), marker);
        File.WriteAllText(Path.Combine(package, "licenses", "THIRD_PARTY_NOTICES.txt"), marker);
        File.WriteAllText(Path.Combine(package, "Moongate.exe"), "exe-" + marker);
        var tree = ReleaseIdentity.TreeSha256(Path.Combine(package, "components", "core"), out var files);
        var preview = displayVersion.EndsWith("-preview", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(package, "components", "release.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, product = "Moongate", version = preview ? displayVersion[..^"-preview".Length] : displayVersion,
            channel = preview ? "preview" : "stable", displayVersion, repository = Repository, builtAt = "2026-11-17T00:00:00Z",
            entry = new { path = "Moongate.exe", sha256 = ReleaseIdentity.FileSha256(Path.Combine(package, "Moongate.exe")) },
            components = new[] { new { id = "core", files, treeSha256 = tree } }
        }));
        return package;
    }

    private static string ToolRoot(string package) => Path.Combine(package, "userdata");

    private string Archive(string displayVersion, string marker)
    {
        var source = Path.Combine(root, "archive-source-" + Guid.NewGuid().ToString("N"));
        Package(Path.Combine(source, "Moongate"), displayVersion, marker);
        var zip = Path.Combine(root, "Moongate-v" + displayVersion + "-" + Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(Path.Combine(source, "Moongate"), zip, CompressionLevel.Fastest, includeBaseDirectory: true);
        return zip;
    }

    private string WithEntry(string archive, string name, bool replace = false)
    {
        var copy = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        File.Copy(archive, copy);
        using var zip = ZipFile.Open(copy, ZipArchiveMode.Update);
        if (replace) zip.GetEntry(name)!.Delete();
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write("tampered");
        return copy;
    }

    private static UpdatePlan Plan(string displayVersion, long length, string sha256)
    {
        var now = DateTimeOffset.UtcNow;
        var name = "Moongate-v" + displayVersion + "-Windows-x64.zip";
        return new(1, Guid.NewGuid().ToString("N"), displayVersion.Split('-')[0], displayVersion, "preview", "v" + displayVersion, name, length, sha256,
            [new("GithubDirect", new Uri("https://github.com/MinamiChiwa/Moongate/releases/download/v" + displayVersion + "/" + name))],
            "GithubApi", now, now.AddDays(7));
    }

    private static PortableUpdateService Service(PortableInstallation installation, GitHubReleaseSource source, Func<HttpMessageHandler>? handler = null) =>
        new(installation, source, null!, null!, () => new UpdatePolicy(), new ActivityBarrier(), null!, null!, TimeProvider.System,
            createHandler: handler, freeSpace: _ => long.MaxValue);

    private static ReplacerTimeouts Fast() => new() { Health = TimeSpan.FromSeconds(5), ProcessExit = TimeSpan.FromSeconds(5) };

    private static string Release(string tag, bool prerelease)
    {
        var name = "Moongate-" + tag + "-Windows-x64.zip";
        return JsonSerializer.Serialize(new
        {
            tag_name = tag, draft = false, prerelease,
            assets = new object[]
            {
                new { id = 11, name, state = "uploaded", size = 1024 },
                new { id = 12, name = name + ".sha256", state = "uploaded", size = 100 }
            }
        });
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    /// <summary>测试用替换器环境：时钟随 Sleep 前进，启动程序只调用钩子。</summary>
    private sealed class FakeEnvironment(Action<string> launch) : IReplacerEnvironment
    {
        private DateTime now = new(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
        public bool Exits { get; init; } = true;
        public List<int> Waited { get; } = [];
        public List<string> Launches { get; } = [];
        public int Stops { get; private set; }
        public DateTime UtcNow => now;
        public bool WaitForExit(int processId, long startTimeUtcTicks, TimeSpan timeout)
        {
            Waited.Add(processId);
            if (!Exits) now += timeout;
            return Exits;
        }
        public void Launch(string packageRoot)
        {
            Launches.Add(packageRoot);
            launch(packageRoot);
        }
        public void StopPackageProcesses(string packageRoot) => Stops++;
        public void Sleep(TimeSpan duration) => now += duration;
    }
}

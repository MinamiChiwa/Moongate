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
    public async Task DirectCheckFollowsLatestRedirectAndStagesIncrementalUpdate()
    {
        var release = DeltaRelease();
        var installation = new PortableInstallation(release.Installed, ToolRoot(release.Installed), ReleaseIdentity.Read(release.Installed)!);
        var handler = release.Handler();
        using var source = new GitHubReleaseSource(handler, retryCount: 0);
        var service = Service(installation, source, release.Handler);

        // 版本来自 releases/latest 的跳转地址，资产地址按文件名拼出，不访问 api.github.com。
        Assert.Equal("v0.1.2", GitHubReleaseSource.ParseLatestRedirect(Repository, new Uri(LatestTag)));
        Assert.Null(GitHubReleaseSource.ParseLatestRedirect(Repository, new Uri("https://github.com/MinamiChiwa/Moongate/releases")));
        var offer = await service.CheckAsync(CancellationToken.None);
        Assert.DoesNotContain(handler.Requests, request => request.RequestUri!.Host == "api.github.com");
        var plan = await service.ReadPlanAsync(offer.PlanId!);
        Assert.Equal(Download + "Moongate-v0.1.2-Windows-x64.zip", plan.Sources[0].Uri.AbsoluteUri);
        // 检查只做版本发现：能分段时下载方式与大小留空，由后台比对补上。
        Assert.Equal(("0.1.2", null, null), (offer.ReleaseId, offer.DownloadKind, offer.DownloadBytes));
        Assert.NotNull(plan.Ranges);
        // 本机缺的文件来自多次变更（入口、宿主、发行身份、声明与新增文件），比对后给出它们合并后的区间字节数。
        var manifest = UpdateManifest.Parse(release.Manifest, "0.1.2", "Moongate-v0.1.2-Windows-x64.zip", release.FullSha);
        var missing = manifest.Files.Where(file => file.Path is not ("components/core/shared.dll" or "components/core/zz-shared.dll")).ToArray();
        var segments = DeltaUpdate.Segments(missing.DistinctBy(file => file.Sha256));
        Assert.True(segments.Count >= 2);
        await service.EstimateAsync(CancellationToken.None);
        Assert.Equal(new UpdatePlanEstimate("incremental", segments.Sum(segment => segment.Length), segments.Count, 2, missing.Length, 1),
            UpdatePlanStore.ReadEstimate(installation.ToolRoot, plan.PlanId));

        // 下载只对完整包发单区间 Range 请求，请求的区间正是合并后的区间，按策略的并发（出厂 2）同时请求，下载字节等于区间之和；相同文件取自本机。
        var status = await service.DownloadAsync(plan, (_, _, _) => Task.CompletedTask);
        Assert.Equal((2, missing.Length), (status.ReusedFiles, status.DownloadedFiles));
        Assert.All(release.DownloadRequests, request => Assert.NotNull(request.Headers.Range));
        Assert.Equal(segments.Select(segment => (segment.Start, segment.End - 1)).Order(),
            release.DownloadRequests.Select(request => (request.Headers.Range!.Ranges.Single().From!.Value, request.Headers.Range.Ranges.Single().To!.Value)).Order());
        Assert.Equal(2, release.MaxInFlight);
        Assert.Equal(segments.Sum(segment => segment.Length), release.ServedBytes);
        Assert.True(release.ServedBytes < release.Full.Length * DeltaUpdate.FullPackageShare);
        var staged = UpdateLayout.StagedPackage(installation.ToolRoot, plan.PlanId);
        Assert.Equal("added", File.ReadAllText(Path.Combine(staged, "components", "core", "added.dll")));
        Assert.False(File.Exists(Path.Combine(staged, "components", "core", "stale.dll")));
        Assert.Null(ReleaseIdentity.Read(staged)!.Verify(staged));
        Assert.Empty(Directory.EnumerateFiles(UpdateLayout.Downloads(installation.ToolRoot)));
    }

    [Fact]
    public void AdjacentSegmentsAreMergedWhenTheGapIsSmall()
    {
        static UpdateManifestFile At(long offset, long length) =>
            new("components/" + offset + ".dll", length, new string('a', 64), offset, length, "stored", 0);
        var gap = DeltaUpdate.MergeGap;
        Assert.Equal([new UpdateRangeSegment(100, 300 + gap - 1), new UpdateRangeSegment(400 + 2 * gap, 50)],
            DeltaUpdate.Segments([At(400 + 2 * gap, 50), At(100, 100), At(200 + gap - 1, 200), At(150, 10), At(500, 0)]));
    }

    [Fact]
    public async Task IncrementalUpdateFallsBackToFullPackage()
    {
        // 服务器忽略 Range、返回 200：同一次下载改用完整包。
        var ignored = DeltaRelease(rangeIgnored: true);
        var installation = new PortableInstallation(ignored.Installed, ToolRoot(ignored.Installed), ReleaseIdentity.Read(ignored.Installed)!);
        using (var source = new GitHubReleaseSource(ignored.Handler(), retryCount: 0))
        {
            var service = Service(installation, source, ignored.Handler);
            var plan = await service.ReadPlanAsync((await service.CheckAsync(CancellationToken.None)).PlanId!);
            Assert.NotNull(plan.Ranges);
            var status = await service.DownloadAsync(plan, (_, _, _) => Task.CompletedTask);
            Assert.Equal((ulong)ignored.Full.Length, status.TotalBytes!.Value.Value);
            Assert.Contains(ignored.DownloadRequests, request => request.Headers.Range is null);
            Assert.Equal("0.1.2", ReleaseIdentity.Read(UpdateLayout.StagedPackage(installation.ToolRoot, plan.PlanId))!.DisplayVersion);
        }

        // 某段解压后的内容与清单哈希不符：套用被拒绝、不留暂存与区间文件，下载改用完整包。
        var tampered = DeltaRelease(tamper: true);
        installation = new PortableInstallation(tampered.Installed, ToolRoot(tampered.Installed), ReleaseIdentity.Read(tampered.Installed)!);
        using (var source = new GitHubReleaseSource(tampered.Handler(), retryCount: 0))
        {
            var service = Service(installation, source, tampered.Handler);
            var plan = await service.ReadPlanAsync((await service.CheckAsync(CancellationToken.None)).PlanId!);
            await service.DownloadAsync(plan, (_, _, _) => Task.CompletedTask);
            Assert.Contains(tampered.DownloadRequests, request => request.Headers.Range is not null);
            Assert.Contains(tampered.DownloadRequests, request => request.Headers.Range is null);
            var staged = UpdateLayout.StagedPackage(installation.ToolRoot, plan.PlanId);
            Assert.Equal("added", File.ReadAllText(Path.Combine(staged, "components", "core", "added.dll")));
            Assert.DoesNotContain(Directory.EnumerateFiles(UpdateLayout.Downloads(installation.ToolRoot)), path => path.Contains(".seg-", StringComparison.Ordinal));
        }

        // 本机文件几乎都要换（缺的超过完整包的 80%）或清单没有数据区（schema 1）：检查直接选完整包。
        var modified = DeltaRelease();
        File.WriteAllText(Path.Combine(modified.Installed, "components", "core", "shared.dll"), "edited");
        File.WriteAllText(Path.Combine(modified.Installed, "components", "core", "zz-shared.dll"), "edited");
        installation = new PortableInstallation(modified.Installed, ToolRoot(modified.Installed), ReleaseIdentity.Read(modified.Installed)!);
        using (var source = new GitHubReleaseSource(modified.Handler(), retryCount: 0))
        {
            var service = Service(installation, source, modified.Handler);
            var offer = await service.CheckAsync(CancellationToken.None);
            await service.EstimateAsync(CancellationToken.None);
            Assert.Equal(("full", (long)modified.Full.Length), (UpdatePlanStore.ReadEstimate(installation.ToolRoot, offer.PlanId!)!.DownloadKind,
                UpdatePlanStore.ReadEstimate(installation.ToolRoot, offer.PlanId!)!.DownloadBytes));
        }
        var legacy = DeltaRelease(legacyManifest: true);
        installation = new PortableInstallation(legacy.Installed, ToolRoot(legacy.Installed), ReleaseIdentity.Read(legacy.Installed)!);
        using (var source = new GitHubReleaseSource(legacy.Handler(), retryCount: 0))
            Assert.Equal("full", (await Service(installation, source, legacy.Handler).CheckAsync(CancellationToken.None)).DownloadKind);
    }

    [Fact]
    public async Task StartupDropsSavedUpdatesThatAreNotNewerAndKeepsTheOneAfterRollback()
    {
        // 0.2.0-preview 更新到 0.3.0-preview 后启动：0.3.0-preview 与更旧的计划、清单、暂存、下载与上一版文件都被回收。
        var package = Package("Moongate", "0.3.0-preview", "new");
        var installation = new PortableInstallation(package, ToolRoot(package), ReleaseIdentity.Read(package)!);
        var root = installation.ToolRoot;
        var installed = Plan("0.3.0-preview", 10, new string('c', 64));
        var older = Plan("0.2.1-preview", 10, new string('d', 64));
        foreach (var plan in new[] { installed, older })
        {
            await UpdatePlanStore.SaveAsync(root, plan);
            Directory.CreateDirectory(UpdateLayout.StagedPackage(root, plan.PlanId));
        }
        Directory.CreateDirectory(UpdateLayout.Downloads(root));
        File.WriteAllText(ResumableAssetDownloader.CachePath(UpdateLayout.Downloads(root), installed.Sha256) + ".part", "partial");
        File.WriteAllText(Path.Combine(UpdateLayout.Plans(root), "manifest-" + new string('e', 64) + ".json"), "{}");
        Directory.CreateDirectory(UpdateLayout.Previous(root, installed.PlanId));
        PortableUpdateStartup.ReportReady(installation);
        Assert.Empty(Directory.EnumerateFileSystemEntries(UpdateLayout.Plans(root)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(UpdateLayout.Root(root), "staging")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(UpdateLayout.Downloads(root)));
        Assert.False(Directory.Exists(Path.Combine(UpdateLayout.Root(root), "previous")));

        // 安装 0.3.0-preview 失败回到 0.2.0-preview：事务结束后的启动只保留高于当前版本的那份计划，暂存与下载回收。
        var (oldPackage, toolRoot, transaction) = Prepared();
        var offered = Plan("0.3.0-preview", 10, new string('f', 64)) with { PlanId = transaction };
        await UpdatePlanStore.SaveAsync(toolRoot, offered);
        await UpdatePlanStore.SaveAsync(toolRoot, Plan("0.2.0-preview", 10, new string('a', 64)));
        UpdateJournal.Read(toolRoot)!.Append(UpdateJournal.RolledBack);
        new UpdateResult(transaction, "op-test", UpdateResult.RolledBack, "0.2.0-preview", "0.3.0-preview", "update.healthTimeout", DateTime.UtcNow).Write(toolRoot);
        PortableUpdateStartup.ReportReady(new PortableInstallation(oldPackage, toolRoot, ReleaseIdentity.Read(oldPackage)!));
        Assert.Equal([transaction + ".json"], Directory.EnumerateFiles(UpdateLayout.Plans(toolRoot)).Select(Path.GetFileName));
        Assert.False(Directory.Exists(Path.Combine(UpdateLayout.Root(toolRoot), "staging")));
        Assert.Equal("update.healthTimeout", UpdateResult.Read(toolRoot)!.Reason);
    }

    [Fact]
    public async Task AutomaticCheckAfterStartupFeedsStateAndTheInstalledVersionRetiresTheOffer()
    {
        var old = Package("Moongate", "0.2.0-preview", "old");
        var toolRoot = ToolRoot(old);
        var newer = Package("installed-0.3.0", "0.3.0-preview", "new");
        var storage = new StorageOptions(Path.Combine(toolRoot, "bootstrap"), true, DataRoot: Path.Combine(toolRoot, "data"));
        var plan = Plan("0.3.0-preview", 10, new string('a', 64));
        await UpdatePlanStore.SaveAsync(toolRoot, plan);
        var clock = new ManualClock();
        using var lease = DataRootLease.Open(storage);
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var operations = new OperationStore(database, clock);
        var policy = new UpdatePolicyStore(database, clock);
        using var source = new GitHubReleaseSource(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), retryCount: 0);
        UpdateApiService Api(string package) =>
            new(new PortableInstallation(package, toolRoot, ReleaseIdentity.Read(package)!), Service(new PortableInstallation(package, toolRoot,
                ReleaseIdentity.Read(package)!), source), operations, policy, clock);

        // 从未检查：最近检查为空（前端写“尚未检查”），不报告已是最新。
        Assert.Equal(("idle", null), (Api(old).State().State, Api(old).State().LastCheck));

        // 宿主启动后 60 秒自动检查一次，检查结果进入状态。
        var checker = new FixedChecker(new UpdateCheckResult(true, "0.3.0-preview", plan.Sha256, plan.PlanId, plan.ExpiresAt,
            UpdateSourceKind.GithubDirect, 10, "full", 10));
        var executor = new UpdateCheckOperations(operations, policy, checker, clock);
        // 宿主启动：排定 60 秒后检查，到期后调度循环自行发起检查。
        await policy.ScheduleStartupAsync();
        clock.Now += UpdatePolicyStore.StartupDelay;
        await executor.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Api(old).State().LastCheck is null && DateTime.UtcNow < deadline) await Task.Delay(20);
        }
        finally { await executor.StopAsync(CancellationToken.None); }
        Assert.Equal(1, checker.Calls);
        var offered = Api(old).State();
        Assert.Equal(("available", "0.3.0-preview", "available"), (offered.State, offered.Available?.ReleaseId, offered.LastCheck?.Outcome));

        // 更新到 0.3.0-preview（应用内安装或手动覆盖）后：同一检查结果不再报为可用，也不算这一版的检查。
        var updated = Api(newer).State();
        Assert.Equal(("idle", null, null), (updated.State, updated.Available, updated.LastCheck));

        // 计划被回收（更新后启动的残留回收）后旧版本也不再显示；计划仍在时（安装失败回退）旧版本照常显示可用更新。
        File.Delete(Path.Combine(UpdateLayout.Plans(toolRoot), plan.PlanId + ".json"));
        Assert.Null(Api(old).State().Available);
    }

    [Fact]
    public async Task CheckDuringGameInputStillReportsTheNewVersionAndSizeFollowsWhenIdle()
    {
        var release = DeltaRelease();
        var installation = new PortableInstallation(release.Installed, ToolRoot(release.Installed), ReleaseIdentity.Read(release.Installed)!);
        var toolRoot = installation.ToolRoot;
        var storage = new StorageOptions(Path.Combine(toolRoot, "bootstrap"), true, DataRoot: Path.Combine(toolRoot, "data"));
        var clock = new ManualClock();
        using var lease = DataRootLease.Open(storage);
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var operations = new OperationStore(database, clock);
        var policy = new UpdatePolicyStore(database, clock);
        var barrier = new ActivityBarrier();
        using var source = new GitHubReleaseSource(release.Handler(), retryCount: 0);
        var service = Service(installation, source, release.Handler, barrier);
        var api = new UpdateApiService(installation, service, operations, policy, clock);
        var executor = new UpdateCheckOperations(operations, policy, service, clock);
        // 染色或合奏正在占用游戏输入：本机文件哈希在这期间暂停。
        var input = barrier.EnterActivity("color-run", "game.resource");
        await policy.ScheduleStartupAsync();
        clock.Now += UpdatePolicyStore.StartupDelay;
        await executor.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (api.State().LastCheck is null && DateTime.UtcNow < deadline) await Task.Delay(20);
            var during = api.State();
            Assert.Equal(("available", "0.1.2", "available"), (during.State, during.Available?.ReleaseId, during.LastCheck?.Outcome));
            // 大小还没算出：不写大小，也不当作 0。
            Assert.Equal((null, null), (during.Available!.DownloadKind, during.Available.DownloadBytes));

            // 活动结束后后台比对补上增量大小。
            input.Dispose();
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (api.State().Available?.DownloadKind is null && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.Equal("incremental", api.State().Available!.DownloadKind);
            Assert.True(api.State().Available!.DownloadBytes!.Value.Value < (ulong)release.Full.Length);
        }
        finally
        {
            input.Dispose();
            await executor.StopAsync(CancellationToken.None);
        }
    }

    private sealed class FixedChecker(UpdateCheckResult result) : IUpdateCheckService
    {
        public int Calls { get; private set; }
        public bool IsConfigured => true;
        public Task<UpdateCheckResult> CheckAsync(CancellationToken token) { Calls++; return Task.FromResult(result); }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
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

    private const string Download = "https://github.com/MinamiChiwa/Moongate/releases/download/v0.1.2/";
    private const string LatestTag = "https://github.com/MinamiChiwa/Moongate/releases/tag/v0.1.2";

    /// <summary>
    /// 本机 0.1.1 与 GitHub 上的 0.1.2：两版共有 <c>shared.dll</c> 与 <c>zz-shared.dll</c>，新版改了入口、宿主、声明与发行身份并新增
    /// <c>added.dll</c> 与 <c>t-added.dll</c>（按文件名排在两个共有文件之间，使缺的文件分成几段），
    /// 本机多出的 <c>stale.dll</c> 不在新版里。清单按 package-update-assets.py 的 schema 2 生成，记录每个文件在完整包里的数据区。
    /// </summary>
    private DeltaFixture DeltaRelease(bool rangeIgnored = false, bool tamper = false, bool legacyManifest = false)
    {
        // 共有文件取得足够大，使缺的文件远小于完整包（与真实发行中不变的运行时同理）。
        var shared = string.Concat(Enumerable.Range(0, 8000).Select(_ => Guid.NewGuid().ToString("N")));
        var other = string.Concat(Enumerable.Range(0, 8000).Select(_ => Guid.NewGuid().ToString("N")));
        var installed = Package("installed-" + Guid.NewGuid().ToString("N"), "0.1.1", "old",
            new Dictionary<string, string> { ["shared.dll"] = shared, ["zz-shared.dll"] = other, ["stale.dll"] = "old-only" });
        var source = Path.Combine(root, "release-" + Guid.NewGuid().ToString("N"), "Moongate");
        Package(source, "0.1.2", "new", new Dictionary<string, string>
            { ["shared.dll"] = shared, ["zz-shared.dll"] = other, ["added.dll"] = "added", ["t-added.dll"] = "added-between" });
        var full = Path.Combine(Path.GetDirectoryName(source)!, "Moongate-v0.1.2-Windows-x64.zip");
        ZipFile.CreateFromDirectory(source, full, CompressionLevel.Fastest, includeBaseDirectory: true);
        var fullBytes = File.ReadAllBytes(full);
        var fullSha = ReleaseIdentity.FileSha256(full);
        var located = Locate(fullBytes);
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(source, path).StartsWith("userdata", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(source, path).Replace('\\', '/')).Order(StringComparer.Ordinal)
            .Select(path =>
            {
                var file = Path.Combine(source, path);
                var (offset, compressed, method, crc) = located["Moongate/" + path];
                return legacyManifest
                    ? (object)new { path, size = new FileInfo(file).Length, sha256 = ReleaseIdentity.FileSha256(file) }
                    : new { path, size = new FileInfo(file).Length, sha256 = ReleaseIdentity.FileSha256(file), dataOffset = offset,
                        compressedSize = compressed, compression = method, crc32 = crc };
            }).ToArray();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = legacyManifest ? 1 : 2, product = "Moongate", displayVersion = "0.1.2",
            package = new { name = "Moongate-v0.1.2-Windows-x64.zip", size = fullBytes.Length, sha256 = fullSha },
            files, deltas = Array.Empty<object>()
        });
        // 篡改：Range 请求取到的 added.dll 数据区被改动，整包下载仍是原内容。
        var served = fullBytes.ToArray();
        if (tamper)
        {
            var (offset, compressed, _, _) = located["Moongate/components/core/added.dll"];
            for (var index = 0; index < compressed; index++) served[offset + index] ^= 0x5a;
        }
        return new(installed, full, fullBytes, served, manifest, fullSha, rangeIgnored);
    }

    /// <summary>按中央目录与本地文件头算出 zip 里每个条目的数据区：偏移、压缩后长度、压缩方式与 CRC-32。</summary>
    private static Dictionary<string, (long Offset, long Compressed, string Method, uint Crc)> Locate(byte[] zip)
    {
        var end = zip.Length - 22;
        while (BitConverter.ToUInt32(zip, end) != 0x06054b50) end--;
        var count = BitConverter.ToUInt16(zip, end + 10);
        var position = (int)BitConverter.ToUInt32(zip, end + 16);
        var result = new Dictionary<string, (long, long, string, uint)>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var method = BitConverter.ToUInt16(zip, position + 10);
            var crc = BitConverter.ToUInt32(zip, position + 16);
            var compressed = BitConverter.ToUInt32(zip, position + 20);
            int nameLength = BitConverter.ToUInt16(zip, position + 28), extraLength = BitConverter.ToUInt16(zip, position + 30),
                commentLength = BitConverter.ToUInt16(zip, position + 32);
            var header = (int)BitConverter.ToUInt32(zip, position + 42);
            var name = Encoding.UTF8.GetString(zip, position + 46, nameLength).Replace('\\', '/');
            var data = header + 30 + BitConverter.ToUInt16(zip, header + 26) + BitConverter.ToUInt16(zip, header + 28);
            result[name] = (data, compressed, method == 0 ? "stored" : "deflate", crc);
            position += 46 + nameLength + extraLength + commentLength;
        }
        return result;
    }

    private sealed record DeltaFixture(string Installed, string FullPath, byte[] Full, byte[] Served, byte[] Manifest, string FullSha, bool RangeIgnored)
    {
        public List<HttpRequestMessage> DownloadRequests { get; } = [];
        private long served;
        private int inFlight;
        private int maxInFlight;
        public long ServedBytes => Interlocked.Read(ref served);
        /// <summary>同时在处理的 Range 请求数的最大值；每个 Range 响应停留 100 ms，使并发可见。</summary>
        public int MaxInFlight => maxInFlight;

        public FakeHandler Handler()
        {
            FakeHandler? handler = null;
            handler = new FakeHandler(request =>
            {
                if (handler != Checks) DownloadRequests.Add(request);
                return request.RequestUri!.AbsoluteUri switch
                {
                    "https://github.com/MinamiChiwa/Moongate/releases/latest" =>
                        new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(LatestTag) } },
                    Download + "Moongate-v0.1.2-Windows-x64.zip.sha256" => Text(FullSha + "  Moongate-v0.1.2-Windows-x64.zip\n"),
                    Download + "Moongate-v0.1.2-Windows-x64.zip.manifest.json" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Manifest) },
                    Download + "Moongate-v0.1.2-Windows-x64.zip" => Asset(request),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                };
            });
            Checks ??= handler;
            return handler;
        }

        // 支持单区间 Range 的发行下载地址；RangeIgnored 时像不支持分段的服务器一样返回 200 与整个文件。
        private HttpResponseMessage Asset(HttpRequestMessage request)
        {
            if (request.Headers.Range?.Ranges.SingleOrDefault() is not { From: { } from, To: { } to } || RangeIgnored)
            {
                Interlocked.Add(ref served, Full.Length);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Full), Headers = { ETag = new("\"asset\"") } };
            }
            var length = (int)(to - from + 1);
            Interlocked.Add(ref served, length);
            var now = Interlocked.Increment(ref inFlight);
            for (var seen = maxInFlight; now > seen; seen = maxInFlight) Interlocked.CompareExchange(ref maxInFlight, now, seen);
            Thread.Sleep(100);
            Interlocked.Decrement(ref inFlight);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(Served, (int)from, length), Headers = { ETag = new("\"asset\"") } };
            response.Content.Headers.ContentRange = new(from, to, Full.Length);
            return response;
        }

        // 第一个处理器供 GitHubReleaseSource 检查使用，之后创建的供下载使用。
        private FakeHandler? Checks { get; set; }
    }

    private string Package(string name, string displayVersion, string marker, IReadOnlyDictionary<string, string>? coreFiles = null)
    {
        var package = Path.IsPathRooted(name) ? name : Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(package, "components", "core"));
        Directory.CreateDirectory(Path.Combine(package, "licenses"));
        foreach (var (file, content) in coreFiles ?? new Dictionary<string, string>())
            File.WriteAllText(Path.Combine(package, "components", "core", file), content);
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

    private static PortableUpdateService Service(PortableInstallation installation, GitHubReleaseSource source, Func<HttpMessageHandler>? handler = null,
        ActivityBarrier? barrier = null) =>
        new(installation, source, null!, null!, () => new UpdatePolicy(), barrier ?? new ActivityBarrier(), null!, null!, TimeProvider.System,
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

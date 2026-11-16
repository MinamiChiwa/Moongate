using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using MabiTool.Host.Updates;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;
using MabiTool.Providers.GitHubReleases;
using MabiTool.Updates.Tests;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class UpdateDownloadExecutionTests
{
    [Fact]
    public async Task Selected_components_share_blobs_and_publish_one_verified_tree_without_installing()
    {
        using var h = await Harness.CreateAsync(multiple: true);
        var result = await h.DownloadAsync();
        Assert.Equal(4, result.DownloadedFiles);
        Assert.Equal(0, result.ReusedFiles);
        Assert.Equal(2, h.Requests.Count);
        Assert.Equal(2, h.Requests.Distinct().Count());
        await h.VerifyStageAsync();
        Assert.False(Directory.Exists(Path.Combine(h.Stage, "components", "optional")));
        Assert.False(Directory.Exists(h.Options.VersionsRoot));
        Assert.Equal(new[] { "preparing", "downloading", "verifying" }, h.Reports.Select(item => item.Phase).Distinct());
        Assert.Equal((ulong)h.F.Artifacts.Files.Sum(item => item.DownloadLength), result.VerifiedBytes.Value);
        Assert.Equal(result.VerifiedBytes, result.TotalBytes);
        Assert.Equal(UpdateSourceKind.GithubDirect, result.Source!.Value.KnownValue);
        Assert.True(result.MaximumDownloadBytes.Value >= result.RequiredDownloadBytes.Value);
        h.AssertNoWork();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_installed_tree_is_reused_and_damaged_nominal_reuse_is_budgeted(bool nominalReuse)
    {
        using var h = await Harness.CreateAsync(nominalReuse: nominalReuse);
        Directory.CreateDirectory(h.Current);
        await File.WriteAllTextAsync(Path.Combine(h.Current, "same"), "same");
        await File.WriteAllTextAsync(Path.Combine(h.Current, "app"), "bad");
        await File.WriteAllTextAsync(Path.Combine(h.Current, "removed"), "preserve old version");
        // 相似目录不作为当前版本来源。
        Directory.CreateDirectory(h.F.Path("installation/versions/newer/components/core"));
        await File.WriteAllTextAsync(h.F.Path("installation/versions/newer/components/core/app"), "new");
        if (nominalReuse) Assert.Equal(0, h.Plan.DownloadBytes);
        var result = await h.DownloadAsync();
        Assert.Equal(1, result.ReusedFiles);
        Assert.Equal(1, result.DownloadedFiles);
        Assert.Single(h.Requests);
        Assert.Equal(nominalReuse ? result.RequiredDownloadBytes.Value : 0UL, result.RepairDownloadBytes.Value);
        Assert.True(result.RequiredDownloadBytes.Value > 0);
        Assert.Equal("bad", await File.ReadAllTextAsync(Path.Combine(h.Current, "app")));
        Assert.True(File.Exists(Path.Combine(h.Current, "removed")));
        await h.VerifyStageAsync();
        h.AssertNoWork();
    }

    [Fact]
    public async Task Actual_space_budget_is_reported_before_network_and_includes_repair_and_full_fallback()
    {
        using var h = await Harness.CreateAsync(nominalReuse: true);
        var service = h.Service(freeSpace: _ => 1);
        var error = await Assert.ThrowsAsync<UpdatePlanException>(() => h.DownloadAsync(service));
        Assert.Equal("update.diskSpaceInsufficient", error.Code);
        var status = Assert.Single(h.Reports).Status;
        Assert.True(status.RepairDownloadBytes.Value > 0);
        Assert.True(status.MaximumDownloadBytes.Value > status.RequiredDownloadBytes.Value);
        Assert.True(status.PeakAdditionalBytes.Value > status.MaximumDownloadBytes.Value);
        Assert.Empty(h.Requests);
        Assert.False(Directory.Exists(h.Stage));
        h.AssertNoWork();
    }

    [Fact]
    public async Task Existing_stage_is_reverified_and_damaged_stage_is_preserved_for_diagnosis()
    {
        using var h = await Harness.CreateAsync();
        await h.DownloadAsync();
        var requests = h.Requests.Count;
        var reused = await h.DownloadAsync();
        Assert.True(reused.ReusedStage);
        Assert.Equal(0UL, reused.RequiredDownloadBytes.Value);
        Assert.Equal(requests, h.Requests.Count);
        await File.WriteAllTextAsync(Path.Combine(h.Stage, "components/core/app"), "damaged");
        var error = await Assert.ThrowsAsync<FileTreeException>(() => h.DownloadAsync());
        Assert.Equal("update.targetTreeMismatch", error.Code);
        Assert.Equal("damaged", await File.ReadAllTextAsync(Path.Combine(h.Stage, "components/core/app")));
        Assert.Equal(requests, h.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_or_revoked_authorization_prevents_asset_requests(bool revoke)
    {
        using var h = await Harness.CreateAsync();
        if (revoke) h.Revoke();
        else h.Clock.Now = h.F.Now.AddHours(2);
        var error = await Assert.ThrowsAsync<TrustValidationException>(() => h.DownloadAsync());
        Assert.Equal(revoke ? "update.signatureInvalid" : "update.metadataExpired", error.Code);
        Assert.Empty(h.Requests);
        Assert.False(Directory.Exists(h.Stage));
    }

    [Fact]
    public async Task Replaced_frozen_plan_is_rejected_without_download()
    {
        using var h = await Harness.CreateAsync();
        var frozen = h.Plan with { CreatedAt = h.Plan.CreatedAt.AddSeconds(-1) };
        var error = await Assert.ThrowsAsync<UpdatePlanException>(() => h.Downloader.DownloadAsync(frozen, h.ReportAsync));
        Assert.Equal("update.planStale", error.Code);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Authorization_expiring_during_download_prevents_publication_and_retains_cache()
    {
        using var h = await Harness.CreateAsync();
        h.BeforeAsset = (_, _) => { h.Clock.Now = h.F.Now.AddHours(2); return Task.CompletedTask; };
        var error = await Assert.ThrowsAsync<TrustValidationException>(() => h.DownloadAsync());
        Assert.Equal("update.metadataExpired", error.Code);
        Assert.False(Directory.Exists(h.Stage));
        Assert.NotEmpty(Directory.GetFiles(h.Options.CacheRoot, "*.blob"));
        h.AssertNoWork();
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task Full_package_fallback_rebuilds_same_tree_when_file_assets_fail_integrity()
    {
        using var h = await Harness.CreateAsync();
        h.CorruptFileAssets = true;
        var result = await h.DownloadAsync();
        Assert.Equal(2, result.DownloadedFiles);
        Assert.Contains(h.F.Artifacts.FullPackage!.DownloadSha256 + ".blob", h.Requests);
        await h.VerifyStageAsync();
        h.AssertNoWork();
    }

    [Fact]
    public async Task Cancellation_removes_private_work_retains_verified_cache_and_retry_uses_it()
    {
        using var h = await Harness.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        h.BeforeAsset = async (_, token) =>
        {
            if (h.Requests.Count == 2) { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        };
        var pending = h.DownloadAsync(token: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(Directory.Exists(h.Stage));
        Assert.Single(Directory.GetFiles(h.Options.CacheRoot, "*.blob"));
        Assert.Empty(h.Barrier.Snapshot().Activities);
        h.AssertNoWork();
        h.BeforeAsset = null;
        var previous = h.Requests.Count;
        var result = await h.DownloadAsync();
        Assert.Equal(1, h.Requests.Count - previous);
        Assert.Equal(h.F.Artifacts.Files[1].DownloadLength, (long)result.RequiredDownloadBytes.Value);
        await h.VerifyStageAsync();
    }

    [Fact]
    public async Task Game_activity_pauses_verification_and_cancel_releases_staging_activity()
    {
        using var h = await Harness.CreateAsync();
        Directory.CreateDirectory(h.Current);
        await File.WriteAllTextAsync(Path.Combine(h.Current, "same"), "same");
        using var game = h.Barrier.EnterActivity("game", "game.resource");
        using var cancellation = new CancellationTokenSource();
        var pending = h.DownloadAsync(token: cancellation.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!h.Barrier.Snapshot().Activities.Any(item => item.Kind == "update.verification"))
            await Task.Delay(10, timeout.Token);
        Assert.False(pending.IsCompleted);
        Assert.Empty(h.Requests);
        Assert.Null(h.Barrier.TryEnterMaintenance("install"));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(h.Barrier.Snapshot().Activities);
        h.AssertNoWork();
    }

    [Fact]
    public async Task User_downloads_join_one_operation_survive_request_disconnect_and_end_staged()
    {
        using var h = await Harness.CreateAsync();
        using var lease = DataRootLease.Open(new(h.F.Path("bootstrap"), true, h.F.Path("data")));
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations, activityBarrier: h.Barrier);
        var operations = new OperationStore(database, h.Clock);
        var policy = new UpdatePolicyStore(database, h.Clock);
        await policy.SaveAsync(new(AutomaticChecks: false), UpdatePolicyStore.ETag(policy.Get()));
        using var worker = new UpdateCheckOperations(operations, policy, h.Checker, h.Clock, h.Downloader);
        h.CheckBudgetReport = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.BeforeAsset = async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        await worker.StartAsync(default);
        try
        {
            using var request = new CancellationTokenSource();
            var first = await worker.SubmitDownloadAsync("owner", h.Plan.PlanId, "first-download-key", h.ETag, request.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            request.Cancel();
            var joined = await worker.SubmitDownloadAsync("other", h.Plan.PlanId, "joined-download-key", h.ETag);
            Assert.Equal(first.Id, joined.Id);
            Assert.Equal("resource.busy", (await Assert.ThrowsAsync<OperationException>(() => worker.SubmitAsync("owner", "conflicting-check-key"))).Code);
            var running = operations.Get(first.Id, "other", false);
            Assert.Equal("downloading", running.Phase);
            Assert.True(running.Result!.Value.GetProperty("requiredDownloadBytes").GetString() is { Length: > 0 });
            release.TrySetResult();
            var done = await TerminalAsync(operations, first.Id);
            Assert.Equal(OperationState.Succeeded, done.State.KnownValue);
            Assert.Equal("staged", done.Phase);
            Assert.Equal(1, done.Progress);
            Assert.False(Directory.Exists(h.Options.VersionsRoot));
            h.Clock.Now = h.F.Now.AddHours(2);
            var retry = await worker.SubmitDownloadAsync("owner", h.Plan.PlanId, "first-download-key", h.ETag);
            Assert.Equal(first.Id, retry.Id);
            Assert.Equal("staged", retry.Phase);
            Assert.Equal(2, h.Requests.Count);
        }
        finally { release.TrySetResult(); await worker.StopAsync(default); }
        await h.VerifyStageAsync();
    }

    [Fact]
    public async Task Explicit_cancellation_reaches_active_download_and_releases_all_owned_work()
    {
        using var h = await Harness.CreateAsync();
        using var lease = DataRootLease.Open(new(h.F.Path("bootstrap"), true, h.F.Path("data")));
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations, activityBarrier: h.Barrier);
        var operations = new OperationStore(database, h.Clock);
        var policy = new UpdatePolicyStore(database, h.Clock);
        await policy.SaveAsync(new(AutomaticChecks: false), UpdatePolicyStore.ETag(policy.Get()));
        h.CheckBudgetReport = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.BeforeAsset = async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped.TrySetResult(); }
        };
        using var worker = new UpdateCheckOperations(operations, policy, h.Checker, h.Clock, h.Downloader);
        await worker.StartAsync(default);
        try
        {
            var submitted = await worker.SubmitDownloadAsync("owner", h.Plan.PlanId, "cancel-download-key", h.ETag);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await operations.CancelAsync(submitted.Id, "owner", true, "user-stop");
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await worker.StopAsync(default); }
        Assert.False(Directory.Exists(h.Stage));
        h.AssertNoWork();
        Assert.Empty(h.Barrier.Snapshot().Activities);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expected_failure_retains_budget_and_next_operation_can_be_accepted(bool insufficientSpace)
    {
        using var h = await Harness.CreateAsync();
        if (!insufficientSpace)
        {
            Directory.CreateDirectory(h.Stage);
            await File.WriteAllTextAsync(Path.Combine(h.Stage, "unexpected"), "preserve");
        }
        using var lease = DataRootLease.Open(new(h.F.Path("bootstrap"), true, h.F.Path("data")));
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations, activityBarrier: h.Barrier);
        var operations = new OperationStore(database, h.Clock);
        var policy = new UpdatePolicyStore(database, h.Clock);
        await policy.SaveAsync(new(AutomaticChecks: false), UpdatePolicyStore.ETag(policy.Get()));
        using var worker = new UpdateCheckOperations(operations, policy, h.Checker, h.Clock,
            insufficientSpace ? h.Service(_ => 1) : h.Downloader);
        await worker.StartAsync(default);
        try
        {
            var first = await worker.SubmitDownloadAsync("owner", h.Plan.PlanId, "damaged-stage-key", h.ETag);
            var failed = await TerminalAsync(operations, first.Id);
            Assert.Equal(OperationState.Failed, failed.State.KnownValue);
            Assert.Equal(insufficientSpace ? "update.diskSpaceInsufficient" : "update.targetTreeMismatch", failed.Problem!.Code);
            Assert.NotNull(failed.Result);
            if (insufficientSpace) Assert.NotEqual("0", failed.Result.Value.GetProperty("peakAdditionalBytes").GetString());
            else Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(h.Stage, "unexpected")));
            var next = await worker.SubmitAsync("owner", "next-check-key");
            Assert.Equal(OperationState.Succeeded, (await TerminalAsync(operations, next.Id)).State.KnownValue);
        }
        finally { await worker.StopAsync(default); }
        Assert.Empty(h.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("../release")]
    [InlineData("C:\\release")]
    [InlineData("CON")]
    [InlineData("release.")]
    [InlineData("release/other")]
    public async Task Installed_release_identity_requires_one_valid_local_directory(string? releaseId)
    {
        using var h = await Harness.CreateAsync();
        var invalid = h.Options with { InstalledReleaseId = releaseId };
        var error = Record.Exception(invalid.Validate);
        Assert.True(error is ArgumentException or FileTreeException);
        Assert.False(Directory.Exists(h.Options.VersionsRoot));
    }

    [Fact]
    public async Task Download_reports_do_not_overwrite_cancelled_terminal_and_success_is_staged()
    {
        using var h = await Harness.CreateAsync();
        using var lease = DataRootLease.Open(new(h.F.Path("bootstrap"), true, h.F.Path("data")));
        await using var database = SqliteStore.Open(lease, UpdatePolicyStore.Migrations);
        var operations = new OperationStore(database, h.Clock);
        var admitted = await operations.AdmitUpdateAsync("owner", UpdateAction.Download, h.Plan, "download-report-key", h.ETag);
        var id = admitted.Snapshot.Id;
        await operations.BeginUpdateAsync(id);
        Assert.True(await operations.ReportUpdateDownloadAsync(id, "preparing", JsonSerializer.SerializeToElement(new { requiredDownloadBytes = "42" })));
        Assert.Equal("42", operations.Get(id, "owner", true).Result!.Value.GetProperty("requiredDownloadBytes").GetString());
        var cancelled = await operations.CancelAsync(id, "owner", true, "stop");
        Assert.False(await operations.ReportUpdateDownloadAsync(id, "verifying", JsonSerializer.SerializeToElement(new { requiredDownloadBytes = "0" })));
        Assert.Equal(JsonSerializer.Serialize(cancelled, ContractJson.Options),
            JsonSerializer.Serialize(await operations.FinishUpdateAsync(id), ContractJson.Options));
    }

    private static async Task<OperationSnapshot> TerminalAsync(OperationStore operations, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var value = operations.Get(id, "owner", true);
            if (value.State.KnownValue is OperationState.Succeeded or OperationState.Failed or OperationState.Cancelled) return value;
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class Harness : IDisposable
    {
        internal SignedReleaseFixture F { get; } = new(buildArtifacts: true);
        internal ActivityBarrier Barrier { get; } = new();
        internal Clock Clock { get; }
        internal UpdateInstallationOptions Options { get; }
        internal ReleaseUpdateCheckService Checker { get; }
        internal ReleaseUpdateDownloadService Downloader { get; }
        internal UpdatePlan Plan { get; private set; } = null!;
        internal string ETag => OperationStore.UpdatePlanETag(Plan);
        internal string Stage => Path.Combine(Options.StagingRoot, Plan.PlanId);
        internal string Current => Path.Combine(Options.CurrentVersionRoot!, "components/core");
        internal ConcurrentQueue<string> Requests { get; } = new();
        internal List<(string Phase, UpdateDownloadStatus Status)> Reports { get; } = [];
        internal Func<string, CancellationToken, Task>? BeforeAsset { get; set; }
        internal bool CorruptFileAssets { get; set; }
        internal bool CheckBudgetReport { get; set; } = true;
        private readonly ReleaseIndexSourceProvider provider;

        private Harness(bool multiple, bool nominalReuse)
        {
            Clock = new(F.Now);
            var node = F.Payload();
            node["releaseSequence"] = 2;
            var components = node["platforms"]![0]!["components"]!.AsArray();
            if (multiple)
            {
                foreach (var id in new[] { "second", "optional" })
                {
                    var copy = components[0]!.DeepClone();
                    copy["componentId"] = id;
                    copy["optional"] = id == "optional";
                    foreach (var file in copy["files"]!.AsArray()) file!["componentId"] = id;
                    components.Add(copy);
                }
                components[1]!["dependencies"] = JsonSerializer.SerializeToNode(new[] { new { componentId = "core", minimumVersion = "1.0.0" } });
            }
            var manifest = F.Release(node);
            var index = JsonSerializer.SerializeToUtf8Bytes(F.Index(manifest, releaseSequence: 2), SignedReleaseFixture.Options);
            provider = new(new Handler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                return Task.FromResult(path.EndsWith("channel-index.json", StringComparison.Ordinal) ? Response(index) :
                    path.EndsWith(".manifest.json", StringComparison.Ordinal) ? Response(manifest) : new(HttpStatusCode.NotFound));
            }), retryCount: 0);
            Options = new("mabitool", F.Path("installation"), new("owner/project", "stable", null, null), F.Root, 1, new('0', 64),
                new(new(1, 0, 0), 1, 1, "win-x64", new(10, 0, 19045), 100_000_000,
                    nominalReuse ? [new("core", new(1, 0, 0), 7, F.Artifacts.TargetTreeHash)] : []),
                new(new(1, 0, 0), 1, "self-contained"), "release-old");
            Options.Validate();
            Checker = new(Options, provider, Clock);
            Downloader = Service();
        }

        internal static async Task<Harness> CreateAsync(bool multiple = false, bool nominalReuse = false)
        {
            var h = new Harness(multiple, nominalReuse);
            try
            {
                var offer = await h.Checker.CheckAsync(default);
                h.Plan = await h.Downloader.ReadPlanAsync(offer.PlanId!);
                return h;
            }
            catch { h.Dispose(); throw; }
        }

        internal ReleaseUpdateDownloadService Service(Func<string, long>? freeSpace = null) => new(Options, Checker, provider,
            () => new(), Barrier, () => new Handler(async (request, token) =>
            {
                if (CheckBudgetReport) Assert.Contains(Reports, item => item.Phase == "preparing");
                var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
                Requests.Enqueue(name);
                if (BeforeAsset is { } before) await before(name, token);
                var package = name == F.Artifacts.FullPackage!.DownloadSha256 + ".blob";
                if (CorruptFileAssets && !package) return Response("wrong"u8.ToArray());
                return Response(await File.ReadAllBytesAsync(F.Path(package ? "built/full.zip" : "built/blobs/" + name), token));
            }), freeSpace);

        internal Task<UpdateDownloadStatus> DownloadAsync(ReleaseUpdateDownloadService? service = null, CancellationToken token = default) =>
            (service ?? Downloader).DownloadAsync(Plan, ReportAsync, token);

        internal Task ReportAsync(string phase, UpdateDownloadStatus status, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Reports.Add((phase, status)); return Task.CompletedTask; }

        internal async Task VerifyStageAsync()
        {
            var target = Plan.Components.SelectMany(component => F.Artifacts.Files.Select(artifact =>
                artifact.File with { RelativePath = "components/" + component.ComponentId + "/" + artifact.File.RelativePath })).ToArray();
            await FileTreePlanner.VerifyAsync(Stage, target);
        }

        internal void Revoke()
        {
            var root = F.Root with { RootVersion = 2, Keys = F.Root.Keys.Select(key => key.KeyId == "release" ? key with { Revoked = true } : key).ToArray() };
            new UpdateTrustStore(Path.Combine(Options.StateRoot, "trust.json"), F.Root, F.Now).ApplyRootRotation(
                SignedReleaseFixture.Sign(JsonSerializer.SerializeToUtf8Bytes(root, SignedReleaseFixture.Options), ("root", F.RootSigner)), F.Now);
        }

        internal void AssertNoWork()
        {
            if (Directory.Exists(Options.StagingRoot)) Assert.Empty(Directory.GetDirectories(Options.StagingRoot, "*.work-*"));
        }

        public void Dispose() { provider.Dispose(); F.Dispose(); }
    }

    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

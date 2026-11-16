using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Updates;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.Updates.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class UpdateApiTests
{
    [Fact]
    public async Task PolicyIsSharedConditionalIdempotentAndRecoverableThroughSseAndRestart()
    {
        await using var host = await StorageApiTests.Host.Start(); using var owner = host.Client(); using var other = host.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var ct = timeout.Token;
        using var reader = host.Client((await Pair(owner, [ApiPermissions.SystemRead], ct)).Token);
        string? etag = null;
        var initial = await owner.GetUpdatePolicyAsync(ct, metadata => etag = metadata.ETag);
        Assert.NotNull(etag); Assert.False((await reader.GetUpdateStateAsync(ct)).Configured);
        Assert.Equal(403, (await Assert.ThrowsAsync<MabiToolApiException>(() => reader.SaveUpdatePolicyAsync(new(false), "denied-policy-key", etag!, ct))).Status);
        var filter = new EventStreamOptions(["update.policy-changed"], [], null);
        var before = await reader.GetEventSnapshotAsync(filter, ct);
        await using (var stream = reader.StreamEventsAsync(filter with { LastEventId = before.EventCursor }, ct).GetAsyncEnumerator(ct))
        {
            var pending = stream.MoveNextAsync().AsTask();
            var request = initial.Policy with { AutomaticChecks = false, CheckIntervalHours = 2 };
            var saved = await owner.SaveUpdatePolicyAsync(request, "http-policy-save", etag!, ct);
            Assert.Equal(saved, await other.GetUpdatePolicyAsync(ct));
            Assert.Equal(saved, await other.SaveUpdatePolicyAsync(request, "http-policy-save", etag!, ct));
            Assert.Equal("revision.stale", (await Assert.ThrowsAsync<MabiToolApiException>(() => owner.SaveUpdatePolicyAsync(
                new(false, 3), "http-policy-stale", etag!, ct))).Problem!.Code);
            Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<MabiToolApiException>(() => owner.SaveUpdatePolicyAsync(
                new(false, 3), "http-policy-save", etag!, ct))).Problem!.Code);
            Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(saved, stream.Current.Data.Deserialize<EventEnvelope<UpdatePolicySnapshot>>(ContractJson.Options)!.Data);
        }
        var policy = await owner.GetUpdatePolicyAsync(ct);
        await host.RestartAsync(ct); using var restarted = host.Client();
        Assert.Equal(policy, await restarted.SaveUpdatePolicyAsync(policy.Policy, "http-policy-save", etag!, ct));
        Assert.Equal(policy, (await restarted.GetUpdateStateAsync(ct)).Policy);
    }

    [Fact]
    public async Task MissingSourceProducesPersistentFailureAndReadClientsSeeSharedStatus()
    {
        await using var host = await StorageApiTests.Host.Start(); using var owner = host.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = timeout.Token;
        using var reader = host.Client((await Pair(owner, [ApiPermissions.SystemRead], ct)).Token);
        Assert.Equal(403, (await Assert.ThrowsAsync<MabiToolApiException>(() => reader.CreateUpdateCheckAsync(new(), "denied-update-check", ct))).Status);
        var accepted = await owner.CreateUpdateCheckAsync(new(), "source-not-configured", ct);
        var terminal = await StorageApiTests.WaitForOperation(owner, accepted.Id, ct);
        Assert.Equal(OperationState.Failed, terminal.State.KnownValue);
        Assert.Equal("update.sourceNotConfigured", terminal.Problem!.Code);
        Assert.Equal(accepted.Id, (await reader.GetOperationAsync(accepted.Id, ct)).Id);
        var state = await reader.GetUpdateStateAsync(ct); Assert.False(state.Configured); Assert.Null(state.Available);
        Assert.Equal(terminal.Id, state.Operation!.Id);
        Assert.Equal(terminal.Id, (await owner.CreateUpdateCheckAsync(new(), "source-not-configured", ct)).Id);
        Assert.Equal("update.sourceNotConfigured", (await Assert.ThrowsAsync<MabiToolApiException>(() => reader.GetUpdatePlanAsync(new('a', 32), ct))).Problem!.Code);
        await host.RestartAsync(ct); using var restarted = host.Client();
        Assert.Equal(terminal.Id, (await restarted.GetUpdateStateAsync(ct)).Operation!.Id);
        Assert.Equal(OperationState.Failed, (await restarted.CreateUpdateCheckAsync(new(), "source-not-configured", ct)).State.KnownValue);
    }

    [Fact]
    public async Task SignedCheckAndExplicitDownloadShareOnePlanAndKeepStageAndReceiptAcrossEpochs()
    {
        await using var host = await SignedHost.Start(); using var owner = host.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120)); var ct = timeout.Token;
        using var first = host.Client((await Pair(owner, [ApiPermissions.SystemRead, ApiPermissions.UpdatesManage], ct)).Token);
        using var second = host.Client((await Pair(owner, [ApiPermissions.SystemRead, ApiPermissions.UpdatesManage], ct)).Token);
        Assert.Empty(host.Requests);
        var check = await first.CreateUpdateCheckAsync(new(), "signed-public-check", ct);
        Assert.Equal(OperationState.Succeeded, (await StorageApiTests.WaitForOperation(second, check.Id, ct)).State.KnownValue);
        var offer = (await second.GetUpdateStateAsync(ct)).Available!;
        Assert.Equal(UpdateSourceKind.GithubDirect, offer.Source.KnownValue);
        string? etag = null;
        var plan = await first.GetUpdatePlanAsync(offer.PlanId!, ct, metadata => etag = metadata.ETag);
        Assert.Equal(Json(plan), Json(await second.GetUpdatePlanAsync(plan.Id, ct)));
        Assert.False(plan.Expired); Assert.Single(plan.Components);
        Assert.Equal(2, host.Requests.Count); Assert.False(Directory.Exists(host.Options.StagingRoot));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeAsset = async token => { reached.TrySetResult(); await resume.Task.WaitAsync(token); };
        var download = await first.DownloadUpdatePlanAsync(plan.Id, "public-download-first", etag!, ct);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(download.Id, (await second.DownloadUpdatePlanAsync(plan.Id, "public-download-second", etag!, ct)).Id);
        }
        finally { resume.TrySetResult(); }
        var terminal = await StorageApiTests.WaitForOperation(second, download.Id, ct);
        Assert.Equal(OperationState.Succeeded, terminal.State.KnownValue); Assert.Equal("staged", terminal.Phase);
        var progressFilter = new EventStreamOptions(["update.progress"], [download.Id], null);
        var progressSnapshot = await second.GetEventSnapshotAsync(progressFilter, ct);
        var progress = Assert.Single(progressSnapshot.Resources).Data.Deserialize<UpdateProgress>(ContractJson.Options)!;
        if ((progress.Revision?.Value ?? 0) < terminal.Revision.Value)
        {
            await using var stream = second.StreamEventsAsync(progressFilter with { LastEventId = progressSnapshot.EventCursor }, ct).GetAsyncEnumerator(ct);
            while ((progress.Revision?.Value ?? 0) < terminal.Revision.Value)
            {
                Assert.True(await stream.MoveNextAsync());
                progress = stream.Current.Data.Deserialize<EventEnvelope<UpdateProgress>>(ContractJson.Options)!.Data;
            }
            progressSnapshot = await second.GetEventSnapshotAsync(progressFilter, ct);
            progress = Assert.Single(progressSnapshot.Resources).Data.Deserialize<UpdateProgress>(ContractJson.Options)!;
        }
        var actualDownload = terminal.Result!.Value.Deserialize<UpdateDownloadStatus>(ContractJson.Options)!;
        Assert.Equal(download.Id, progress.OperationId); Assert.Equal(plan.Id, progress.PlanId);
        Assert.Equal(terminal.Revision, progress.Revision); Assert.Equal("staged", progress.Phase);
        Assert.Equal(actualDownload.VerifiedBytes, progress.VerifiedBytes); Assert.Equal(progress.VerifiedBytes, progress.TotalBytes);
        Assert.True(progress.VerifiedBytes.Value > 0); Assert.Equal(UpdateSourceKind.GithubDirect, progress.Source!.Value.KnownValue);
        Assert.Equal(Json(progressSnapshot.Resources), Json((await first.GetEventSnapshotAsync(progressFilter, ct)).Resources));
        var target = host.Fixture.Artifacts.Files.Select(item => item.File with { RelativePath = "components/core/" + item.File.RelativePath }).ToArray();
        await FileTreePlanner.VerifyAsync(Path.Combine(host.Options.StagingRoot, plan.Id), target, ct);
        Assert.False(Directory.Exists(host.Options.VersionsRoot));
        var filter = new EventStreamOptions(["operation.changed"], [download.Id], null);
        var before = await second.GetEventSnapshotAsync(filter, ct); var oldEpoch = (await owner.GetSystemInfoAsync(ct)).InstanceId;
        await host.Restart(ct); using var restarted = host.Client();
        Assert.NotEqual(oldEpoch, (await restarted.GetSystemInfoAsync(ct)).InstanceId);
        Assert.Equal(terminal.Id, (await restarted.GetUpdateStateAsync(ct)).Operation!.Id);
        Assert.Equal("staged", (await restarted.GetUpdateStateAsync(ct)).State);
        Assert.Equal(Json(plan), Json(await restarted.GetUpdatePlanAsync(plan.Id, ct)));
        var resumed = await restarted.DownloadUpdatePlanAsync(plan.Id, "public-owner-resume", etag!, ct);
        var reused = await StorageApiTests.WaitForOperation(restarted, resumed.Id, ct);
        Assert.Equal(OperationState.Succeeded, reused.State.KnownValue);
        Assert.True(reused.Result!.Value.Deserialize<UpdateDownloadStatus>(ContractJson.Options)!.ReusedStage);
        Assert.Equal(resumed.Id, (await restarted.DownloadUpdatePlanAsync(plan.Id, "public-owner-resume", etag!, ct)).Id);
        await using var stale = restarted.StreamEventsAsync(filter with { LastEventId = before.EventCursor }, ct).GetAsyncEnumerator(ct);
        Assert.Equal("events.resyncRequired", (await Assert.ThrowsAsync<MabiToolApiException>(() => stale.MoveNextAsync().AsTask())).Problem!.Code);
        host.Clock.Now = host.Fixture.Now.AddHours(2);
        Assert.True((await restarted.GetUpdatePlanAsync(plan.Id, ct)).Expired);
        Assert.Equal("update.planStale", (await Assert.ThrowsAsync<MabiToolApiException>(() => restarted.DownloadUpdatePlanAsync(plan.Id, "public-expired-plan", etag!, ct))).Problem!.Code);
        Assert.Equal("resource.notFound", (await Assert.ThrowsAsync<MabiToolApiException>(() => restarted.GetUpdatePlanAsync(new('a', 32), ct))).Problem!.Code);
        var repository = new UpdatePlanRepository(host.Options.StateRoot); var saved = await repository.ReadAsync(plan.Id, ct);
        await File.WriteAllTextAsync(Path.Combine(host.Options.StateRoot, "plans", plan.Id + ".json"), Json(saved with { Plan = saved.Plan with { DownloadBytes = -1 } }), ct);
        Assert.Equal("update.planInvalid", (await Assert.ThrowsAsync<MabiToolApiException>(() => restarted.GetUpdatePlanAsync(plan.Id, ct))).Problem!.Code);
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ContractJson.Options);
    private static async Task<AccessGrant> Pair(MabiToolClient owner, IReadOnlyList<string> permissions, CancellationToken ct)
    {
        var challenge = await owner.CreatePairingChallengeAsync(new("updates-api-client", null, permissions), ct);
        return await owner.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code), ct);
    }

    private sealed class SignedHost : IAsyncDisposable
    {
        internal SignedReleaseFixture Fixture { get; } = new(buildArtifacts: true);
        internal Clock Clock { get; }
        internal UpdateInstallationOptions Options { get; }
        internal ConcurrentQueue<string> Requests { get; } = new();
        internal Func<CancellationToken, Task>? BeforeAsset { get; set; }
        private readonly string token = LocalAuthService.NewSecret();
        private readonly byte[] manifest;
        private readonly byte[] index;
        private WebApplication app = null!;
        private Uri address = null!;
        private SignedHost()
        {
            Clock = new(Fixture.Now); manifest = Fixture.Release();
            index = JsonSerializer.SerializeToUtf8Bytes(Fixture.Index(manifest), SignedReleaseFixture.Options);
            Options = new("mabitool", Fixture.Path("installation"), new("owner/project", "stable", null, null), Fixture.Root, 0, new('0', 64),
                new(new(1, 0, 0), 1, 1, "win-x64", new(10, 0, 19045), 100_000_000, []), new(new(1, 0, 0), 1, "self-contained"));
        }
        internal static async Task<SignedHost> Start()
        {
            var host = new SignedHost();
            try { await host.Open(); return host; }
            catch { await host.DisposeAsync(); throw; }
        }
        private async Task Open()
        {
            app = BackendApplication.Build(new(0, token, new HashSet<string>()), Clock,
                new(Fixture.Path("bootstrap"), true, Fixture.Path("data")), updateInstallation: Options,
                updateHttpHandler: () => new Handler(async (request, ct) =>
                {
                    var name = Path.GetFileName(request.RequestUri!.AbsolutePath); Requests.Enqueue(name);
                    if (name == "channel-index.json") return Response(index);
                    if (name.EndsWith(".manifest.json", StringComparison.Ordinal)) return Response(manifest);
                    if (BeforeAsset is { } before) await before(ct);
                    return Response(await File.ReadAllBytesAsync(Fixture.Path(name == Fixture.Artifacts.FullPackage!.DownloadSha256 + ".blob" ? "built/full.zip" : "built/blobs/" + name), ct));
                }));
            await app.StartAsync();
            address = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        }
        internal MabiToolClient Client(string? credential = null) => new(address, _ => ValueTask.FromResult(credential ?? token));
        internal async Task Restart(CancellationToken ct) { await app.StopAsync(ct); await app.DisposeAsync(); await Open(); }
        public async ValueTask DisposeAsync()
        {
            if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
            Fixture.Dispose();
        }
        private static HttpResponseMessage Response(byte[] value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}

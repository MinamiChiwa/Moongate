using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Diagnostics;
using MabiTool.Host.Rooms;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class RoomRoundApiTests
{
    [Theory]
    [InlineData(MusicStopRule.Manual)]
    [InlineData(MusicStopRule.Duration)]
    [InlineData(MusicStopRule.FirstCycle)]
    public async Task ConfirmedRoundsDispatchStartAndStopAndReleaseTheInputLease(MusicStopRule rule)
    {
        using var area = new RoomInputApiTests.Area(); await using var app = area.Build(); await app.StartAsync();
        using var client = RoomInputApiTests.Client(app);
        await using var server = await new RoomServerListenerFactory().StartAsync(RoomServerMode.Loopback, CancellationToken.None);
        var room = await SetUp(client, server.LocalEndpoint);
        var initial = await Read(client);
        var request = new RoomRoundRequest("ensemble", [room.MemberId!], 1000, rule,
            rule == MusicStopRule.Duration ? 400 : null, rule == MusicStopRule.FirstCycle ? 300 : null);
        var accepted = await Post(client, ApiContract.PrepareRoomRound.Path, request, "prepare-round", initial.ETag);
        var finished = await Terminal(client, accepted.Id);
        Assert.Equal(OperationState.Succeeded, finished.State.KnownValue); Assert.Equal(EffectStatus.Unknown, finished.EffectStatus.KnownValue);
        var result = finished.Result!.Value.Deserialize<RoomRoundCommandResult>(ContractJson.Options)!;
        Assert.True(result.ServerConfirmed); Assert.Equal(rule, result.Round.StopRule!.Value.KnownValue);
        await Wait(() => area.Device.Commands.Count >= 2);
        if (rule != MusicStopRule.Duration)
        {
            var current = await WaitRound(client, snapshot => snapshot.Phase == "performing" && snapshot.Receipts.Count >= 1);
            Assert.Null(current.Snapshot.StopAction);
            if (rule == MusicStopRule.FirstCycle)
            {
                Assert.Equal("waiting-for-score", current.Snapshot.CycleStopStatus);
                Assert.Equal(300, current.Snapshot.TailHoldMs);
                Assert.Equal("playback-timing-unverified", current.Snapshot.StopTimingUnavailable);
            }
            var stop = await Post(client, Command(current.Snapshot.Id!), new RoomRoundControlRequest(RoomRoundControl.StopAll), "stop-round", current.ETag);
            Assert.Equal(OperationState.Succeeded, (await Terminal(client, stop.Id)).State.KnownValue);
        }
        await Wait(() => area.Device.Commands.Count == 4);
        Assert.Equal(["down:Space", "up:Space", "down:Escape", "up:Escape"], area.Device.Commands);
        var ended = await WaitRound(client, snapshot => snapshot.Phase == "stopped" && snapshot.Receipts.Count >= 2);
        Assert.All(ended.Snapshot.Receipts, receipt => Assert.False(receipt.GameResponseVerified));
        Assert.Null(app.Services.GetRequiredService<ResourceLeaseCoordinator>().FindConflict(["foreground-input"]));
        Assert.Equal(accepted.Id, (await Post(client, ApiContract.PrepareRoomRound.Path, request, "prepare-round", initial.ETag)).Id);
        await app.StopAsync();
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task InterruptionContinueKeepsTheOriginalStartAndHostStopEndsTheRound()
    {
        using var area = new RoomInputApiTests.Area(); await using var app = area.Build(); await app.StartAsync();
        using var client = RoomInputApiTests.Client(app);
        await using var server = await new RoomServerListenerFactory().StartAsync(RoomServerMode.Loopback, CancellationToken.None);
        var room = await SetUp(client, server.LocalEndpoint); var initial = await Read(client);
        var start = await Post(client, ApiContract.PrepareRoomRound.Path, new RoomRoundRequest("ensemble", [room.MemberId!], 1000), "start-to-interrupt", initial.ETag);
        await Terminal(client, start.Id); await Wait(() => area.Device.Commands.Count == 2);
        var performing = await WaitRound(client, snapshot => snapshot.Phase == "performing" && snapshot.Receipts.Count >= 1);
        var interrupted = await Post(client, Command(performing.Snapshot.Id!), new RoomRoundControlRequest(RoomRoundControl.Interrupt, "演奏者报告中断"),
            "report-interruption", performing.ETag);
        Assert.Equal(OperationState.Succeeded, (await Terminal(client, interrupted.Id)).State.KnownValue);
        var pause = await Read(client); Assert.Equal("interrupted", pause.Snapshot.Phase);
        Assert.Equal("player-report", pause.Snapshot.Interruption!.Value.GetProperty("evidenceSource").GetString());
        using var sdk = new MabiToolClient(client.BaseAddress!, _ => ValueTask.FromResult(client.DefaultRequestHeaders.Authorization!.Parameter!));
        var continued = await sdk.ControlRoomRoundAsync(pause.Snapshot.Id!, new(RoomRoundControl.Continue), "continue-round", pause.ETag);
        Assert.Equal(OperationState.Succeeded, (await Terminal(client, continued.Id)).State.KnownValue);
        var running = await Read(client); Assert.Equal("performing", running.Snapshot.Phase);
        Assert.Equal(performing.Snapshot.StartAction, running.Snapshot.StartAction); Assert.Null(running.Snapshot.Interruption);
        Assert.Equal(2, area.Device.Commands.Count);
        var stop = await Post(client, Command(running.Snapshot.Id!), new RoomRoundControlRequest(RoomRoundControl.StopAll), "stop-continued-round", running.ETag);
        await Terminal(client, stop.Id);
        try { await Wait(() => area.Device.Commands.Count >= 4); }
        catch (TaskCanceledException)
        {
            throw new TimeoutException("继续后的停止输入未提交：" + JsonSerializer.Serialize(new
                { commands = area.Device.Commands.ToArray(), round = (await Read(client)).Snapshot }, ContractJson.Options));
        }
        Assert.Equal(4, area.Device.Commands.Count);
        await app.StopAsync();
    }

    [Fact]
    public async Task HostStartsWithoutReadinessReportsTimingAndLocalStopInterruptsTheRound()
    {
        using var area = new RoomInputApiTests.Area(); await using var app = area.Build(); await app.StartAsync();
        using var client = RoomInputApiTests.Client(app);
        await using var server = await new RoomServerListenerFactory().StartAsync(RoomServerMode.Loopback, CancellationToken.None);
        await RoomInputApiTests.Bind(client); await RoomInputApiTests.Save(client);
        var room = await RoomInputApiTests.Post<RoomLifecycleSnapshot>(client, ApiContract.CreateRoom.Path, new RoomCreateRequest(server.LocalEndpoint, "房主"));
        using (var mode = await client.PutAsJsonAsync(ApiContract.SetRoomMode.Path, new RoomModeRequest("ensemble"), ContractJson.Options)) mode.EnsureSuccessStatusCode();
        var idle = await WaitRound(client, snapshot => snapshot.Timing is not null);
        Assert.Null(idle.Snapshot.Timing!.StartRemainingMs); Assert.InRange(idle.Snapshot.Timing.RoundTripMs, 0, 1000);
        var start = await Post(client, ApiContract.PrepareRoomRound.Path, new RoomRoundRequest("ensemble", [room.MemberId!], 1500), "host-not-ready", idle.ETag);
        Assert.Equal(OperationState.Succeeded, (await Terminal(client, start.Id)).State.KnownValue);
        var counting = await Read(client);
        Assert.InRange(counting.Snapshot.Timing!.StartRemainingMs!.Value, 1, 1500);
        await Wait(() => area.Device.Commands.Count == 2);
        var performing = await WaitRound(client, snapshot => snapshot.Phase == "performing");
        Assert.True(performing.Snapshot.Timing!.StartRemainingMs < 0);

        var stopped = await RoomInputApiTests.Post<RoomLocalStopResult>(client, ApiContract.StopRoomLocally.Path, new { });
        Assert.Equal("stopScheduled", stopped.LocalAction); Assert.True(stopped.ServerConfirmed);
        await Wait(() => area.Device.Commands.Count == 4);
        Assert.Equal(["down:Space", "up:Space", "down:Escape", "up:Escape"], area.Device.Commands);
        var interrupted = await WaitRound(client, snapshot => snapshot.Phase == "interrupted");
        Assert.Equal(room.MemberId, interrupted.Snapshot.Interruption!.Value.GetProperty("memberId").GetString());
        Assert.Equal("local-stop", interrupted.Snapshot.Interruption.Value.GetProperty("source").GetString());
        var end = await Post(client, Command(interrupted.Snapshot.Id!), new RoomRoundControlRequest(RoomRoundControl.StopAll), "stop-after-local", interrupted.ETag);
        Assert.Equal(OperationState.Succeeded, (await Terminal(client, end.Id)).State.KnownValue);
        await Wait(() => app.Services.GetRequiredService<ResourceLeaseCoordinator>().FindConflict(["foreground-input"]) is null);
        await app.StopAsync();
    }

    [Fact]
    public async Task GameOperationOffSimulatesRoundsWithoutAGameBindingAndResetsOnRestart()
    {
        using var area = new RoomInputApiTests.Area();
        await using (var app = area.Build())
        {
            await app.StartAsync(); using var client = RoomInputApiTests.Client(app);
            var preferences = (await client.GetFromJsonAsync<RoomPreferences>(ApiContract.RoomPreferences.Path, ContractJson.Options))!;
            Assert.Equal("community", preferences.PublicProvider); Assert.Equal("wss://47.111.3.134/", preferences.CommunityEndpoint);
            Assert.True((await client.GetFromJsonAsync<RoomLifecycleSnapshot>(ApiContract.RoomSession.Path, ContractJson.Options))!.OperateGame);
            using (var off = await client.PutAsJsonAsync(ApiContract.SetRoomGameOperation.Path, new RoomGameOperationRequest(false), ContractJson.Options))
                Assert.False((await off.Content.ReadFromJsonAsync<RoomLifecycleSnapshot>(ContractJson.Options))!.OperateGame);
            await using var server = await new RoomServerListenerFactory().StartAsync(RoomServerMode.Loopback, CancellationToken.None);
            var room = await RoomInputApiTests.Post<RoomLifecycleSnapshot>(client, ApiContract.CreateRoom.Path, new RoomCreateRequest(server.LocalEndpoint, "房主"));
            using (var ready = await client.PutAsJsonAsync(ApiContract.SetRoomReadiness.Path, new RoomReadinessRequest(true, true, false, false, ReadyMode: "emote"), ContractJson.Options))
            {
                Assert.True(ready.IsSuccessStatusCode, await ready.Content.ReadAsStringAsync());
                Assert.True((await ready.Content.ReadFromJsonAsync<RoomLifecycleSnapshot>(ContractJson.Options))!.LocalPreparation!.Simulation);
            }
            var initial = await Read(client);
            var start = await Post(client, ApiContract.PrepareRoomRound.Path, new RoomRoundRequest("emote", [room.MemberId!], 1000), "simulated-round", initial.ETag);
            Assert.Equal(OperationState.Succeeded, (await Terminal(client, start.Id)).State.KnownValue);
            var ended = await WaitRound(client, snapshot => snapshot.Phase == "stopped" && snapshot.Receipts.Count == 1);
            Assert.True(ended.Snapshot.Receipts.Single().Simulated);
            Assert.Empty(area.Device.Commands);
            Assert.Null(app.Services.GetRequiredService<ResourceLeaseCoordinator>().FindConflict(["foreground-input"]));
            await app.StopAsync();
        }
        await using (var app = area.Build(false))
        {
            await app.StartAsync(); using var client = RoomInputApiTests.Client(app);
            Assert.True((await client.GetFromJsonAsync<RoomLifecycleSnapshot>(ApiContract.RoomSession.Path, ContractJson.Options))!.OperateGame);
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task CancelBeforeCountdownKeepsNewRoundsSafeFromOldCommandsAndViewerWrites()
    {
        using var area = new RoomInputApiTests.Area(); await using var app = area.Build(); await app.StartAsync();
        using var client = RoomInputApiTests.Client(app);
        await using var server = await new RoomServerListenerFactory().StartAsync(RoomServerMode.Loopback, CancellationToken.None);
        var room = await SetUp(client, server.LocalEndpoint); var initial = await Read(client);
        var request = new RoomRoundRequest("ensemble", [room.MemberId!], 5000);
        var start = await Post(client, ApiContract.PrepareRoomRound.Path, request, "cancel-before-start", initial.ETag); await Terminal(client, start.Id);
        // 准备操作完成时服务端可能仍在 preparing，随后提交为 committed 会推进控制修订；取提交后的修订再取消。
        var first = await WaitRound(client, snapshot => snapshot.Phase == "committed");
        var cancel = await Post(client, Command(first.Snapshot.Id!), new RoomRoundControlRequest(RoomRoundControl.Cancel), "cancel-countdown", first.ETag);
        await Terminal(client, cancel.Id); Assert.Empty(area.Device.Commands);
        await RoomInputApiTests.Ready(client); var before = await Read(client);
        var next = await Post(client, ApiContract.PrepareRoomRound.Path, request, "new-round", before.ETag); await Terminal(client, next.Id);
        // 新一轮同样在准备操作完成后才离开 preparing，等其落定再用其修订验证旧轮次命令。
        var current = await WaitRound(client, snapshot => snapshot.Id != first.Snapshot.Id && snapshot.Phase != "preparing");
        Assert.NotEqual(first.Snapshot.Id, current.Snapshot.Id);
        await Error(client, Command(first.Snapshot.Id!), new RoomRoundControlRequest(RoomRoundControl.StopAll), "stop-old-round", current.ETag,
            HttpStatusCode.Conflict, "room.planStale");
        await Error(client, ApiContract.PrepareRoomRound.Path, request with { CountdownMs = 6000 }, "cancel-before-start", initial.ETag,
            HttpStatusCode.Conflict, "idempotency.conflict");
        await Error(client, ApiContract.PrepareRoomRound.Path, request, "use-stale-revision", initial.ETag, HttpStatusCode.PreconditionFailed, "revision.stale");
        var auth = app.Services.GetRequiredService<LocalAuthService>();
        var challenge = auth.CreateChallenge(new("观察客户端", null, [ApiPermissions.SystemRead]));
        var viewer = auth.Redeem(new(challenge.ChallengeId, challenge.Code), null);
        using var other = RoomInputApiTests.Client(app, viewer.Token);
        using var privateResult = await other.GetAsync(ApiContract.Operation.Path.Replace("{id}", start.Id));
        Assert.Equal(HttpStatusCode.NotFound, privateResult.StatusCode);
        await Error(other, Command(current.Snapshot.Id!), new RoomRoundControlRequest(RoomRoundControl.StopAll), "viewer-stop", current.ETag,
            HttpStatusCode.Forbidden, "permission.denied");
        Assert.Empty(area.Device.Commands);
        await app.StopAsync();
    }

    [Fact]
    public async Task DeferredCancellationCompletesTheSentCommandAndQueuedCancellationAndRevocationDoNotSend()
    {
        using var area = new RoomInputApiTests.Area(); await using var app = area.Build(); await app.StartAsync();
        var auth = app.Services.GetRequiredService<LocalAuthService>();
        var challenge = auth.CreateChallenge(new("控制客户端", null, [ApiPermissions.SystemRead, ApiPermissions.RoomsCoordinate]));
        var grant = auth.Redeem(new(challenge.ChallengeId, challenge.Code), null);
        using var client = RoomInputApiTests.Client(app, grant.Token); using var owner = RoomInputApiTests.Client(app);
        await using var server = await RoomSessionManagerTests.FakeRoomServer.StartAsync();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CommandHook = message => { if (message.GetProperty("type").GetString() != "prepare") return Task.CompletedTask;
            observed.TrySetResult(); return resume.Task; };
        try
        {
            var room = await RoomInputApiTests.Post<RoomLifecycleSnapshot>(client, ApiContract.CreateRoom.Path, new RoomCreateRequest(server.Endpoint, "房主"));
            // 参与者不含房主：本用例只核对协议命令的取消与撤权，不涉及房主本机输入上报。
            var request = new RoomRoundRequest("emote", ["remote-member"], 5000); var initial = await Read(client);
            var sent = await Post(client, ApiContract.PrepareRoomRound.Path, request, "sent-command", initial.ETag);
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var deferred = await RoomInputApiTests.Post<OperationSnapshot>(client, ApiContract.CancelOperation.Path.Replace("{id}", sent.Id), new CancelOperationRequest("取消确认等待"));
            Assert.Equal(CancellationState.Deferred, deferred.Cancellation.State.KnownValue);
            var queued = await Post(client, ApiContract.PrepareRoomRound.Path, request, "queued-command", initial.ETag);
            var cancelled = await RoomInputApiTests.Post<OperationSnapshot>(client, ApiContract.CancelOperation.Path.Replace("{id}", queued.Id), new CancelOperationRequest("取消排队"));
            Assert.Equal(OperationState.Cancelled, cancelled.State.KnownValue); Assert.Equal(EffectStatus.None, cancelled.EffectStatus.KnownValue);
            var revoked = await Post(client, ApiContract.PrepareRoomRound.Path, request, "revoked-command", initial.ETag);
            auth.Revoke(grant.Session.ClientId); resume.TrySetResult();
            var completed = await Terminal(owner, sent.Id); Assert.Equal(OperationState.Succeeded, completed.State.KnownValue);
            Assert.Equal(CancellationState.Completed, completed.Cancellation.State.KnownValue);
            var refused = await Terminal(owner, revoked.Id); Assert.Equal("permission.denied", refused.Problem!.Code);
            Assert.Equal(EffectStatus.None, refused.EffectStatus.KnownValue);
            // 执行阶段的拒绝同时记入诊断：rooms 模块 warning，带错误码与操作 ID。
            await app.Services.GetRequiredService<DiagnosticRecorder>().FlushAsync();
            var records = await RoomInputApiTests.Post<ApiList<DiagnosticRecordEntry>>(owner, ApiContract.QueryDiagnosticRecords.Path,
                new DiagnosticRecordQuery(Modules: ["rooms"]));
            Assert.Contains(records.Items, entry => entry.Record.Level == "warning" && entry.Record.Code == "permission.denied" &&
                entry.Record.OperationId == revoked.Id);
            Assert.Single(server.Commands, message => message.GetProperty("type").GetString() == "prepare");
            await Wait(() => !app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities.Any(value => value.Kind == "room.round"));
            Assert.Empty(app.Services.GetRequiredService<ModuleRuntime>().ActiveOperations("rooms"));
        }
        finally { resume.TrySetResult(); await app.StopAsync(); }
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task UnconfirmedCommandSurvivesRestartAndIdempotentRetryDoesNotReplayIt()
    {
        using var area = new RoomInputApiTests.Area(); await using var server = await RoomSessionManagerTests.FakeRoomServer.StartAsync();
        server.ConfirmCommands = false;
        RoomRoundRequest request; string etag, id;
        await using (var app = area.Build())
        {
            await app.StartAsync(); using var client = RoomInputApiTests.Client(app);
            var room = await RoomInputApiTests.Post<RoomLifecycleSnapshot>(client, ApiContract.CreateRoom.Path, new RoomCreateRequest(server.Endpoint, "房主"));
            request = new("emote", ["remote-member"]); etag = (await Read(client)).ETag;
            var accepted = await Post(client, ApiContract.PrepareRoomRound.Path, request, "unconfirmed-restart", etag); id = accepted.Id;
            var terminal = await Terminal(client, id);
            Assert.Equal(OperationState.Failed, terminal.State.KnownValue); Assert.Equal("room.commandUnconfirmed", terminal.Problem!.Code);
            Assert.Equal(EffectStatus.Unknown, terminal.EffectStatus.KnownValue); await app.StopAsync();
        }
        await using (var app = area.Build(false))
        {
            await app.StartAsync(); using var client = RoomInputApiTests.Client(app);
            var previous = await Post(client, ApiContract.PrepareRoomRound.Path, request, "unconfirmed-restart", etag);
            Assert.Equal(id, previous.Id); Assert.Equal(OperationState.Failed, previous.State.KnownValue);
            Assert.Equal("disconnected", (await Read(client)).Snapshot.Phase);
            await Error(client, ApiContract.PrepareRoomRound.Path, request, "new-old-epoch", etag, HttpStatusCode.PreconditionFailed, "revision.stale");
            Assert.Single(server.Commands, message => message.GetProperty("type").GetString() == "prepare");
            await app.StopAsync();
        }
    }

    [Theory]
    [InlineData("ensemble", "duration", null, null)]
    [InlineData("ensemble", "manual", 1000, null)]
    [InlineData("emote", "first-cycle", null, 0)]
    [InlineData("ensemble", "first-cycle", null, 10001)]
    [InlineData("ensemble", "future", null, null)]
    public async Task InvalidStopRulesAreRejectedBeforeACommandIsSent(string mode, string rule, int? duration, int? tail)
    {
        using var area = new RoomInputApiTests.Area(); await using var app = area.Build(); await app.StartAsync();
        using var client = RoomInputApiTests.Client(app);
        var initial = await Read(client);
        await Error(client, ApiContract.PrepareRoomRound.Path, new RoomRoundRequest(mode, ["member"], 5000, new OpenEnum<MusicStopRule>(rule), duration, tail),
            "invalid-stop-rule", initial.ETag, HttpStatusCode.BadRequest, "request.invalid");
        Assert.Empty(area.Device.Commands); await app.StopAsync();
    }

    private static string Command(string id) => ApiContract.ControlRoomRound.Path.Replace("{id}", id);
    private static Task<OperationSnapshot> Post(HttpClient client, string path, object body, string key, string etag) =>
        RoomInputApiTests.Post<OperationSnapshot>(client, path, body, key, etag);
    private static async Task<RoomLifecycleSnapshot> SetUp(HttpClient client, string endpoint)
    {
        await RoomInputApiTests.Bind(client); await RoomInputApiTests.Save(client);
        var room = await RoomInputApiTests.Post<RoomLifecycleSnapshot>(client, ApiContract.CreateRoom.Path, new RoomCreateRequest(endpoint, "房主"));
        using var mode = await client.PutAsJsonAsync(ApiContract.SetRoomMode.Path, new RoomModeRequest("ensemble"), ContractJson.Options); mode.EnsureSuccessStatusCode();
        await RoomInputApiTests.Ready(client); return room;
    }
    private static async Task<(RoomRoundSnapshot Snapshot, string ETag)> Read(HttpClient client)
    {
        using var response = await client.GetAsync(ApiContract.CurrentRoomRound.Path); response.EnsureSuccessStatusCode();
        return ((await response.Content.ReadFromJsonAsync<RoomRoundSnapshot>(ContractJson.Options))!, response.Headers.ETag!.ToString());
    }
    private static async Task<(RoomRoundSnapshot Snapshot, string ETag)> WaitRound(HttpClient client, Func<RoomRoundSnapshot, bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true) { var value = await Read(client); if (condition(value.Snapshot)) return value; await Task.Delay(10, timeout.Token); }
    }
    private static async Task<OperationSnapshot> Terminal(HttpClient client, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var result = (await client.GetFromJsonAsync<OperationSnapshot>(ApiContract.Operation.Path.Replace("{id}", id), ContractJson.Options, timeout.Token))!;
            if (SqliteStore.IsTerminal(result.State.KnownValue)) return result; await Task.Delay(10, timeout.Token);
        }
    }
    private static async Task Error(HttpClient client, string path, object body, string key, string? etag, HttpStatusCode status, string code)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: ContractJson.Options) };
        request.Headers.Add("Idempotency-Key", key); if (etag is not null) request.Headers.Add("If-Match", etag);
        using var response = await client.SendAsync(request); Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code);
    }
    private static async Task Wait(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); while (!condition()) await Task.Delay(10, timeout.Token); }
}

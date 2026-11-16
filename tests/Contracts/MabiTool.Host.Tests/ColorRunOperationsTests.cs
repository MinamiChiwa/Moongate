using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Color;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.ModuleSdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[SupportedOSPlatform("windows")]
[Collection("Persistent backend")]
public sealed class ColorRunOperationsTests
{
    private const string Token = "color_only_0123456789012345678901234567890123456789";
    private static ColorProfile Profile => new("profile", 1, "离线多区域方案", ColorStrategy.Screenshot,
        Enumerable.Range(0, 3).Select(i => new ColorRegion("region-" + i, "#202020", ["#212121"],
            i == 0 ? ColorMatchMode.ExactHex : ColorMatchMode.Similar, 8, i + 1, true)).ToArray(), new(1));

    [Fact]
    public async Task RoomAndColorShareInputOwnershipAndKeepRoomIdentityAcrossSwitches()
    {
        using var area = new Area(); await using var app = area.Build(); await app.StartAsync(); using var owner = Client(app);
        await using var server = await RoomSessionManagerTests.FakeRoomServer.StartAsync();
        await RoomInputApiTests.Save(owner);
        var room = await RoomInputApiTests.Post<RoomLifecycleSnapshot>(owner, ApiContract.CreateRoom.Path, new RoomCreateRequest(server.Endpoint, "Host"));
        var ready = await RoomInputApiTests.Ready(owner);
        var binding = area.Bindings.Current!.Session;
        var request = new ColorRunStartRequest(binding.Id, binding.BindingRevision, Profile);
        await Error(owner, ApiContract.StartColorRun.Path, request, "color-while-ready", 409, "resource.busy");
        var oldId = ready.LocalPreparation!.Id!;
        await RoomInputApiTests.Post<RoomLifecycleSnapshot>(owner, ApiContract.ReleaseRoomInput.Path.Replace("{id}", oldId), new { }, "switch-to-color");
        var operation = await Start(owner, request, "color-after-release");
        var runId = operation.Result!.Value.GetProperty("id").GetString()!;
        await WaitRun(owner, runId, value => value.Interaction?.RetryEnabled == true);
        using (var conflict = await owner.PutAsJsonAsync(ApiContract.SetRoomReadiness.Path,
            new RoomReadinessRequest(true, false, true, true, ReadyMode: "ensemble"), ContractJson.Options))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("resource.busy", (await conflict.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code);
        }
        await Post<OperationSnapshot>(owner, ApiContract.CancelOperation.Path.Replace("{id}", operation.Id), new CancelOperationRequest(null));
        Assert.Equal(OperationState.Cancelled, (await WaitOperation(owner, operation.Id)).State.KnownValue);
        var next = await RoomInputApiTests.Ready(owner);
        Assert.Equal("connected", next.State); Assert.Equal(room.MemberId, next.MemberId); Assert.Equal(room.Code, next.Code);
        Assert.NotEqual(oldId, next.LocalPreparation!.Id);
        await app.StopAsync();
    }

    [Fact]
    public void DelayedReceiptPreservesNewerMeasurementAndNewCommandInvalidatesCurrent()
    {
        using var area = new Area();
        var measured = new ColorMeasurement(new Dictionary<string, string> { ["region-0"] = "#202020" },
            DateTimeOffset.UtcNow, new("offline-test", "1", "TW", "test"), new(1), ObservationQuality.Confirmed);
        var run = new ColorRun("run", "operation", area.Bindings.Current!.Session, Profile, "waiting-user",
            99999, "new-board", measured, measured, measured, null)
            { Interaction = new(new(8), "new-board", true, true),
                Result = new("run", 2, "matched", "target_matched", measured, measured, measured) };
        foreach (var revision in new ulong[] { 7, 8 })
            Assert.Same(run, ColorRunOperations.ApplyInteractionReceipt(run, new(new(revision), "old-board", false, false)));
        var changed = ColorRunOperations.ApplyInteractionReceipt(run, new(new(9), "new-board", false, false));
        Assert.Null(changed.CurrentVerified);
        Assert.Null(changed.Result!.CurrentVerified); Assert.Equal("unverified", changed.Result.Outcome);
        Assert.Same(measured, changed.BestMeasured); Assert.Same(measured, changed.LastMeasured);
        Assert.Equal(new UInt64String(9), changed.Interaction!.Revision);
    }

    [Fact]
    public void ProgressKeepsOriginalStageCountsAndConvertsRemainingTimeToHostClock()
    {
        using var area = new Area();
        var run = new ColorRun("run", "operation", area.Bindings.Current!.Session, Profile, "capture", 400_000, null, null, null, null, null);
        var at = DateTimeOffset.UnixEpoch;
        JsonElement Data(object value) => JsonSerializer.SerializeToElement(value);
        run = ColorRunOperations.ProjectProgress(run, "screenshot_progress", Data(new { stage = "capture", current = 2, total = 14, seconds = 90 }), 50, 400_000, 1000, at);
        Assert.Equal(new ColorRunProgress("capture", 2, 14, 1000, at, 91_000, 1000, at), run.Progress);
        // 同阶段无计数的事件保留步骤；effective_deadline 优先于 seconds，并按同帧剩余量换算到宿主时钟。
        run = ColorRunOperations.ProjectProgress(run, "screenshot_progress", Data(new { stage = "capture", effective_deadline = 80, seconds = 1 }), 50, 400_000, 2000, at.AddSeconds(1));
        Assert.Equal(new ColorRunProgress("capture", 2, 14, 1000, at, 32_000, 2000, at.AddSeconds(1)), run.Progress);
        run = ColorRunOperations.ProjectProgress(run, "native_progress", Data(new { stage = "search" }), 50, 20_000, 3000, at.AddSeconds(2));
        Assert.Equal(("search", (int?)null, (int?)null, 3000d), (run.Progress!.Stage, run.Progress.Current, run.Progress.Total, run.Progress.StageStartedMonotonicMs));
        run = ColorRunOperations.ProjectProgress(run, "native_timer", Data(new { remaining_seconds = 400, captured_monotonic = 40 }), 50, 20_000, 4000, at);
        Assert.Equal(20_000, run.Progress!.DeadlineMonotonicMs);
        Assert.Equal("worker.resultInvalid", Assert.Throws<WorkerException>(() => ColorRunOperations.ProjectProgress(run, "native_progress",
            Data(new { stage = "capture", current = 5, total = 4 }), 50, 20_000, 5000, at)).Code);
        var layout = ColorRunOperations.WithRegionCount(run, 2);
        Assert.Equal(2, layout.RegionCount);
        Assert.Equal(new Dictionary<string, bool> { ["region-0"] = true, ["region-1"] = true, ["region-2"] = false }, layout.AvailableByRegion);
        Assert.Equal("worker.resultInvalid", Assert.Throws<WorkerException>(() => ColorRunOperations.WithRegionCount(run, 4)).Code);
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task HttpLifecycleUsesRealWorkerAndRejectsStaleCommandsAndReleasesResources()
    {
        using var area = new Area();
        var binding = area.Bindings.Current!.Session;
        await using var app = area.Build();
        await app.StartAsync();
        using var http = Client(app);
        var request = new ColorRunStartRequest(binding.Id, binding.BindingRevision, Profile);
        Assert.True((await Post<ColorPreview>(http, "/api/v1/color/previews", new ColorPreviewRequest(Profile, ["#202020", "#202020"]))).Accepted);
        var pool = app.Services.GetRequiredService<ColorWorkerPool>();
        var previewProcessId = pool.ProcessId;
        var operation = await Start(http, request, "color-first");
        var runId = operation.Result!.Value.GetProperty("id").GetString()!;
        var run = await WaitRun(http, runId, value => value.Interaction?.RetryEnabled == true);
        Assert.Equal(previewProcessId, pool.ProcessId);
        await Error(http, "/api/v1/color/previews", new ColorPreviewRequest(Profile, ["#202020", "#202020"]), null, 409, "resource.busy");
        Assert.Equal("region-0", run.Candidates!.Candidates[0].HexByRegion.Keys.First());
        Assert.Equal(2, run.Candidates.Candidates[0].HexByRegion.Count);
        Assert.Equal(binding, run.BindingSnapshot);
        Assert.Equal(operation.Id, (await Start(http, request, "color-first")).Id);
        await Error(http, "/api/v1/color/runs", request with { Profile = Profile with { Name = "修改后" } }, "color-first", 409, "idempotency.conflict");
        await Error(http, "/api/v1/color/runs", request, "another-start", 409, "resource.busy");

        var revision = run.Interaction!.Revision;
        await Error(http, $"/api/v1/color/runs/{runId}/selections",
            new ColorCandidateSelectionRequest("expired", "choice", revision), null, 409, "color.candidateUnavailable");
        var selected = await Post<ColorInteractionReceipt>(http, $"/api/v1/color/runs/{runId}/selections",
            new ColorCandidateSelectionRequest("board-0", "choice", revision));
        Assert.True(selected.Accepted);
        await Error(http, $"/api/v1/color/runs/{runId}/retries", new ColorRetryRequest(revision), null, 409, "color.interactionConflict");
        run = await WaitRun(http, runId, value => value.Interaction is { RetryEnabled: true, SelectionEnabled: true } state &&
            state.Revision.Value >= selected.Interaction.Revision.Value + 3);
        var deadline = run.DeadlineMonotonicMs;
        await Post<ColorInteractionReceipt>(http, $"/api/v1/color/runs/{runId}/retries", new ColorRetryRequest(run.Interaction!.Revision));
        run = await WaitRun(http, runId, value => value.Candidates?.Id == "board-1" && value.Interaction?.RetryEnabled == true);
        Assert.True(run.DeadlineMonotonicMs <= deadline);
        await Error(http, $"/api/v1/color/runs/{runId}/selections",
            new ColorCandidateSelectionRequest("board-0", "choice", run.Interaction!.Revision), null, 409, "color.candidateUnavailable");
        await Post<OperationSnapshot>(http, $"/api/v1/operations/{operation.Id}/cancel", new CancelOperationRequest("用户停止"));
        var terminal = await WaitOperation(http, operation.Id);
        Assert.Equal(OperationState.Cancelled, terminal.State.KnownValue);
        run = (await http.GetFromJsonAsync<ColorRun>($"/api/v1/color/runs/{runId}", ContractJson.Options))!;
        Assert.Null(run.CurrentVerified); Assert.Null(run.Candidates);
        Assert.False(run.Interaction!.SelectionEnabled); Assert.False(run.Interaction.RetryEnabled);
        await Error(http, $"/api/v1/color/runs/{runId}/retries", new ColorRetryRequest(run.Interaction.Revision), null, 409, "color.runInactive");
        var leases = app.Services.GetRequiredService<ResourceLeaseCoordinator>();
        var next = leases.Acquire(new("music", "next", binding.Id, binding.BindingRevision,
            ["foreground-input", "activity-control:" + binding.Id], TimeSpan.FromSeconds(5)));
        Assert.True(leases.Release(next.Id));
        Assert.Equal(0, area.Device.Actions);
        Assert.Equal(new UInt64String(1), pool.Snapshot().StartedWorkers);
        await app.StopAsync();
    }

    [Fact]
    public async Task ResultQueryAndEventSnapshotAreOwnedAndPersistAfterHostRestart()
    {
        using var area = new Area();
        string runId, operationId, principal, historyAssetId;
        await using (var app = area.Build("complete"))
        {
            await app.StartAsync(); using var owner = Client(app);
            var challenge = await Post<PairingChallenge>(owner, "/api/v1/auth/pairing-challenges",
                new PairingChallengeRequest("染色前端", null, [ApiPermissions.SystemRead, ApiPermissions.ColorControl]));
            var grant = await Post<AccessGrant>(owner, "/api/v1/auth/pairings", new PairingRequest(challenge.ChallengeId, challenge.Code));
            principal = grant.Session.ClientId;
            using var controller = Client(app, grant.Token);
            var binding = area.Bindings.Current!.Session;
            var operation = await Start(controller, new(binding.Id, binding.BindingRevision, Profile), "owned-run");
            operationId = operation.Id;
            runId = operation.Result!.Value.GetProperty("id").GetString()!;
            var run = await WaitRun(controller, runId, value => value.Interaction?.SelectionEnabled == true);
            await Error(controller, $"/api/v1/color/history/{runId}/assets", new { }, null, 409, "resource.busy");
            await Post<ColorLibraryChanged>(owner, "/api/v1/color/library/edits",
                new ColorLibraryEdit([Profile with { Name = "新编辑方案", Regions = Profile.Regions.Select(r => r with { TargetHex = "#FFFFFF" }).ToArray() }], [], null),
                "library-while-running", "\"color-library-0\"");
            var frozen = (await controller.GetFromJsonAsync<ColorRun>($"/api/v1/color/runs/{runId}", ContractJson.Options))!;
            Assert.Equal(Profile.Name, frozen.RulesSnapshot.Name);
            Assert.All(frozen.RulesSnapshot.Regions, r => Assert.Equal("#202020", r.TargetHex));
            var current = await WaitRun(controller, runId, value => value.Interaction?.RetryEnabled == true);
            await Post<ColorInteractionReceipt>(controller, $"/api/v1/color/runs/{runId}/selections",
                new ColorCandidateSelectionRequest("board-0", "choice", current.Interaction!.Revision));
            var terminal = await WaitOperation(controller, operation.Id);
            Assert.Equal(OperationState.Succeeded, terminal.State.KnownValue);
            run = (await controller.GetFromJsonAsync<ColorRun>($"/api/v1/color/runs/{runId}", ContractJson.Options))!;
            Assert.Equal("matched", run.Result!.Outcome);
            Assert.Equal("offline-color-test", run.CurrentVerified!.Source.Provider);
            Assert.Equal(2, run.CurrentVerified.HexByRegion.Count);
            Assert.Equal(principal, run.Owner);
            var history = await Post<ApiList<ColorHistoryItem>>(controller, "/api/v1/color/history/query", new ColorHistoryQuery(1));
            Assert.Equal(runId, Assert.Single(history.Items).Id); Assert.Null(history.NextCursor);
            Assert.Empty((await Post<ApiList<ColorHistoryItem>>(controller, "/api/v1/color/history/query", new ColorHistoryQuery(1, operation.Id))).Items);
            var asset = await Post<AssetSnapshot>(controller, $"/api/v1/color/history/{runId}/assets", new { });
            historyAssetId = asset.Id;
            Assert.Equal(asset, await Post<AssetSnapshot>(owner, $"/api/v1/color/history/{runId}/assets", new { }));
            var content = await Post<ColorAssetContent>(controller, $"/api/v1/color/assets/{asset.Id}/content", new ColorAssetRangeRequest(0, 262144));
            var exported = JsonSerializer.Deserialize<ColorRun>(Convert.FromBase64String(content.Base64), ContractJson.Options)!;
            Assert.Equal(run.Id, exported.Id); Assert.Equal(run.BestMeasured!.ObservedAt, exported.BestMeasured!.ObservedAt);
            await Error(controller, $"/api/v1/color/assets/{asset.Id}/content", new ColorAssetRangeRequest(-1, 1), null, 400, "request.invalid");

            var readerChallenge = await Post<PairingChallenge>(owner, "/api/v1/auth/pairing-challenges", new PairingChallengeRequest("只读前端", null, [ApiPermissions.SystemRead]));
            var readerGrant = await Post<AccessGrant>(owner, "/api/v1/auth/pairings", new PairingRequest(readerChallenge.ChallengeId, readerChallenge.Code));
            using var reader = Client(app, readerGrant.Token);
            Assert.Empty((await Post<ApiList<ColorHistoryItem>>(reader, "/api/v1/color/history/query", new ColorHistoryQuery())).Items);
            Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/v1/color/history/{runId}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/v1/color/assets/{asset.Id}")).StatusCode);
            await Error(reader, $"/api/v1/color/assets/{asset.Id}/content", new ColorAssetRangeRequest(0, 1), null, 404, "resource.notFound");
            Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/v1/color/runs/{runId}")).StatusCode);
            await Error(reader, "/api/v1/color/runs", new ColorRunStartRequest(binding.Id, binding.BindingRevision, Profile), "denied-run", 403, "permission.denied");
            ProjectionSnapshot snapshot;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            do
            {
                snapshot = await Snapshot(controller, timeout.Token);
                if (snapshot.Resources.Count == 0 || snapshot.Resources[0].Data.GetProperty("phase").GetString() != "succeeded") await Task.Delay(20, timeout.Token);
                else break;
            } while (true);
            Assert.Single(snapshot.Resources);
            Assert.Empty((await Snapshot(reader)).Resources);
            Assert.Equal(runId, (await Snapshot(owner)).Resources.Single().ResourceId);
            await app.StopAsync();
        }
        await using (var reopened = area.Build())
        {
            await reopened.StartAsync(); using var http = Client(reopened);
            var run = (await http.GetFromJsonAsync<ColorRun>($"/api/v1/color/runs/{runId}", ContractJson.Options))!;
            Assert.Equal("matched", run.Result!.Outcome);
            Assert.Equal(operationId, run.OperationId);
            Assert.Equal(historyAssetId, (await Post<AssetSnapshot>(http, $"/api/v1/color/history/{runId}/assets", new { })).Id);
            Assert.Equal(runId, Assert.Single((await Post<ApiList<ColorHistoryItem>>(http, "/api/v1/color/history/query", new ColorHistoryQuery())).Items).Id);
            await reopened.StopAsync();
        }
    }

    [Fact]
    public async Task CrashRecoveryClearsInteractionAndCurrentEvidenceWithoutStartingWorker()
    {
        using var area = new Area();
        string operationId, runId;
        using (var lease = DataRootLease.Open(area.Storage))
        await using (var store = SqliteStore.Open(lease))
        {
            var operations = new OperationStore(store);
            var binding = area.Bindings.Current!.Session;
            var admission = await operations.AdmitColorAsync("client", new(binding.Id, binding.BindingRevision, Profile),
                "crash-start", () => binding, "old-epoch", 99999, true);
            operationId = admission.Snapshot.Id; runId = admission.Snapshot.Result!.Value.GetProperty("id").GetString()!;
            await operations.BeginColorAsync(operationId);
            await operations.UpdateColorAsync(operationId, run => run with { Interaction = new(new(10), "old", true, true),
                CurrentVerified = new(new Dictionary<string, string> { ["region-0"] = "#202020" }, DateTimeOffset.UtcNow,
                    new("offline-test", "1", "TW", "test"), new(1), ObservationQuality.Confirmed) });
        }
        await using var app = area.Build(); await app.StartAsync(); using var http = Client(app);
        var recovered = (await http.GetFromJsonAsync<ColorRun>($"/api/v1/color/runs/{runId}", ContractJson.Options))!;
        Assert.Equal("interrupted", recovered.Phase); Assert.Null(recovered.CurrentVerified);
        Assert.False(recovered.Interaction!.RetryEnabled); Assert.False(recovered.Interaction.SelectionEnabled);
        Assert.Equal(OperationState.Interrupted, (await http.GetFromJsonAsync<OperationSnapshot>($"/api/v1/operations/{operationId}", ContractJson.Options))!.State.KnownValue);
        await app.StopAsync();
    }

    [Fact]
    public async Task HistoryKeepsLatestThirtyTerminalRunsAndPrunesInterruptedRunsOnRecovery()
    {
        using var area = new Area();
        // 运行完成时间早于此刻，启动恢复按真实时间写入的中断运行是最新的一条。
        var clock = new InteractionClock();
        clock.MoveWall(TimeSpan.FromHours(-1));
        var runs = new List<string>();
        string active;
        using (var lease = DataRootLease.Open(area.Storage))
        await using (var store = SqliteStore.Open(lease))
        {
            var operations = new OperationStore(store, clock);
            var binding = area.Bindings.Current!.Session;
            async Task<string> Admit(int index)
            {
                clock.MoveWall(TimeSpan.FromSeconds(1));
                var admission = await operations.AdmitColorAsync("client", new(binding.Id, binding.BindingRevision, Profile),
                    $"history-run-{index:D2}", () => binding, "epoch", 99999, true);
                runs.Add(admission.Snapshot.Result!.Value.GetProperty("id").GetString()!);
                await operations.BeginColorAsync(admission.Snapshot.Id);
                return admission.Snapshot.Id;
            }
            for (var index = 0; index < OperationStore.ColorHistoryRetention + 2; index++)
                Assert.True(await operations.UpdateColorAsync(await Admit(index), run => run, OperationState.Succeeded));
            var history = operations.ColorHistory("client", false, new(50));
            Assert.Equal(runs.AsEnumerable().Reverse().Take(OperationStore.ColorHistoryRetention), history.Items.Select(item => item.Id));
            Assert.Equal("resource.notFound", Assert.Throws<OperationException>(() => operations.ColorOperationId(runs[1])).Code);
            // 进行中的运行不计入保留条数，也不会被裁掉。
            active = await Admit(99);
            Assert.Equal(OperationStore.ColorHistoryRetention, operations.ColorHistory("client", false, new(50)).Items.Count);
        }
        using (var lease = DataRootLease.Open(area.Storage))
        await using (var store = SqliteStore.Open(lease))
        {
            Assert.Equal(1, await store.RecoverOperationsAsync());
            var operations = new OperationStore(store, clock);
            var history = operations.ColorHistory("client", false, new(50));
            Assert.Equal(OperationStore.ColorHistoryRetention, history.Items.Count);
            Assert.Equal(active, history.Items[0].OperationId);
            Assert.Equal(OperationState.Interrupted, history.Items[0].State.KnownValue);
            Assert.DoesNotContain(runs[2], history.Items.Select(item => item.Id));
        }
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task OriginalScreenshotControllerPreservesMeasuredHistoryWhenCancelledThroughHost()
    {
        using var area = new Area();
        var binding = area.Bindings.Current!.Session;
        await using var app = area.Build("2", "screenshot_peer");
        await app.StartAsync(); using var http = Client(app);
        var operation = await Start(http, new(binding.Id, binding.BindingRevision, Profile), "real-controller");
        var runId = operation.Result!.Value.GetProperty("id").GetString()!;
        var run = await WaitRun(http, runId, value => value.Phase == "waiting-user" && value.BestMeasured is not null);
        Assert.NotNull(run.LastMeasured); Assert.Null(run.CurrentVerified);
        Assert.Equal(2, run.BestMeasured!.HexByRegion.Count);
        await Post<OperationSnapshot>(http, $"/api/v1/operations/{operation.Id}/cancel", new CancelOperationRequest("stop"));
        var terminal = await WaitOperation(http, operation.Id);
        Assert.Equal(OperationState.Cancelled, terminal.State.KnownValue);
        run = (await http.GetFromJsonAsync<ColorRun>($"/api/v1/color/runs/{runId}", ContractJson.Options))!;
        Assert.NotNull(run.BestMeasured); Assert.NotNull(run.LastMeasured); Assert.Null(run.CurrentVerified);
        Assert.Equal(EffectStatus.None, terminal.EffectStatus.KnownValue);
        Assert.Equal(0, area.Device.Actions);
        await app.StopAsync();
    }

    [Fact]
    public async Task WorkerCrashReleasesLeaseAndDoesNotReplayTheRun()
    {
        using var area = new Area(); var binding = area.Bindings.Current!.Session;
        await using var app = area.Build("crash");
        await app.StartAsync(); using var http = Client(app);
        var operation = await Start(http, new(binding.Id, binding.BindingRevision, Profile), "crash-worker");
        var runId = operation.Result!.Value.GetProperty("id").GetString()!;
        var run = await WaitRun(http, runId, value => value.Interaction?.RetryEnabled == true);
        using (var response = await http.PostAsJsonAsync($"/api/v1/color/runs/{runId}/retries", new ColorRetryRequest(run.Interaction!.Revision), ContractJson.Options))
        {
            // 命令确认可能先于故障送达；运行终态独立表达后续中断。
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.InternalServerError });
            if (response.StatusCode == HttpStatusCode.OK)
                Assert.True((await response.Content.ReadFromJsonAsync<ColorInteractionReceipt>(ContractJson.Options))!.Accepted);
            else Assert.Equal("operation.executionFailed", (await response.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code);
        }
        var terminal = await WaitOperation(http, operation.Id);
        Assert.Equal(OperationState.Interrupted, terminal.State.KnownValue);
        run = (await http.GetFromJsonAsync<ColorRun>($"/api/v1/color/runs/{runId}", ContractJson.Options))!;
        Assert.Null(run.Candidates); Assert.Null(run.CurrentVerified); Assert.False(run.Interaction!.RetryEnabled);
        Assert.Equal(operation.Id, (await Start(http, new(binding.Id, binding.BindingRevision, Profile), "crash-worker")).Id);
        var leases = app.Services.GetRequiredService<ResourceLeaseCoordinator>();
        var next = leases.Acquire(new("music", "after-crash", binding.Id, binding.BindingRevision,
            ["foreground-input", "activity-control:" + binding.Id], TimeSpan.FromSeconds(5)));
        Assert.True(leases.Release(next.Id));
        Assert.Equal(0, area.Device.Actions);
        await app.StopAsync();
    }

    [Fact]
    public async Task InvalidRulesMissingRuntimeAndShutdownHaveExplicitResults()
    {
        using var area = new Area(); var binding = area.Bindings.Current!.Session;
        var request = new ColorRunStartRequest(binding.Id, binding.BindingRevision, Profile);
        await using (var unconfigured = BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: area.Storage))
        {
            await unconfigured.StartAsync(); using var http = Client(unconfigured);
            await Error(http, "/api/v1/color/runs", request, "no-runtime", 422, "capability.unavailable");
            await Error(http, "/api/v1/color/runs", request with { Profile = Profile with { Regions = [] } }, "bad-rules", 400, "request.invalid");
            await unconfigured.StopAsync();
        }
        await using var app = area.Build(); await app.StartAsync(); using var controller = Client(app);
        await Error(controller, "/api/v1/color/runs", request with { ExpectedBindingRevision = new(100) }, "old-binding", 409, "input.bindingChanged");
        var operation = await Start(controller, request, "shutdown-run");
        var runId = operation.Result!.Value.GetProperty("id").GetString()!;
        await WaitRun(controller, runId, value => value.Interaction?.RetryEnabled == true);
        await app.StopAsync();
        var terminal = app.Services.GetRequiredService<OperationStore>().Get(operation.Id, "owner", true);
        Assert.Equal(OperationState.Cancelled, terminal.State.KnownValue);
        Assert.Equal(0, area.Device.Actions);
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task ExplicitPresenceChecksPermissionsAndOwnershipAndAllowsOwnerRecovery()
    {
        using var area = new Area(); var clock = new InteractionClock();
        await using var app = area.Build(clock: clock); await app.StartAsync(); using var owner = Client(app);
        var grant = await Pair(owner, ApiPermissions.SystemRead, ApiPermissions.ColorControl);
        using var controller = Client(app, grant.Token);
        var binding = area.Bindings.Current!.Session;
        var operation = await Start(controller, new(binding.Id, binding.BindingRevision, Profile), "presence-owner");
        var id = operation.Result!.Value.GetProperty("id").GetString()!;
        await WaitRun(controller, id, value => value.Interaction?.RetryEnabled == true);
        var presence = await Post<ColorInteractionPresence>(controller, $"/api/v1/color/runs/{id}/presence", new { });
        Assert.Equal(id, presence.RunId); Assert.Equal(5000, presence.ValidForMs);
        Assert.Equal((await owner.GetFromJsonAsync<SystemInfo>("/api/v1/system/info", ContractJson.Options))!.InstanceId, presence.HostClockEpoch);
        var readerGrant = await Pair(owner, ApiPermissions.SystemRead);
        using var reader = Client(app, readerGrant.Token);
        await Error(reader, $"/api/v1/color/runs/{id}/presence", new { }, null, 403, "permission.denied");
        var otherGrant = await Pair(owner, ApiPermissions.SystemRead, ApiPermissions.ColorControl);
        using var other = Client(app, otherGrant.Token);
        await Error(other, $"/api/v1/color/runs/{id}/presence", new { }, null, 404, "resource.notFound");

        clock.Advance(TimeSpan.FromSeconds(4));
        await Post<ColorInteractionPresence>(owner, $"/api/v1/color/runs/{id}/presence", new { });
        app.Services.GetRequiredService<LocalAuthService>().Revoke(grant.Session.ClientId);
        clock.Advance(TimeSpan.FromSeconds(2));
        await Post<ColorInteractionPresence>(owner, $"/api/v1/color/runs/{id}/presence", new { });
        var run = (await owner.GetFromJsonAsync<ColorRun>($"/api/v1/color/runs/{id}", ContractJson.Options))!;
        Assert.Equal("waiting-user", run.Phase);
        clock.MoveWall(TimeSpan.FromDays(-30));
        await Post<ColorInteractionPresence>(owner, $"/api/v1/color/runs/{id}/presence", new { });
        clock.Advance(TimeSpan.FromSeconds(6));
        await Error(owner, $"/api/v1/color/runs/{id}/presence", new { }, null, 409, "color.runInactive");
        Assert.Equal(OperationState.Cancelled, (await WaitOperation(owner, operation.Id)).State.KnownValue);
        Assert.Equal("frontend-lost", (await owner.GetFromJsonAsync<ColorRun>($"/api/v1/color/runs/{id}", ContractJson.Options))!.StopReason);
        await app.StopAsync();
    }

    [Fact]
    public async Task QueriesAndEventSnapshotsDoNotRenewPresenceAndRevocationCancelsTheLastClient()
    {
        using var area = new Area(); var clock = new InteractionClock();
        await using var app = area.Build(clock: clock); await app.StartAsync(); using var owner = Client(app);
        var binding = area.Bindings.Current!.Session;
        var first = await Start(owner, new(binding.Id, binding.BindingRevision, Profile), "readonly-presence");
        var id = first.Result!.Value.GetProperty("id").GetString()!;
        await WaitRun(owner, id, value => value.Interaction?.RetryEnabled == true);
        clock.Advance(TimeSpan.FromSeconds(4));
        await owner.GetFromJsonAsync<ColorRun>($"/api/v1/color/runs/{id}", ContractJson.Options);
        await Snapshot(owner);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(OperationState.Cancelled, (await WaitOperation(owner, first.Id)).State.KnownValue);

        var grant = await Pair(owner, ApiPermissions.SystemRead, ApiPermissions.ColorControl);
        using var controller = Client(app, grant.Token);
        var second = await Start(controller, new(binding.Id, binding.BindingRevision, Profile), "revoke-presence");
        var secondId = second.Result!.Value.GetProperty("id").GetString()!;
        await WaitRun(controller, secondId, value => value.Interaction?.RetryEnabled == true);
        app.Services.GetRequiredService<LocalAuthService>().Revoke(grant.Session.ClientId);
        Assert.Equal(OperationState.Cancelled, (await WaitOperation(owner, second.Id)).State.KnownValue);
        Assert.Equal("frontend-lost", (await owner.GetFromJsonAsync<ColorRun>($"/api/v1/color/runs/{secondId}", ContractJson.Options))!.StopReason);
        await app.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresenceMonitorAndEmergencyShortcutReleaseInputWhileDatabaseWritesAreBlocked(bool emergency)
    {
        using var area = new Area(); var clock = new InteractionClock(); var shortcut = new SignalShortcuts();
        await using var app = area.Build(clock: clock, shortcuts: shortcut); await app.StartAsync(); using var owner = Client(app);
        Assert.Equal("F9", (await owner.GetFromJsonAsync<ApiList<GlobalShortcutSnapshot>>("/api/v1/system/shortcuts", ContractJson.Options))!.Items.Single().Registered!.Key);
        var binding = area.Bindings.Current!.Session;
        var operation = await Start(owner, new(binding.Id, binding.BindingRevision, Profile), "blocked-presence");
        var id = operation.Result!.Value.GetProperty("id").GetString()!;
        await WaitRun(owner, id, value => value.Interaction?.RetryEnabled == true);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var blocked = app.Services.GetRequiredService<SqliteStore>().WriteAsync<int>(_ =>
        { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("injected-write-block"); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            if (emergency) await Task.Run(() => shortcut.Signal("emergency-stop")).WaitAsync(TimeSpan.FromSeconds(2));
            else clock.Advance(TimeSpan.FromSeconds(6));
            await WaitReleased(app.Services.GetRequiredService<ResourceLeaseCoordinator>(), binding);
            Assert.False(blocked.IsCompleted);
            await Error(owner, $"/api/v1/color/runs/{id}/presence", new { }, null, 409, "color.runInactive");
        }
        finally { release.Set(); await Assert.ThrowsAsync<InvalidOperationException>(() => blocked); }
        Assert.Equal(OperationState.Cancelled, (await WaitOperation(owner, operation.Id)).State.KnownValue);
        Assert.Equal(emergency ? "emergency-stop" : "frontend-lost",
            (await owner.GetFromJsonAsync<ColorRun>($"/api/v1/color/runs/{id}", ContractJson.Options))!.StopReason);
        await app.StopAsync(); Assert.True(shortcut.Stopped);
    }

    private static async Task<AccessGrant> Pair(HttpClient owner, params string[] permissions)
    {
        var challenge = await Post<PairingChallenge>(owner, "/api/v1/auth/pairing-challenges", new PairingChallengeRequest("测试交互客户端", null, permissions));
        return await Post<AccessGrant>(owner, "/api/v1/auth/pairings", new PairingRequest(challenge.ChallengeId, challenge.Code));
    }
    private static async Task WaitReleased(ResourceLeaseCoordinator leases, GameSession binding)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (true)
        {
            try
            {
                var next = leases.Acquire(new("music", "after-stop", binding.Id, binding.BindingRevision,
                    ["foreground-input", "activity-control:" + binding.Id], TimeSpan.FromSeconds(5)));
                Assert.True(leases.Release(next.Id)); return;
            }
            catch (ResourceLeaseException error) when (error.Code == "resource.busy") { await Task.Delay(10, timeout.Token); }
        }
    }
    private sealed class InteractionClock : TimeProvider
    {
        private long offsetMs;
        private long wallTicks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => (long)(Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency) + Interlocked.Read(ref offsetMs);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref wallTicks));
        public void Advance(TimeSpan duration) => Interlocked.Add(ref offsetMs, (long)duration.TotalMilliseconds);
        public void MoveWall(TimeSpan duration) => Interlocked.Add(ref wallTicks, duration.Ticks);
    }
    private sealed class SignalShortcuts : IGlobalShortcutSource
    {
        public event Action<string>? ActionReceived;
        public bool Stopped;
        public void Signal(string action) => ActionReceived?.Invoke(action);
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token) { Stopped = true; return Task.CompletedTask; }
        public IReadOnlyList<ShortcutRegistrationResult> GetRegistrations() => [new("emergency-stop", new(ShortcutKey.F9), new(ShortcutKey.F9), ShortcutRegistrationStatus.Registered, null, false)];
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static HttpClient Client(WebApplication app, string token = Token)
    {
        var http = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return http;
    }
    private static Task<OperationSnapshot> Start(HttpClient http, ColorRunStartRequest request, string key) => Post<OperationSnapshot>(http, "/api/v1/color/runs", request, key);
    private static async Task<ProjectionSnapshot> Snapshot(HttpClient http, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/event-snapshot");
        request.Headers.Add("X-Event-Topics", "color.run.changed");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectionSnapshot>(ContractJson.Options, ct))!;
    }
    private static async Task<T> Post<T>(HttpClient http, string path, object request, string? key = null, string? ifMatch = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(request, options: ContractJson.Options) };
        if (key is not null) message.Headers.Add("Idempotency-Key", key);
        if (ifMatch is not null) message.Headers.Add("If-Match", ifMatch);
        using var response = await http.SendAsync(message);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options))!;
    }
    private static async Task Error(HttpClient http, string path, object request, string? key, int status, string code)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(request, options: ContractJson.Options) };
        if (key is not null) message.Headers.Add("Idempotency-Key", key);
        using var response = await http.SendAsync(message);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Code);
    }
    private static async Task<ColorRun> WaitRun(HttpClient http, string id, Func<ColorRun, bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var nextPresence = Stopwatch.GetTimestamp();
        while (true)
        {
            if (Stopwatch.GetTimestamp() >= nextPresence)
            {
                await Post<ColorInteractionPresence>(http, $"/api/v1/color/runs/{id}/presence", new { });
                nextPresence = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            }
            var value = (await http.GetFromJsonAsync<ColorRun>($"/api/v1/color/runs/{id}", ContractJson.Options, timeout.Token))!;
            if (ready(value)) return value;
            Assert.DoesNotContain(value.Phase, new[] { "failed", "interrupted" });
            await Task.Delay(20, timeout.Token);
        }
    }
    private static async Task<OperationSnapshot> WaitOperation(HttpClient http, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var value = (await http.GetFromJsonAsync<OperationSnapshot>($"/api/v1/operations/{id}", ContractJson.Options, timeout.Token))!;
            if (SqliteStore.IsTerminal(value.State.KnownValue)) return value;
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class Area : IDisposable
    {
        public string Root { get; }
        private readonly string repository;
        private readonly string python;
        public StorageOptions Storage => new(Path.Combine(Root, "bootstrap"), true, DataRoot: Path.Combine(Root, "data"));
        public GameSessionBindingTracker Bindings { get; } = new();
        public OfflineDevice Device { get; } = new();
        public Area()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MabiTool.slnx"))) dir = dir.Parent;
            repository = dir!.FullName;
            Root = Path.Combine(repository, "artifacts", "tmp", "color-host-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            var start = new ProcessStartInfo("python") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("import sys; print(sys.executable)");
            using var process = Process.Start(start)!; python = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            Bindings.Update(new("TW", 100, DateTimeOffset.UtcNow, new(500), "offline", new(0, 0, 200, 120), 96, 96, false));
        }
        public WebApplication Build(string? mode = null, string entry = "interaction_peer", TimeProvider? clock = null, IGlobalShortcutSource? shortcuts = null) => BackendApplication.Build(new(0, Token, new HashSet<string>()), clock: clock, storage: Storage,
            shortcutSource: shortcuts,
            colorFactory: services =>
            {
                var leases = services.GetRequiredService<ResourceLeaseCoordinator>();
                var options = new ColorWorkerOptions(python, Path.Combine(repository, "workers", "color-engine", "tests"), Root)
                { EntryModule = entry, Arguments = mode is null ? [] : [mode],
                    HeartbeatInterval = TimeSpan.FromMilliseconds(100), HeartbeatTimeout = TimeSpan.FromSeconds(3) };
                return new(options, Bindings, leases, new(leases, Bindings, Device), Device, providerId: "offline-color-test", workerPool: new(options, clock));
            });
        public void Dispose()
        {
            var allowed = Path.GetFullPath(Path.Combine(repository, "artifacts", "tmp")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(Root, true);
        }
    }

    private sealed class OfflineDevice : IGameInputDevice, IGameCaptureSource
    {
        public int Actions { get; private set; }
        public UInt64String? ForegroundWindowIdentity => new(500);
        public void Press(InputControl control) => Actions++;
        public void Release(InputControl control) => Actions++;
        public void MovePointer(PhysicalScreenPoint point) => Actions++;
        public void ReleaseAll() => Actions++;
        public ValueTask<GameCapture> CaptureAsync(GameWindowBinding binding, CancellationToken token) =>
            ValueTask.FromResult(new GameCapture(200, 120, Enumerable.Repeat((byte)255, 200 * 120 * 3).ToArray()));
    }
}

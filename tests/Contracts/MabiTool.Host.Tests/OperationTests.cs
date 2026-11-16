using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class OperationTests
{
    private const string Token = "operations_only_0123456789012345678901234567890123456789";
    private const string Origin = "http://localhost:5173";
    private static string ETag(string id = "color", ulong revision = 0) => OperationStore.ConfigurationETag(new(id, true, new(revision)));

    [Fact]
    public async Task HttpAdmissionIsDurableConditionalAndIdempotentAndSdkReadsHeaders()
    {
        using var workspace = new Workspace();
        string operationId;
        await using (var app = BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: workspace.Options))
        {
            await app.StartAsync();
            using var sdk = Sdk(app);
            using var client = Http(app);
            ApiResponseMetadata? metadata = null;
            var initial = await sdk.GetModuleConfigurationAsync("color", onResponse: value => metadata = value);
            Assert.True(initial.Enabled);
            Assert.Equal(ETag(), metadata!.ETag);
            Assert.Equal("idempotency.required", (await Problem(client, "{\"enabled\":false}", null, ETag())).Code);
            Assert.Equal(428, (await Problem(client, "{\"enabled\":false}", "missing-condition", null)).Status);
            Assert.Equal(400, (await Problem(client, "{\"enabled\":false,\"ignored\":true}", "unknown-fields", ETag())).Status);
            var accepted = await sdk.ConfigureModuleAsync("color", new(false), "same-request", ETag(), onResponse: value => metadata = value);
            operationId = accepted.Id;
            Assert.Equal(202, metadata!.Status);
            Assert.Equal("/api/v1/operations/" + operationId, metadata.Location);
            var requests = Enumerable.Range(0, 20).Select(_ => sdk.ConfigureModuleAsync("color", new(false), "same-request", ETag()));
            Assert.All(await Task.WhenAll(requests), value => Assert.Equal(operationId, value.Id));
            // 属性空白与转义写法不改变类型化请求的含义。
            using var semanticRetry = await Post(client, "{ \"enabl\\u0065d\" : false }", "same-request", ETag());
            Assert.Equal(HttpStatusCode.Accepted, semanticRetry.StatusCode);
            Assert.Equal(operationId, (await semanticRetry.Content.ReadFromJsonAsync<OperationSnapshot>(ContractJson.Options))!.Id);
            Assert.Equal(409, (await Problem(client, "{\"enabled\":true}", "same-request", ETag())).Status);
            var terminal = await Terminal(sdk, operationId);
            Assert.Equal(OperationState.Succeeded, terminal.State.KnownValue);
            Assert.Equal(EffectStatus.None, terminal.EffectStatus.KnownValue);
            var saved = await sdk.GetModuleConfigurationAsync("color", onResponse: value => metadata = value);
            Assert.False(saved.Enabled);
            Assert.Equal(1UL, saved.Revision.Value);
            Assert.Equal(ETag(revision: 1), metadata!.ETag);
            Assert.Equal(412, (await Problem(client, "{\"enabled\":true}", "stale-condition", ETag())).Status);
            Assert.Equal(409, (await Problem(client, "{\"enabled\":false}", "same-request", ETag(revision: 1))).Status);
            var cancelled = await sdk.CancelOperationAsync(operationId, new("late"));
            Assert.Equal(Encoded(terminal), Encoded(cancelled));
            var modules = await sdk.GetModulesAsync();
            Assert.False(modules.Items.Single(m => m.Id == "color").Enabled);
            Assert.Equal(1L, app.Services.GetRequiredService<SqliteStore>().ReadScalar("SELECT count(*) FROM core_idempotency"));
            await app.StopAsync();
        }
        await using (var restarted = BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: workspace.Options))
        {
            await restarted.StartAsync();
            using var sdk = Sdk(restarted);
            Assert.False((await sdk.GetModulesAsync()).Items.Single(m => m.Id == "color").Enabled);
            Assert.Equal(operationId, (await sdk.ConfigureModuleAsync("color", new(false), "same-request", ETag())).Id);
            Assert.Equal(OperationState.Succeeded, (await sdk.GetOperationAsync(operationId)).State.KnownValue);
            await restarted.StopAsync();
        }
    }

    [Fact]
    public async Task EssentialModulesRefuseToBeDisabled()
    {
        using var workspace = new Workspace();
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: workspace.Options);
        await app.StartAsync();
        using var sdk = Sdk(app);
        // 核心模块的停用请求以稳定错误码拒绝，不受理、不排队，模块保持启用。
        var refused = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.ConfigureModuleAsync("game-access", new(false), "essential-key", ETag()));
        Assert.Equal(409, refused.Status);
        Assert.Equal("module.essential", refused.Problem!.Code);
        Assert.True((await sdk.GetModulesAsync()).Items.Single(module => module.Id == "game-access").Enabled);
        await app.StopAsync();
    }

    [Fact]
    public async Task OperationOwnershipPermissionsAndBrowserConditionalHeadersAreEnforced()
    {
        using var workspace = new Workspace();
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>([Origin])), storage: workspace.Options);
        await app.StartAsync();
        using var owner = Sdk(app);
        async Task<AccessGrant> Pair(string name, bool write)
        {
            var challenge = await owner.CreatePairingChallengeAsync(new(name, null, write ? [ApiPermissions.SystemRead, ApiPermissions.ModulesConfigure] : [ApiPermissions.SystemRead]));
            return await owner.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code));
        }
        var a = await Pair("a", true); var b = await Pair("b", true); var reader = await Pair("reader", false);
        using var first = Sdk(app, a.Token); using var second = Sdk(app, b.Token); using var readOnly = Sdk(app, reader.Token);
        var firstOperation = await first.ConfigureModuleAsync("color", new(false), "shared-key", ETag());
        await Terminal(first, firstOperation.Id);
        Assert.Equal(404, (await Assert.ThrowsAsync<MabiToolApiException>(() => second.GetOperationAsync(firstOperation.Id))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<MabiToolApiException>(() => second.CancelOperationAsync(firstOperation.Id, new(null)))).Status);
        Assert.Equal(firstOperation.Id, (await owner.GetOperationAsync(firstOperation.Id)).Id);
        Assert.Equal(403, (await Assert.ThrowsAsync<MabiToolApiException>(() => readOnly.ConfigureModuleAsync("color", new(true), "reader-key", ETag(revision: 1)))).Status);
        var secondOperation = await second.ConfigureModuleAsync("color", new(true), "shared-key", ETag(revision: 1));
        Assert.NotEqual(firstOperation.Id, secondOperation.Id);
        await Terminal(second, secondOperation.Id);
        var browserChallenge = await owner.CreatePairingChallengeAsync(new("browser", Origin, [ApiPermissions.SystemRead, ApiPermissions.ModulesConfigure]));
        var auth = app.Services.GetRequiredService<LocalAuthService>();
        var browser = auth.Redeem(new(browserChallenge.ChallengeId, browserChallenge.Code), Origin);
        using var http = Http(app, browser.Token);
        http.DefaultRequestHeaders.Add("Origin", Origin);
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/modules/color/configuration");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,idempotency-key,if-match");
        using var allowed = await http.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        using var response = await Post(http, "{\"enabled\":false}", "browser-key", ETag(revision: 2));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("ETag, Location, Content-Length, Content-Disposition, X-Content-Sha256",
            response.Headers.GetValues("Access-Control-Expose-Headers").Single());
        await app.StopAsync();
    }

    [Fact]
    public async Task QueuedCancellationAndDeferredCancellationAreDurableAndIdempotent()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease);
        var operations = new OperationStore(store);
        var queued = await operations.AdmitConfigurationAsync("a", "color", true, new(false), "queued-key", ETag());
        Assert.Equal("resource.busy", (await Assert.ThrowsAsync<OperationException>(() => operations.AdmitConfigurationAsync("b", "color", true, new(false), "other-key", ETag()))).Code);
        var cancelled = await operations.CancelAsync(queued.Snapshot.Id, "a", false, "user-stop");
        Assert.Equal(OperationState.Cancelled, cancelled.State.KnownValue);
        Assert.Null(await operations.BeginConfigurationAsync(cancelled.Id));
        Assert.Equal(Encoded(cancelled), Encoded(await operations.CancelAsync(cancelled.Id, "a", false, "second")));
        Assert.True(operations.Configuration("color", true).Enabled);
        var running = await operations.AdmitConfigurationAsync("a", "color", true, new(false), "running-key", ETag());
        Assert.NotNull(await operations.BeginConfigurationAsync(running.Snapshot.Id));
        Assert.Equal("resource.busy", (await Assert.ThrowsAsync<OperationException>(() => operations.BeginConfigurationAsync(running.Snapshot.Id))).Code);
        var deferred = await operations.CancelAsync(running.Snapshot.Id, "a", false, "during-application");
        Assert.Equal(OperationState.CancelRequested, deferred.State.KnownValue);
        Assert.Equal(CancellationState.Deferred, deferred.Cancellation.State.KnownValue);
        var finished = await operations.FinishConfigurationAsync(running.Snapshot.Id, false);
        Assert.Equal(OperationState.Succeeded, finished.State.KnownValue);
        Assert.Equal(CancellationState.Completed, finished.Cancellation.State.KnownValue);
        Assert.False(operations.Configuration("color", true).Enabled);
        Assert.Equal(1UL, operations.Configuration("color", true).Revision.Value);
        Assert.Equal(Encoded(finished), Encoded(await operations.CancelAsync(finished.Id, "owner", true, null)));
        var events = store.ReadPendingEvents(100);
        Assert.Single(events, x => x.Event.EventType == "module.configuration-changed");
    }

    [Fact]
    public async Task CancellationAndExecutionClaimAreSerializedUnderConcurrentRequests()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease);
        var operations = new OperationStore(store);
        for (var i = 0; i < 32; i++)
        {
            var id = "fixture-" + i;
            var admitted = await operations.AdmitConfigurationAsync("a", id, true, new(false), "race-key", ETag(id));
            var begin = Task.Run(() => operations.BeginConfigurationAsync(admitted.Snapshot.Id));
            var cancel = Task.Run(() => operations.CancelAsync(admitted.Snapshot.Id, "a", false, "race"));
            await Task.WhenAll((Task)begin, cancel);
            if (await begin is null)
            {
                Assert.Equal(OperationState.Cancelled, (await cancel).State.KnownValue);
                Assert.Equal(0UL, operations.Configuration(id, true).Revision.Value);
            }
            else
            {
                Assert.Equal(CancellationState.Deferred, (await cancel).Cancellation.State.KnownValue);
                await operations.FinishConfigurationAsync(admitted.Snapshot.Id, false);
                Assert.Equal(1UL, operations.Configuration(id, true).Revision.Value);
            }
        }
    }

    [Fact]
    public async Task FailedApplicationPreservesConfigurationAndReleasesResource()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease);
        var operations = new OperationStore(store);
        var admitted = await operations.AdmitConfigurationAsync("a", "color", true, new(false), "failure-key", ETag());
        await operations.BeginConfigurationAsync(admitted.Snapshot.Id);
        var failed = await operations.FinishConfigurationAsync(admitted.Snapshot.Id, false, ApiErrors.Create("module.inUse", "模块使用中", "fixture"));
        Assert.Equal(OperationState.Failed, failed.State.KnownValue);
        Assert.Equal("module.inUse", failed.Problem!.Code);
        Assert.True(operations.Configuration("color", true).Enabled);
        Assert.Equal(0UL, operations.Configuration("color", true).Revision.Value);
        Assert.True((await operations.AdmitConfigurationAsync("a", "color", true, new(false), "retry-new-key", ETag())).Created);
    }

    [Fact]
    public async Task ExecutorContinuesAfterCallerCancellationAndAdmissionRejectsActiveDemand()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease);
        var operations = new OperationStore(store);
        var catalog = new ModuleCatalog(new(1, 0), [new(new("fixture", new(1, 0), new(1, 0), new(2, 0), []), _ => ValueTask.FromResult<IAsyncDisposable>(new EmptyModule()))]);
        await using var runtime = new ModuleRuntime(catalog);
        using var executor = new ModuleConfigurationOperations(operations, runtime, catalog);
        await executor.StartAsync(default);
        var demand = await runtime.AcquireAsync("fixture", "active-demand");
        var busy = await Assert.ThrowsAsync<OperationException>(() => executor.SubmitAsync("a", "fixture", new(false), "active-demand-key", ETag("fixture")));
        Assert.Equal("module.inUse", busy.Code);
        Assert.Equal("active-demand", busy.Details!["operationIds"][0].GetString());
        Assert.Equal(0L, store.ReadScalar("SELECT count(*) FROM core_operations"));
        await demand.DisposeAsync();
        using var caller = new CancellationTokenSource();
        var submitted = await executor.SubmitAsync("a", "fixture", new(false), "active-demand-key", ETag("fixture"), caller.Token);
        caller.Cancel();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        OperationSnapshot current;
        do { current = operations.Get(submitted.Id, "a", false); if (SqliteStore.IsTerminal(current.State.KnownValue)) break; await Task.Delay(10); } while (DateTime.UtcNow < deadline);
        Assert.Equal(OperationState.Succeeded, current.State.KnownValue);
        Assert.False(operations.Configuration("fixture", true).Enabled);
        await executor.StopAsync(default);
    }

    [Fact]
    public async Task ActualExecutorSkipsCancelledQueueEntriesAndDefersCancellationDuringApplication()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease); var operations = new OperationStore(store);
        var blocking = new BlockingModule();
        var catalog = new ModuleCatalog(new(1, 0), [
            new(new("first", new(1, 0), new(1, 0), new(2, 0), []), _ => ValueTask.FromResult<IAsyncDisposable>(blocking)),
            new(new("second", new(1, 0), new(1, 0), new(2, 0), []), _ => ValueTask.FromResult<IAsyncDisposable>(new EmptyModule()))]);
        await using var runtime = new ModuleRuntime(catalog);
        await (await runtime.AcquireAsync("first", "initialize")).DisposeAsync();
        using var executor = new ModuleConfigurationOperations(operations, runtime, catalog, store.Activities);
        await executor.StartAsync(default);
        var first = await executor.SubmitAsync("a", "first", new(false), "executor-first", ETag("first"));
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(store.Activities.Snapshot().Activities, x => x.Kind == "module.configuration");
        Assert.Null(store.Activities.TryEnterMaintenance("storage-maintenance"));
        var second = await executor.SubmitAsync("a", "second", new(false), "executor-second", ETag("second"));
        Assert.Equal(CancellationState.Deferred, (await operations.CancelAsync(first.Id, "a", false, "stop-first")).Cancellation.State.KnownValue);
        Assert.Equal(OperationState.Cancelled, (await operations.CancelAsync(second.Id, "a", false, "stop-second")).State.KnownValue);
        try
        {
            Assert.True(operations.Configuration("second", true).Enabled);
        }
        finally { blocking.Release.TrySetResult(); }
        await executor.StopAsync(default);
        Assert.Equal(OperationState.Succeeded, operations.Get(first.Id, "a", false).State.KnownValue);
        Assert.Equal(CancellationState.Completed, operations.Get(first.Id, "a", false).Cancellation.State.KnownValue);
        Assert.False(operations.Configuration("first", true).Enabled);
        Assert.Equal(OperationState.Cancelled, operations.Get(second.Id, "a", false).State.KnownValue);
        Assert.Empty(store.Activities.Snapshot().Activities);
    }

    [Fact]
    public async Task OutboxFailureRollsBackAdmissionIdentityAndResourceReservation()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease); var operations = new OperationStore(store);
        await store.WriteAsync(tx => tx.Execute("""
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<10000)
            INSERT INTO core_outbox(event_id,resource_type,resource_id,revision,event_type,payload,created_at)
            SELECT 'full-'||x,'fixture','fixture','1','fixture.changed','{}','2026-11-08T00:00:00+00:00' FROM n;
            """));
        Assert.Equal("storage.outboxFull", (await Assert.ThrowsAsync<StorageException>(() => operations.AdmitConfigurationAsync("a", "color", true, new(false), "rollback-key", ETag()))).Code);
        Assert.Equal(0L, store.ReadScalar("SELECT count(*) FROM core_operations"));
        Assert.Equal(0L, store.ReadScalar("SELECT count(*) FROM core_idempotency"));
        Assert.Equal(0L, store.ReadScalar("SELECT count(*) FROM core_module_configuration"));
    }

    [Fact]
    public async Task QueuedConfigurationKeepsReceivedTimeAndStartsKeyRetentionAtAdmission()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease);
        var clock = new ManualClock(); var operations = new OperationStore(store, clock);
        var warmup = await operations.AdmitConfigurationAsync("a", "prewarm", true, new(false), "clock-warmup", ETag("prewarm"));
        await operations.CancelAsync(warmup.Snapshot.Id, "a", false, null);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var blocker = store.WriteAsync(_ => { entered.Set(); release.Wait(); return true; });
        var received = clock.Now;
        Task<OperationAdmission>? queued = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            queued = operations.AdmitConfigurationAsync("a", "color", true, new(false), "queued-clock", ETag());
            Assert.False(queued.IsCompleted);
            Assert.Equal(0L, store.ReadScalar("SELECT count(*) FROM core_idempotency WHERE key='queued-clock'"));
            clock.Now += TimeSpan.FromHours(1);
        }
        finally { release.Set(); }
        await blocker;
        var accepted = await queued!;
        Assert.Equal(received, accepted.Snapshot.Timestamps.CreatedAt);
        Assert.Equal((clock.Now + OperationStore.KeyRetention).ToUnixTimeMilliseconds() + 1,
            store.ReadScalar("SELECT expires_at FROM core_idempotency WHERE key='queued-clock'"));
        var retry = await operations.AdmitConfigurationAsync("a", "color", true, new(false), "queued-clock", ETag(), queueAvailable: false);
        Assert.False(retry.Created);
        Assert.Equal(accepted.Snapshot.Id, retry.Snapshot.Id);
        Assert.Equal(received, retry.Snapshot.Timestamps.CreatedAt);
        Assert.Equal(2L, store.ReadScalar("SELECT count(*) FROM core_operations"));
    }

    [Fact]
    public async Task IdempotencyCapacityRetainsKeysForAtLeast24HoursAndExpiredKeysCanBeReused()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease);
        var clock = new ManualClock(); var operations = new OperationStore(store, clock);
        var original = await operations.AdmitConfigurationAsync("a", "color", true, new(false), "retained-key", ETag());
        await operations.CancelAsync(original.Snapshot.Id, "a", false, null);
        await store.WriteAsync(tx => tx.Execute("""
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<9999)
            INSERT INTO core_idempotency SELECT 'fixture','fixture','fixture',CAST(x AS TEXT),'hash',$op,$expiry FROM n;
            """, ("$op", original.Snapshot.Id), ("$expiry", (clock.GetUtcNow() + TimeSpan.FromDays(2)).ToUnixTimeMilliseconds())));
        clock.Now += TimeSpan.FromHours(24) - TimeSpan.FromMilliseconds(1);
        Assert.Equal(original.Snapshot.Id, (await operations.AdmitConfigurationAsync("a", "color", true, new(false), "retained-key", ETag(), false)).Snapshot.Id);
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(() => operations.AdmitConfigurationAsync("a", "color", true, new(false), "new-key-1", ETag()))).Code);
        Assert.Equal(1L, store.ReadScalar("SELECT count(*) FROM core_operations"));
        clock.Now += TimeSpan.FromMilliseconds(3);
        var next = await operations.AdmitConfigurationAsync("a", "color", true, new(true), "retained-key", ETag());
        Assert.True(next.Created); Assert.NotEqual(original.Snapshot.Id, next.Snapshot.Id);
        Assert.Equal(10000L, store.ReadScalar("SELECT count(*) FROM core_idempotency"));
    }

    [Fact]
    public async Task ActiveCapacityAndQueuePressureRejectWithoutCommittingAndDoNotHideRetries()
    {
        using var workspace = new Workspace(); using var lease = workspace.Lease();
        await using var store = SqliteStore.Open(lease); var operations = new OperationStore(store);
        var first = await operations.AdmitConfigurationAsync("a", "fixture-0", true, new(false), "capacity-key", ETag("fixture-0"));
        // 单事务构造其余活动记录，集中验证容量边界，避免逐项 fsync 放大测试 I/O。
        await store.WriteAsync(tx =>
        {
            for (var i = 1; i < OperationStore.MaximumActiveOperations; i++)
            {
                var snapshot = first.Snapshot with { Id = "op-capacity-" + i, ResourceRefs = [new("module", "fixture-" + i)] };
                tx.Execute("INSERT INTO core_operations VALUES($id,$snapshot,0)", ("$id", snapshot.Id), ("$snapshot", Encoded(snapshot)));
            }
            return true;
        });
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(() => operations.AdmitConfigurationAsync("a", "extra", true, new(false), "capacity-key", ETag("extra")))).Code);
        Assert.False((await operations.AdmitConfigurationAsync("a", "fixture-0", true, new(false), "capacity-key", ETag("fixture-0"), false)).Created);
        Assert.Equal(128L, store.ReadScalar("SELECT count(*) FROM core_operations"));
        Assert.Equal(0L, store.ReadScalar("SELECT count(*) FROM core_module_configuration WHERE id='extra'"));
        await operations.CancelAsync(first.Snapshot.Id, "a", false, null);
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(() => operations.AdmitConfigurationAsync("a", "extra", true, new(false), "capacity-key", ETag("extra"), false))).Code);
        Assert.True((await operations.AdmitConfigurationAsync("a", "extra", true, new(false), "capacity-key", ETag("extra"))).Created);
    }

    [Theory]
    [InlineData("operation-queued")]
    [InlineData("operation-applying")]
    public async Task ForcedTerminationRetainsAdmissionAndRestartNeverExecutesIt(string scenario)
    {
        using var workspace = new Workspace();
        var ready = Path.Combine(workspace.Directory, "ready");
        var assembly = Path.Combine(workspace.Repository, "artifacts/build/bin/MabiTool.PersistenceProbe", new DirectoryInfo(AppContext.BaseDirectory).Name, "MabiTool.PersistenceProbe.dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { assembly, scenario, workspace.Options.BootstrapDirectory, workspace.Options.DataRoot!, ready }) start.ArgumentList.Add(argument);
        string operationId;
        using (var process = Process.Start(start)!)
        {
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!File.Exists(ready) && !process.HasExited && DateTime.UtcNow < deadline) await Task.Delay(20);
                if (!File.Exists(ready))
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    Assert.Fail("持久化探针未就绪：" + await process.StandardError.ReadToEndAsync());
                }
                operationId = File.ReadAllText(ready);
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: workspace.Options);
        await app.StartAsync(); using var sdk = Sdk(app);
        var recovered = await sdk.GetOperationAsync(operationId);
        Assert.Equal(OperationState.Interrupted, recovered.State.KnownValue);
        Assert.Equal(EffectStatus.Unknown, recovered.EffectStatus.KnownValue);
        Assert.Equal("backend-restarted", recovered.Phase);
        Assert.Equal(operationId, (await sdk.ConfigureModuleAsync("color", new(false), "crash-operation-key", ETag())).Id);
        Assert.True((await sdk.GetModuleConfigurationAsync("color")).Enabled);
        Assert.Equal(0UL, (await sdk.GetModuleConfigurationAsync("color")).Revision.Value);
        var next = await sdk.ConfigureModuleAsync("color", new(false), "explicit-new-key", ETag());
        Assert.NotEqual(operationId, next.Id);
        Assert.Equal(OperationState.Succeeded, (await Terminal(sdk, next.Id)).State.KnownValue);
        await app.StopAsync();
    }

    private static string Encoded(OperationSnapshot value) => JsonSerializer.Serialize(value, ContractJson.Options);
    private static Uri Address(Microsoft.AspNetCore.Builder.WebApplication app) => new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    private static MabiToolClient Sdk(Microsoft.AspNetCore.Builder.WebApplication app, string token = Token) => new(Address(app), _ => ValueTask.FromResult(token));
    private static HttpClient Http(Microsoft.AspNetCore.Builder.WebApplication app, string token = Token)
    {
        var client = new HttpClient { BaseAddress = Address(app) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }
    private static async Task<HttpResponseMessage> Post(HttpClient client, string json, string? key, string? etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/modules/color/configuration") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }
    private static async Task<ApiProblem> Problem(HttpClient client, string json, string? key, string? etag)
    {
        using var response = await Post(client, json, key, etag);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return (await response.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!;
    }
    private static async Task<OperationSnapshot> Terminal(MabiToolClient sdk, string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var current = await sdk.GetOperationAsync(id);
            if (SqliteStore.IsTerminal(current.State.KnownValue)) return current;
            await Task.Delay(10);
        }
        throw new TimeoutException("操作未在 10 秒内完成");
    }
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class EmptyModule : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class BlockingModule : IAsyncDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask DisposeAsync() { Entered.TrySetResult(); await Release.Task; }
    }
    private sealed class Workspace : IDisposable
    {
        public string Repository { get; }
        public string Directory { get; }
        public StorageOptions Options => new(Path.Combine(Directory, "bootstrap"), true, Path.Combine(Directory, "data"));
        public Workspace()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "MabiTool.slnx"))) current = current.Parent;
            Repository = current!.FullName;
            Directory = Path.Combine(Repository, "artifacts/tmp/operation-tests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
        }
        public DataRootLease Lease() => DataRootLease.Open(Options);
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}

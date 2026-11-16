using System.IO.Compression;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Diagnostics;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>诊断第二批（记录补全与直接查询）与第三批（限时调试采集）。</summary>
[Collection("Persistent backend")]
public sealed class DiagnosticCaptureApiTests
{
    private const string Token = "diagnostic_capture_tests_0123456789012345678901234567890123";

    [Fact]
    public void RecordFieldsAreWhitelistedAndLegacyRecordsStayValid()
    {
        var at = DateTimeOffset.UtcNow;
        var record = DiagnosticSanitizer.Create(at, "debug", "combat-observation", component: "observation.capture",
            @event: "observation.frame.rejected", fields: new Dictionary<string, string>
            { ["branch"] = "damage.time", ["bad key"] = "x", ["count"] = "3", ["path"] = @"C:\Users\someone" },
            sequence: new(7), detail: "free text");
        Assert.Equal("debug", record.Level); Assert.Equal("observation.frame.rejected", record.Event);
        Assert.Equal(new Dictionary<string, string> { ["branch"] = "damage.time" }, record.Fields);
        Assert.Null(record.Detail); Assert.Equal(7UL, record.Sequence!.Value.Value);
        Assert.True(DiagnosticSanitizer.IsValid(record));
        var tampered = record with { Fields = new Dictionary<string, string> { ["branch"] = "Alice Smith" } };
        Assert.False(DiagnosticSanitizer.IsValid(tampered));
        Assert.False(DiagnosticSanitizer.IsValid(record with { Detail = "text" }));

        var unknown = DiagnosticSanitizer.Create(at, "info", "core", component: "Bad Component", @event: "made.up",
            fields: new Dictionary<string, string> { ["state"] = "bound" });
        Assert.Null(unknown.Event); Assert.Null(unknown.Fields); Assert.Null(unknown.Component);
        var tooMany = new Dictionary<string, string>();
        for (var i = 0; i < 20; i++) tooMany["k" + i] = "1";
        Assert.Null(DiagnosticSanitizer.Create(at, "info", "core", @event: "game.binding.changed", fields: tooMany).Fields);

        // 旧记录与 v1 导出不含新字段，仍可读取与复验。
        var legacy = JsonSerializer.Deserialize<DiagnosticRecord>("""
            {"occurredAt":"2026-11-01T00:00:00+00:00","level":"warning","module":"rooms","code":"resource.busy","traceId":null,"operationId":null,"providerVersion":null,"exceptionType":null}
            """, ContractJson.Options)!;
        Assert.True(DiagnosticSanitizer.IsValid(legacy)); Assert.Null(legacy.Sequence);
        var v1 = JsonSerializer.Deserialize<DiagnosticExportDocument>("""
            {"schemaVersion":1,"createdAt":"2026-11-01T00:00:00+00:00","sessions":[]}
            """, ContractJson.Options)!;
        Assert.Equal(1, v1.SchemaVersion); Assert.Null(v1.Captures); Assert.Null(v1.ContainsDetail);

        Assert.Equal("color", DiagnosticRecorder.ModuleOf("MabiTool.Host.Color.NativeReader"));
        Assert.Equal("rooms", DiagnosticRecorder.ModuleOf("MabiTool.Host.Rooms.RoomSessionManager"));
        Assert.Equal("combat-observation", DiagnosticRecorder.ModuleOf("MabiTool.Host.Combat.CombatOperations"));
        Assert.Equal("inventory", DiagnosticRecorder.ModuleOf("MabiTool.Host.Inventory.InventoryOperations"));
        Assert.Equal("game-access", DiagnosticRecorder.ModuleOf("MabiTool.Host.Game.GameObservationService"));
        Assert.Equal("music-library", DiagnosticRecorder.ModuleOf("MabiTool.Host.Performance.PerformanceSessionService"));
        Assert.Equal("core", DiagnosticRecorder.ModuleOf("MabiTool.Host.Storage.StorageRetentionLifetime"));
        Assert.Equal("core", DiagnosticRecorder.ModuleOf("MabiTool.Host.Colorful"));
    }

    [Fact]
    public void DetailSanitizerReplacesRootsAndRemovesSecretsAndAddresses()
    {
        var sanitizer = new DiagnosticDetailSanitizer(@"C:\Users\alice\Moongate\data", @"C:\Program Files\Moongate", @"C:\Users\alice");
        var token = new string('A', 20) + new string('7', 20);
        var text = sanitizer.Clean($"""
            open C:\Users\alice\Moongate\data\store\main.sqlite failed
            at C:\Program Files\Moongate\MabiTool.Host.dll and c:/users/alice/Documents/x.json
            source D:\build\agent\src\Host\GameObservationService.cs:line 42 share \\server\share\dir\file.txt
            token {token} Authorization: Bearer abc.def-ghi peer 192.168.1.10:8080 v6 fe80:0:0:0:1:2:3:4 at 12:34:56
            """ + "\u0001");
        Assert.Contains(@"<data>\store\main.sqlite", text); Assert.Contains(@"<install>\MabiTool.Host.dll", text);
        Assert.Contains("<user>/Documents/x.json", text); Assert.Contains(@"<path>\GameObservationService.cs:line 42", text);
        Assert.Contains(@"<path>\file.txt", text);
        Assert.DoesNotContain("alice", text); Assert.DoesNotContain(@"D:\build", text); Assert.DoesNotContain("server", text);
        Assert.DoesNotContain(token, text); Assert.DoesNotContain("abc.def", text); Assert.Contains("Bearer <redacted>", text);
        Assert.DoesNotContain("192.168.1.10", text); Assert.DoesNotContain("fe80", text); Assert.Contains("12:34:56", text);
        Assert.False(text.Contains('\u0001'));
        Assert.Equal(text, DiagnosticDetailSanitizer.StripSecrets(text));
        // 附加文字（Worker 输出尾部）超长时保留末尾，组合结果受单条上限约束。
        var composed = sanitizer.Compose(new InvalidOperationException("boom"), string.Concat(Enumerable.Repeat("line ", 4000)) + "LAST");
        Assert.NotNull(composed); Assert.True(composed!.Length <= DiagnosticSanitizer.MaximumDetailChars);
        Assert.StartsWith("System.InvalidOperationException: boom", composed); Assert.EndsWith("LAST", composed);
    }

    [Fact]
    public async Task ProblemRecordsKeepTraceIdsAndOnlyTheRouteTemplate()
    {
        var at = DateTimeOffset.UtcNow;
        // 三种合法关联 ID：W3C trace-id、Kestrel TraceIdentifier（请求序号是大写十六进制）与 Operation ID；其余形态丢弃。
        foreach (var id in new[] { "4bf92f3577b34da6a3ce929d0e0e4736", "0HNQ1JQLS16H4:00000963", "0HNQ1JQLS16H4:0000000A", "op-0123456789abcdef0123456789abcdef" })
            Assert.Equal(id, DiagnosticSanitizer.Create(at, "warning", "core", traceId: id).TraceId);
        foreach (var id in new[] { "0HNQ1JQLS16H4:0000000a", "0hnq1jqls16h4:00000963", "Alice:00000001", "op-123" })
            Assert.Null(DiagnosticSanitizer.Create(at, "warning", "core", traceId: id).TraceId);
        // 路由只保留模板：实际 ID、查询串与非固定方法被丢弃。
        var kept = DiagnosticSanitizer.Create(at, "warning", "core", httpMethod: "GET", route: "/api/v1/operations/{id}");
        Assert.Equal(("GET", "/api/v1/operations/{id}"), (kept.HttpMethod, kept.Route)); Assert.True(DiagnosticSanitizer.IsValid(kept));
        foreach (var route in new[] { "/api/v1/operations/op-0123456789abcdef0123456789abcdef", "/api/v1/combat/entities/9007199254740995/effects",
            "/api/v1/modules?id=x", "api/v1/system/info", @"/api/v1/files/C:\Users" })
            Assert.Null(DiagnosticSanitizer.Create(at, "warning", "core", route: route).Route);
        Assert.Null(DiagnosticSanitizer.Create(at, "warning", "core", httpMethod: "get").HttpMethod);
        Assert.False(DiagnosticSanitizer.IsValid(kept with { Route = "/api/v1/operations/op-0123456789abcdef0123456789abcdef" }));

        using var area = new Workspace(); await using var host = await Host.Start(area); using var sdk = host.Sdk();
        var missing = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.GetOperationAsync("op-0123456789abcdef0123456789abcdef"));
        Assert.Equal(404, missing.Status);
        await host.App.Services.GetRequiredService<DiagnosticRecorder>().FlushAsync();
        var record = Assert.Single((await sdk.QueryDiagnosticRecordsAsync(new(TraceId: missing.Problem!.TraceId))).Items).Record;
        Assert.Equal(("resource.notFound", "GET", "/api/v1/operations/{id}"), (record.Code, record.HttpMethod, record.Route));
        Assert.DoesNotContain("0123456789abcdef", JsonSerializer.Serialize(record, ContractJson.Options));
    }

    [Fact]
    public async Task LifecycleEventsAndQueryFilterPageAndIsolatePrincipals()
    {
        using var area = new Workspace(); await using var host = await Host.Start(area); using var sdk = host.Sdk();
        var controls = host.App.Services.GetRequiredService<GameControlContext>();
        controls.Bindings.Update(new("TW", 4242, DateTimeOffset.UtcNow, new(500), "tw-build", new(0, 0, 800, 600), 96, 96, false));
        controls.Bindings.Update(new("TW", 4242, DateTimeOffset.UtcNow.AddDays(-1), new(500), "tw-build", new(0, 0, 800, 600), 96, 96, true));
        var recorder = host.App.Services.GetRequiredService<DiagnosticRecorder>();
        var logger = recorder.CreateLogger("MabiTool.Host.Combat.CombatOperations");
        for (var i = 0; i < 3; i++)
            logger.Log(LogLevel.Warning, default, new KeyValuePair<string, object?>[] { new("code", "combat.observationGap") }, null, (_, _) => "unused");
        await recorder.FlushAsync();

        var binding = await sdk.QueryDiagnosticRecordsAsync(new(Events: ["game.binding.changed"]));
        Assert.Equal(2, binding.Items.Count); Assert.Null(binding.NextCursor);
        var first = binding.Items[0].Record;
        Assert.Equal(("info", "game-access", "game.binding"), (first.Level, first.Module, first.Component));
        Assert.Equal("bound", first.Fields!["state"]); Assert.Equal("true", first.Fields["sessionChanged"]); Assert.Equal("false", first.Fields["minimized"]);
        Assert.Equal("true", binding.Items[1].Record.Fields!["minimized"]);
        Assert.True(binding.Items[1].Record.Sequence!.Value.Value > first.Sequence!.Value.Value);
        Assert.DoesNotContain("4242", JsonSerializer.Serialize(binding, ContractJson.Options));

        var page = await sdk.QueryDiagnosticRecordsAsync(new(Modules: ["combat-observation"], Levels: ["warning"], Limit: 2));
        Assert.Equal(2, page.Items.Count); Assert.NotNull(page.NextCursor);
        Assert.All(page.Items, item => Assert.Equal(("combat-observation", "combat.observationGap"), (item.Record.Module, item.Record.Code)));
        var rest = await sdk.QueryDiagnosticRecordsAsync(new(Modules: ["combat-observation"], Levels: ["warning"], Limit: 2, Cursor: page.NextCursor));
        Assert.Single(rest.Items); Assert.Null(rest.NextCursor);
        var newest = await sdk.QueryDiagnosticRecordsAsync(new(Modules: ["combat-observation"], Levels: ["warning"], Limit: 2, Order: "desc"));
        Assert.Equal(2, newest.Items.Count); Assert.StartsWith("d-", newest.NextCursor);
        Assert.Equal(rest.Items[0].Record.Sequence, newest.Items[0].Record.Sequence);
        Assert.True(newest.Items[0].Record.Sequence!.Value.Value > newest.Items[1].Record.Sequence!.Value.Value);
        var oldest = await sdk.QueryDiagnosticRecordsAsync(new(Modules: ["combat-observation"], Levels: ["warning"], Limit: 2, Order: "desc", Cursor: newest.NextCursor));
        Assert.Single(oldest.Items); Assert.Null(oldest.NextCursor);
        Assert.Equal(page.Items[0].Record.Sequence, oldest.Items[0].Record.Sequence);
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.QueryDiagnosticRecordsAsync(new(Modules: ["combat-observation"], Limit: 2, Order: "desc", Cursor: page.NextCursor)))).Problem!.Code);
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.QueryDiagnosticRecordsAsync(new(Order: "newest")))).Problem!.Code);
        Assert.Empty((await sdk.QueryDiagnosticRecordsAsync(new(Modules: ["combat-observation"], To: DateTimeOffset.UtcNow.AddDays(-1)))).Items);
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.QueryDiagnosticRecordsAsync(new(Limit: DiagnosticStore.MaximumQueryRecords + 1)))).Problem!.Code);
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.QueryDiagnosticRecordsAsync(new(Events: ["not.registered"])))).Problem!.Code);

        // 配对主体只读取本主体会话，看不到 Owner 会话。
        var grant = await host.Pair("diagnostic-reader", [ApiPermissions.SystemRead]);
        using var reader = host.Sdk(grant.Token);
        Assert.Empty((await reader.QueryDiagnosticRecordsAsync(new())).Items);
        Assert.Equal("resource.notFound", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            reader.QueryDiagnosticRecordsAsync(new(SessionId: binding.Items[0].SessionId)))).Problem!.Code);
        Assert.Equal("permission.denied", (await Assert.ThrowsAsync<MabiToolApiException>(() => reader.GetDiagnosticCaptureAsync())).Problem!.Code);
        // diagnostics.debug 只授予 Owner，配对客户端不可申请。
        Assert.Throws<LocalApiException>(() => host.App.Services.GetRequiredService<LocalAuthService>()
            .CreateChallenge(new("debug-client", null, [ApiPermissions.DiagnosticsDebug])));
    }

    [Fact]
    public async Task CaptureGatesLevelsPushesWithLimitStopsAndExportsV2WithCleanDetail()
    {
        using var area = new Workspace(); await using var host = await Host.Start(area); using var sdk = host.Sdk();
        var trace = host.App.Services.GetRequiredService<DiagnosticTrace>();
        var hub = host.App.Services.GetRequiredService<StateEventHub>();
        var paths = host.App.Services.GetRequiredService<PathService>();
        var token = new string('k', 40);
        var failure = new InvalidOperationException($"boom {paths.DataRoot}\\store\\x.json token {token} peer 10.1.2.3:443");
        void Rejected() => trace.Emit(DiagnosticEvents.FrameRejected, new DiagnosticFields().Value("branch", "damage.time"), exception: failure);

        // 未开启采集：调试事件只做门限判断，不进入任何记录。
        Assert.False(trace.IsEnabled(DiagnosticEvents.FrameRejected)); Rejected();
        await host.App.Services.GetRequiredService<DiagnosticRecorder>().FlushAsync();
        Assert.Empty((await sdk.QueryDiagnosticRecordsAsync(new(Levels: ["debug", "trace"]))).Items);
        Assert.Null((await sdk.GetDiagnosticCaptureAsync()).Capture);

        var owner = host.App.Services.GetRequiredService<LocalAuthService>().OwnerGrant().Session;
        await using var debugStream = await hub.SubscribeAsync(owner, EventFilter.Create(["diagnostics.record"]));
        await using var plainStream = await hub.SubscribeAsync(new("client-plain", "plain", [ApiPermissions.SystemRead, ApiPermissions.DiagnosticsExport], null, null, false),
            EventFilter.Create(["diagnostics.record"]));
        var request = new DiagnosticCaptureRequest(["game-access", "combat-observation"], "debug", 600, IncludeDetail: true);
        var capture = await sdk.StartDiagnosticCaptureAsync(request, "capture-start-1");
        Assert.Equal(("active", "debug", true), (capture.State, capture.Level, capture.IncludeDetail));
        Assert.Equal(new[] { "combat-observation", "game-access" }, capture.Modules);
        Assert.Equal(capture.Id, (await sdk.StartDiagnosticCaptureAsync(request, "capture-start-1")).Id);
        Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.StartDiagnosticCaptureAsync(request with { DurationSeconds = 5 }, "capture-start-1"))).Problem!.Code);
        var conflict = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.StartDiagnosticCaptureAsync(request, "capture-start-2"));
        Assert.Equal((409, "diagnostics.captureActive"), (conflict.Status, conflict.Problem!.Code));
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.StartDiagnosticCaptureAsync(request with { DurationSeconds = 601 }, "capture-start-3"))).Problem!.Code);

        Assert.True(trace.IsEnabled(DiagnosticEvents.FrameRejected));
        Assert.False(trace.IsEnabled(DiagnosticEvents.InventoryFrame)); Assert.False(trace.IsEnabled(DiagnosticEvents.ChannelRequest, "game-access"));
        for (var i = 0; i < 120; i++) Rejected();
        trace.Emit(DiagnosticEvents.InventoryFrame, new DiagnosticFields().Value("kind", "full"));
        trace.Emit(DiagnosticEvents.ChannelRequest, new DiagnosticFields().Value("method", "observation.capture"), "game-access");
        await Wait(async () => (await sdk.GetDiagnosticCaptureAsync()).Capture!.Records.Value >= 120);

        var pushed = new List<EventDelivery>();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            try { while (true) { while (debugStream.TryRead(out var item)) pushed.Add(item!); await debugStream.WaitAsync(timeout.Token); } }
            catch (OperationCanceledException) { }
        Assert.InRange(pushed.Count, 1, DiagnosticCaptureService.PushPerSecond * 3);
        Assert.False(plainStream.TryRead(out _));
        var pushedRecord = pushed[0].Data.Deserialize<DiagnosticRecord>(ContractJson.Options)!;
        Assert.Equal("diagnostics.record", pushed[0].Type); Assert.Equal(capture.Id, pushed[0].ResourceId);
        Assert.Equal("observation.frame.rejected", pushedRecord.Event); Assert.Contains("<data>", pushedRecord.Detail);

        var stopped = await sdk.StopDiagnosticCaptureAsync(capture.Id);
        Assert.Equal(("ended", "stopped"), (stopped.State, stopped.EndReason));
        // 120 条在一个秒窗口内到达：至多 50 条实时推送，其余计入推送丢弃。
        Assert.True(stopped.PushDropped.Value >= 1); Assert.Equal(120UL, stopped.Records.Value); Assert.True(stopped.DetailBytes.Value > 0);
        Assert.False(trace.IsEnabled(DiagnosticEvents.FrameRejected));
        Assert.Equal(stopped.EndedAt, (await sdk.StopDiagnosticCaptureAsync(capture.Id)).EndedAt);
        Assert.Equal(capture.Id, (await sdk.GetDiagnosticCaptureAsync()).Capture!.Id);

        var records = await sdk.QueryDiagnosticRecordsAsync(new(SessionId: capture.SessionId, Events: ["observation.frame.rejected"], Limit: 500));
        Assert.Equal(120, records.Items.Count);
        var detail = records.Items[0].Record.Detail!;
        Assert.Contains(@"<data>\store\x.json", detail); Assert.Contains("<redacted>", detail); Assert.Contains("<address>", detail);
        Assert.DoesNotContain(paths.DataRoot, detail); Assert.DoesNotContain(token, detail); Assert.DoesNotContain("10.1.2.3", detail);
        Assert.Equal("InvalidOperationException", records.Items[0].Record.ExceptionType);
        Assert.Empty((await sdk.QueryDiagnosticRecordsAsync(new(SessionId: capture.SessionId, Modules: ["inventory"]))).Items);
        Assert.Empty((await sdk.QueryDiagnosticRecordsAsync(new(SessionId: capture.SessionId, Levels: ["trace"]))).Items);
        Assert.Contains(capture.SessionId, (await sdk.GetDiagnosticsAsync()).Sessions.Where(x => x.Module == "capture").Select(x => x.Id));
        // 采集会话带文字详情：只导出权限、无调试权限的配对主体看不到它。
        var grant = await host.Pair("exporter", [ApiPermissions.SystemRead, ApiPermissions.DiagnosticsExport]);
        using var exporter = host.Sdk(grant.Token);
        Assert.Equal("resource.notFound", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            exporter.QueryDiagnosticRecordsAsync(new(SessionId: capture.SessionId)))).Problem!.Code);

        var session = (await sdk.GetDiagnosticsAsync()).Sessions.Single(x => x.Id == capture.SessionId);
        var operation = await sdk.ExportDiagnosticsAsync(new([new(session.Id, session.Revision)]), "capture-export-1");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            while (!SqliteStore.IsTerminal(operation.State.KnownValue)) { await Task.Delay(20, timeout.Token); operation = await sdk.GetOperationAsync(operation.Id); }
        Assert.Equal(OperationState.Succeeded, operation.State.KnownValue);
        var artifact = operation.Result!.Value.Deserialize<DiagnosticExportArtifact>(ContractJson.Options)!;
        var bytes = Convert.FromBase64String((await sdk.ReadDiagnosticExportAsync(artifact.Id, new(0, 262144))).ContentBase64);
        DiagnosticExportDocument document;
        using (var input = new MemoryStream(bytes)) using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            document = (await JsonSerializer.DeserializeAsync<DiagnosticExportDocument>(gzip, ContractJson.Options))!;
        Assert.Equal(2, document.SchemaVersion); Assert.True(document.ContainsDetail);
        var exported = Assert.Single(document.Captures!);
        Assert.Equal((capture.Id, "ended"), (exported.Capture.Id, exported.Capture.State));
        Assert.NotNull(exported.StartRuntime); Assert.NotNull(exported.EndRuntime);
        Assert.Equal(120, Assert.Single(document.Sessions).Records.Count(x => x.Event == "observation.frame.rejected" && x.Detail is not null));
        var text = JsonSerializer.Serialize(document, ContractJson.Options);
        Assert.DoesNotContain(paths.DataRoot, text); Assert.DoesNotContain(token, text);
    }

    [Fact]
    public async Task CaptureWithoutDetailExpiresAndHostStopEndsIt()
    {
        using var area = new Workspace();
        DiagnosticCaptureSnapshot running;
        await using (var host = await Host.Start(area))
        {
            using var sdk = host.Sdk(); var trace = host.App.Services.GetRequiredService<DiagnosticTrace>();
            var capture = await sdk.StartDiagnosticCaptureAsync(new(["combat-observation"], "trace", 1), "capture-expire-1");
            Assert.False(capture.IncludeDetail);
            trace.Emit(DiagnosticEvents.FrameRejected, new DiagnosticFields().Value("branch", "frame.stale"), exception: new InvalidOperationException("secret text"));
            await Wait(async () => (await sdk.GetDiagnosticCaptureAsync()).Capture!.State == "ended");
            var ended = (await sdk.GetDiagnosticCaptureAsync()).Capture!;
            Assert.Equal("expired", ended.EndReason); Assert.False(trace.IsEnabled(DiagnosticEvents.FrameRejected));
            var record = Assert.Single((await sdk.QueryDiagnosticRecordsAsync(new(SessionId: capture.SessionId, Events: ["observation.frame.rejected"]))).Items).Record;
            Assert.Null(record.Detail); Assert.Equal("InvalidOperationException", record.ExceptionType);

            running = await sdk.StartDiagnosticCaptureAsync(new(["core"], "debug", 600), "capture-host-stop");
        }
        // 宿主停止时进行中的采集收尾；重新启动后读取到已结束的采集，门限关闭。
        await using (var again = await Host.Start(area))
        {
            using var sdk = again.Sdk();
            var current = (await sdk.GetDiagnosticCaptureAsync()).Capture!;
            Assert.Equal((running.Id, "ended", "hostStopping"), (current.Id, current.State, current.EndReason));
            Assert.Equal("completed", (await sdk.GetDiagnosticsAsync()).Sessions.Single(x => x.Id == running.SessionId).State);
            Assert.False(again.App.Services.GetRequiredService<DiagnosticTrace>().IsEnabled(DiagnosticEvents.FrameRejected));
        }
    }

    [Fact]
    public async Task RestartInterruptsActiveCaptureAndCaptureFileBudgetIsEnforced()
    {
        using var area = new Workspace();
        using var root = DataRootLease.Open(area.Storage);
        await using var database = SqliteStore.Open(root, BackendApplication.DatabaseMigrations);
        await using var assets = new AssetStore(database, root.Paths);
        var store = new DiagnosticStore(database, root.Paths, assets);
        var session = await store.BeginAsync("owner", DiagnosticStore.CaptureModule);
        var now = DateTimeOffset.UtcNow;
        var capture = new DiagnosticCaptureSnapshot(Guid.NewGuid().ToString("N"), "owner", session.Id, ["core"], "debug", true, "active", null,
            now, now.AddMinutes(1), null, new(0), new(0), new(0), new(0), new(0));
        await store.SaveCaptureAsync(capture, null, null);
        var detail = string.Concat(Enumerable.Repeat("detail line ", 650));
        DiagnosticRecord Record(int i) => DiagnosticSanitizer.Create(now, "debug", "core", sequence: new((ulong)i + 1), detail: detail, allowDetail: true);
        DiagnosticSessionSnapshot? latest = null;
        for (var batch = 0; batch < 5; batch++)
            latest = await store.AppendAsync(session.Id, Enumerable.Range(batch * 256, 256).Select(Record).ToArray());
        Assert.True(latest!.EventBytes.Value <= DiagnosticStore.MaximumCaptureBytes);
        Assert.True(latest.DroppedRecords.Value > 0); Assert.Equal(1280UL, latest.Records.Value + latest.DroppedRecords.Value);
        // 中文详情经 JSON 转义后仍在单条上限内。
        var chinese = DiagnosticSanitizer.Create(now, "debug", "core", detail: new string('诊', DiagnosticSanitizer.MaximumDetailChars), allowDetail: true);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(chinese, ContractJson.Options)) <= DiagnosticStore.MaximumCaptureRecordBytes);

        var restarted = new DiagnosticStore(database, root.Paths, assets);
        await restarted.RecoverAsync();
        Assert.Equal(("interrupted", "restarted"), (restarted.Capture(capture.Id)!.State, restarted.Capture(capture.Id)!.EndReason));
        Assert.Equal("interrupted", restarted.Sessions().Single(x => x.Id == session.Id).State);
    }

    private static async Task Wait(Func<Task<bool>> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); while (!await condition()) await Task.Delay(20, timeout.Token); }

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Repository(), "artifacts", "tmp", "diagnostics-capture-test-" + Guid.NewGuid().ToString("N"));
        public StorageOptions Storage => new(Path.Combine(Root, "bootstrap"), true, DataRoot: Path.Combine(Root, "data"));
        public Workspace() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            var expected = Path.GetFullPath(Path.Combine(Repository(), "artifacts", "tmp")) + Path.DirectorySeparatorChar;
            Assert.StartsWith(expected, Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
        private static string Repository()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) directory = directory.Parent;
            return directory!.FullName;
        }
    }
    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required Uri Address { get; init; }
        public static async Task<Host> Start(Workspace area)
        {
            var app = BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: area.Storage); await app.StartAsync();
            return new() { App = app, Address = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        }
        public MabiToolClient Sdk(string token = Token) => new(Address, _ => ValueTask.FromResult(token));
        public async Task<AccessGrant> Pair(string name, IReadOnlyList<string> permissions)
        {
            using var sdk = Sdk(); var challenge = await sdk.CreatePairingChallengeAsync(new(name, null, permissions));
            return await sdk.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code));
        }
        public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); }
    }
}

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Diagnostics;
using MabiTool.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class DiagnosticApiTests
{
    private const string Token = "diagnostic_tests_0123456789012345678901234567890123456789";
    private const string Origin = "http://localhost:5173";

    [Fact]
    public async Task NativeAndBrowserExportSameWhitelistedRecordsWithoutFormattingMessages()
    {
        using var area = new Workspace(); await using var host = await Host.Start(area); using var sdk = host.Sdk();
        var recorder = host.App.Services.GetRequiredService<DiagnosticRecorder>();
        var logger = recorder.CreateLogger("MabiTool.Host.Color.NativeReader");
        var secret = "private_token_and_full_score_MML_9876543210";
        var fields = new KeyValuePair<string, object?>[]
        {
            new("code", "worker.commandBusy"), new("operationId", "op-" + new string('a', 32)),
            new("providerVersion", "1.2.3"), new("token", secret), new("packet", secret), new("path", area.Root)
        };
        logger.Log(LogLevel.Error, new EventId(5, secret), fields, new InvalidOperationException(secret),
            (_, _) => throw new InvalidOperationException("自由文本格式化不应执行"));
        await recorder.FlushAsync();
        var snapshot = Assert.Single((await sdk.GetDiagnosticsAsync()).Sessions);
        var request = new DiagnosticExportRequest([new(snapshot.Id, snapshot.Revision)]);
        var operation = await sdk.ExportDiagnosticsAsync(request, "native-diagnostic-export");
        var finished = await Wait(sdk, operation.Id);
        Assert.Equal(OperationState.Succeeded, finished.State.KnownValue); Assert.Equal(EffectStatus.None, finished.EffectStatus.KnownValue);
        var artifact = finished.Result!.Value.Deserialize<DiagnosticExportArtifact>(ContractJson.Options)!;
        Assert.Equal(artifact, await sdk.GetDiagnosticExportAsync(artifact.Id));
        var content = await Download(sdk, artifact);
        var document = await Decode(content);
        var record = Assert.Single(Assert.Single(document.Sessions).Records);
        Assert.Equal("worker.commandBusy", record.Code); Assert.Equal("color", record.Module);
        Assert.Equal("1.2.3", record.ProviderVersion); Assert.Equal("InvalidOperationException", record.ExceptionType);
        Assert.Equal(fields[1].Value, record.OperationId);
        var text = JsonSerializer.Serialize(document, ContractJson.Options);
        Assert.DoesNotContain(secret, text); Assert.DoesNotContain(area.Root, text); Assert.DoesNotContain("\"packet\"", text);
        Assert.Equal(JsonSerializer.Serialize(finished, ContractJson.Options),
            JsonSerializer.Serialize(await sdk.ExportDiagnosticsAsync(request, "native-diagnostic-export"), ContractJson.Options));

        var grant = await host.Pair("browser-diagnostics", [ApiPermissions.SystemRead, ApiPermissions.DiagnosticsExport], Origin);
        using var browser = host.Sdk(grant.Token, Origin);
        var store = host.App.Services.GetRequiredService<DiagnosticStore>();
        var own = await store.BeginAsync(grant.Session.ClientId, "color");
        await store.AppendAsync(own.Id, [DiagnosticSanitizer.Create(DateTimeOffset.UtcNow, "warning", "color", "color.runInactive")]);
        own = store.Get(own.Id, grant.Session.ClientId, false);
        var web = await Wait(browser, (await browser.ExportDiagnosticsAsync(new([new(own.Id, own.Revision)]), "browser-diagnostic-export")).Id);
        var webArtifact = web.Result!.Value.Deserialize<DiagnosticExportArtifact>(ContractJson.Options)!;
        Assert.Equal("color.runInactive", Assert.Single(Assert.Single((await Decode(await Download(browser, webArtifact))).Sessions).Records).Code);
        Assert.Equal("resource.notFound", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            browser.ReadDiagnosticExportAsync(artifact.Id, new()))).Problem!.Code);
        Assert.Equal("resource.notFound", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            browser.ExportDiagnosticsAsync(request, "browser-foreign-source"))).Problem!.Code);
    }

    [Fact]
    public async Task DiagnosticPermissionsAndRetentionRevisionSurviveRestart()
    {
        using var area = new Workspace();
        DiagnosticPolicySnapshot saved;
        await using (var host = await Host.Start(area))
        {
            using var sdk = host.Sdk();
            var policy = await sdk.GetDiagnosticPolicyAsync();
            saved = await sdk.ChangeDiagnosticPolicyAsync(new(3, new(4096), new(4096)), "diagnostic-policy-edit", DiagnosticStore.PolicyETag(policy.Revision));
            Assert.Equal(saved, await sdk.ChangeDiagnosticPolicyAsync(new(3, new(4096), new(4096)), "diagnostic-policy-edit", DiagnosticStore.PolicyETag(policy.Revision)));
            Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
                sdk.ChangeDiagnosticPolicyAsync(new(4), "diagnostic-policy-edit", DiagnosticStore.PolicyETag(policy.Revision)))).Problem!.Code);
            Assert.Equal("revision.stale", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
                sdk.ChangeDiagnosticPolicyAsync(new(4), "diagnostic-policy-stale", DiagnosticStore.PolicyETag(policy.Revision)))).Problem!.Code);
            Assert.Equal("request.invalid", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
                sdk.ChangeDiagnosticPolicyAsync(new(4, RecentProtectionSeconds: 1), "diagnostic-policy-invalid", DiagnosticStore.PolicyETag(saved.Revision)))).Problem!.Code);
            var grant = await host.Pair("diagnostic-reader", [ApiPermissions.SystemRead]);
            using var reader = host.Sdk(grant.Token);
            Assert.Empty((await reader.GetDiagnosticsAsync()).Sessions);
            Assert.Equal("permission.denied", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
                reader.ExportDiagnosticsAsync(new([]), "diagnostic-reader-denied"))).Problem!.Code);
            Assert.Equal("permission.denied", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
                reader.ChangeDiagnosticPolicyAsync(new(), "diagnostic-reader-policy", DiagnosticStore.PolicyETag(saved.Revision)))).Problem!.Code);
        }
        await using (var host = await Host.Start(area))
        {
            using var sdk = host.Sdk();
            Assert.Equal(saved, await sdk.GetDiagnosticPolicyAsync());
            Assert.Contains((await sdk.GetDiagnosticsAsync()).Sessions, x => x.State == "completed");
        }
    }

    [Fact]
    public async Task FixedSourceHashAndWatermarkExcludeLaterRecordsAndProtectCleanup()
    {
        await using var fixture = new Fixture();
        var source = await fixture.Store.BeginAsync("client", "rooms");
        await fixture.Store.AppendAsync(source.Id, [fixture.Record("resource.busy")]);
        source = fixture.Store.Get(source.Id, "client", false);
        var admission = await fixture.Operations.AdmitDiagnosticExportAsync("client", false, new([new(source.Id, source.Revision)]), "fixed-export-source", true);
        var plan = admission.Snapshot.Result!.Value.Deserialize<DiagnosticExportPlan>(ContractJson.Options)!;
        await fixture.Store.AppendAsync(source.Id, [fixture.Record("revision.stale")]);
        await fixture.Store.CompleteAsync(source.Id);
        await fixture.Store.ChangePolicyAsync("owner", new(0, new(1024)), "source-cleanup-policy", DiagnosticStore.PolicyETag(new(0)));
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(0, await fixture.Store.CleanupAsync());
        var artifact = await fixture.Store.ExportAsync("client", plan, default);
        var document = await Decode(await fixture.Read(artifact));
        Assert.Equal(source, Assert.Single(document.Sessions).Session);
        Assert.Equal("resource.busy", Assert.Single(Assert.Single(document.Sessions).Records).Code);
        await fixture.Store.ReleaseSourcesAsync(admission.Snapshot.Id);
        Assert.Equal(1, await fixture.Store.CleanupAsync());
        Assert.Equal("resource.notFound", Assert.Throws<OperationException>(() => fixture.Store.Get(source.Id, "client", false)).Code);
    }

    [Fact]
    public async Task ActiveRecentAndNewlyWrittenSessionsRemainUnderRetentionPressure()
    {
        await using var fixture = new Fixture();
        var old = await fixture.Store.BeginAsync("client", "color"); await fixture.Store.AppendAsync(old.Id, [fixture.Record()]); await fixture.Store.CompleteAsync(old.Id);
        var active = await fixture.Store.BeginAsync("client", "rooms");
        await fixture.Store.ChangePolicyAsync("owner", new(0, new(1024)), "retention-pressure-policy", DiagnosticStore.PolicyETag(new(0)));
        Assert.Equal(0, await fixture.Store.CleanupAsync());
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        var recent = await fixture.Store.BeginAsync("client", "color");
        await fixture.Store.AppendAsync(active.Id, [fixture.Record()]);
        await fixture.Store.CompleteAsync(active.Id);
        await fixture.Store.CompleteAsync(recent.Id);
        Assert.Equal(0, await fixture.Store.CleanupAsync());
        Assert.DoesNotContain(fixture.Store.Sessions(), x => x.Id == old.Id);
        Assert.Contains(fixture.Store.Sessions(), x => x.Id == active.Id);
        Assert.Contains(fixture.Store.Sessions(), x => x.Id == recent.Id);
        fixture.Clock.Advance(TimeSpan.FromMinutes(-10));
        Assert.Equal(0, await fixture.Store.CleanupAsync());
    }

    [Fact]
    public async Task RollingLogsRespectBytesCurrentSegmentAndRecentProtection()
    {
        await using var fixture = new Fixture();
        var policy = DiagnosticStore.Normalize(new(LogBudgetBytes: new(1024)));
        var records = Enumerable.Range(0, 20).Select(_ => fixture.Record()).ToArray();
        await fixture.Store.Logs.AppendAsync(records, policy, default);
        Assert.InRange(fixture.Store.Logs.Bytes, 1, 1024);
        Assert.True(fixture.Store.Logs.DroppedRecords > 0);
        Assert.Equal("resource.capacityExceeded", fixture.Store.Logs.LastError);
        var old = Directory.GetFiles(fixture.Paths.Resolve("logs", "backend")).Single();
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.Store.Logs.AppendAsync([fixture.Record()], policy, default);
        Assert.False(File.Exists(old)); Assert.Null(fixture.Store.Logs.LastError);
        Assert.InRange(fixture.Store.Logs.Bytes, 1, 1024);
    }

    [Fact]
    public async Task LogBytesFollowWritesRotationReclaimRestartAndCleanupWithoutReadingTheDirectory()
    {
        await using var fixture = new Fixture();
        var root = fixture.Paths.Resolve("logs", "backend");
        long Disk() => Directory.Exists(root) ? Directory.GetFiles(root, "log-*.jsonl").Sum(x => new FileInfo(x).Length) : 0;
        await fixture.Store.RecoverAsync();
        Assert.Equal(0, fixture.Store.Logs.LastKnownBytes);
        var small = DiagnosticStore.Normalize(new(LogBudgetBytes: new(1024)));
        await fixture.Store.Logs.AppendAsync(Enumerable.Range(0, 20).Select(_ => fixture.Record()).ToArray(), small, default);
        Assert.True(fixture.Store.Logs.DroppedRecords > 0);
        Assert.Equal(Disk(), fixture.Store.Logs.LastKnownBytes);
        var first = Directory.GetFiles(root).Single();
        // 旧段过保护期后被回收，新记录写入轮转出的新段。
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.Store.Logs.AppendAsync([fixture.Record()], small, default);
        Assert.False(File.Exists(first));
        Assert.Equal(Disk(), fixture.Store.Logs.LastKnownBytes);

        // 重启后启动恢复测量一次，随后写入新的当前段。
        var restarted = new DiagnosticStore(fixture.Database, fixture.Paths, fixture.Assets, fixture.Clock);
        Assert.Null(restarted.Logs.LastKnownBytes);
        await restarted.RecoverAsync();
        Assert.Equal(Disk(), restarted.Logs.Bytes);
        var previous = Directory.GetFiles(root).Single();
        await restarted.Logs.AppendAsync([fixture.Record()], DiagnosticStore.Normalize(new()), default);
        Assert.Equal(2, Directory.GetFiles(root).Length);
        Assert.Equal(Disk(), restarted.Logs.Bytes);

        // 存储清理删除旧段后与磁盘一致。
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        IStorageMaintenanceParticipant participant = restarted;
        var item = (await participant.InspectAsync()).Single(x => x.Key == "log/" + Path.GetFileName(previous));
        Assert.True(item.Eligible);
        using (var maintenance = fixture.Database.Activities.TryEnterMaintenance("log-cleanup")!)
            Assert.True((await participant.DeleteAsync(await participant.FreezeAsync(item), maintenance)).Deleted);
        Assert.False(File.Exists(previous));
        var bytes = restarted.Logs.Bytes;
        Assert.Equal(Disk(), bytes);

        // 读取不再枚举目录：目录外部新增的段不影响读数，下一次写入重新核对。
        await File.WriteAllTextAsync(Path.Combine(root, "log-" + Guid.NewGuid().ToString("N") + ".jsonl"), "{}\n");
        Assert.Equal(bytes, restarted.Logs.Bytes);
        Assert.NotEqual(Disk(), restarted.Logs.Bytes);
        await restarted.Logs.AppendAsync([fixture.Record()], DiagnosticStore.Normalize(new()), default);
        Assert.Equal(Disk(), restarted.Logs.Bytes);
    }

    [Theory]
    [InlineData("changed-hash")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-field")]
    [InlineData("invalid-utf8")]
    [InlineData("truncated")]
    [InlineData("missing")]
    [InlineData("oversized")]
    public async Task DamagedDiagnosticFilesRejectExportAndRemainAvailableForInspection(string mode)
    {
        await using var fixture = new Fixture();
        var source = await fixture.Store.BeginAsync("client", "color");
        await fixture.Store.AppendAsync(source.Id, [fixture.Record()]);
        source = fixture.Store.Get(source.Id, "client", false);
        var path = fixture.Paths.Resolve("diagnostics", source.Module, source.Id, "events.jsonl");
        var valid = File.ReadAllText(path);
        var bytes = mode switch
        {
            "unknown-field" => Encoding.UTF8.GetBytes(valid.TrimEnd().TrimEnd('}') + ",\"packet\":\"private-packet\"}\n"),
            "duplicate-field" => Encoding.UTF8.GetBytes(valid.TrimEnd().TrimEnd('}') + ",\"level\":\"warning\"}\n"),
            "invalid-utf8" => new byte[] { 0xFF, 0xFE, 0x0A },
            "truncated" => Encoding.UTF8.GetBytes(valid[..^1]),
            "oversized" => new byte[DiagnosticStore.MaximumSessionBytes + 1],
            _ => Encoding.UTF8.GetBytes(valid.Replace("resource.busy", "revision.stale", StringComparison.Ordinal))
        };
        if (mode == "missing") File.Delete(path); else File.WriteAllBytes(path, bytes);
        if (mode is not ("changed-hash" or "missing"))
        {
            source = source with { EventBytes = new((ulong)bytes.Length), EventSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
            await fixture.Database.WriteAsync(tx => tx.Execute("UPDATE platform_diagnostic_sessions SET snapshot=$json WHERE id=$id",
                ("$json", JsonSerializer.Serialize(source, ContractJson.Options)), ("$id", source.Id)));
        }
        if (mode == "oversized")
        {
            Assert.Equal("storage.assetDamaged", (await Assert.ThrowsAsync<StorageException>(() =>
                fixture.Operations.AdmitDiagnosticExportAsync("client", false, new([new(source.Id, source.Revision)]), "damaged-export-source", true))).Code);
        }
        else
        {
            var admitted = await fixture.Operations.AdmitDiagnosticExportAsync("client", false, new([new(source.Id, source.Revision)]), "damaged-export-source", true);
            var plan = admitted.Snapshot.Result!.Value.Deserialize<DiagnosticExportPlan>(ContractJson.Options)!;
            Assert.Equal("storage.assetDamaged", (await Assert.ThrowsAsync<StorageException>(() => fixture.Store.ExportAsync("client", plan, default))).Code);
            await fixture.Store.ReleaseSourcesAsync(admitted.Snapshot.Id);
        }
        Assert.Equal(mode != "missing", File.Exists(path));
        Assert.Equal(0L, fixture.Database.ReadScalar("SELECT count(*) FROM platform_diagnostic_exports"));
    }

    [Fact]
    public async Task SourceAdmissionRejectsForeignDuplicateAndStaleVersionsBeforePinning()
    {
        await using var fixture = new Fixture(); var source = await fixture.Store.BeginAsync("client", "rooms");
        Assert.Equal("resource.notFound", (await Assert.ThrowsAsync<OperationException>(() =>
            fixture.Operations.AdmitDiagnosticExportAsync("other", false, new([new(source.Id, source.Revision)]), "foreign-export-source", true))).Code);
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() =>
            fixture.Operations.AdmitDiagnosticExportAsync("client", false, new([new(source.Id, source.Revision), new(source.Id, source.Revision)]), "duplicate-export-source", true))).Code);
        Assert.Equal("revision.stale", (await Assert.ThrowsAsync<OperationException>(() =>
            fixture.Operations.AdmitDiagnosticExportAsync("client", false, new([new(source.Id, new(0))]), "stale-export-source", true))).Code);
        Assert.Equal(0L, fixture.Database.ReadScalar("SELECT count(*) FROM platform_diagnostic_sources"));
        Assert.Equal(0L, fixture.Database.ReadScalar("SELECT count(*) FROM core_operations"));
    }

    [Fact]
    public async Task InterruptedRestartReleasesSourcePinsAndDoesNotRequeueExports()
    {
        await using var fixture = new Fixture(); var source = await fixture.Store.BeginAsync("client", "rooms");
        await fixture.Store.AppendAsync(source.Id, [fixture.Record()]);
        source = fixture.Store.Get(source.Id, "client", false);
        var admitted = await fixture.Operations.AdmitDiagnosticExportAsync("client", false, new([new(source.Id, source.Revision)]), "restart-export-source", true);
        await fixture.Database.RecoverOperationsAsync();
        await fixture.Store.RecoverAsync();
        Assert.Equal(OperationState.Interrupted, fixture.Operations.Get(admitted.Snapshot.Id, "client", false).State.KnownValue);
        Assert.Equal("interrupted", fixture.Store.Get(source.Id, "client", false).State);
        Assert.Equal(0L, fixture.Database.ReadScalar("SELECT count(*) FROM platform_diagnostic_sources"));
        Assert.Equal(0L, fixture.Database.ReadScalar("SELECT count(*) FROM platform_diagnostic_exports"));
    }

    [Fact]
    public async Task ExportExpiryHonorsOtherAssetPinsAndCorruptArchiveCleanup()
    {
        await using var fixture = new Fixture(); var source = await fixture.Store.BeginAsync("client", "rooms");
        var plan = new DiagnosticExportPlan([source], fixture.Clock.GetUtcNow());
        var artifact = await fixture.Store.ExportAsync("client", plan, default);
        var pin = await fixture.Assets.PinAsync(artifact.Asset.Id, "active-download");
        fixture.Clock.Advance(DiagnosticStore.ExportLifetime + TimeSpan.FromSeconds(1));
        await fixture.Store.CleanupAsync();
        Assert.NotNull(fixture.Assets.Get(artifact.Asset.Id));
        Assert.Equal("resource.notFound", Assert.Throws<OperationException>(() => fixture.Store.GetExport(artifact.Id, "client", false)).Code);
        await fixture.Assets.UnpinAsync(pin);
        var blob = fixture.Paths.Resolve("assets", "sha256", artifact.Asset.Sha256[..2], artifact.Asset.Sha256);
        File.WriteAllText(blob, "damaged");
        await fixture.Store.CleanupAsync();
        Assert.Null(fixture.Assets.Get(artifact.Asset.Id));
        Assert.False(File.Exists(blob));
    }

    [Fact]
    public async Task ExportReadReportsDamagedAndInvalidAssetsOverHttpAndReleasesReadPins()
    {
        using var area = new Workspace(); await using var host = await Host.Start(area); using var sdk = host.Sdk();
        var store = host.App.Services.GetRequiredService<DiagnosticStore>();
        var source = await store.BeginAsync("owner", "rooms");
        var operation = await Wait(sdk, (await sdk.ExportDiagnosticsAsync(
            new([new(source.Id, source.Revision)]), "diagnostic-http-damage")).Id);
        var artifact = operation.Result!.Value.Deserialize<DiagnosticExportArtifact>(ContractJson.Options)!;
        var invalid = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.ReadDiagnosticExportAsync(
            artifact.Id, new(checked((int)artifact.Asset.SizeBytes.Value + 1), 128)));
        Assert.Equal(422, invalid.Problem!.Status); Assert.Equal("storage.assetInvalid", invalid.Problem.Code);
        var paths = host.App.Services.GetRequiredService<PathService>();
        var blob = paths.Resolve("assets", "sha256", artifact.Asset.Sha256[..2], artifact.Asset.Sha256);
        File.WriteAllText(blob, "changed diagnostic export");
        var damaged = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.ReadDiagnosticExportAsync(artifact.Id, new()));
        Assert.Equal(422, damaged.Problem!.Status); Assert.Equal("storage.assetDamaged", damaged.Problem.Code);
        var database = host.App.Services.GetRequiredService<SqliteStore>();
        Assert.Equal(0L, database.ReadScalar("SELECT count(*) FROM platform_asset_pins WHERE owner LIKE 'diagnostic-read:%'"));
        await store.BeginAsync("owner", "rooms").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(blob));
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task SessionByteLimitPreservesAcceptedRecordsAndExportsDroppedCount()
    {
        await using var fixture = new Fixture();
        var source = await fixture.Store.BeginAsync("client", "rooms");
        var record = DiagnosticSanitizer.Create(fixture.Clock.GetUtcNow(), "warning", "rooms", "resource.busy",
            new string('a', 32), "op-" + new string('b', 32), "1.2.3", "InvalidOperationException");
        var batch = Enumerable.Repeat(record, 256).ToArray();
        var submitted = 0UL;
        for (var i = 0; i < 32 && source.DroppedRecords.Value == 0; i++)
        {
            await fixture.Store.AppendAsync(source.Id, batch); submitted += (ulong)batch.Length;
            source = fixture.Store.Get(source.Id, "client", false);
        }
        Assert.True(source.DroppedRecords.Value > 0);
        Assert.Equal(submitted, source.Records.Value + source.DroppedRecords.Value);
        Assert.InRange(source.EventBytes.Value, 1UL, (ulong)DiagnosticStore.MaximumSessionBytes);
        var path = fixture.Paths.Resolve("diagnostics", "rooms", source.Id, "events.jsonl");
        Assert.Equal((long)source.EventBytes.Value, new FileInfo(path).Length);
        var artifact = await fixture.Store.ExportAsync("client", new([source], fixture.Clock.GetUtcNow()), default);
        var exported = Assert.Single((await Decode(await fixture.Read(artifact))).Sessions);
        Assert.Equal(source, exported.Session); Assert.Equal(source.Records.Value, (ulong)exported.Records.Count);
        Assert.All(exported.Records, value => Assert.Equal(record, value));
    }

    [Fact]
    public async Task ExportAssetCapacityRecoversAfterExpiry()
    {
        await using var fixture = new Fixture();
        var source = await fixture.Store.BeginAsync("client", "rooms");
        var plan = new DiagnosticExportPlan([source], fixture.Clock.GetUtcNow());
        var artifacts = new List<DiagnosticExportArtifact>();
        for (var i = 0; i < DiagnosticStore.MaximumExports; i++)
            artifacts.Add(await fixture.Store.ExportAsync("client", plan, default));
        Assert.Equal(DiagnosticStore.MaximumExports, artifacts.Select(x => x.Id).Distinct().Count());
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(
            () => fixture.Store.ExportAsync("client", plan, default))).Code);
        fixture.Clock.Advance(DiagnosticStore.ExportLifetime + TimeSpan.FromSeconds(1));
        var next = await fixture.Store.ExportAsync("client", plan, default);
        Assert.Equal(1L, fixture.Database.ReadScalar("SELECT count(*) FROM platform_diagnostic_exports"));
        Assert.Equal(next, fixture.Store.GetExport(next.Id, "client", false));
        Assert.All(artifacts, value => Assert.Equal("resource.notFound", Assert.Throws<OperationException>(
            () => fixture.Store.GetExport(value.Id, "client", false)).Code));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedExportSurvivesRequestDisconnectAndExplicitCancellationReleasesSources(bool cancelExport)
    {
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(async phase =>
        {
            if (phase != AssetCommitPhase.BlobPublished) return;
            published.TrySetResult(); await release.Task;
        });
        using var executor = new DiagnosticExportOperations(fixture.Operations, fixture.Store);
        await executor.StartAsync(default);
        OperationSnapshot accepted;
        OperationSnapshot queued;
        try
        {
            var source = await fixture.Store.BeginAsync("client", "rooms");
            var request = new DiagnosticExportRequest([new(source.Id, source.Revision)]);
            using var disconnected = new CancellationTokenSource();
            accepted = await executor.SubmitAsync("client", false, request, "disconnect-export-request", disconnected.Token);
            await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            disconnected.Cancel();
            Assert.Equal(OperationState.Running, fixture.Operations.Get(accepted.Id, "client", false).State.KnownValue);
            queued = await executor.SubmitAsync("client", false, request, "queued-cancel-export", default);
            await executor.CancelAsync(queued.Id, "client", false, "user", default);
            Assert.Equal(OperationState.Cancelled, (await executor.SubmitAsync(
                "client", false, request, "queued-cancel-export", default)).State.KnownValue);
            if (cancelExport)
                Assert.Equal(OperationState.Cancelled, (await executor.CancelAsync(accepted.Id, "client", false, "user", default)).State.KnownValue);
            release.TrySetResult();
            using var finished = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!SqliteStore.IsTerminal(fixture.Operations.Get(accepted.Id, "client", false).State.KnownValue) ||
                (long)fixture.Database.ReadScalar("SELECT count(*) FROM platform_diagnostic_sources")! != 0)
                await Task.Delay(10, finished.Token);
        }
        finally { release.TrySetResult(); await executor.StopAsync(default); }
        Assert.Equal(cancelExport ? OperationState.Cancelled : OperationState.Succeeded,
            fixture.Operations.Get(accepted.Id, "client", false).State.KnownValue);
        Assert.Equal(OperationState.Cancelled, fixture.Operations.Get(queued.Id, "client", false).State.KnownValue);
        Assert.Equal(0L, fixture.Database.ReadScalar("SELECT count(*) FROM platform_diagnostic_sources"));
        Assert.Equal(cancelExport ? 0L : 1L, fixture.Database.ReadScalar("SELECT count(*) FROM platform_diagnostic_exports"));
    }

    [Fact]
    public async Task DiagnosticRegistrationAndQueueHaveExplicitCapacity()
    {
        await using var fixture = new Fixture();
        for (var i = 0; i < DiagnosticStore.MaximumActiveSessions; i++) await fixture.Store.BeginAsync("client", "rooms");
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(() => fixture.Store.BeginAsync("client", "rooms"))).Code);
        using var recorder = new DiagnosticRecorder(fixture.Store, fixture.Clock);
        for (var i = 0; i < 256; i++) Assert.True(recorder.Record(fixture.Record()));
        Assert.False(recorder.Record(fixture.Record())); Assert.Equal(1UL, recorder.DroppedRecords);
    }

    private static async Task<byte[]> Download(MabiToolClient sdk, DiagnosticExportArtifact artifact)
    {
        using var result = new MemoryStream(); var offset = 0;
        while ((ulong)offset < artifact.Asset.SizeBytes.Value)
        {
            var chunk = await sdk.ReadDiagnosticExportAsync(artifact.Id, new(offset, 128));
            Assert.Equal(offset, chunk.Offset); Assert.Equal(artifact.Asset.Sha256, chunk.Sha256);
            var bytes = Convert.FromBase64String(chunk.ContentBase64); Assert.NotEmpty(bytes);
            await result.WriteAsync(bytes); offset += bytes.Length;
        }
        Assert.Equal(artifact.Asset.Sha256, Convert.ToHexStringLower(SHA256.HashData(result.ToArray())));
        return result.ToArray();
    }
    private static async Task<DiagnosticExportDocument> Decode(byte[] bytes)
    {
        using var input = new MemoryStream(bytes); using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return (await JsonSerializer.DeserializeAsync<DiagnosticExportDocument>(gzip, ContractJson.Options))!;
    }
    private static async Task<OperationSnapshot> Wait(MabiToolClient sdk, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true) { var operation = await sdk.GetOperationAsync(id, timeout.Token); if (SqliteStore.IsTerminal(operation.State.KnownValue)) return operation; await Task.Delay(10, timeout.Token); }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Repository(), "artifacts", "tmp", "diagnostics-test-" + Guid.NewGuid().ToString("N"));
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
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Workspace area = new();
        private readonly DataRootLease root;
        public Clock Clock { get; } = new();
        public PathService Paths => root.Paths;
        public SqliteStore Database { get; }
        public AssetStore Assets { get; }
        public DiagnosticStore Store { get; }
        public OperationStore Operations { get; }
        public Fixture(Func<AssetCommitPhase, Task>? phaseHook = null)
        {
            root = DataRootLease.Open(area.Storage); Database = SqliteStore.Open(root, BackendApplication.DatabaseMigrations);
            Assets = new(Database, Paths, Clock, phaseHook); Store = new(Database, Paths, Assets, Clock); Operations = new(Database, Clock);
        }
        public DiagnosticRecord Record(string code = "resource.busy") => DiagnosticSanitizer.Create(Clock.GetUtcNow(), "warning", "rooms", code);
        public Task<byte[]> Read(DiagnosticExportArtifact artifact) => Assets.ReadRangeAsync(artifact.Asset.Id, 0, 262144, DiagnosticStore.MaximumExportBytes);
        public async ValueTask DisposeAsync() { await Assets.DisposeAsync(); await Database.DisposeAsync(); root.Dispose(); area.Dispose(); }
    }
    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required Uri Address { get; init; }
        public static async Task<Host> Start(Workspace area)
        {
            var app = BackendApplication.Build(new(0, Token, new HashSet<string> { Origin }), storage: area.Storage); await app.StartAsync();
            return new() { App = app, Address = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        }
        public MabiToolClient Sdk(string token = Token, string? origin = null)
        {
            if (origin is null) return new(Address, _ => ValueTask.FromResult(token));
            return new(Address, _ => ValueTask.FromResult(token), new OriginHandler(origin));
        }
        public async Task<AccessGrant> Pair(string name, IReadOnlyList<string> permissions, string? origin = null)
        {
            using var sdk = Sdk(); var challenge = await sdk.CreatePairingChallengeAsync(new(name, origin, permissions));
            if (origin is null) return await sdk.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code));
            using var browser = Sdk(Token, origin); return await browser.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code));
        }
        public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); }
    }
    private sealed class OriginHandler(string origin) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { request.Headers.Add("Origin", origin); return base.SendAsync(request, ct); }
    }
}

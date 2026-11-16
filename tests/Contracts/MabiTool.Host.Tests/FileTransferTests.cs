using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Files;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class FileTransferTests
{
    private const string Token = "file_transfers_0123456789012345678901234567890123456789";
    private const string Origin = "http://localhost:5173";

    [Trait("Category", "Slow")]
    [Fact]
    public async Task BackgroundCleanupWaitsForMaintenanceAndResumesWithoutStoppingHost()
    {
        using var area = new Workspace();
        var clock = new Clock();
        await using var host = await Host.Start(area, clock);
        using var sdk = host.Sdk();
        var upload = await sdk.CreateFileUploadAsync(new("maintenance.json", new(1), Hash([1]), "application/json"), "maintenance-cleanup-key");
        clock.Advance(TimeSpan.FromMinutes(16));
        var cleanup = host.App.Services.GetServices<IHostedService>().OfType<FileTransferLifetime>().Single();
        var lifetime = host.App.Services.GetRequiredService<IHostApplicationLifetime>();
        ActivityBarrier.MaintenanceLease? maintenance;
        using var entryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((maintenance = host.Database.Activities.TryEnterMaintenance("file-cleanup-test")) is null)
            await Task.Delay(10, entryDeadline.Token);
        using (maintenance)
        {
            await Task.WhenAny(cleanup.ExecuteTask!, Task.Delay(TimeSpan.FromSeconds(31)));
            Assert.False(cleanup.ExecuteTask!.IsCompleted, cleanup.ExecuteTask.Exception?.GetBaseException().ToString());
            Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
            Assert.Equal(1L, host.Database.ReadScalar("SELECT count(*) FROM platform_file_uploads WHERE id=$id", ("$id", upload.Id)));
        }
        using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        while ((long)host.Database.ReadScalar("SELECT count(*) FROM platform_file_uploads WHERE id=$id", ("$id", upload.Id))! != 0 ||
            host.Database.Activities.Snapshot().Activities.Any(activity => activity.Owner == "platform-files" && activity.Kind == "storage.cleanup"))
            await Task.Delay(20, cleanupDeadline.Token);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.False(cleanup.ExecuteTask!.IsCompleted);
        Assert.DoesNotContain(host.Database.Activities.Snapshot().Activities, activity => activity.Owner == "platform-files" && activity.Kind == "storage.cleanup");
    }

    [Fact]
    public async Task CleanupCancellationAndStorageFailureReleaseActivityBeforeRetry()
    {
        using var area = new Workspace();
        var clock = new Clock();
        await using var host = await Host.Start(area, clock);
        var files = host.App.Services.GetRequiredService<FileTransferStore>();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.CleanupAsync(cancelled.Token));
        Assert.DoesNotContain(host.Database.Activities.Snapshot().Activities, activity => activity.Kind == "storage.cleanup");
        using var sdk = host.Sdk();
        await sdk.CreateFileUploadAsync(new("failure.json", new(1), Hash([1]), "application/json"), "failed-cleanup-key");
        clock.Advance(TimeSpan.FromDays(2));
        await host.Database.WriteAsync(tx => tx.Execute("CREATE TRIGGER reject_file_cleanup BEFORE DELETE ON platform_file_keys BEGIN SELECT RAISE(ABORT,'cleanup-failure'); END"));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => files.CleanupAsync());
        Assert.DoesNotContain(host.Database.Activities.Snapshot().Activities, activity => activity.Kind == "storage.cleanup");
        await host.Database.WriteAsync(tx => tx.Execute("DROP TRIGGER reject_file_cleanup"));
        await files.CleanupAsync();
        Assert.Equal(0L, host.Database.ReadScalar("SELECT count(*) FROM platform_file_keys"));
        Assert.DoesNotContain(host.Database.Activities.Snapshot().Activities, activity => activity.Kind == "storage.cleanup");
    }

    [Fact]
    public async Task BrowserStreamsUploadIntoMusicPreviewAndIdempotentCommit()
    {
        using var area = new Workspace(); await using var host = await Host.Start(area);
        var grant = await host.Pair([ApiPermissions.SystemRead, ApiPermissions.FilesTransfer, ApiPermissions.MusicEdit], Origin);
        using var browser = host.Sdk(grant.Token, Origin);
        var bytes = Encoding.UTF8.GetBytes("{\"gap_sec\":4,\"theme\":\"dark\"}");
        var upload = await Upload(browser, "settings.json", bytes);
        Assert.Equal("ready", upload.State);
        Assert.Equal(upload.Asset, await browser.GetAssetAsync(upload.Asset!.Id));
        var request = new FileImportRequest("mobifolio", "browser-files", "原曲庫", [upload.Id], Scope: new("TW", "realm", new(18446744073709551615)));
        var preview = await browser.PreviewFileImportAsync(request, "browser-file-preview");
        Assert.Equal("mobifolio-1", preview.ParserVersion); Assert.Equal("ready", preview.Music!.State);
        Assert.Equal(preview.Id, (await browser.PreviewFileImportAsync(request, "browser-file-preview")).Id);
        Assert.Equal(preview.Id, (await browser.GetFileImportAsync(preview.Id)).Id);
        Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<MabiToolApiException>(() => browser.PreviewFileImportAsync(
            request with { SourceLabel = "changed" }, "browser-file-preview"))).Problem!.Code);
        var accepted = await browser.CommitFileImportAsync(preview.Id, "browser-file-commit", preview.TargetETag);
        var finished = await Wait(browser, accepted.Id);
        Assert.Equal(OperationState.Succeeded, finished.State.KnownValue); Assert.Equal(EffectStatus.None, finished.EffectStatus.KnownValue);
        Assert.Equal(accepted.Id, (await browser.CommitFileImportAsync(preview.Id, "browser-file-commit", preview.TargetETag)).Id);
        Assert.Equal(4, (await browser.GetMusicPreferencesAsync()).GapSeconds);
        await using var downloaded = await browser.DownloadAssetAsync(upload.Asset.Id);
        Assert.Equal(upload.SizeBytes, downloaded.SizeBytes); Assert.Equal(upload.Sha256, downloaded.Sha256);
        using var content = new MemoryStream(); await downloaded.Content.CopyToAsync(content);
        Assert.Equal(bytes, content.ToArray());
        var otherGrant = await host.Pair([ApiPermissions.SystemRead]); using var other = host.Sdk(otherGrant.Token);
        Assert.Equal("resource.notFound", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            other.GetAssetAsync(upload.Asset.Id))).Problem!.Code);
    }

    [Theory]
    [InlineData("mabisync", "settings.json", "{\"nickname\":\"原角色\",\"timingOffsetMs\":35}")]
    [InlineData("color-studio", "settings.json", "{\"search_strategy\":\"screenshot\",\"screenshot_default_revision\":1}")]
    public async Task ZipUploadUsesExistingSettingsPreviewAndCommit(string project, string name, string json)
    {
        using var area = new Workspace(); await using var host = await Host.Start(area); using var sdk = host.Sdk();
        var zipped = Zip([(name, Encoding.UTF8.GetBytes(json), 0)]);
        var upload = await Upload(sdk, "settings.zip", zipped, "application/zip");
        var preview = await sdk.PreviewFileImportAsync(new(project, project + "-zip", "原設定", [], upload.Id), project + "-zip-preview");
        Assert.Equal("ready", preview.Settings!.State);
        var operation = await Wait(sdk, (await sdk.CommitFileImportAsync(preview.Id, project + "-zip-commit", preview.TargetETag)).Id);
        Assert.Equal(OperationState.Succeeded, operation.State.KnownValue);
        Assert.Equal(preview.Id, (await sdk.GetFileImportAsync(preview.Id)).Id);
        Assert.Equal(zipped, await ReadAsset(sdk, upload.Asset!.Id));
    }

    [Fact]
    public async Task UploadValidatesBodyIdentityRevisionPermissionsAndFailureLeavesNoReadyAsset()
    {
        using var area = new Workspace(); await using var host = await Host.Start(area); using var sdk = host.Sdk();
        var bytes = Encoding.UTF8.GetBytes("{\"gap_sec\":2}");
        var created = await sdk.CreateFileUploadAsync(new("settings.json", new((ulong)bytes.Length), Hash(bytes)), "upload-identity-create");
        using var input = new MemoryStream(bytes);
        var ready = await sdk.UploadFileContentAsync(created.Id, input, "upload-identity-content", FileTransferStore.ETag(created));
        Assert.True(input.CanRead);
        input.Position = 0;
        Assert.Equal(ready, await sdk.UploadFileContentAsync(created.Id, input, "upload-identity-content", FileTransferStore.ETag(created)));
        using var altered = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', bytes.Length)));
        Assert.Equal("storage.assetDamaged", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.UploadFileContentAsync(created.Id, altered, "upload-identity-content", FileTransferStore.ETag(created)))).Problem!.Code);
        using var stale = new MemoryStream(bytes);
        Assert.Equal("revision.stale", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.UploadFileContentAsync(created.Id, stale, "upload-identity-stale", FileTransferStore.ETag(created)))).Problem!.Code);
        var invalid = await sdk.CreateFileUploadAsync(new("bad.json", new((ulong)bytes.Length), Hash(bytes)), "upload-damaged-create");
        using var incomplete = new MemoryStream(bytes[..^1]);
        Assert.Equal("storage.assetDamaged", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.UploadFileContentAsync(invalid.Id, incomplete, "upload-damaged-content", FileTransferStore.ETag(invalid)))).Problem!.Code);
        var failed = await sdk.GetFileUploadAsync(invalid.Id); Assert.Equal("failed", failed.State); Assert.Null(failed.Asset);
        var grant = await host.Pair([ApiPermissions.SystemRead, ApiPermissions.FilesTransfer]);
        using var limited = host.Sdk(grant.Token);
        var owned = await Upload(limited, "settings.json", bytes);
        Assert.Equal("permission.denied", (await Assert.ThrowsAsync<MabiToolApiException>(() => limited.PreviewFileImportAsync(
            new("mobifolio", "no-module", "原設定", [owned.Id], Scope: new("TW", "realm", new(1))), "limited-file-preview"))).Problem!.Code);
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.CreateFileUploadAsync(new("settings.json", new((ulong)bytes.Length), "not-a-hash"), "upload-bad-hash"))).Problem!.Code);
    }

    [Theory]
    [InlineData("../settings.json")]
    [InlineData("/settings.json")]
    [InlineData("C:/settings.json")]
    [InlineData("settings.json:stream")]
    [InlineData("NUL.json")]
    [InlineData("folder\\settings.json")]
    public async Task ArchivePathsRejectEscapeBeforeBusinessChanges(string path)
    {
        var bytes = Zip([(path, Encoding.UTF8.GetBytes("{}"), 0)]);
        using var input = new MemoryStream(bytes);
        var error = await Assert.ThrowsAsync<OperationException>(() => FilePackageReader.ReadAsync(input, 8 * 1024 * 1024, default));
        Assert.Equal("storage.pathRejected", error.Code);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("case")]
    [InlineData("unicode")]
    [InlineData("directory-case")]
    [InlineData("link")]
    [InlineData("entries")]
    [InlineData("expanded")]
    [InlineData("actual-expanded")]
    [InlineData("parser-budget")]
    public async Task ArchiveLimitsAndPathAliasesRejectBeforeExtraction(string mode)
    {
        var entries = mode switch
        {
            "same" => new[] { ("settings.json", "{}"u8.ToArray(), 0), ("settings.json", "{}"u8.ToArray(), 0) },
            "case" => [("settings.json", "{}"u8.ToArray(), 0), ("SETTINGS.json", "{}"u8.ToArray(), 0)],
            "unicode" => [("\u00E9.json", "{}"u8.ToArray(), 0), ("e\u0301.json", "{}"u8.ToArray(), 0)],
            "directory-case" => [("A/settings.json", "{}"u8.ToArray(), 0), ("a/config.json", "{}"u8.ToArray(), 0)],
            "link" => [("settings.json", "{}"u8.ToArray(), unchecked((int)0xA1FF0000))],
            "entries" => Enumerable.Range(0, FilePackageReader.MaximumEntries + 1).Select(x => (x + ".json", Array.Empty<byte>(), 0)).ToArray(),
            "parser-budget" => [("settings.json", new byte[1025], 0)],
            _ => [("settings.json", new byte[4096], 0)]
        };
        var zipped = Zip(entries);
        if (mode is "expanded" or "actual-expanded")
        {
            var central = FindCentral(zipped);
            BinaryPrimitives.WriteUInt32LittleEndian(zipped.AsSpan(central + 24), mode == "expanded" ? 1_073_741_825U : 1U);
        }
        using var input = new MemoryStream(zipped);
        var error = await Assert.ThrowsAsync<OperationException>(() => FilePackageReader.ReadAsync(input, mode == "parser-budget" ? 1024 : 8 * 1024 * 1024, default));
        Assert.Equal(mode switch { "expanded" or "entries" or "actual-expanded" => "storage.archiveLimitExceeded",
            "parser-budget" => "request.bodyTooLarge", _ => "storage.pathRejected" }, error.Code);
    }

    [Theory]
    [InlineData("crc")]
    [InlineData("local-name")]
    [InlineData("local-method")]
    [InlineData("both-lengths")]
    public async Task ArchiveChecksActualPayloadAndLocalDirectory(string mode)
    {
        var zipped = Zip([("settings.json", new byte[4096], 0)]);
        var central = FindCentral(zipped);
        switch (mode)
        {
            case "crc": zipped[central + 16] ^= 1; break;
            case "local-name": zipped[30] ^= 1; break;
            case "local-method": BinaryPrimitives.WriteUInt16LittleEndian(zipped.AsSpan(8), 0); break;
            case "both-lengths":
                BinaryPrimitives.WriteUInt32LittleEndian(zipped.AsSpan(22), 1);
                BinaryPrimitives.WriteUInt32LittleEndian(zipped.AsSpan(central + 24), 1); break;
        }
        using var input = new MemoryStream(zipped);
        var error = await Assert.ThrowsAsync<OperationException>(() => FilePackageReader.ReadAsync(input, 8 * 1024 * 1024, default));
        Assert.Equal(mode == "both-lengths" ? "storage.archiveLimitExceeded" : "storage.assetDamaged", error.Code);
    }

    [Fact]
    public async Task ExportFreezesSelectedAssetsRequiresPrivateFileChoiceAndStreamsManifest()
    {
        using var area = new Workspace(); await using var host = await Host.Start(area); using var sdk = host.Sdk();
        var bytes = Encoding.UTF8.GetBytes("{\"theme\":\"dark\"}");
        var source = await Upload(sdk, "settings.json", bytes);
        var request = new FileExportRequest([new(source.Asset!.Id, source.Asset.Revision)]);
        Assert.Equal("storage.scopeDenied", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.ExportFilesAsync(request, "private-export-denied"))).Problem!.Code);
        Assert.Equal(0L, host.Database.ReadScalar("SELECT count(*) FROM platform_file_export_sources"));
        Assert.Equal("revision.stale", (await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.ExportFilesAsync(
            request with { IncludeSelectedFiles = true, Sources = [new(source.Asset.Id, new(0))] }, "private-export-stale"))).Problem!.Code);
        var accepted = await sdk.ExportFilesAsync(request with { IncludeSelectedFiles = true }, "private-export-valid");
        var finished = await Wait(sdk, accepted.Id); Assert.Equal(OperationState.Succeeded, finished.State.KnownValue);
        var artifact = finished.Result!.Value.Deserialize<FileExportArtifact>(ContractJson.Options)!;
        Assert.Equal(artifact.Id, (await sdk.GetFileExportAsync(artifact.Id)).Id);
        Assert.Contains("selected-files", artifact.ContentCategories);
        var another = await Upload(sdk, "settings.json", Encoding.UTF8.GetBytes("{\"theme\":\"light\"}"));
        Assert.NotEqual(source.Asset.Id, another.Asset!.Id);
        using var zipped = new MemoryStream(await ReadAsset(sdk, artifact.Asset.Id));
        using var archive = new ZipArchive(zipped);
        var manifest = (await JsonSerializer.DeserializeAsync<FileExportManifest>(archive.GetEntry("manifest.json")!.Open(), ContractJson.Options))!;
        var entry = Assert.Single(manifest.Files); Assert.Equal(source.Asset.Id, entry.SourceId); Assert.Equal(source.Sha256, entry.Sha256);
        using var payload = new MemoryStream(); await archive.GetEntry(entry.Path)!.Open().CopyToAsync(payload); Assert.Equal(bytes, payload.ToArray());
        Assert.Equal(accepted.Id, (await sdk.ExportFilesAsync(request with { IncludeSelectedFiles = true }, "private-export-valid")).Id);
        Assert.Equal(0L, host.Database.ReadScalar("SELECT count(*) FROM platform_file_export_sources"));
    }

    [Fact]
    public async Task NativeSelectionPreservesSourceAndSaveHandlesExistingAndChangedTargets()
    {
        using var area = new Workspace(); await using var host = await Host.Start(area); using var sdk = host.Sdk();
        var sourcePath = Path.Combine(area.Root, "settings.json"); var bytes = Encoding.UTF8.GetBytes("{\"theme\":\"dark\"}");
        await File.WriteAllBytesAsync(sourcePath, bytes);
        var registry = host.App.Services.GetRequiredService<FileSelectionRegistry>();
        var session = host.App.Services.GetRequiredService<LocalAuthService>().OwnerGrant().Session;
        Assert.False((await sdk.GetFileCapabilitiesAsync()).NativeSelections);
        var selected = await registry.RegisterReadAsync(session, sourcePath);
        Assert.True((await sdk.GetFileCapabilitiesAsync()).NativeSelections);
        var upload = await sdk.UploadFileSelectionAsync(new(selected.Id), "native-selected-upload");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(sourcePath));
        var finished = await Wait(sdk, (await sdk.ExportFilesAsync(
            new([new(upload.Asset!.Id, upload.Asset.Revision)], IncludeSelectedFiles: true), "native-selected-export")).Id);
        var artifact = finished.Result!.Value.Deserialize<FileExportArtifact>(ContractJson.Options)!;
        var target = Path.Combine(area.Root, "export.zip"); await File.WriteAllTextAsync(target, "original");
        var chosen = await registry.RegisterSaveAsync(session, target);
        var saved = await sdk.SaveFileExportAsync(artifact.Id, new(chosen.Id), "native-selected-save");
        Assert.Equal("export (1).zip", saved.Name); Assert.False(saved.Overwritten); Assert.Equal("original", await File.ReadAllTextAsync(target));
        Assert.Equal(saved, await sdk.SaveFileExportAsync(artifact.Id, new(chosen.Id), "native-selected-save"));
        Assert.Equal(artifact.Asset.Sha256, Hash(await File.ReadAllBytesAsync(Path.Combine(area.Root, saved.Name))));
        var overwrite = await registry.RegisterSaveAsync(session, target, true);
        await File.WriteAllTextAsync(target, "changed");
        Assert.Equal("storage.targetChanged", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.SaveFileExportAsync(artifact.Id, new(overwrite.Id), "native-target-changed"))).Problem!.Code);
        Assert.Equal("changed", await File.ReadAllTextAsync(target));
        var fresh = await registry.RegisterSaveAsync(session, target, true);
        Assert.True((await sdk.SaveFileExportAsync(artifact.Id, new(fresh.Id), "native-target-overwrite")).Overwritten);
        Assert.Equal(artifact.Asset.Sha256, Hash(await File.ReadAllBytesAsync(target)));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(sourcePath));
    }

    [Fact]
    public async Task SelectionScopeExpiryAndRestartInvalidateOldGrants()
    {
        using var area = new Workspace(); var clock = new Clock();
        var path = Path.Combine(area.Root, "settings.json"); await File.WriteAllTextAsync(path, "{}");
        string oldId;
        await using (var host = await Host.Start(area, clock))
        {
            using var sdk = host.Sdk(); var registry = host.App.Services.GetRequiredService<FileSelectionRegistry>();
            var session = host.App.Services.GetRequiredService<LocalAuthService>().OwnerGrant().Session;
            var selected = await registry.RegisterReadAsync(session, path); oldId = selected.Id;
            var grant = await host.Pair([ApiPermissions.SystemRead, ApiPermissions.FilesTransfer]); using var other = host.Sdk(grant.Token);
            Assert.Equal("resource.notFound", (await Assert.ThrowsAsync<MabiToolApiException>(() => other.GetFileSelectionAsync(selected.Id))).Problem!.Code);
            var write = await registry.RegisterSaveAsync(session, Path.Combine(area.Root, "new.zip"));
            Assert.Equal("storage.scopeDenied", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
                sdk.UploadFileSelectionAsync(new(write.Id), "wrong-selected-action"))).Problem!.Code);
            clock.Advance(TimeSpan.FromMinutes(6)); clock.JumpWall(TimeSpan.FromHours(-1));
            Assert.Equal("storage.selectionExpired", (await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.GetFileSelectionAsync(selected.Id))).Problem!.Code);
        }
        await using (var host = await Host.Start(area, clock))
        {
            using var sdk = host.Sdk();
            Assert.Equal("resource.notFound", (await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.GetFileSelectionAsync(oldId))).Problem!.Code);
            Assert.Equal("{}", await File.ReadAllTextAsync(path));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadPinBlocksExpiryCleanupAndDisconnectReleasesFile(bool revoke)
    {
        using var area = new Workspace(); var clock = new Clock(); await using var host = await Host.Start(area, clock);
        var grant = await host.Pair([ApiPermissions.SystemRead, ApiPermissions.FilesTransfer]); using var sdk = host.Sdk(grant.Token);
        var bytes = new byte[6 * 1024 * 1024]; RandomNumberGenerator.Fill(bytes);
        var upload = await Upload(sdk, "sample.bin", bytes, "application/octet-stream");
        var read = await sdk.DownloadAssetAsync(upload.Asset!.Id);
        clock.Advance(TimeSpan.FromMinutes(16));
        var files = host.App.Services.GetRequiredService<FileTransferStore>(); var assets = host.App.Services.GetRequiredService<AssetStore>();
        await files.CleanupAsync();
        Assert.NotNull(assets.Get(upload.Asset.Id));
        Assert.True((long)host.Database.ReadScalar("SELECT count(*) FROM platform_asset_pins WHERE owner LIKE 'file-read:%'")! > 0);
        if (revoke)
        {
            host.App.Services.GetRequiredService<LocalAuthService>().Revoke(grant.Session.ClientId);
            using var stopped = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<Exception>(() => read.Content.CopyToAsync(Stream.Null, stopped.Token));
        }
        await read.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((long)host.Database.ReadScalar("SELECT count(*) FROM platform_asset_pins WHERE owner LIKE 'file-read:%'")! != 0)
            await Task.Delay(10, timeout.Token);
        await files.CleanupAsync(); Assert.Null(assets.Get(upload.Asset.Id));
    }

    [Fact]
    public async Task StreamingSdkRejectsOversizedDeclaredDownloadWithoutReadingBody()
    {
        var content = new ByteArrayContent([1]);
        content.Headers.ContentLength = FilePackageReader.MaximumUploadBytes + 1;
        using var sdk = new MabiToolClient(new("http://127.0.0.1:19998/"), _ => ValueTask.FromResult(Token),
            new ResponseHandler(() => new(HttpStatusCode.OK) { Content = content }));
        await Assert.ThrowsAsync<InvalidDataException>(() => sdk.DownloadAssetAsync(new string('a', 32)));
    }

    private static async Task<FileUploadSnapshot> Upload(MabiToolClient sdk, string name, byte[] bytes, string media = "application/json")
    {
        var key = "file-create-" + Guid.NewGuid().ToString("N");
        var created = await sdk.CreateFileUploadAsync(new(name, new((ulong)bytes.Length), Hash(bytes), media), key);
        using var input = new MemoryStream(bytes);
        return await sdk.UploadFileContentAsync(created.Id, input, "file-content-" + Guid.NewGuid().ToString("N"), FileTransferStore.ETag(created));
    }
    private static async Task<byte[]> ReadAsset(MabiToolClient sdk, string id)
    { await using var downloaded = await sdk.DownloadAssetAsync(id); using var bytes = new MemoryStream(); await downloaded.Content.CopyToAsync(bytes); return bytes.ToArray(); }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Zip(IEnumerable<(string Path, byte[] Bytes, int Attributes)> entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var source in entries) { var entry = archive.CreateEntry(source.Path, CompressionLevel.Fastest); entry.ExternalAttributes = source.Attributes; using var stream = entry.Open(); stream.Write(source.Bytes); }
        return output.ToArray();
    }
    private static int FindCentral(byte[] bytes)
    {
        for (var i = 0; i <= bytes.Length - 46; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == 0x02014b50) return i;
        throw new InvalidOperationException("缺少测试 ZIP 目录");
    }
    private static async Task<OperationSnapshot> Wait(MabiToolClient sdk, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true) { var operation = await sdk.GetOperationAsync(id, timeout.Token); if (SqliteStore.IsTerminal(operation.State.KnownValue)) return operation; await Task.Delay(10, timeout.Token); }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow; private long timestamp;
        public override DateTimeOffset GetUtcNow() => now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan elapsed) { now += elapsed; timestamp += elapsed.Ticks; }
        public void JumpWall(TimeSpan elapsed) => now += elapsed;
    }
    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Repository(), "artifacts", "tmp", "files-test-" + Guid.NewGuid().ToString("N"));
        public StorageOptions Storage => new(Path.Combine(Root, "bootstrap"), true, DataRoot: Path.Combine(Root, "data"));
        public Workspace() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            Assert.StartsWith(Path.GetFullPath(Path.Combine(Repository(), "artifacts", "tmp")) + Path.DirectorySeparatorChar, Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase);
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
        public SqliteStore Database => App.Services.GetRequiredService<SqliteStore>();
        public static async Task<Host> Start(Workspace area, TimeProvider? clock = null)
        {
            var app = BackendApplication.Build(new(0, Token, new HashSet<string> { Origin }), clock, area.Storage); await app.StartAsync();
            return new() { App = app, Address = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        }
        public MabiToolClient Sdk(string token = Token, string? origin = null) => new(Address, _ => ValueTask.FromResult(token), origin is null ? null : new OriginHandler(origin));
        public async Task<AccessGrant> Pair(IReadOnlyList<string> permissions, string? origin = null)
        {
            using var sdk = Sdk(); var challenge = await sdk.CreatePairingChallengeAsync(new("files-client", origin, permissions));
            using var redeemer = Sdk(Token, origin); return await redeemer.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code));
        }
        public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); }
    }
    private sealed class OriginHandler(string origin) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { request.Headers.Add("Origin", origin); return base.SendAsync(request, ct); }
    }
    private sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response());
    }
}

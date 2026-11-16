using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.MusicLibrary.Domain;
using MabiTool.Modules.MusicLibrary.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class MusicImportApiTests
{
    private const string Token = "import_tests_0123456789012345678901234567890123456789";
    private const string Origin = "http://localhost:5173";
    private static ImportSourceFile SourceFile(string name, string json) => new(name, Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
    private static MusicImportRequest Request(params ImportSourceFile[] files) => new("old-backup", "原曲庫", "TW", "realm", new(ulong.MaxValue), files);
    private static MusicImportRequest Full()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "tests/Domain/MabiTool.MusicLibrary.Tests/Fixtures/mobifolio-import.v1.json")));
        return Request(fixture.RootElement.EnumerateObject().Select(x => SourceFile(x.Name, x.Value.GetRawText())).ToArray());
    }

    [Fact]
    public async Task SdkImportsAllDataTracksOperationRetriesAndOwnerScopedEvents()
    {
        using var workspace = new Workspace(); await using var host = await Host.Start(workspace);
        using var sdk = host.Sdk(); using var reader = await host.Pair("reader", false);
        var preview = await sdk.PreviewMusicImportAsync(Full(), "sdk-preview");
        Assert.Equal("ready", preview.State);
        ApiResponseMetadata? metadata = null;
        var admitted = await sdk.CommitMusicImportAsync(preview.Id, "sdk-commit", MusicLibraryStore.ETag(preview.ExpectedLibraryRevision), onResponse: value => metadata = value);
        Assert.Equal(202, metadata!.Status);
        Assert.Equal("/api/v1/operations/" + admitted.Id, metadata.Location);
        var completed = await Complete(sdk, admitted);
        Assert.Equal(OperationState.Succeeded, completed.State.KnownValue);
        Assert.Equal(EffectStatus.None, completed.EffectStatus.KnownValue);
        var receipt = completed.Result!.Value.Deserialize<MusicImportReceipt>(ContractJson.Options)!;
        Assert.Equal(preview.Id, receipt.ImportId);
        Assert.Equal(1UL, receipt.LibraryRevision.Value);
        Assert.Equal(admitted.Id, (await sdk.CommitMusicImportAsync(preview.Id, "sdk-commit", MusicLibraryStore.ETag(new(0)))).Id);
        Assert.Equal("committed", (await sdk.GetMusicImportAsync(preview.Id)).State);
        Assert.Equal(preview.Id, (await sdk.PreviewMusicImportAsync(Full(), "sdk-second-preview")).Id);
        Assert.Equal(2, (await sdk.GetMusicPlaybackHistoryAsync()).Items.Count);
        Assert.Equal(4.5, (await sdk.GetMusicPreferencesAsync()).GapSeconds);
        Assert.Equal(2, (await reader.GetMusicPreferencesAsync()).GapSeconds);
        var foreign = await Assert.ThrowsAsync<MabiToolApiException>(() => reader.GetMusicImportAsync(preview.Id));
        Assert.Equal("permission.denied", foreign.Problem!.Code);
        var denied = await Assert.ThrowsAsync<MabiToolApiException>(() => reader.PreviewMusicImportAsync(Full(), "reader-preview"));
        Assert.Equal(403, denied.Status);
        for (var i = 0; i < 100; i++)
        {
            var snapshot = await sdk.GetEventSnapshotAsync(new(["music.import.committed"], [preview.Id], null));
            if (snapshot.Resources.Count > 0) break;
            await Task.Delay(20);
        }
        Assert.Single((await sdk.GetEventSnapshotAsync(new(["music.import.committed"], [preview.Id], null))).Resources);
        Assert.Empty((await reader.GetEventSnapshotAsync(new(["music.import.committed"], [], null))).Resources);
    }

    [Fact]
    public async Task BrowserClientUploadsChunkedFilesAndSharesImportedLibrary()
    {
        using var workspace = new Workspace(); await using var host = await Host.Start(workspace);
        using var owner = host.Sdk();
        var challenge = await owner.CreatePairingChallengeAsync(new("browser", Origin, [ApiPermissions.SystemRead, ApiPermissions.MusicEdit]));
        using var pairing = host.Http(); pairing.DefaultRequestHeaders.TryAddWithoutValidation("Origin", Origin);
        using var pairingResponse = await pairing.PostAsJsonAsync(ApiContract.Pairings.Path, new PairingRequest(challenge.ChallengeId, challenge.Code), ContractJson.Options);
        pairingResponse.EnsureSuccessStatusCode();
        var grant = (await pairingResponse.Content.ReadFromJsonAsync<AccessGrant>(ContractJson.Options))!;
        using var http = host.Http(grant.Token); http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", Origin);
        var source = Request(SourceFile("scores.json", JsonSerializer.Serialize(new { items = Enumerable.Range(0, 2000).Select(x => new { Name = "原曲名稱 " + x + new string('x', 50) }) })));
        var body = JsonSerializer.SerializeToUtf8Bytes(source, ContractJson.Options);
        Assert.True(body.Length > 64 * 1024);
        using var post = new HttpRequestMessage(HttpMethod.Post, ApiContract.PreviewMusicImport.Path) { Content = new ChunkedContent(body) };
        post.Headers.Add("Idempotency-Key", "browser-preview");
        using var result = await http.SendAsync(post);
        Assert.Equal(HttpStatusCode.Created, result.StatusCode);
        Assert.Equal(Origin, result.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var preview = (await result.Content.ReadFromJsonAsync<MusicImportPreview>(ContractJson.Options))!;
        Assert.Equal(2000, preview.Counts.Scores);
        using var commit = new HttpRequestMessage(HttpMethod.Post, ApiContract.CommitMusicImport.Path.Replace("{id}", preview.Id)) { Content = JsonContent.Create(new { }) };
        commit.Headers.Add("Idempotency-Key", "browser-commit"); commit.Headers.Add("If-Match", MusicLibraryStore.ETag(new(0)));
        using var response = await http.SendAsync(commit);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var operation = (await response.Content.ReadFromJsonAsync<OperationSnapshot>(ContractJson.Options))!;
        Assert.Equal(OperationState.Succeeded, (await Complete(owner, operation)).State.KnownValue);
        var page = await owner.QueryMusicLibraryAsync(new(MusicCollection.Scores, null, null, preview.ScopeKey, "", 200, null));
        Assert.Equal(200, page.Scores.Count); Assert.NotNull(page.NextCursor);
        using var other = await host.Pair("other-editor", true);
        Assert.Equal("resource.notFound", (await Assert.ThrowsAsync<MabiToolApiException>(() => other.GetMusicImportAsync(preview.Id))).Problem!.Code);
    }

    [Fact]
    public async Task LargePreviewUsesSpecificSdkResponseBudget()
    {
        using var workspace = new Workspace(); await using var host = await Host.Start(workspace);
        using var sdk = host.Sdk();
        var request = Request(SourceFile("instruments.json", JsonSerializer.Serialize(new { items = new[] { new { Name = "鋼琴", note = new string('x', 4 * 1024 * 1024 + 128) } } })));
        var preview = await sdk.PreviewMusicImportAsync(request, "large-response-preview");
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(preview, ContractJson.Options).Length > 4 * 1024 * 1024);
        Assert.Equal(preview.Id, (await sdk.GetMusicImportAsync(preview.Id)).Id);
        Assert.Equal("piano", Assert.Single(preview.Instruments).InstrumentId);
    }

    [Fact]
    public async Task AuthenticationPrecedesImportBudgetAndOrdinaryRequestsKeep64KiB()
    {
        using var workspace = new Workspace(); await using var host = await Host.Start(workspace);
        using var denied = host.Http("invalid"); denied.DefaultRequestHeaders.ExpectContinue = true;
        using var oversized = new HttpRequestMessage(HttpMethod.Post, ApiContract.PreviewMusicImport.Path)
        { Content = new ByteArrayContent(new byte[MobiFolioImport.MaximumRequestBytes + 1]) };
        oversized.Content.Headers.ContentType = new("application/json");
        using var unauthorized = await denied.SendAsync(oversized);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var owner = host.Http(); owner.DefaultRequestHeaders.ExpectContinue = true;
        using var tooLarge = new HttpRequestMessage(HttpMethod.Post, ApiContract.PreviewMusicImport.Path)
        { Content = new ByteArrayContent(new byte[MobiFolioImport.MaximumRequestBytes + 1]) };
        tooLarge.Content.Headers.ContentType = new("application/json");
        using var sizeError = await owner.SendAsync(tooLarge);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, sizeError.StatusCode);
        var batch = new MusicEditBatch([new("f", null, new string('x', 70_000), "1")], [], [], [], null);
        using var ordinary = new HttpRequestMessage(HttpMethod.Post, ApiContract.EditMusicLibrary.Path)
        { Content = new ChunkedContent(JsonSerializer.SerializeToUtf8Bytes(batch, ContractJson.Options)) };
        ordinary.Headers.Add("Idempotency-Key", "ordinary-too-large"); ordinary.Headers.Add("If-Match", MusicLibraryStore.ETag(new(0)));
        using var normalError = await owner.SendAsync(ordinary);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, normalError.StatusCode);
    }

    [Fact]
    public async Task QueuedCancellationPreventsCommitAndApplyingCancellationFinishesTransaction()
    {
        using var workspace = new Workspace(); await using var host = await Host.Start(workspace);
        using var sdk = host.Sdk();
        var preview = await sdk.PreviewMusicImportAsync(Request(SourceFile("settings.json", "{}")), "cancellation-preview");
        var operations = host.App.Services.GetRequiredService<OperationStore>();
        var imports = host.App.Services.GetRequiredService<MusicImportStore>();
        var first = await operations.AdmitImportAsync("owner", preview.Id, "queued-cancel", MusicLibraryStore.ETag(new(0)), true);
        Assert.Equal(OperationState.Cancelled, (await operations.CancelAsync(first.Snapshot.Id, "owner", false, "user-cancelled")).State.KnownValue);
        Assert.Null(await operations.BeginImportAsync(first.Snapshot.Id));
        Assert.Null(imports.Receipt("owner", preview.Id));
        var second = await operations.AdmitImportAsync("owner", preview.Id, "applying-cancel", MusicLibraryStore.ETag(new(0)), true);
        Assert.NotNull(await operations.BeginImportAsync(second.Snapshot.Id));
        var cancelled = await operations.CancelAsync(second.Snapshot.Id, "owner", false, "user-cancelled");
        Assert.Equal(CancellationState.Deferred, cancelled.Cancellation.State.KnownValue);
        var receipt = await imports.CommitAsync("owner", preview.Id, "applying-cancel", MusicLibraryStore.ETag(new(0)));
        var finished = await operations.FinishImportAsync(second.Snapshot.Id, receipt);
        Assert.Equal(OperationState.Succeeded, finished.State.KnownValue);
        Assert.Equal(CancellationState.Completed, finished.Cancellation.State.KnownValue);
        Assert.Equal(1UL, receipt.LibraryRevision.Value);
    }

    [Fact]
    public async Task StalePreviewIsRejectedBeforeOperationAdmission()
    {
        using var workspace = new Workspace(); await using var host = await Host.Start(workspace);
        using var sdk = host.Sdk();
        var preview = await sdk.PreviewMusicImportAsync(Request(SourceFile("settings.json", "{}")), "stale-api-preview");
        await sdk.EditMusicLibraryAsync(new([new("f", null, "f", "0")], [], [], [], null), "stale-api-edit", MusicLibraryStore.ETag(new(0)));
        var error = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.CommitMusicImportAsync(preview.Id, "stale-api-commit", MusicLibraryStore.ETag(new(0))));
        Assert.Equal("revision.stale", error.Problem!.Code);
        Assert.Equal(0L, host.App.Services.GetRequiredService<SqliteStore>().ReadScalar("SELECT count(*) FROM core_operations WHERE json_extract(snapshot,'$.kind')='music.import'"));
    }

    private static async Task<OperationSnapshot> Complete(MabiToolClient sdk, OperationSnapshot operation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!SqliteStore.IsTerminal(operation.State.KnownValue))
        { await Task.Delay(20, deadline.Token); operation = await sdk.GetOperationAsync(operation.Id, deadline.Token); }
        return operation;
    }
    private sealed class ChunkedContent : HttpContent
    {
        private readonly byte[] bytes;
        public ChunkedContent(byte[] bytes) { this.bytes = bytes; Headers.ContentType = new("application/json"); }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required Uri Address { get; init; }
        public static async Task<Host> Start(Workspace workspace)
        {
            var app = BackendApplication.Build(new(0, Token, new HashSet<string> { Origin }), storage: workspace.Options);
            await app.StartAsync();
            return new() { App = app, Address = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        }
        public MabiToolClient Sdk(string token = Token) => new(Address, _ => ValueTask.FromResult(token));
        public HttpClient Http(string token = Token)
        { var http = new HttpClient { BaseAddress = Address }; http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return http; }
        public async Task<MabiToolClient> Pair(string name, bool edit)
        {
            using var owner = Sdk();
            var challenge = await owner.CreatePairingChallengeAsync(new(name, null, edit ? [ApiPermissions.SystemRead, ApiPermissions.MusicEdit] : [ApiPermissions.SystemRead]));
            return Sdk((await owner.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code))).Token);
        }
        public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); }
    }
    private static string Root()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MabiTool.slnx"))) root = root.Parent;
        return root!.FullName;
    }
    private sealed class Workspace : IDisposable
    {
        private readonly string directory = Path.Combine(Root(), "artifacts", "tmp", "music-import-api", Guid.NewGuid().ToString("N"));
        public StorageOptions Options => new(Path.Combine(directory, "bootstrap"), true, Path.Combine(directory, "data"));
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}

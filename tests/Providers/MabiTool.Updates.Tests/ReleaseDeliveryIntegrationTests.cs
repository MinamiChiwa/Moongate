using System.Net;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Updates;
using MabiTool.Providers.GitHubReleases;
using Xunit;
using Fixture = MabiTool.Updates.Tests.SignedReleaseFixture;

namespace MabiTool.Updates.Tests;

public sealed class ReleaseDeliveryIntegrationTests
{
    [Theory]
    [InlineData("direct")]
    [InlineData("api")]
    [InlineData("relay")]
    public async Task Source_chain_delivers_signed_release_and_actual_target_tree(string mode)
    {
        using var f = new Fixture(buildArtifacts: true);
        var manifestBytes = f.Release();
        var handler = new DeliveryHandler(f, manifestBytes, mode);
        using var provider = new ReleaseIndexSourceProvider(handler, retryCount: 0);
        var settings = new UpdateSourceSettings("owner/project", "stable", null, mode == "relay" ? "https://relay.example/" : null);
        var rawIndex = await provider.FetchChannelIndexAsync(settings);
        var index = f.Store.VerifyChannelIndex(SignedPayloadEnvelope.Parse(rawIndex.Content), "mabitool", "stable", f.Now);
        var retrieved = await provider.FetchReleaseManifestAsync(settings, index.TargetManifestSha256, index.TargetManifestLength);
        var release = f.Store.VerifyReleaseManifest(retrieved.Content, index, "win-x64", f.Now);
        var planner = new UpdatePlanner(new Clock(f.Now));
        var context = new UpdatePlannerContext(new(1, 0, 0), 1, 1, "win-x64", new(10, 0, 19045), 100_000_000, []);
        var runtime = new ReleaseRuntimeContext(new(1, 0, 0), 1, "self-contained");
        var plan = planner.CreatePlan(release, context, runtime);
        planner.ValidatePlan(plan, release, context, runtime);
        Assert.Equal(release.Manifest.ManifestSha256, plan.ManifestSha256);
        var budget = new UpdateIoBudget();
        using var downloader = new ResumableAssetDownloader(new ForwardHandler(handler), new() { RetriesPerSource = 0 }, budget: budget);
        var stage = await new FileTreeStager(downloader, budget).StageAsync(f.Path("current"), f.Path("stage"), f.Path("cache"), release.Components["core"].Artifacts,
            item => provider.BuildReleaseAssetSources(settings, release.Manifest.ReleaseId, item.DownloadSha256)
                .Select(endpoint => new AssetDownloadSource(endpoint.Source.ToString(), endpoint.Uri, endpoint.Relocate, endpoint.Resolve)).ToArray());
        Assert.Equal(1, stage.ReusedFiles);
        Assert.Equal("new", File.ReadAllText(f.Path("stage/app")));
        Assert.Equal("old", File.ReadAllText(f.Path("current/app")));
        Assert.Equal(mode switch { "direct" => UpdateSourceKind.GithubDirect, "api" => UpdateSourceKind.GithubApi, _ => UpdateSourceKind.Relay }, retrieved.Source);
        if (mode == "direct") Assert.All(handler.Requests, uri => Assert.Equal("github.com", uri.Host));
        if (mode == "api") Assert.DoesNotContain(handler.Requests, uri => uri.Host == "relay.example");
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsolutePath.EndsWith('/'));
    }

    [Fact]
    public async Task Expired_api_asset_location_relocates_same_hash_and_reuses_shared_catalog()
    {
        using var f = new Fixture(buildArtifacts: true);
        var handler = new DeliveryHandler(f, f.Release(), "api") { ExpireFirstBlob = true };
        using var provider = new ReleaseIndexSourceProvider(handler, retryCount: 0);
        var settings = new UpdateSourceSettings("owner/project", "stable", null, null);
        var file = f.Artifacts.Files.Single(item => item.File.RelativePath == "app");
        var sources = provider.BuildReleaseAssetSources(settings, "release-1", file.DownloadSha256)
            .Select(endpoint => new AssetDownloadSource(endpoint.Source.ToString(), endpoint.Uri, endpoint.Relocate, endpoint.Resolve)).ToArray();
        using var downloader = new ResumableAssetDownloader(new ForwardHandler(handler), new() { RetriesPerSource = 1 }, delay: (_, _) => Task.CompletedTask);
        var result = await downloader.DownloadAsync(new(sources, f.Path("cache/app.blob"), file.DownloadLength, file.DownloadSha256));
        Assert.Equal(UpdateSourceKind.GithubApi.ToString(), result.SourceId);
        Assert.Contains(handler.Requests, uri => uri.AbsolutePath.EndsWith("/101", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, uri => uri.AbsolutePath.EndsWith("/201", StringComparison.Ordinal));
        Assert.Equal(2, handler.Requests.Count(uri => uri.AbsolutePath.EndsWith("/tags/release-1", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Failed_lazy_resolution_can_fall_back_and_cancellation_stops_before_request()
    {
        using var workspace = new UpdateTestWorkspace();
        var bytes = "target"u8.ToArray();
        var calls = 0;
        using var downloader = new ResumableAssetDownloader(new SimpleHandler(_ => { calls++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; }),
            new() { RetriesPerSource = 0 });
        AssetDownloadSource[] sources = [new("api", new("https://api.github.com/"), Resolve: _ => throw new HttpRequestException("missing")), new("relay", new("https://relay.example/blob"))];
        var result = await downloader.DownloadAsync(new(sources, workspace.PathFor("blob"), bytes.Length, Fixture.Hash(bytes)));
        Assert.Equal("relay", result.SourceId);
        Assert.Equal(1, calls);
        using var cancel = new CancellationTokenSource();
        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = downloader.DownloadAsync(new([new("api", new("https://api.github.com/"), Resolve: async token =>
        {
            resolving.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return new("https://api.github.com/asset");
        })], workspace.PathFor("cancelled"), bytes.Length, Fixture.Hash(bytes)), cancel.Token);
        await resolving.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, calls);
        Assert.False(File.Exists(workspace.PathFor("cancelled")));
    }

    [Fact]
    public async Task Resolved_url_still_obeys_registered_host_restrictions()
    {
        using var workspace = new UpdateTestWorkspace();
        using var downloader = new ResumableAssetDownloader(new SimpleHandler(_ => throw new InvalidOperationException("不得请求未登记主机")), new() { RetriesPerSource = 0 });
        var error = await Assert.ThrowsAsync<AssetDownloadException>(() => downloader.DownloadAsync(new(
            [new("api", new("https://api.github.com/"), Resolve: _ => Task.FromResult(new Uri("https://evil.example/x")))],
            workspace.PathFor("blob"), 0, Fixture.Hash([]))));
        Assert.Equal("update.sourceUriInvalid", error.Code);
    }

    private sealed class SimpleHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
    private sealed class ForwardHandler(DeliveryHandler delivery) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(delivery.Respond(request));
    }
    private sealed class DeliveryHandler : HttpMessageHandler
    {
        private readonly Fixture f;
        private readonly byte[] manifest;
        private readonly byte[] index;
        private readonly string mode;
        private readonly Dictionary<string, byte[]> contents;
        internal List<Uri> Requests { get; } = [];
        internal bool ExpireFirstBlob { get; init; }
        private bool expired;
        internal DeliveryHandler(Fixture fixture, byte[] manifest, string mode)
        {
            f = fixture; this.manifest = manifest; this.mode = mode;
            index = JsonSerializer.SerializeToUtf8Bytes(f.Index(manifest), Fixture.Options);
            contents = f.Artifacts.Files.ToDictionary(item => item.DownloadSha256 + ".blob", item => File.ReadAllBytes(f.Path("built/blobs/" + item.DownloadSha256 + ".blob")));
            contents.Add(Fixture.Hash(manifest) + ".manifest.json", manifest);
        }
        internal HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            HttpResponseMessage result;
            if (uri.Host == "github.com" && mode != "direct" || uri.Host == "api.github.com" && mode == "relay") result = Status(HttpStatusCode.NotFound);
            else if (uri.Host == "api.github.com")
            {
                if (uri.AbsolutePath.EndsWith("/tags/channel-stable", StringComparison.Ordinal)) result = Json(new { id = 42, tag_name = "channel-stable", assets = new[] { new { name = "channel-index.json", url = "https://api.github.com/repos/owner/project/releases/assets/99" } } });
                else if (uri.AbsolutePath.EndsWith("/tags/release-1", StringComparison.Ordinal)) result = Json(new { id = 43, tag_name = "release-1" });
                else if (uri.Query.Length > 0)
                {
                    var names = contents.Keys.Where(name => uri.AbsolutePath.Contains("/42/", StringComparison.Ordinal) == name.EndsWith(".manifest.json", StringComparison.Ordinal)).ToArray();
                    var firstId = uri.AbsolutePath.Contains("/42/", StringComparison.Ordinal) ? 501 : expired ? 201 : 101;
                    result = Json(names.Select((name, i) => new { id = firstId + i, name, state = "uploaded" }));
                }
                else if (uri.AbsolutePath.EndsWith("/99", StringComparison.Ordinal)) result = Bytes(index);
                else
                {
                    var id = int.Parse(uri.Segments[^1], System.Globalization.CultureInfo.InvariantCulture);
                    var names = contents.Keys.Where(name => name.EndsWith(".blob", StringComparison.Ordinal)).ToArray();
                    if (id == 501) result = Bytes(manifest);
                    else if (ExpireFirstBlob && !expired) { expired = true; result = Status(HttpStatusCode.Gone); }
                    else if (id >= 201) result = Bytes(contents[names[id - 201]]);
                    else result = Bytes(contents[names[id - 101]]);
                }
            }
            else if (uri.AbsolutePath.EndsWith("channel-index.json", StringComparison.Ordinal) || uri.AbsolutePath == "/v1/channels/stable") result = Bytes(index);
            else if (uri.AbsolutePath.StartsWith("/v1/manifests/", StringComparison.Ordinal)) result = Bytes(manifest);
            else if (uri.AbsolutePath.StartsWith("/v1/blobs/", StringComparison.Ordinal)) result = Bytes(contents[uri.Segments[^1] + ".blob"]);
            else result = Bytes(contents[uri.Segments[^1]]);
            result.RequestMessage = request;
            return result;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Respond(request));
        private static HttpResponseMessage Json(object value) => Bytes(JsonSerializer.SerializeToUtf8Bytes(value));
        private static HttpResponseMessage Bytes(byte[] value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };
        private static HttpResponseMessage Status(HttpStatusCode value) => new(value) { Content = new ByteArrayContent([]) };
    }
}

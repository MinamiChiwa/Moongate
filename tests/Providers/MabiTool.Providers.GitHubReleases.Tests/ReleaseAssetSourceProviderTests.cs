using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Providers.GitHubReleases;
using Xunit;

namespace MabiTool.Providers.GitHubReleases.Tests;

public sealed class ReleaseAssetSourceProviderTests
{
    private static readonly UpdateSourceSettings Settings = new("Mobi-Lan/MobiFolio", "stable", null, null);
    private static readonly byte[] Manifest = "signed-manifest-envelope"u8.ToArray();
    private static readonly string Hash = Digest(Manifest);
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task Direct_manifest_is_bound_to_original_bytes_and_skips_fallbacks()
    {
        var handler = new Handler(request =>
        {
            Assert.EndsWith("/channel-stable/" + Hash + ".manifest.json", request.RequestUri!.AbsolutePath);
            return Bytes(Manifest);
        });
        using var provider = new ReleaseIndexSourceProvider(handler, retryCount: 0);
        var result = await provider.FetchReleaseManifestAsync(Settings with { RelayBaseUri = "https://relay.example/" }, Hash, Manifest.Length);
        Assert.Equal(Manifest, result.Content);
        Assert.Equal(UpdateSourceKind.GithubDirect, result.Source);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("overlong")]
    public async Task Corrupt_direct_manifest_falls_back_to_same_pinned_identity(string problem)
    {
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.Host == "github.com")
            {
                var bytes = problem == "hash" ? Enumerable.Repeat((byte)'x', Manifest.Length).ToArray() : [.. Manifest, 1];
                var result = Bytes(bytes);
                if (problem == "overlong") result.Content.Headers.ContentLength = null;
                return result;
            }
            return Api(request, "channel-stable", Hash + ".manifest.json", Manifest);
        });
        using var provider = new ReleaseIndexSourceProvider(handler, retryCount: 0);
        var result = await provider.FetchReleaseManifestAsync(Settings, Hash, Manifest.Length);
        Assert.Equal(UpdateSourceKind.GithubApi, result.Source);
        Assert.Equal(Manifest, result.Content);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Empty_relay_makes_no_request_and_configured_relay_is_last()
    {
        var handler = new Handler(request => request.RequestUri!.Host == "relay.example" ? Bytes(Manifest) : Status(HttpStatusCode.NotFound));
        using var provider = new ReleaseIndexSourceProvider(handler, retryCount: 0);
        var error = await Assert.ThrowsAsync<UpdateSourceException>(() => provider.FetchReleaseManifestAsync(Settings, Hash, Manifest.Length));
        Assert.Equal("update.allSourcesFailed", error.Code);
        Assert.Equal(2, handler.Requests.Count);
        var result = await provider.FetchReleaseManifestAsync(Settings with { RelayBaseUri = "https://relay.example/prefix/" }, Hash, Manifest.Length);
        Assert.Equal(UpdateSourceKind.Relay, result.Source);
        Assert.Equal("/prefix/v1/manifests/" + Hash, result.RetrievedFrom.AbsolutePath);
        Assert.Equal(new[] { "github.com", "api.github.com", "github.com", "api.github.com", "relay.example" }, handler.Requests.Select(uri => uri.Host));
    }

    [Fact]
    public async Task Sources_are_lazy_and_parallel_resolvers_share_one_catalog()
    {
        var handler = new Handler(request => Api(request, "release-1", Hash + ".blob", []));
        using var provider = new ReleaseIndexSourceProvider(handler, retryCount: 0);
        var sources = provider.BuildReleaseAssetSources(Settings, "release-1", Hash);
        Assert.Empty(handler.Requests);
        Assert.Equal(new[] { UpdateSourceKind.GithubDirect, UpdateSourceKind.GithubApi }, sources.Select(item => item.Source));
        Assert.Null(sources[0].Resolve);
        var resolved = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => sources[1].Resolve!(default)));
        Assert.All(resolved, uri => Assert.Equal("https://api.github.com/repos/Mobi-Lan/MobiFolio/releases/assets/7", uri.AbsoluteUri));
        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/release-1/" + Hash + ".blob", sources[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task Cancelling_catalog_waiter_preserves_loader_and_shared_result()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var provider = new ReleaseIndexSourceProvider(new AsyncHandler(async (request, token) =>
        {
            Interlocked.Increment(ref calls);
            if (request.RequestUri!.AbsolutePath.Contains("/tags/", StringComparison.Ordinal))
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return Api(request, "release-1", Hash + ".blob", []);
        }), retryCount: 0);
        var source = provider.BuildReleaseAssetSources(Settings, "release-1", Hash)[1];
        var loader = source.Resolve!(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource();
        var waiter = source.Resolve!(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        release.TrySetResult();
        var uri = await loader.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(uri, await source.Resolve!(default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Catalog_eviction_refetches_old_release_and_retains_recent_release()
    {
        var handler = new Handler(request =>
        {
            var tag = request.RequestUri!.Segments[^1];
            return request.RequestUri.AbsolutePath.Contains("/tags/", StringComparison.Ordinal)
                ? Json(new { id = 42, tag_name = tag }) : Json(new[] { new { id = 7, name = Hash + ".blob", state = "uploaded" } });
        });
        using var provider = new ReleaseIndexSourceProvider(handler, retryCount: 0);
        for (var i = 1; i <= 5; i++)
            await provider.BuildReleaseAssetSources(Settings, "release-" + i, Hash)[1].Resolve!(default);
        Assert.Equal(10, handler.Requests.Count);
        await provider.BuildReleaseAssetSources(Settings, "release-5", Hash)[1].Resolve!(default);
        Assert.Equal(10, handler.Requests.Count);
        await provider.BuildReleaseAssetSources(Settings, "release-1", Hash)[1].Resolve!(default);
        Assert.Equal(12, handler.Requests.Count);
    }

    [Fact]
    public void Relay_blob_uses_content_identity_under_configured_base_path()
    {
        var handler = new Handler(_ => throw new InvalidOperationException());
        using var provider = new ReleaseIndexSourceProvider(handler);
        var sources = provider.BuildReleaseAssetSources(Settings with { RelayBaseUri = "https://relay.example/prefix/" }, "release-1", Hash);
        Assert.Equal("https://relay.example/prefix/v1/blobs/" + Hash, sources[^1].Uri.AbsoluteUri);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Paged_catalog_finds_later_assets_and_ignores_remote_urls()
    {
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/tags/", StringComparison.Ordinal)) return Json(new { id = 42, tag_name = "release-1" });
            if (request.RequestUri.Query.EndsWith("&page=1", StringComparison.Ordinal))
                return Json(Enumerable.Range(1, 100).Select(id => new { id, name = "unrelated-" + id, state = "uploaded" }));
            return Json(new[] { new { id = 7, name = Hash + ".blob", state = "uploaded", url = "https://127.0.0.1/private", browser_download_url = "https://evil.example/x" } });
        });
        using var provider = new ReleaseIndexSourceProvider(handler, retryCount: 0);
        var uri = await provider.BuildReleaseAssetSources(Settings, "release-1", Hash)[1].Resolve!(default);
        Assert.Equal("/repos/Mobi-Lan/MobiFolio/releases/assets/7", uri.AbsolutePath);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("api.github.com", request.Host));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("not-uploaded")]
    [InlineData("id-type")]
    [InlineData("wrong-tag")]
    [InlineData("json")]
    [InlineData("array")]
    public async Task Invalid_catalog_cannot_locate_assets(string problem)
    {
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/tags/", StringComparison.Ordinal))
                return problem == "wrong-tag" ? Json(new { id = 42, tag_name = "other" }) : problem == "json" ? Bytes("{"u8.ToArray()) : Json(new { id = 42, tag_name = "release-1" });
            var entry = new { id = 7, name = Hash + ".blob", state = problem == "not-uploaded" ? "starter" : "uploaded" };
            return problem switch
            {
                "duplicate" => Json(new[] { entry, entry }),
                "id-type" => Json(new[] { new { id = "7", name = Hash + ".blob", state = "uploaded" } }),
                "array" => Json(new { error = true }),
                _ => Json(new[] { entry })
            };
        });
        using var provider = new ReleaseIndexSourceProvider(handler, retryCount: 0);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.BuildReleaseAssetSources(Settings, "release-1", Hash)[1].Resolve!(default));
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsolutePath.Contains("/assets/7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cache_expires_and_relocation_forces_same_asset_lookup()
    {
        var clock = new ManualClock();
        var generation = 7;
        var handler = new Handler(request => Api(request, "release-1", Hash + ".blob", [], generation));
        using var provider = new ReleaseIndexSourceProvider(handler, clock, retryCount: 0);
        var source = provider.BuildReleaseAssetSources(Settings, "release-1", Hash)[1];
        Assert.EndsWith("/7", (await source.Resolve!(default)).AbsolutePath);
        generation = 8;
        Assert.EndsWith("/7", (await source.Resolve!(default)).AbsolutePath);
        Assert.EndsWith("/8", (await source.Relocate!(default)).AbsolutePath);
        generation = 9;
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.EndsWith("/9", (await source.Resolve!(default)).AbsolutePath);
        Assert.Equal(6, handler.Requests.Count);
    }

    [Fact]
    public async Task New_manifest_cache_miss_refreshes_channel_catalog()
    {
        var current = Hash;
        var handler = new Handler(request => Api(request, "channel-stable", current + ".manifest.json", Manifest));
        using var provider = new ReleaseIndexSourceProvider(handler, retryCount: 0);
        // 强制经 API 获取旧清单，再刷新同一渠道下的新清单。
        handler.DirectFails = true;
        await provider.FetchReleaseManifestAsync(Settings, Hash, Manifest.Length);
        var next = "next-signed-manifest"u8.ToArray();
        current = Digest(next);
        handler.AssetContent = next;
        var result = await provider.FetchReleaseManifestAsync(Settings, current, next.Length);
        Assert.Equal(next, result.Content);
        Assert.Equal(8, handler.Requests.Count);
    }

    [Fact]
    public async Task Api_rate_limit_uses_bounded_retry_after_and_can_recover()
    {
        var calls = 0;
        var handler = new Handler(request =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                var limited = Status(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new(TimeSpan.FromMinutes(10));
                return limited;
            }
            return Api(request, "release-1", Hash + ".blob", []);
        });
        var waits = new List<TimeSpan>();
        using var provider = new ReleaseIndexSourceProvider(handler, retryCount: 1, delay: (duration, _) => { waits.Add(duration); return Task.CompletedTask; });
        await provider.BuildReleaseAssetSources(Settings, "release-1", Hash)[1].Resolve!(default);
        Assert.Equal(new[] { TimeSpan.FromSeconds(60) }, waits);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Header_timeout_and_caller_cancellation_are_bounded()
    {
        using var provider = new ReleaseIndexSourceProvider(new BlockingHandler(), retryCount: 0,
            timeouts: new(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)));
        var error = await Assert.ThrowsAsync<UpdateSourceException>(() => provider.FetchReleaseManifestAsync(Settings, Hash, Manifest.Length));
        Assert.All(error.Failures, item => Assert.Equal("update.metadataTimeout", item.Code));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchReleaseManifestAsync(Settings, Hash, Manifest.Length, cancellation.Token));
    }

    [Fact]
    public async Task Body_idle_timeout_and_total_backoff_budget_return_stable_failures()
    {
        using (var provider = new ReleaseIndexSourceProvider(new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) }), retryCount: 0,
            timeouts: new(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50))))
        {
            var error = await Assert.ThrowsAsync<UpdateSourceException>(() => provider.FetchReleaseManifestAsync(Settings, Hash, Manifest.Length));
            Assert.Equal(2, error.Failures.Count);
            Assert.All(error.Failures, failure => Assert.Equal("update.metadataTimeout", failure.Code));
        }
        using var limited = new ReleaseIndexSourceProvider(new Handler(_ =>
        {
            var result = Status(HttpStatusCode.TooManyRequests);
            result.Headers.RetryAfter = new(TimeSpan.FromSeconds(60)); return result;
        }), retryCount: 1, timeouts: new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)));
        Assert.Equal("update.metadataTimeout", (await Assert.ThrowsAsync<UpdateSourceException>(() => limited.FetchChannelIndexAsync(Settings))).Code);
    }

    [Fact]
    public async Task Explicit_api_base_is_used_to_construct_asset_endpoint()
    {
        var handler = new Handler(request => Api(request, "release-1", Hash + ".blob", []));
        using var provider = new ReleaseIndexSourceProvider(handler, retryCount: 0);
        var source = provider.BuildReleaseAssetSources(Settings with { ApiBaseUri = "https://api-mirror.example/github/" }, "release-1", Hash)[1];
        var uri = await source.Resolve!(default);
        Assert.Equal("https://api-mirror.example/github/repos/Mobi-Lan/MobiFolio/releases/assets/7", uri.AbsoluteUri);
        Assert.All(handler.Requests, item => Assert.Equal("api-mirror.example", item.Host));
    }

    [Theory]
    [InlineData("../release")]
    [InlineData("/absolute")]
    [InlineData("release?query")]
    public async Task Invalid_release_identifier_never_reaches_network(string release)
    {
        var handler = new Handler(_ => throw new InvalidOperationException());
        using var provider = new ReleaseIndexSourceProvider(handler);
        Assert.Throws<UpdateSourceException>(() => provider.BuildReleaseAssetSources(Settings, release, Hash));
        await Assert.ThrowsAsync<UpdateSourceException>(() => provider.FetchReleaseManifestAsync(Settings, "../hash", 1));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Relay_and_api_redirects_cannot_escape_configured_hosts()
    {
        var handler = new Handler(request => request.RequestUri!.Host == "github.com" || request.RequestUri.Host == "api.github.com"
            ? Status(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/x") } });
        using var provider = new ReleaseIndexSourceProvider(handler, retryCount: 0);
        var error = await Assert.ThrowsAsync<UpdateSourceException>(() => provider.FetchReleaseManifestAsync(Settings with { RelayBaseUri = "https://relay.example" }, Hash, Manifest.Length));
        Assert.Contains(error.Failures, failure => failure.Code == "update.redirectHostRejected");
        Assert.Equal(3, handler.Requests.Count);
    }

    private static HttpResponseMessage Api(HttpRequestMessage request, string tag, string name, byte[] content, int id = 7)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.Contains("/tags/", StringComparison.Ordinal)) return Json(new { id = 42, tag_name = tag });
        if (request.RequestUri.Query.Length > 0) return Json(new[] { new { id, name, state = "uploaded" } });
        Assert.Contains("/releases/assets/", path);
        Assert.Contains("application/octet-stream", request.Headers.Accept.ToString());
        return Bytes(content);
    }
    private static HttpResponseMessage Json(object body) => Bytes(JsonSerializer.SerializeToUtf8Bytes(body));
    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];
        internal bool DirectFails { get; set; }
        internal byte[]? AssetContent { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!);
            var result = DirectFails && request.RequestUri!.Host == "github.com" ? Status(HttpStatusCode.NotFound) : response(request);
            if (AssetContent is not null && request.RequestUri!.AbsolutePath.Contains("/assets/7", StringComparison.Ordinal)) result.Content = new ByteArrayContent(AssetContent);
            result.RequestMessage = request;
            return Task.FromResult(result);
        }
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException(); }
    }
    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        internal void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }
}

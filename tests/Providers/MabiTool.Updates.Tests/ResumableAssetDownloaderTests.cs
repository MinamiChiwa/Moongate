using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Infrastructure.Updates;
using Xunit;

namespace MabiTool.Updates.Tests;

public sealed class ResumableAssetDownloaderTests
{
    [Fact]
    public async Task Downloads_and_verifies_asset()
    {
        var bytes = "abcdef"u8.ToArray();
        using var client = Client(_ => Response(HttpStatusCode.OK, bytes, "\"v1\""));
        using var temp = new TempDirectory();
        var result = await Downloader(client).DownloadAsync(Request(temp, bytes));
        Assert.False(result.Resumed);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(AssetPath(temp)));
    }

    [Fact]
    public async Task Appends_only_matching_range_and_strong_validator()
    {
        var bytes = "abcdef"u8.ToArray();
        using var temp = new TempDirectory();
        await SeedPartial(temp, bytes, "abc", "\"v1\"");
        using var client = Client(request =>
        {
            Assert.Equal(3, request.Headers.Range!.Ranges.Single().From);
            Assert.Equal("\"v1\"", request.Headers.GetValues("If-Range").Single());
            var response = Response(HttpStatusCode.PartialContent, "def"u8.ToArray(), "\"v1\"");
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6);
            return response;
        });
        var result = await Downloader(client).DownloadAsync(Request(temp, bytes));
        Assert.True(result.Resumed);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(AssetPath(temp)));
    }

    [Fact]
    public async Task Uses_full_200_body_once_when_server_ignores_range()
    {
        var bytes = "abcdef"u8.ToArray();
        using var temp = new TempDirectory();
        await SeedPartial(temp, bytes, "abc", "\"v1\"");
        var calls = 0;
        using var client = Client(request => { calls++; Assert.NotNull(request.Headers.Range); return Response(HttpStatusCode.OK, bytes, "\"v2\""); });
        var result = await Downloader(client).DownloadAsync(Request(temp, bytes));
        Assert.True(result.Restarted);
        Assert.False(result.Resumed);
        Assert.Equal(1, calls);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(AssetPath(temp)));
    }

    [Fact]
    public async Task Invalid_206_discards_partial_then_restarts_from_zero()
    {
        var bytes = "abcdef"u8.ToArray();
        using var temp = new TempDirectory();
        await SeedPartial(temp, bytes, "abc", "\"v1\"");
        var calls = 0;
        using var client = Client(request =>
        {
            calls++;
            if (calls == 1)
            {
                var invalid = Response(HttpStatusCode.PartialContent, "def"u8.ToArray(), "\"changed\"");
                invalid.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6);
                return invalid;
            }
            Assert.Null(request.Headers.Range);
            return Response(HttpStatusCode.OK, bytes, "\"changed\"");
        });
        var result = await Downloader(client).DownloadAsync(Request(temp, bytes));
        Assert.True(result.Restarted);
        Assert.Equal(2, calls);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(AssetPath(temp)));
    }

    [Fact]
    public async Task Tries_next_source_after_expiring_temporary_url()
    {
        var bytes = "abcdef"u8.ToArray();
        using var temp = new TempDirectory();
        using var client = Client(request => request.RequestUri!.Host == "expired.test"
            ? Response(HttpStatusCode.Forbidden, [])
            : Response(HttpStatusCode.OK, bytes, "\"v1\""));
        var request = Request(temp, bytes) with
        {
            Sources = [new("direct", new Uri("https://expired.test/asset")), new("api", new Uri("https://api.test/asset"))]
        };
        var result = await Downloader(client).DownloadAsync(request);
        Assert.Equal("api", result.SourceId);
    }

    [Fact]
    public async Task Rejects_content_with_wrong_hash_and_keeps_current_destination()
    {
        var bytes = "abcdef"u8.ToArray();
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(AssetPath(temp), "old-version");
        using var client = Client(_ => Response(HttpStatusCode.OK, "abcdeg"u8.ToArray(), "\"v1\""));
        await Assert.ThrowsAsync<AssetDownloadException>(() => Downloader(client).DownloadAsync(Request(temp, bytes)));
        Assert.Equal("old-version", await File.ReadAllTextAsync(AssetPath(temp)));
    }

    [Fact]
    public async Task Integrity_failure_discards_corrupt_partial_and_tries_next_source()
    {
        var bytes = "abcdef"u8.ToArray();
        foreach (var corrupt in new[] { "abcdeg"u8.ToArray(), "abcdefg"u8.ToArray() })
        {
            using var temp = new TempDirectory();
            var calls = new List<string>();
            using var client = Client(request =>
            {
                calls.Add(request.RequestUri!.Host);
                if (request.RequestUri.Host == "direct.test") return Response(HttpStatusCode.OK, corrupt, "\"bad\"");
                Assert.Null(request.Headers.Range);
                return Response(HttpStatusCode.OK, bytes, "\"good\"");
            });
            var result = await Downloader(client).DownloadAsync(Request(temp, bytes) with
            {
                Sources = [new("direct", new Uri("https://direct.test/asset")), new("api", new Uri("https://api.test/asset"))]
            });
            Assert.Equal(new[] { "direct.test", "api.test" }, calls);
            Assert.Equal("api", result.SourceId);
            Assert.True(result.Restarted);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(AssetPath(temp)));
            Assert.False(File.Exists(AssetPath(temp) + ".part"));
        }
    }

    [Theory]
    [InlineData(false, "update.assetHashMismatch")]
    [InlineData(true, "update.assetLengthMismatch")]
    public async Task Exhausted_fallbacks_report_integrity_error_and_keep_existing_file(bool overlong, string expected)
    {
        var bytes = "abcdef"u8.ToArray();
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(AssetPath(temp), "old-version");
        using var client = Client(request => request.RequestUri!.Host == "direct.test"
            ? Response(HttpStatusCode.OK, overlong ? "abcdefg"u8.ToArray() : "abcdeg"u8.ToArray(), "\"bad\"")
            : Response(HttpStatusCode.NotFound, []));
        var error = await Assert.ThrowsAsync<AssetDownloadException>(() => Downloader(client).DownloadAsync(Request(temp, bytes) with
        {
            Sources = [new("direct", new Uri("https://direct.test/asset")), new("api", new Uri("https://api.test/asset"))]
        }));
        Assert.Equal(expected, error.Code);
        Assert.Equal("old-version", await File.ReadAllTextAsync(AssetPath(temp)));
        Assert.False(File.Exists(AssetPath(temp) + ".part"));
    }

    [Fact]
    public async Task Cancels_stream_and_preserves_resumable_partial()
    {
        using var temp = new TempDirectory();
        using var cts = new CancellationTokenSource();
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new CancellingContent(cts)
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Downloader(client).DownloadAsync(Request(temp, "abcdef"u8.ToArray()), cts.Token));
        Assert.True(File.Exists(AssetPath(temp) + ".part"));
    }

    [Fact]
    public async Task Accepts_complete_matching_416_partial()
    {
        var bytes = "abcdef"u8.ToArray();
        using var temp = new TempDirectory();
        await SeedPartial(temp, bytes, "abcdef", "\"v1\"");
        using var client = Client(_ => Response(HttpStatusCode.RequestedRangeNotSatisfiable, []));
        var result = await Downloader(client).DownloadAsync(Request(temp, bytes));
        Assert.Equal(bytes.Length, result.Length);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(AssetPath(temp)));
    }

    [Fact]
    public async Task Restarts_incomplete_416_then_downloads_full_content()
    {
        var bytes = "abcdef"u8.ToArray();
        using var temp = new TempDirectory();
        await SeedPartial(temp, bytes, "abc", "\"v1\"");
        var calls = 0;
        using var client = Client(_ => ++calls == 1
            ? Response(HttpStatusCode.RequestedRangeNotSatisfiable, [])
            : Response(HttpStatusCode.OK, bytes, "\"v1\""));
        var result = await Downloader(client).DownloadAsync(Request(temp, bytes));
        Assert.True(result.Restarted);
        Assert.Equal(2, calls);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(AssetPath(temp)));
    }

    [Fact]
    public async Task Verified_content_cache_avoids_all_network_requests()
    {
        var bytes = "abcdef"u8.ToArray();
        using var temp = new TempDirectory();
        var path = ResumableAssetDownloader.CachePath(temp.Path, Hash(bytes));
        await File.WriteAllBytesAsync(path, bytes);
        using var handler = Client(_ => throw new InvalidOperationException("缓存命中时不应请求网络"));
        using var downloader = Downloader(handler);
        var result = await downloader.DownloadAsync(Request(temp, bytes) with { DestinationPath = path });
        Assert.True(result.ReusedCache);
        Assert.Equal(0, result.Attempts);
    }

    [Fact]
    public async Task Corrupt_cache_is_replaced_only_after_successful_verification()
    {
        var bytes = "abcdef"u8.ToArray();
        using var temp = new TempDirectory();
        await File.WriteAllBytesAsync(AssetPath(temp), "abcdeg"u8.ToArray());
        using var handler = Client(_ => Response(HttpStatusCode.OK, bytes));
        using var downloader = Downloader(handler);
        Assert.False((await downloader.DownloadAsync(Request(temp, bytes))).ReusedCache);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(AssetPath(temp)));
    }

    [Fact]
    public async Task Disk_precheck_preserves_existing_destination()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(AssetPath(temp), "old");
        using var handler = Client(_ => Response(HttpStatusCode.OK, "abcdef"u8.ToArray()));
        using var downloader = new ResumableAssetDownloader(handler, freeSpace: _ => 0);
        var error = await Assert.ThrowsAsync<AssetDownloadException>(() => downloader.DownloadAsync(Request(temp, "abcdef"u8.ToArray())));
        Assert.Equal("update.diskSpaceInsufficient", error.Code);
        Assert.Equal("old", await File.ReadAllTextAsync(AssetPath(temp)));
    }

    [Fact]
    public async Task Broken_stream_resumes_after_restart_using_persisted_validator()
    {
        var bytes = "abcdef"u8.ToArray();
        using var temp = new TempDirectory();
        using (var handler = Client(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) };
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return response;
        }))
        using (var downloader = new ResumableAssetDownloader(handler, new() { RetriesPerSource = 0 }))
        {
            var failure = await Assert.ThrowsAsync<AssetDownloadException>(() => downloader.DownloadAsync(Request(temp, bytes)));
            Assert.Equal("update.allSourcesUnavailable", failure.Code);
            Assert.Equal("abc", await File.ReadAllTextAsync(AssetPath(temp) + ".part"));
            Assert.False(File.Exists(AssetPath(temp)));
        }
        using var recoveryHandler = Client(request =>
        {
            Assert.Equal(3, request.Headers.Range!.Ranges.Single().From);
            var response = Response(HttpStatusCode.PartialContent, "def"u8.ToArray(), "\"v1\"");
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6);
            return response;
        });
        using var recovery = Downloader(recoveryHandler);
        Assert.True((await recovery.DownloadAsync(Request(temp, bytes))).Resumed);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(AssetPath(temp)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Switch_source_resumes_only_with_matching_strong_validator(bool sameValidator)
    {
        var bytes = "abcdef"u8.ToArray();
        using var temp = new TempDirectory();
        await SeedPartial(temp, bytes, "abc", "\"v1\"");
        var calls = 0;
        using var handler = Client(request =>
        {
            calls++;
            if (request.RequestUri!.Host == "direct.test") return Response(HttpStatusCode.Forbidden, []);
            if (request.Headers.Range is null)
                return Response(HttpStatusCode.OK, bytes, "\"v2\"");
            var response = Response(HttpStatusCode.PartialContent, "def"u8.ToArray(), sameValidator ? "\"v1\"" : "\"v2\"");
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6);
            return response;
        });
        using var downloader = Downloader(handler);
        var result = await downloader.DownloadAsync(Request(temp, bytes) with
        { Sources = [new("direct", new("https://direct.test/a")), new("api", new("https://api.test/a"))] });
        Assert.Equal("api", result.SourceId);
        Assert.Equal(sameValidator, result.Resumed);
        Assert.Equal(sameValidator ? 2 : 3, calls);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(AssetPath(temp)));
    }

    [Fact]
    public async Task Network_failure_uses_bounded_retries_before_next_source()
    {
        var calls = new List<string>();
        var waits = new List<TimeSpan>();
        var bytes = "abcdef"u8.ToArray();
        using var temp = new TempDirectory();
        using var handler = Client(request =>
        {
            calls.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == "direct.test") throw new HttpRequestException();
            return Response(HttpStatusCode.OK, bytes);
        });
        using var downloader = new ResumableAssetDownloader(handler, delay: (duration, _) => { waits.Add(duration); return Task.CompletedTask; });
        var result = await downloader.DownloadAsync(Request(temp, bytes) with
        { Sources = [new("direct", new("https://direct.test/a")), new("api", new("https://api.test/a"))] });
        Assert.Equal(["direct.test", "direct.test", "direct.test", "api.test"], calls);
        Assert.Equal([TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)], waits);
        Assert.Equal("api", result.SourceId);
    }

    [Theory]
    [InlineData("\"changed\"", 3, 5, 6)]
    [InlineData("\"v1\"", 2, 5, 6)]
    [InlineData("\"v1\"", 3, 5, 7)]
    [InlineData("W/\"v1\"", 3, 5, 6)]
    public async Task Invalid_range_or_validator_restarts_safely(string etag, long from, long to, long total)
    {
        var bytes = "abcdef"u8.ToArray();
        using var temp = new TempDirectory();
        await SeedPartial(temp, bytes, "abc", "\"v1\"");
        var calls = 0;
        using var handler = Client(request =>
        {
            if (++calls > 1) { Assert.Null(request.Headers.Range); return Response(HttpStatusCode.OK, bytes); }
            var response = Response(HttpStatusCode.PartialContent, "def"u8.ToArray(), etag);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, total);
            return response;
        });
        using var downloader = Downloader(handler);
        Assert.True((await downloader.DownloadAsync(Request(temp, bytes))).Restarted);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(AssetPath(temp)));
    }

    [Fact]
    public async Task Rejects_oversized_stream_without_replacing_destination()
    {
        using var temp = new TempDirectory();
        using var handler = Client(_ => Response(HttpStatusCode.OK, new byte[100]));
        using var downloader = Downloader(handler);
        var error = await Assert.ThrowsAsync<AssetDownloadException>(() => downloader.DownloadAsync(Request(temp, "abcdef"u8.ToArray())));
        Assert.Equal("update.assetLengthMismatch", error.Code);
        Assert.False(File.Exists(AssetPath(temp)));
    }

    [Fact]
    public async Task Rejects_unregistered_redirect_before_sending_to_it()
    {
        var calls = 0;
        using var temp = new TempDirectory();
        using var handler = Client(_ =>
        {
            calls++;
            var response = Response(HttpStatusCode.Redirect, []);
            response.Headers.Location = new Uri("https://unknown.test/asset");
            return response;
        });
        using var downloader = Downloader(handler);
        var error = await Assert.ThrowsAsync<AssetDownloadException>(() => downloader.DownloadAsync(Request(temp, "abcdef"u8.ToArray())));
        Assert.Equal("update.sourceUriInvalid", error.Code);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Idle_timeout_has_finite_retry_and_preserves_current_destination()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(AssetPath(temp), "old");
        using var handler = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new StalledStream()) });
        using var downloader = new ResumableAssetDownloader(handler, new()
        { IdleTimeout = TimeSpan.FromMilliseconds(20), RetriesPerSource = 0 });
        var error = await Assert.ThrowsAsync<AssetDownloadException>(() => downloader.DownloadAsync(Request(temp, "abcdef"u8.ToArray())));
        Assert.Equal("update.allSourcesUnavailable", error.Code);
        Assert.Equal("old", await File.ReadAllTextAsync(AssetPath(temp)));
    }

    [Fact]
    public async Task Expired_temporary_url_is_relocated_for_same_immutable_asset()
    {
        var bytes = "abcdef"u8.ToArray();
        using var temp = new TempDirectory();
        var resolutions = 0;
        using var handler = Client(request => request.RequestUri!.AbsolutePath == "/expired"
            ? Response(HttpStatusCode.Forbidden, []) : Response(HttpStatusCode.OK, bytes));
        using var downloader = Downloader(handler);
        var result = await downloader.DownloadAsync(Request(temp, bytes) with
        {
            Sources = [new("api", new("https://api.test/expired"), _ =>
            {
                resolutions++;
                return Task.FromResult(new Uri("https://api.test/refreshed"));
            })]
        });
        Assert.Equal(1, resolutions);
        Assert.Equal("api", result.SourceId);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(AssetPath(temp)));
    }

    [Fact]
    public async Task Retry_after_is_capped_and_success_stops_source_fallback()
    {
        using var temp = new TempDirectory();
        var bytes = "abcdef"u8.ToArray();
        var calls = 0;
        var waits = new List<TimeSpan>();
        using var handler = Client(request =>
        {
            Assert.Equal("direct.test", request.RequestUri!.Host);
            if (++calls > 1) return Response(HttpStatusCode.OK, bytes);
            var response = Response(HttpStatusCode.TooManyRequests, []);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return response;
        });
        using var downloader = new ResumableAssetDownloader(handler, delay: (duration, _) =>
        { waits.Add(duration); return Task.CompletedTask; });
        await downloader.DownloadAsync(Request(temp, bytes) with
        { Sources = [new("direct", new("https://direct.test/a")), new("api", new("https://api.test/a"))] });
        Assert.Contains(TimeSpan.FromSeconds(60), waits);
        Assert.All(waits, duration => Assert.InRange(duration, TimeSpan.Zero, TimeSpan.FromSeconds(60)));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Same_destination_waiters_share_verified_cache_and_cancel_without_releasing_owner()
    {
        using var temp = new TempDirectory();
        var bytes = "abcdef"u8.ToArray();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new AsyncHandler(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Response(HttpStatusCode.OK, bytes);
        });
        using var downloader = new ResumableAssetDownloader(handler);
        var first = downloader.DownloadAsync(Request(temp, bytes));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var joined = downloader.DownloadAsync(Request(temp, bytes));
        using var cancellation = new CancellationTokenSource();
        var cancelled = downloader.DownloadAsync(Request(temp, bytes), cancellation.Token);
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            Assert.False(joined.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { release.TrySetResult(); await first; }
        Assert.True((await joined).ReusedCache);
        Assert.Equal(1, calls);
        Assert.True((await downloader.DownloadAsync(Request(temp, bytes))).ReusedCache);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(AssetPath(temp)));
    }

    [Fact]
    public async Task Cancelled_cache_owner_allows_waiter_to_finish_same_destination()
    {
        using var temp = new TempDirectory();
        var bytes = "abcdef"u8.ToArray();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new AsyncHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return Response(HttpStatusCode.OK, bytes);
        });
        using var downloader = new ResumableAssetDownloader(handler);
        using var cancellation = new CancellationTokenSource();
        var first = downloader.DownloadAsync(Request(temp, bytes), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = downloader.DownloadAsync(Request(temp, bytes));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).ReusedCache);
        Assert.Equal(2, calls);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(AssetPath(temp)));
    }

    [Fact]
    public async Task Concurrent_downloads_obey_budget_and_waiting_downloads_can_cancel()
    {
        using var temp = new TempDirectory();
        var bytes = "abcdef"u8.ToArray();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new AsyncHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 2) entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Response(HttpStatusCode.OK, bytes);
        });
        using var downloader = new ResumableAssetDownloader(handler);
        var first = downloader.DownloadAsync(Request(temp, bytes) with { DestinationPath = Path.Combine(temp.Path, "first") });
        // 条带允许少量不相关目标串行；本例选择不同条带验证总并发上限。
        var firstStripe = (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(Path.Combine(temp.Path, "first")) % 64;
        var secondPath = Enumerable.Range(0, 128).Select(i => Path.Combine(temp.Path, "second-" + i))
            .First(path => (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(path) % 64 != firstStripe);
        var second = downloader.DownloadAsync(Request(temp, bytes) with { DestinationPath = secondPath });
        using var cancellation = new CancellationTokenSource();
        var waiting = downloader.DownloadAsync(Request(temp, bytes) with { DestinationPath = Path.Combine(temp.Path, "waiting") }, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Volatile.Read(ref calls));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(2, Volatile.Read(ref calls));
        }
        finally { release.TrySetResult(); await Task.WhenAll(first, second); }
    }

    [Fact]
    public async Task Damaged_partial_metadata_restarts_without_range()
    {
        using var temp = new TempDirectory();
        var bytes = "abcdef"u8.ToArray();
        await File.WriteAllTextAsync(AssetPath(temp) + ".part", "wrong");
        await File.WriteAllTextAsync(AssetPath(temp) + ".part.json", "{broken");
        using var handler = Client(request =>
        { Assert.Null(request.Headers.Range); return Response(HttpStatusCode.OK, bytes); });
        using var downloader = Downloader(handler);
        Assert.True((await downloader.DownloadAsync(Request(temp, bytes))).Restarted);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(AssetPath(temp)));
    }

    private static ResumableAssetDownloader Downloader(HttpMessageHandler handler) =>
        new(handler, delay: (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
    private static AssetDownloadRequest Request(TempDirectory temp, byte[] bytes) =>
        new([new("direct", new Uri("https://example.test/asset"))], AssetPath(temp), bytes.Length, Hash(bytes));
    private static string AssetPath(TempDirectory temp) => Path.Combine(temp.Path, "asset.bin");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static async Task SeedPartial(TempDirectory temp, byte[] expected, string prefix, string validator)
    {
        await File.WriteAllTextAsync(AssetPath(temp) + ".part", prefix);
        await File.WriteAllTextAsync(AssetPath(temp) + ".part.json",
            JsonSerializer.Serialize(new { length = expected.Length, sha256 = Hash(expected), validator }));
    }
    private static HttpMessageHandler Client(Func<HttpRequestMessage, HttpResponseMessage> handler) => new StubHandler(handler);
    private static HttpResponseMessage Response(HttpStatusCode status, byte[] bytes, string? etag = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
        if (etag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
    private sealed class CancellingContent(CancellationTokenSource cts) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = -1; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new CancelOnReadStream(cts));
    }
    private sealed class CancelOnReadStream(CancellationTokenSource cts) : MemoryStream("abcdef"u8.ToArray())
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cts.Cancel();
            return ValueTask.FromCanceled<int>(cancellationToken);
        }
    }
    private sealed class BrokenStream() : MemoryStream("abc"u8.ToArray())
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == Length) throw new IOException("模拟网络中断");
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
    private sealed class TempDirectory : IDisposable
    {
        private readonly UpdateTestWorkspace workspace = new();
        public string Path => workspace.Root;
        public void Dispose() => workspace.Dispose();
    }
}

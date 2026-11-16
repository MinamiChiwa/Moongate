using System.Net;
using System.Text;
using MabiTool.Contracts;
using MabiTool.Providers.GitHubReleases;
using Xunit;

namespace MabiTool.Providers.GitHubReleases.Tests;

public sealed class ReleaseIndexSourceProviderTests
{
    private static readonly UpdateSourceSettings Settings = new("Mobi-Lan/MobiFolio", "stable", null, null);

    [Fact]
    public async Task Direct_success_stops_before_api_and_relay()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{\"version\":1}"));
        using var provider = new ReleaseIndexSourceProvider(handler, retryCount: 0);

        var result = await provider.FetchChannelIndexAsync(Settings with { RelayBaseUri = "https://relay.example/" });

        Assert.Equal(UpdateSourceKind.GithubDirect, result.Source);
        Assert.Single(handler.Requests);
        Assert.Equal("github.com", handler.Requests[0].Host);
    }

    [Fact]
    public async Task Empty_relay_is_never_requested_after_github_sources_fail()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.NotFound, "{}"));
        using var provider = new ReleaseIndexSourceProvider(handler, retryCount: 0);

        var error = await Assert.ThrowsAsync<UpdateSourceException>(() => provider.FetchChannelIndexAsync(Settings));

        Assert.Equal("update.allSourcesFailed", error.Code);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request => request.Host == "relay.example");
    }

    [Fact]
    public async Task Api_fallback_resolves_release_asset_after_direct_misses()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.Host switch
        {
            "github.com" => Json(HttpStatusCode.NotFound, "{}"),
            "api.github.com" when request.RequestUri.AbsolutePath.Contains("/releases/tags/") =>
                Json(HttpStatusCode.OK, """{"id":42,"tag_name":"channel-stable","assets":[{"name":"channel-index.json","url":"https://api.github.com/repos/Mobi-Lan/MobiFolio/releases/assets/42"}]}"""),
            _ => Json(HttpStatusCode.OK, "{\"index\":true}")
        });
        using var provider = new ReleaseIndexSourceProvider(handler, retryCount: 0);

        var index = await provider.FetchChannelIndexAsync(Settings);

        Assert.Equal(UpdateSourceKind.GithubApi, index.Source);
        Assert.Equal("42", index.ReleaseId);
        Assert.Equal(new[] { "github.com", "api.github.com", "api.github.com" }, handler.Requests.Select(uri => uri.Host));
    }

    [Fact]
    public async Task Configured_relay_is_third_and_rate_limit_uses_bounded_retry_delay()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.Host switch
        {
            "github.com" => Json(HttpStatusCode.NotFound, "{}"),
            "api.github.com" => Json(HttpStatusCode.NotFound, "{}"),
            _ => Json(HttpStatusCode.TooManyRequests, "{}", response => response.Headers.RetryAfter =
                new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(20)))
        });
        var delays = new List<TimeSpan>();
        using var provider = new ReleaseIndexSourceProvider(handler, delay: (duration, _) =>
        {
            delays.Add(duration);
            return Task.CompletedTask;
        }, jitter: () => 0.5, retryCount: 1);

        var error = await Assert.ThrowsAsync<UpdateSourceException>(() => provider.FetchChannelIndexAsync(
            Settings with { RelayBaseUri = "https://relay.example/" }));

        Assert.Equal(new[] { "github.com", "api.github.com", "relay.example", "relay.example" },
            handler.Requests.Select(request => request.Host));
        Assert.Single(delays);
        Assert.Equal(TimeSpan.FromSeconds(20), delays[0]);
        Assert.Contains(error.Failures, failure => failure.Code == "update.rateLimited");
    }

    [Fact]
    public async Task Relay_redirect_to_unconfigured_host_is_rejected()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.Host == "github.com" || request.RequestUri.Host == "api.github.com"
            ? Json(HttpStatusCode.NotFound, "{}")
            : new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://elsewhere.example/index") } });
        using var provider = new ReleaseIndexSourceProvider(handler, retryCount: 0);

        var error = await Assert.ThrowsAsync<UpdateSourceException>(() => provider.FetchChannelIndexAsync(
            Settings with { RelayBaseUri = "https://relay.example/" }));

        Assert.Contains(error.Failures, failure => failure.Code == "update.redirectHostRejected");
        Assert.Equal(3, handler.Requests.Count);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        configure?.Invoke(response);
        return response;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}

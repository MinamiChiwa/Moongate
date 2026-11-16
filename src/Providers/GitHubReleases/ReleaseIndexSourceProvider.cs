using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Providers.GitHubReleases;

public sealed record ReleaseIndexDocument(byte[] Content, UpdateSourceKind Source, Uri RetrievedFrom,
    string? ReleaseId, DateTimeOffset RetrievedAt);

public sealed class UpdateSourceException(string code, IReadOnlyList<UpdateSourceFailure> failures)
    : Exception(code)
{
    public string Code { get; } = code;
    public IReadOnlyList<UpdateSourceFailure> Failures { get; } = failures;
}

public sealed record UpdateSourceFailure(UpdateSourceKind Source, string Code, DateTimeOffset? RetryAt);
public sealed record ReleaseSourceTimeouts(TimeSpan Total, TimeSpan Headers, TimeSpan Idle)
{
    public static ReleaseSourceTimeouts Default { get; } = new(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
}

public sealed partial class ReleaseIndexSourceProvider : IDisposable
{
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow };
    private static readonly HashSet<string> GitHubHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "api.github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"
    };
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly TimeProvider timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Func<double> jitter;
    private readonly int retryCount;
    private readonly TimeSpan maximumRetryDelay;
    private readonly ReleaseSourceTimeouts timeouts;

    public ReleaseIndexSourceProvider(TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<double>? jitter = null,
        int retryCount = 2, TimeSpan? maximumRetryDelay = null, ReleaseSourceTimeouts? timeouts = null)
        : this(CreateSafeClient(), true, timeProvider, delay, jitter, retryCount, maximumRetryDelay, timeouts) { }

    public ReleaseIndexSourceProvider(HttpMessageHandler handler, TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<double>? jitter = null,
        int retryCount = 2, TimeSpan? maximumRetryDelay = null, ReleaseSourceTimeouts? timeouts = null)
        : this(new HttpClient(new SafeRedirectHandler(handler), disposeHandler: true), true,
            timeProvider, delay, jitter, retryCount, maximumRetryDelay, timeouts) { }

    private ReleaseIndexSourceProvider(HttpClient client, bool ownsClient, TimeProvider? timeProvider,
        Func<TimeSpan, CancellationToken, Task>? delay, Func<double>? jitter, int retryCount,
        TimeSpan? maximumRetryDelay, ReleaseSourceTimeouts? timeouts)
    {
        if (retryCount is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(retryCount));
        this.client = client;
        this.ownsClient = ownsClient;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.delay = delay ?? Task.Delay;
        this.jitter = jitter ?? (() => Random.Shared.NextDouble());
        this.retryCount = retryCount;
        this.maximumRetryDelay = maximumRetryDelay ?? TimeSpan.FromSeconds(60);
        this.timeouts = timeouts ?? ReleaseSourceTimeouts.Default;
        if (this.timeouts.Total <= TimeSpan.Zero || this.timeouts.Total > TimeSpan.FromSeconds(90) ||
            this.timeouts.Headers <= TimeSpan.Zero || this.timeouts.Headers > TimeSpan.FromSeconds(10) ||
            this.timeouts.Idle <= TimeSpan.Zero || this.timeouts.Idle > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeouts));
        if (this.maximumRetryDelay < TimeSpan.Zero || this.maximumRetryDelay > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(maximumRetryDelay));
    }

    public async Task<ReleaseIndexDocument> FetchChannelIndexAsync(UpdateSourceSettings settings,
        CancellationToken cancellationToken = default)
    {
        var repository = ParseRepository(settings.GithubRepository);
        var channel = ValidateChannel(settings.Channel);
        return await FetchAcrossSourcesAsync(settings, (source, token) => FetchAsync(source, repository, channel, token), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ReleaseIndexDocument> FetchAcrossSourcesAsync(UpdateSourceSettings settings,
        Func<Source, CancellationToken, Task<ReleaseIndexDocument>> fetch, CancellationToken cancellationToken)
    {
        var sources = BuildSources(ParseRepository(settings.GithubRepository), ValidateChannel(settings.Channel), settings);
        if (sources.Count == 0) throw new UpdateSourceException("update.sourceNotConfigured", []);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeouts.Total);
        var token = deadline.Token;
        var failures = new List<UpdateSourceFailure>();
        try
        {
            foreach (var source in sources)
            {
                for (var attempt = 0; attempt <= retryCount; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (token.IsCancellationRequested) throw new UpdateSourceException("update.metadataTimeout", failures.AsReadOnly());
                    try
                    {
                        return await fetch(source, token).ConfigureAwait(false);
                    }
                    catch (HttpRequestException error)
                    {
                        failures.Add(new(source.Kind, ClassifyNetworkError(error), null));
                        if (attempt < retryCount) await DelayBackoffAsync(attempt, null, token).ConfigureAwait(false);
                    }
                    catch (SourceResponseException error)
                    {
                        DateTimeOffset? retryAt = error.RetryAfter is null ? null : timeProvider.GetUtcNow().Add(
                            error.RetryAfter.Value < TimeSpan.Zero ? TimeSpan.Zero : error.RetryAfter.Value > maximumRetryDelay ? maximumRetryDelay : error.RetryAfter.Value);
                        failures.Add(new(source.Kind, error.Code, retryAt));
                        if (!error.Retryable || attempt == retryCount) break;
                        await DelayBackoffAsync(attempt, error.RetryAfter, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        failures.Add(new(source.Kind, "update.metadataTimeout", null));
                        if (token.IsCancellationRequested) throw new UpdateSourceException("update.metadataTimeout", failures.AsReadOnly());
                    }
                    catch (JsonException) { failures.Add(new(source.Kind, "update.releaseMetadataInvalid", null)); break; }
                    catch (InvalidOperationException) { failures.Add(new(source.Kind, "update.releaseMetadataInvalid", null)); break; }
                    catch (IOException) { failures.Add(new(source.Kind, "update.networkUnavailable", null)); }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new UpdateSourceException("update.metadataTimeout", failures.AsReadOnly()); }
        throw new UpdateSourceException("update.allSourcesFailed", failures.AsReadOnly());
    }

    private async Task<ReleaseIndexDocument> FetchAsync(Source source, string repository, string channel,
        CancellationToken token)
    {
        var releaseId = (string?)null;
        Uri assetUri;
        if (source.Kind == UpdateSourceKind.GithubDirect)
        {
            assetUri = new Uri($"https://github.com/{repository}/releases/download/channel-{channel}/channel-index.json");
        }
        else if (source.Kind == UpdateSourceKind.GithubApi)
        {
            var apiBase = source.BaseUri!;
            var releaseUri = new Uri(apiBase, $"repos/{repository}/releases/tags/channel-{channel}");
            using var releaseResponse = await SendSafeAsync(releaseUri, "application/vnd.github+json", token,
                new HashSet<string>([apiBase.Host], StringComparer.OrdinalIgnoreCase)).ConfigureAwait(false);
            if (!releaseResponse.IsSuccessStatusCode)
                throw ResponseError(releaseResponse, source.Kind);
            var releaseBytes = await ReadBoundedAsync(releaseResponse.Content, 1_048_576, token).ConfigureAwait(false);
            using var releaseJson = JsonDocument.Parse(releaseBytes, JsonOptions);
            var root = releaseJson.RootElement;
            if (!root.TryGetProperty("tag_name", out var tag) || tag.GetString() != $"channel-{channel}" ||
                !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                throw new SourceResponseException("update.releaseMetadataInvalid");
            releaseId = root.TryGetProperty("id", out var id) ? id.GetRawText() : null;
            var asset = assets.EnumerateArray().FirstOrDefault(item =>
                item.TryGetProperty("name", out var name) && name.GetString() == "channel-index.json");
            if (asset.ValueKind == JsonValueKind.Undefined ||
                !asset.TryGetProperty("url", out var assetUrl) || !Uri.TryCreate(assetUrl.GetString(), UriKind.Absolute, out assetUri!))
                throw new SourceResponseException("update.channelIndexMissing");
            EnsureGitHubUri(assetUri);
        }
        else
        {
            assetUri = new Uri(source.BaseUri!, $"v1/channels/{Uri.EscapeDataString(channel)}");
        }

        var allowedHosts = source.Kind switch
        {
            UpdateSourceKind.GithubDirect => GitHubHosts,
            UpdateSourceKind.GithubApi => GitHubHosts,
            _ => new HashSet<string>([source.BaseUri!.Host], StringComparer.OrdinalIgnoreCase)
        };
        using var response = await SendSafeAsync(assetUri,
            source.Kind == UpdateSourceKind.GithubApi ? "application/octet-stream" : "application/json", token, allowedHosts).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw ResponseError(response, source.Kind);
        if (response.Content.Headers.ContentLength is > 1_048_576)
            throw new SourceResponseException("update.metadataTooLarge");
        var bytes = await ReadBoundedAsync(response.Content, 1_048_576, token).ConfigureAwait(false);
        if (bytes.Length == 0) throw new SourceResponseException("update.metadataEmpty");
        return new(bytes, source.Kind, response.RequestMessage?.RequestUri ?? assetUri, releaseId, timeProvider.GetUtcNow());
    }

    private async Task<HttpResponseMessage> SendSafeAsync(Uri uri, string accept, CancellationToken token,
        IReadOnlySet<string> allowedHosts)
    {
        EnsureHttps(uri);
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("MabiTool-UpdateClient/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var headerDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            headerDeadline.CancelAfter(timeouts.Headers);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerDeadline.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or > 399) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirects >= 5) throw new SourceResponseException("update.redirectInvalid");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            EnsureHttps(uri);
            if (!allowedHosts.Contains(uri.Host)) throw new SourceResponseException("update.redirectHostRejected");
        }
    }

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            using var idleDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            idleDeadline.CancelAfter(timeouts.Idle);
            var read = await stream.ReadAsync(buffer, idleDeadline.Token).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximum) throw new SourceResponseException("update.metadataTooLarge");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private SourceResponseException ResponseError(HttpResponseMessage response, UpdateSourceKind source)
    {
        var status = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.TooManyRequests ||
            response.StatusCode == HttpStatusCode.Forbidden && response.Headers.Contains("X-RateLimit-Remaining"))
            return new("update.rateLimited", true, ParseRetryAfter(response));
        if (status >= 500) return new("update.sourceUnavailable", true);
        if (response.StatusCode == HttpStatusCode.NotFound) return new("update.channelNotFound");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new("update.sourceAccessDenied");
        return new(source == UpdateSourceKind.Relay ? "update.relayResponseInvalid" : "update.sourceResponseInvalid");
    }

    private TimeSpan GetBackoff(int attempt)
    {
        var exponent = Math.Min(attempt, 8);
        var baseMs = Math.Min(maximumRetryDelay.TotalMilliseconds, 500 * Math.Pow(2, exponent));
        return TimeSpan.FromMilliseconds(Math.Max(0, baseMs * (0.8 + Math.Clamp(jitter(), 0, 1) * 0.4)));
    }

    private Task DelayBackoffAsync(int attempt, TimeSpan? retryAfter, CancellationToken token)
    {
        var backoff = GetBackoff(attempt);
        if (retryAfter is { } requested) backoff = requested > backoff ? requested : backoff;
        if (backoff > maximumRetryDelay) backoff = maximumRetryDelay;
        return backoff > TimeSpan.Zero ? delay(backoff, token) : Task.CompletedTask;
    }

    private TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var difference = date - timeProvider.GetUtcNow();
            return difference < TimeSpan.Zero ? TimeSpan.Zero : difference;
        }
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
            long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            if (seconds is < -62_135_596_800 or > 253_402_300_799) return null;
            var difference = DateTimeOffset.FromUnixTimeSeconds(seconds) - timeProvider.GetUtcNow();
            return difference < TimeSpan.Zero ? TimeSpan.Zero : difference;
        }
        return null;
    }

    private static List<Source> BuildSources(string repository, string channel, UpdateSourceSettings settings)
    {
        _ = repository;
        _ = channel;
        var result = new List<Source>
        {
            new(UpdateSourceKind.GithubDirect, null),
            new(UpdateSourceKind.GithubApi, ValidateBaseUri(settings.ApiBaseUri ?? "https://api.github.com/"))
        };
        if (!string.IsNullOrWhiteSpace(settings.RelayBaseUri))
            result.Add(new(UpdateSourceKind.Relay, ValidateBaseUri(settings.RelayBaseUri)));
        return result;
    }

    private static string ParseRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository) || repository.Length > 200 || repository.Contains(':') || repository.Contains('?') ||
            repository.Contains('#') || repository.Contains('\\') || repository.Split('/') is not [var owner, var name] ||
            !ValidSegment(owner) || !ValidSegment(name))
            throw new UpdateSourceException("update.repositoryInvalid", []);
        return repository;
    }

    private static bool ValidSegment(string value) => value.Length is > 0 and <= 100 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') && value is not ("." or "..");

    private static string ValidateChannel(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel) || channel.Length > 32 ||
            !channel.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new UpdateSourceException("update.channelInvalid", []);
        return channel;
    }

    private static Uri ValidateBaseUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.IsDefaultPort || IPAddress.TryParse(uri.Host, out _) || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new UpdateSourceException("update.sourceUriInvalid", []);
        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }

    private static void EnsureHttps(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            throw new SourceResponseException("update.sourceUriInvalid");
    }

    private static void EnsureGitHubUri(Uri uri)
    {
        EnsureHttps(uri);
        if (!GitHubHosts.Contains(uri.Host)) throw new SourceResponseException("update.assetHostRejected");
    }

    private static string ClassifyNetworkError(HttpRequestException error) =>
        error.StatusCode is { } status && (int)status >= 500 ? "update.sourceUnavailable" : "update.networkUnavailable";

    private static HttpClient CreateSafeClient() => new(new SafeRedirectHandler(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All
    }), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };

    public void Dispose()
    {
        if (ownsClient) client.Dispose();
    }

    private sealed record Source(UpdateSourceKind Kind, Uri? BaseUri);

    private sealed class SourceResponseException(string code, bool retryable = false, TimeSpan? retryAfter = null)
        : Exception(code)
    {
        public string Code { get; } = code;
        public bool Retryable { get; } = retryable;
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }

    private sealed class SafeRedirectHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { } uri) throw new HttpRequestException("update.sourceUriInvalid");
            EnsureHttps(uri);
            if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(uri.Host, out _))
                throw new HttpRequestException("update.sourceHostRejected");
            return base.SendAsync(request, cancellationToken);
        }
    }
}

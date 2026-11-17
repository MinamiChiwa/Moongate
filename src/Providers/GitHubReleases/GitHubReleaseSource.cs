using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Providers.GitHubReleases;

public sealed class UpdateSourceException(string code, IReadOnlyList<UpdateSourceFailure> failures) : Exception(code)
{
    public string Code { get; } = code;
    public IReadOnlyList<UpdateSourceFailure> Failures { get; } = failures;
}

public sealed record UpdateSourceFailure(UpdateSourceKind Source, string Code, DateTimeOffset? RetryAt);
public sealed record ReleaseAssetEndpoint(UpdateSourceKind Source, Uri Uri);

/// <summary>某个发行里的整包 zip 与同名 <c>.sha256</c>，下载地址按直连、REST API 的顺序排列。</summary>
public sealed record ReleaseCandidate(string Tag, ReleaseVersion Version, string AssetName, long AssetLength,
    IReadOnlyList<ReleaseAssetEndpoint> Package, IReadOnlyList<ReleaseAssetEndpoint> Checksum);

public sealed record ReleaseSourceTimeouts(TimeSpan Total, TimeSpan Headers, TimeSpan Idle)
{
    public static ReleaseSourceTimeouts Default { get; } = new(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
}

/// <summary>
/// 从 GitHub Releases 取最新发行。正式渠道读 <c>releases/latest</c>（GitHub 只返回非草稿、非预发布的最新发行）；
/// 预览渠道读最近 30 个发行，在非草稿发行中取版本最高者（正式与预发布都算）。只接受资产齐全（
/// <c>Moongate-v&lt;版本&gt;-Windows-x64.zip</c> 与同名 <c>.sha256</c>，状态为 uploaded）的发行。
/// 元数据只走 REST API；API 不可达或被限流时本次检查失败、保留当前版本。下载地址先给 github.com 的发行下载链接，
/// 再给 API 资产地址；地址只由仓库名、标签、文件名与数字资产 ID 构造。请求只带固定的 User-Agent，不带任何个人或设备标识，
/// 只接受 HTTPS，重定向限定在 GitHub 的主机，不放宽证书校验。
/// </summary>
public sealed class GitHubReleaseSource : IDisposable
{
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;
    private const int MaximumChecksumBytes = 4096;
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow };
    internal static readonly HashSet<string> GitHubHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "api.github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"
    };
    private static readonly Uri ApiBase = new("https://api.github.com/");
    private readonly HttpClient client;
    private readonly TimeProvider timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly int retryCount;
    private readonly ReleaseSourceTimeouts timeouts;

    public GitHubReleaseSource(HttpMessageHandler? handler = null, TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, int retryCount = 2, ReleaseSourceTimeouts? timeouts = null)
    {
        if (retryCount is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(retryCount));
        client = new HttpClient(new SafeRedirectHandler(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All
        }), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.delay = delay ?? Task.Delay;
        this.retryCount = retryCount;
        this.timeouts = timeouts ?? ReleaseSourceTimeouts.Default;
    }

    public static string AssetName(ReleaseVersion version) => $"Moongate-v{version}-Windows-x64.zip";

    /// <summary>渠道内版本最高、资产齐全的发行；仓库还没有符合条件的发行时返回空。</summary>
    public async Task<ReleaseCandidate?> FindLatestAsync(string repository, bool includePrerelease, CancellationToken cancellationToken = default)
    {
        ValidateRepository(repository);
        return await WithRetryAsync(async token =>
        {
            var uri = new Uri(ApiBase, includePrerelease ? $"repos/{repository}/releases?per_page=30" : $"repos/{repository}/releases/latest");
            using var response = await SendAsync(uri, "application/vnd.github+json", token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) throw ResponseError(response);
            using var document = JsonDocument.Parse(await ReadBoundedAsync(response.Content, MaximumMetadataBytes, token).ConfigureAwait(false), JsonOptions);
            var releases = includePrerelease ? document.RootElement : default;
            if (includePrerelease && releases.ValueKind != JsonValueKind.Array) throw new SourceResponseException("update.releaseMetadataInvalid");
            var items = includePrerelease ? releases.EnumerateArray().ToArray() : [document.RootElement];
            return items.Select(item => Candidate(repository, item, includePrerelease)).OfType<ReleaseCandidate>()
                .OrderByDescending(item => item.Version).FirstOrDefault();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取 <c>.sha256</c>：一行 <c>&lt;64 位十六进制&gt;  &lt;文件名&gt;</c>，文件名须与 zip 相同。返回小写哈希。</summary>
    public async Task<string> FetchChecksumAsync(ReleaseCandidate candidate, CancellationToken cancellationToken = default)
    {
        var failures = new List<UpdateSourceFailure>();
        foreach (var endpoint in candidate.Checksum)
        {
            try
            {
                return await WithRetryAsync(async token =>
                {
                    using var response = await SendAsync(endpoint.Uri, "application/octet-stream", token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) throw ResponseError(response);
                    var text = Encoding.UTF8.GetString(await ReadBoundedAsync(response.Content, MaximumChecksumBytes, token).ConfigureAwait(false));
                    return ParseChecksum(text, candidate.AssetName);
                }, cancellationToken, endpoint.Source).ConfigureAwait(false);
            }
            catch (UpdateSourceException error) when (error.Code != "update.assetMetadataInvalid") { failures.AddRange(error.Failures); }
        }
        throw new UpdateSourceException("update.allSourcesFailed", failures);
    }

    public static string ParseChecksum(string text, string assetName)
    {
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToArray();
        if (lines.Length != 1) throw new UpdateSourceException("update.assetMetadataInvalid", []);
        var parts = lines[0].Split(' ', 2);
        var name = parts.Length == 2 ? parts[1].TrimStart(' ', '*') : "";
        if (parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit) || name != assetName)
            throw new UpdateSourceException("update.assetMetadataInvalid", []);
        return parts[0].ToLowerInvariant();
    }

    private static ReleaseCandidate? Candidate(string repository, JsonElement release, bool includePrerelease)
    {
        if (release.ValueKind != JsonValueKind.Object || !release.TryGetProperty("tag_name", out var tagValue) ||
            tagValue.GetString() is not { Length: > 1 and <= 128 } tag || tag[0] != 'v' || !ReleaseVersion.TryParse(tag[1..], out var version))
            return null;
        if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        var prerelease = release.TryGetProperty("prerelease", out var flag) && flag.ValueKind == JsonValueKind.True;
        if (!includePrerelease && (prerelease || version.IsPrerelease)) return null;
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        var name = AssetName(version);
        (long Id, long Size)? zip = null, checksum = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("name", out var assetName) ||
                !asset.TryGetProperty("state", out var state) || state.GetString() != "uploaded" ||
                !asset.TryGetProperty("id", out var id) || !id.TryGetInt64(out var number) || number < 1 ||
                !asset.TryGetProperty("size", out var size) || !size.TryGetInt64(out var length) || length < 1) continue;
            if (assetName.GetString() == name) zip = (number, length);
            else if (assetName.GetString() == name + ".sha256") checksum = (number, length);
        }
        if (zip is null || checksum is null) return null;
        var escapedTag = Uri.EscapeDataString(tag);
        ReleaseAssetEndpoint[] Endpoints(string file, long assetId) =>
        [
            new(UpdateSourceKind.GithubDirect, new Uri($"https://github.com/{repository}/releases/download/{escapedTag}/{file}")),
            new(UpdateSourceKind.GithubApi, new Uri(ApiBase, $"repos/{repository}/releases/assets/{assetId}"))
        ];
        return new(tag, version, name, zip.Value.Size, Endpoints(name, zip.Value.Id), Endpoints(name + ".sha256", checksum.Value.Id));
    }

    private async Task<T> WithRetryAsync<T>(Func<CancellationToken, Task<T>> fetch, CancellationToken cancellationToken,
        UpdateSourceKind source = UpdateSourceKind.GithubApi)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeouts.Total);
        var failures = new List<UpdateSourceFailure>();
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string code; var retryable = true; TimeSpan? retryAfter = null;
            try { return await fetch(deadline.Token).ConfigureAwait(false); }
            catch (HttpRequestException error) { code = error.Message.StartsWith("update.", StringComparison.Ordinal) ? error.Message : "update.networkUnavailable"; }
            catch (SourceResponseException error) { code = error.Code; retryable = error.Retryable; retryAfter = error.RetryAfter; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { code = "update.metadataTimeout"; retryable = false; }
            catch (JsonException) { code = "update.releaseMetadataInvalid"; retryable = false; }
            catch (IOException) { code = "update.networkUnavailable"; }
            failures.Add(new(source, code, retryAfter is { } wait ? timeProvider.GetUtcNow() + wait : null));
            if (!retryable || attempt >= retryCount || deadline.IsCancellationRequested)
                throw new UpdateSourceException(code, failures.AsReadOnly());
            var backoff = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
            if (retryAfter is { } requested && requested > backoff) backoff = requested;
            if (backoff > TimeSpan.FromSeconds(60)) throw new UpdateSourceException(code, failures.AsReadOnly());
            try { await delay(backoff, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new UpdateSourceException("update.metadataTimeout", failures.AsReadOnly()); }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, string accept, CancellationToken token)
    {
        for (var redirects = 0; ; redirects++)
        {
            EnsureGitHub(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Moongate-Update/1");
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
        }
    }

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength > maximum) throw new SourceResponseException("update.metadataTooLarge");
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            idle.CancelAfter(timeouts.Idle);
            var read = await stream.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximum) throw new SourceResponseException("update.metadataTooLarge");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private SourceResponseException ResponseError(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests ||
            response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0")
            return new("update.rateLimited", true, RetryAfter(response));
        if ((int)response.StatusCode >= 500) return new("update.sourceUnavailable", true);
        if (response.StatusCode == HttpStatusCode.NotFound) return new("update.releaseAssetMissing");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new("update.sourceAccessDenied");
        return new("update.sourceResponseInvalid");
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (response.Headers.RetryAfter?.Date is { } date) return date - timeProvider.GetUtcNow() is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
            long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is > 0 and < 253_402_300_799)
            return DateTimeOffset.FromUnixTimeSeconds(seconds) - timeProvider.GetUtcNow() is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        return null;
    }

    private static void ValidateRepository(string repository)
    {
        if (repository is not { Length: > 2 and <= 200 } || repository.Split('/') is not [var owner, var name] ||
            !Segment(owner) || !Segment(name))
            throw new UpdateSourceException("update.repositoryInvalid", []);
        static bool Segment(string value) => value.Length is > 0 and <= 100 && value is not ("." or "..") &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    }

    internal static void EnsureGitHub(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            throw new SourceResponseException("update.sourceUriInvalid");
        if (!GitHubHosts.Contains(uri.Host)) throw new SourceResponseException("update.redirectHostRejected");
    }

    public void Dispose() => client.Dispose();

    private sealed class SourceResponseException(string code, bool retryable = false, TimeSpan? retryAfter = null) : Exception(code)
    {
        public string Code { get; } = code;
        public bool Retryable { get; } = retryable;
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }

    /// <summary>每个请求（含重定向后的）都只能发往 GitHub 的 HTTPS 主机。</summary>
    private sealed class SafeRedirectHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { } uri || uri.Scheme != Uri.UriSchemeHttps || !GitHubHosts.Contains(uri.Host))
                throw new HttpRequestException("update.sourceHostRejected");
            return base.SendAsync(request, cancellationToken);
        }
    }
}

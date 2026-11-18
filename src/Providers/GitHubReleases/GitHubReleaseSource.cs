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

/// <summary>
/// 渠道内最新的发行。资产地址按命名规则由仓库、标签与文件名拼出（直连），经 REST API 发现时再附该资产的 API 地址。
/// 直连发现时 <see cref="AssetLength"/> 未知（由清单或 HEAD 取得），<see cref="Sha256"/> 为发现时读到的校验值。
/// </summary>
public sealed record ReleaseCandidate(string Repository, string Tag, ReleaseVersion Version, long? AssetLength,
    UpdateSourceKind Source, IReadOnlyDictionary<string, long> ApiAssetIds, string? Sha256 = null)
{
    public string AssetName => GitHubReleaseSource.AssetName(Version);
    public string ManifestName => AssetName + ".manifest.json";
    public IReadOnlyList<ReleaseAssetEndpoint> Package => Endpoints(AssetName);
    public IReadOnlyList<ReleaseAssetEndpoint> Checksum => Endpoints(AssetName + ".sha256");

    /// <summary>发行内某个资产的下载地址：先 github.com 发行下载地址，经 API 发现且列出了该资产时再附 API 资产地址。</summary>
    public IReadOnlyList<ReleaseAssetEndpoint> Endpoints(string fileName)
    {
        var list = new List<ReleaseAssetEndpoint>
        {
            new(UpdateSourceKind.GithubDirect, new Uri(
                $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(Tag)}/{Uri.EscapeDataString(fileName)}"))
        };
        if (ApiAssetIds.TryGetValue(fileName, out var id))
            list.Add(new(UpdateSourceKind.GithubApi, new Uri($"https://api.github.com/repos/{Repository}/releases/assets/{id}")));
        return list;
    }
}

public sealed record ReleaseSourceTimeouts(TimeSpan Total, TimeSpan Headers, TimeSpan Idle)
{
    public static ReleaseSourceTimeouts Default { get; } = new(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
}

/// <summary>
/// 从 GitHub Releases 取最新发行。正式渠道先直连：请求 <c>https://github.com/&lt;仓库&gt;/releases/latest</c> 不跟随跳转，
/// 从跳转地址 <c>releases/tag/v&lt;版本&gt;</c> 得到最新正式版本，资产地址按文件名拼出；直连失败再用 REST API
/// <c>releases/latest</c>（GitHub 只返回非草稿、非预发布的最新发行）。预览渠道先用 API 读最近 30 个发行、在非草稿发行中
/// 取版本最高者（正式与预发布都算），API 失败再直连取最新正式发行。只接受资产齐全（<c>Moongate-v&lt;版本&gt;-Windows-x64.zip</c>
/// 与同名 <c>.sha256</c>）的发行。两种方式都因连接、限流或服务端故障失败时以 <c>update.githubUnreachable</c> 失败。
/// 请求只带固定的 User-Agent，不带任何个人或设备标识，只接受 HTTPS，重定向限定在 GitHub 的主机，不放宽证书校验。
/// </summary>
public sealed class GitHubReleaseSource : IDisposable
{
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;
    private const int MaximumChecksumBytes = 4096;
    public const int MaximumManifestBytes = 16 * 1024 * 1024;
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow };
    internal static readonly HashSet<string> GitHubHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "api.github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"
    };
    // 两种方式都以这些原因失败时，对用户而言就是连不上 GitHub。
    private static readonly HashSet<string> ConnectivityCodes = new(StringComparer.Ordinal)
    {
        "update.networkUnavailable", "update.metadataTimeout", "update.sourceUnavailable", "update.rateLimited",
        "update.sourceAccessDenied", "update.redirectInvalid", "update.sourceResponseInvalid", "update.sourceHostRejected"
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
        UpdateSourceKind[] order = includePrerelease
            ? [UpdateSourceKind.GithubApi, UpdateSourceKind.GithubDirect]
            : [UpdateSourceKind.GithubDirect, UpdateSourceKind.GithubApi];
        var failures = new List<UpdateSourceFailure>();
        foreach (var kind in order)
        {
            try
            {
                return kind == UpdateSourceKind.GithubDirect
                    ? await FindDirectAsync(repository, cancellationToken).ConfigureAwait(false)
                    : await FindApiAsync(repository, includePrerelease, cancellationToken).ConfigureAwait(false);
            }
            catch (UpdateSourceException error)
            {
                if (error.Failures.Count > 0) failures.AddRange(error.Failures);
                else failures.Add(new(kind, error.Code, null));
            }
        }
        var code = failures.All(item => ConnectivityCodes.Contains(item.Code)) ? "update.githubUnreachable" : failures[^1].Code;
        throw new UpdateSourceException(code, failures.AsReadOnly());
    }

    /// <summary>
    /// 直连发现：<c>releases/latest</c> 的 302 跳转地址给出最新正式发行的标签，再按文件名读取同名 <c>.sha256</c>。
    /// 仓库没有发行，或 <c>.sha256</c> 不存在（资产未齐）时返回空。
    /// </summary>
    private async Task<ReleaseCandidate?> FindDirectAsync(string repository, CancellationToken cancellationToken)
    {
        var tag = await WithRetryAsync(async token =>
        {
            using var response = await SendAsync(new Uri($"https://github.com/{repository}/releases/latest"), "text/html", token,
                followRedirects: false).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return "";
            if ((int)response.StatusCode is < 300 or > 399) throw ResponseError(response, redirectExpected: true);
            if (response.Headers.Location is not { } location) throw new SourceResponseException("update.redirectInvalid");
            var absolute = location.IsAbsoluteUri ? location : new Uri(new Uri("https://github.com/"), location);
            try { return ParseLatestRedirect(repository, absolute) ?? ""; }
            catch (UpdateSourceException) { throw new SourceResponseException("update.redirectInvalid"); }
        }, cancellationToken, UpdateSourceKind.GithubDirect).ConfigureAwait(false);
        if (tag.Length is < 2 or > 128 || tag[0] != 'v' || !ReleaseVersion.TryParse(tag[1..], out var version) || version.IsPrerelease) return null;
        var candidate = new ReleaseCandidate(repository, tag, version, null, UpdateSourceKind.GithubDirect, new Dictionary<string, long>());
        var checksum = await WithRetryAsync(async token =>
        {
            using var response = await SendAsync(candidate.Checksum[0].Uri, "application/octet-stream", token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) throw ResponseError(response);
            var text = Encoding.UTF8.GetString(await ReadBoundedAsync(response.Content, MaximumChecksumBytes, token).ConfigureAwait(false));
            return ParseChecksum(text, candidate.AssetName);
        }, cancellationToken, UpdateSourceKind.GithubDirect).ConfigureAwait(false);
        return checksum is null ? null : candidate with { Sha256 = checksum };
    }

    /// <summary>
    /// 解析 <c>releases/latest</c> 的跳转地址：<c>https://github.com/&lt;仓库&gt;/releases/tag/&lt;标签&gt;</c> 返回标签，
    /// <c>https://github.com/&lt;仓库&gt;/releases</c>（仓库还没有发行）返回空，其他地址以 <c>update.redirectInvalid</c> 拒绝。
    /// </summary>
    public static string? ParseLatestRedirect(string repository, Uri location)
    {
        if (!location.IsAbsoluteUri || location.Scheme != Uri.UriSchemeHttps || !location.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !location.IsDefaultPort || !string.IsNullOrEmpty(location.UserInfo))
            throw new UpdateSourceException("update.redirectInvalid", []);
        var path = Uri.UnescapeDataString(location.AbsolutePath).TrimEnd('/');
        var prefix = "/" + repository + "/releases";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new UpdateSourceException("update.redirectInvalid", []);
        var rest = path[prefix.Length..];
        if (rest.Length == 0) return null;
        const string tagPrefix = "/tag/";
        if (!rest.StartsWith(tagPrefix, StringComparison.Ordinal) || rest.Length == tagPrefix.Length || rest[tagPrefix.Length..].Contains('/'))
            throw new UpdateSourceException("update.redirectInvalid", []);
        return rest[tagPrefix.Length..];
    }

    private Task<ReleaseCandidate?> FindApiAsync(string repository, bool includePrerelease, CancellationToken cancellationToken) =>
        WithRetryAsync(async token =>
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
        }, cancellationToken);

    /// <summary>读取 <c>.sha256</c>：一行 <c>&lt;64 位十六进制&gt;  &lt;文件名&gt;</c>，文件名须与 zip 相同。返回小写哈希。</summary>
    public async Task<string> FetchChecksumAsync(ReleaseCandidate candidate, CancellationToken cancellationToken = default)
    {
        if (candidate.Sha256 is { } known) return known;
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

    /// <summary>
    /// 读取发行的文件清单 <c>&lt;完整包名&gt;.manifest.json</c>。清单是可选资产：不存在（404）或读取失败时返回空，由调用方改用完整包。
    /// </summary>
    public async Task<byte[]?> FetchManifestAsync(ReleaseCandidate candidate, CancellationToken cancellationToken = default)
    {
        foreach (var endpoint in candidate.Endpoints(candidate.ManifestName))
        {
            try
            {
                var bytes = await WithRetryAsync(async token =>
                {
                    using var response = await SendAsync(endpoint.Uri, "application/octet-stream", token).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.NotFound) return null;
                    if (!response.IsSuccessStatusCode) throw ResponseError(response);
                    return await ReadBoundedAsync(response.Content, MaximumManifestBytes, token).ConfigureAwait(false);
                }, cancellationToken, endpoint.Source).ConfigureAwait(false);
                if (bytes is not null) return bytes;
            }
            catch (UpdateSourceException) { }
        }
        return null;
    }

    /// <summary>直连发现且没有清单时，以 HEAD 请求（跟随到资产域）读取完整包的字节数。</summary>
    public Task<long> FetchLengthAsync(ReleaseCandidate candidate, CancellationToken cancellationToken = default) =>
        WithRetryAsync(async token =>
        {
            using var response = await SendAsync(candidate.Package[0].Uri, "application/octet-stream", token, HttpMethod.Head).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw ResponseError(response);
            return response.Content.Headers.ContentLength is { } length and > 0 ? length : throw new SourceResponseException("update.assetMetadataInvalid");
        }, cancellationToken, UpdateSourceKind.GithubDirect);

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
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("name", out var assetName) ||
                !asset.TryGetProperty("state", out var state) || state.GetString() != "uploaded" ||
                !asset.TryGetProperty("id", out var id) || !id.TryGetInt64(out var number) || number < 1 ||
                !asset.TryGetProperty("size", out var size) || !size.TryGetInt64(out var length) || length < 1) continue;
            if (assetName.GetString() is { Length: > 0 and <= 255 } listed) ids.TryAdd(listed, number);
            if (assetName.GetString() == name) zip = (number, length);
            else if (assetName.GetString() == name + ".sha256") checksum = (number, length);
        }
        if (zip is null || checksum is null) return null;
        return new(repository, tag, version, zip.Value.Size, UpdateSourceKind.GithubApi, ids);
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
            catch (UpdateSourceException error) { code = error.Code; retryable = false; }
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

    private async Task<HttpResponseMessage> SendAsync(Uri uri, string accept, CancellationToken token, HttpMethod? method = null,
        bool followRedirects = true)
    {
        for (var redirects = 0; ; redirects++)
        {
            EnsureGitHub(uri);
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Moongate-Update/1");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var headerDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            headerDeadline.CancelAfter(timeouts.Headers);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerDeadline.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or > 399 || !followRedirects) return response;
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

    private SourceResponseException ResponseError(HttpResponseMessage response, bool redirectExpected = false)
    {
        if (redirectExpected && response.IsSuccessStatusCode) return new("update.redirectInvalid");
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

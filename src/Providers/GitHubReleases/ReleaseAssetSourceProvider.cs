using System.Collections.ObjectModel;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Providers.GitHubReleases;

public sealed record ReleaseAssetEndpoint(UpdateSourceKind Source, Uri Uri,
    Func<CancellationToken, Task<Uri>>? Resolve = null, Func<CancellationToken, Task<Uri>>? Relocate = null);

public sealed partial class ReleaseIndexSourceProvider
{
    private const int MaximumManifestBytes = 12 * 1024 * 1024;
    private const int MaximumCatalogAssets = 100_000;
    private readonly SemaphoreSlim catalogGate = new(1);
    private readonly Dictionary<string, AssetCatalog> catalogs = new(StringComparer.Ordinal);

    public Task<ReleaseIndexDocument> FetchReleaseManifestAsync(UpdateSourceSettings settings, string manifestSha256,
        long manifestLength, CancellationToken cancellationToken = default)
    {
        var hash = ValidateHash(manifestSha256);
        if (manifestLength is < 1 or > MaximumManifestBytes) throw new UpdateSourceException("update.assetMetadataInvalid", []);
        var repository = ParseRepository(settings.GithubRepository);
        var channel = ValidateChannel(settings.Channel);
        var assetName = hash + ".manifest.json";
        return FetchAcrossSourcesAsync(settings, async (source, token) =>
        {
            var uri = source.Kind switch
            {
                UpdateSourceKind.GithubDirect => new Uri($"https://github.com/{repository}/releases/download/channel-{channel}/{assetName}"),
                UpdateSourceKind.GithubApi => await LocateApiAssetAsync(source.BaseUri!, repository, "channel-" + channel, assetName, false, token).ConfigureAwait(false),
                _ => new Uri(source.BaseUri!, $"v1/manifests/{hash}")
            };
            using var response = await SendSafeAsync(uri, "application/octet-stream", token, AllowedHosts(source)).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw ResponseError(response, source.Kind);
            if (response.Content.Headers.ContentLength is { } declared && declared != manifestLength)
                throw new SourceResponseException("update.manifestBindingMismatch");
            var bytes = await ReadBoundedAsync(response.Content, (int)manifestLength, token).ConfigureAwait(false);
            if (bytes.LongLength != manifestLength || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != hash)
                throw new SourceResponseException("update.manifestBindingMismatch");
            return new(bytes, source.Kind, response.RequestMessage?.RequestUri ?? uri, null, timeProvider.GetUtcNow());
        }, cancellationToken);
    }

    public IReadOnlyList<ReleaseAssetEndpoint> BuildReleaseAssetSources(UpdateSourceSettings settings, string releaseId, string sha256)
    {
        var repository = ParseRepository(settings.GithubRepository);
        _ = ValidateChannel(settings.Channel);
        if (releaseId is not { Length: > 0 and <= 128 } || !char.IsAsciiLetterOrDigit(releaseId[0]) ||
            !releaseId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_'))
            throw new UpdateSourceException("update.releaseIdInvalid", []);
        var hash = ValidateHash(sha256);
        var name = hash + ".blob";
        var sources = BuildSources(repository, settings.Channel, settings);
        var result = new List<ReleaseAssetEndpoint>();
        foreach (var source in sources)
        {
            if (source.Kind == UpdateSourceKind.GithubDirect)
                result.Add(new(source.Kind, new Uri($"https://github.com/{repository}/releases/download/{releaseId}/{name}")));
            else if (source.Kind == UpdateSourceKind.GithubApi)
            {
                // Uri 只登记允许的 API 主机；到达该来源时才解析资产，不能提前消耗 API。
                result.Add(new(source.Kind, source.BaseUri!,
                    token => ResolveEndpointAsync(source.BaseUri!, repository, releaseId, name, false, token),
                    token => ResolveEndpointAsync(source.BaseUri!, repository, releaseId, name, true, token)));
            }
            else result.Add(new(source.Kind, new Uri(source.BaseUri!, $"v1/blobs/{hash}")));
        }
        return result.AsReadOnly();
    }

    private async Task<Uri> ResolveEndpointAsync(Uri apiBase, string repository, string tag, string name, bool refresh, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeouts.Total);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return await LocateApiAssetAsync(apiBase, repository, tag, name, refresh, deadline.Token).ConfigureAwait(false); }
                catch (SourceResponseException error) when (error.Retryable && attempt < retryCount)
                { await DelayBackoffAsync(attempt, error.RetryAfter, deadline.Token).ConfigureAwait(false); }
                catch (HttpRequestException) when (attempt < retryCount)
                { await DelayBackoffAsync(attempt, null, deadline.Token).ConfigureAwait(false); }
            }
        }
        catch (SourceResponseException error) { throw new HttpRequestException(error.Code, error); }
        catch (JsonException error) { throw new HttpRequestException("update.releaseMetadataInvalid", error); }
        catch (InvalidOperationException error) { throw new HttpRequestException("update.releaseMetadataInvalid", error); }
        catch (IOException error) { throw new HttpRequestException("update.networkUnavailable", error); }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested) { throw new HttpRequestException("update.metadataTimeout", error); }
    }

    private async Task<Uri> LocateApiAssetAsync(Uri apiBase, string repository, string tag, string name, bool refresh, CancellationToken token)
    {
        var cacheKey = apiBase.AbsoluteUri + repository + "/" + tag;
        await catalogGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!refresh && catalogs.TryGetValue(cacheKey, out var cached) &&
                timeProvider.GetElapsedTime(cached.Timestamp) < TimeSpan.FromMinutes(5) && cached.Assets.TryGetValue(name, out var existing))
                return existing;
            var loaded = await ReadCatalogAsync(apiBase, repository, tag, token).ConfigureAwait(false);
            // 目录合计有界；来源获取仅在更新期间发生，最多缓存四个发行。
            foreach (var key in catalogs.Keys.ToArray())
                if (catalogs.Count >= 4 || catalogs.Values.Sum(item => item.Assets.Count) + loaded.Count > MaximumCatalogAssets || key == cacheKey)
                    catalogs.Remove(key);
            catalogs[cacheKey] = new(timeProvider.GetTimestamp(), loaded);
            return Find(loaded, name);
        }
        finally { catalogGate.Release(); }
    }

    private async Task<IReadOnlyDictionary<string, Uri>> ReadCatalogAsync(Uri apiBase, string repository, string tag, CancellationToken token)
    {
        var allowed = new HashSet<string>([apiBase.Host], StringComparer.OrdinalIgnoreCase);
        using var response = await SendSafeAsync(new Uri(apiBase, $"repos/{repository}/releases/tags/{Uri.EscapeDataString(tag)}"),
            "application/vnd.github+json", token, allowed).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw ResponseError(response, UpdateSourceKind.GithubApi);
        using var document = JsonDocument.Parse(await ReadBoundedAsync(response.Content, 1_048_576, token).ConfigureAwait(false), JsonOptions);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tagName) || tagName.ValueKind != JsonValueKind.String || tagName.GetString() != tag ||
            !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var releaseId) || releaseId < 1)
            throw new SourceResponseException("update.releaseMetadataInvalid");
        var assets = new Dictionary<string, Uri>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        for (var page = 1; page <= 1001; page++)
        {
            using var pageResponse = await SendSafeAsync(new Uri(apiBase, $"repos/{repository}/releases/{releaseId}/assets?per_page=100&page={page}"),
                "application/vnd.github+json", token, allowed).ConfigureAwait(false);
            if (!pageResponse.IsSuccessStatusCode) throw ResponseError(pageResponse, UpdateSourceKind.GithubApi);
            using var pageDocument = JsonDocument.Parse(await ReadBoundedAsync(pageResponse.Content, 1_048_576, token).ConfigureAwait(false), JsonOptions);
            var items = pageDocument.RootElement;
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 100) throw new SourceResponseException("update.releaseMetadataInvalid");
            foreach (var item in items.EnumerateArray())
            {
                if (++count > MaximumCatalogAssets || item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("name", out var assetName) || assetName.ValueKind != JsonValueKind.String || assetName.GetString() is not { Length: > 0 and <= 255 } text ||
                    !seen.Add(text)) throw new SourceResponseException("update.releaseMetadataInvalid");
                if (!IsContentAsset(text)) continue;
                if (!item.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String || state.GetString() != "uploaded") continue;
                if (!item.TryGetProperty("id", out var assetId) || assetId.ValueKind != JsonValueKind.Number || !assetId.TryGetInt64(out var number) || number < 1)
                    throw new SourceResponseException("update.releaseMetadataInvalid");
                // 地址从受信配置及数字 ID 构造，不采用远端 url/browser_download_url。
                assets.Add(text, new Uri(apiBase, $"repos/{repository}/releases/assets/{number}"));
            }
            if (items.GetArrayLength() < 100) return new ReadOnlyDictionary<string, Uri>(assets);
        }
        throw new SourceResponseException("update.releaseCatalogTooLarge");
    }

    private static bool IsContentAsset(string name) => (name.Length == 69 && name.EndsWith(".blob", StringComparison.Ordinal) ||
        name.Length == 78 && name.EndsWith(".manifest.json", StringComparison.Ordinal)) && name.AsSpan(0, 64).IndexOfAnyExcept("0123456789abcdef") < 0;
    private static Uri Find(IReadOnlyDictionary<string, Uri> assets, string name) => assets.TryGetValue(name, out var uri)
        ? uri : throw new SourceResponseException("update.releaseAssetMissing");
    private static string ValidateHash(string hash)
    {
        if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit)) throw new UpdateSourceException("update.assetMetadataInvalid", []);
        return hash.ToLowerInvariant();
    }
    private static IReadOnlySet<string> AllowedHosts(Source source)
    {
        var allowed = source.Kind == UpdateSourceKind.Relay ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(GitHubHosts, StringComparer.OrdinalIgnoreCase);
        if (source.BaseUri is not null) allowed.Add(source.BaseUri.Host);
        return allowed;
    }
    private sealed record AssetCatalog(long Timestamp, IReadOnlyDictionary<string, Uri> Assets);
}

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace MabiTool.Infrastructure.Updates;

public sealed record AssetDownloadSource(string Id, Uri Uri, Func<CancellationToken, Task<Uri>>? Relocate = null,
    Func<CancellationToken, Task<Uri>>? Resolve = null);
public sealed record AssetDownloadRequest(
    IReadOnlyList<AssetDownloadSource> Sources,
    string DestinationPath,
    long Length,
    string Sha256);
public sealed record AssetDownloadResult(
    string SourceId,
    long Length,
    string Sha256,
    bool Resumed,
    bool Restarted,
    bool ReusedCache,
    int Attempts);
public sealed record AssetDownloadOptions
{
    public int Concurrency { get; init; } = 2;
    public int RetriesPerSource { get; init; } = 2;
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
public sealed class AssetDownloadException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class ResumableAssetDownloader : IDisposable
{
    private const int BufferSize = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient client;
    private readonly AssetDownloadOptions options;
    private readonly SemaphoreSlim slots;
    private readonly SemaphoreSlim[] cacheGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly Func<string, long> freeSpace;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly UpdateIoBudget budget;

    public ResumableAssetDownloader() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }

    public ResumableAssetDownloader(HttpMessageHandler handler, AssetDownloadOptions? options = null,
        Func<string, long>? freeSpace = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        UpdateIoBudget? budget = null)
    {
        this.options = options ?? new();
        if (this.options.Concurrency is < 1 or > 4 || this.options.RetriesPerSource is < 0 or > 2 ||
            this.options.HeaderTimeout <= TimeSpan.Zero || this.options.IdleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        slots = new SemaphoreSlim(this.options.Concurrency);
        this.freeSpace = freeSpace ?? (path => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace);
        this.delay = delay ?? Task.Delay;
        this.budget = budget ?? new();
    }

    public static string CachePath(string cacheRoot, string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new AssetDownloadException("update.assetMetadataInvalid");
        return Path.Combine(Path.GetFullPath(cacheRoot), sha256.ToLowerInvariant() + ".blob");
    }

    public async Task<AssetDownloadResult> DownloadAsync(AssetDownloadRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        using var activity = budget.EnterActivity("update.download");
        var destination = Path.GetFullPath(request.DestinationPath);
        var cacheGate = cacheGates[(int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(destination) % (uint)cacheGates.Length)];
        // 有界条带锁合并同目标写入；跨进程仍由文件锁裁决。
        await cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ValidatePaths(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var ownership = new FileStream(destination + ".lock", FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                try { return await DownloadCoreAsync(request, cancellationToken).ConfigureAwait(false); }
                catch (IOException error) when (IsDiskFull(error))
                { throw new AssetDownloadException("update.diskSpaceInsufficient"); }
            }
            finally { slots.Release(); }
        }
        finally { cacheGate.Release(); }
    }

    private async Task<AssetDownloadResult> DownloadCoreAsync(AssetDownloadRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var destination = Path.GetFullPath(request.DestinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partPath = destination + ".part";
        var metadataPath = partPath + ".json";
        if (File.Exists(destination) && new FileInfo(destination).Length == request.Length &&
            await IsHashAsync(destination, request.Sha256, cancellationToken).ConfigureAwait(false))
            return Result("cache", request, false, false, true, 0);

        var metadata = await ReadMetadataAsync(metadataPath, cancellationToken).ConfigureAwait(false);
        var restarted = false;
        if (metadata is null || metadata.Length != request.Length ||
            !string.Equals(metadata.Sha256, request.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(partPath) || new FileInfo(partPath).Length > request.Length)
        {
            restarted = File.Exists(partPath);
            DeletePartial(partPath, metadataPath);
            metadata = null;
        }

        var attempts = 0;
        AssetDownloadException? integrityFailure = null;
        foreach (var source in request.Sources)
        {
            var sourceUri = source.Uri;
            var resolved = source.Resolve is null;
            var relocated = false;
            for (var retry = 0; retry <= options.RetriesPerSource; retry++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (retry > 0)
                    await delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, retry - 1)), cancellationToken).ConfigureAwait(false);
                attempts++;
                if (!resolved)
                {
                    try { sourceUri = await source.Resolve!(cancellationToken).ConfigureAwait(false); resolved = true; }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (HttpRequestException) { continue; }
                    catch (OperationCanceledException) { continue; }
                }
                var existingLength = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                using var message = new HttpRequestMessage(HttpMethod.Get, sourceUri);
                var canResume = existingLength > 0 && metadata?.Validator is { Length: > 0 };
                if (canResume)
                {
                    message.Headers.Range = new RangeHeaderValue(existingLength, null);
                    message.Headers.TryAddWithoutValidation("If-Range", metadata!.Validator);
                }

                HttpResponseMessage response;
                try
                {
                    response = await SendSafeAsync(message, request.Sources, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (HttpRequestException) { continue; }
                catch (OperationCanceledException) { continue; }

                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingLength > 0)
                    {
                        if (existingLength == request.Length &&
                            await IsHashAsync(partPath, request.Sha256, cancellationToken).ConfigureAwait(false))
                            return await CompleteAsync(request, partPath, metadataPath, source.Id, true, restarted, attempts, cancellationToken).ConfigureAwait(false);
                        DeletePartial(partPath, metadataPath);
                        metadata = null;
                        restarted = true;
                        continue;
                    }

                    if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    {
                        if (source.Relocate is not null && !relocated)
                        {
                            try { sourceUri = await source.Relocate(cancellationToken).ConfigureAwait(false); }
                            catch (HttpRequestException) { break; }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
                            relocated = true;
                            continue;
                        }
                        break;
                    }
                    if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500 &&
                        response.StatusCode != HttpStatusCode.InsufficientStorage)
                    {
                        var retryAfter = response.Headers.RetryAfter?.Delta ??
                            (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
                        if (retryAfter > TimeSpan.Zero)
                            await delay(retryAfter > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : retryAfter.Value, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (response.StatusCode == HttpStatusCode.InsufficientStorage)
                        throw new AssetDownloadException("update.diskSpaceInsufficient");
                    if (!response.IsSuccessStatusCode)
                        throw new AssetDownloadException("update.assetDownloadFailed");

                    var append = false;
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        var range = response.Content.Headers.ContentRange;
                        var responseValidator = GetStrongValidator(response);
                        var expectedBytes = range?.From is { } from && range.To is { } to ? to - from + 1 : -1;
                        if (!canResume || range?.From != existingLength || range.Length != request.Length ||
                            range.Unit != "bytes" || range.To is null || range.To >= request.Length || expectedBytes < 0 ||
                            response.Content.Headers.ContentLength is { } contentLength && contentLength != expectedBytes ||
                            metadata?.Validator is null || !string.Equals(responseValidator, metadata.Validator, StringComparison.Ordinal))
                        {
                            DeletePartial(partPath, metadataPath);
                            metadata = null;
                            restarted = true;
                            continue;
                        }
                        append = true;
                    }
                    else if (response.StatusCode != HttpStatusCode.OK)
                    {
                        throw new AssetDownloadException("update.assetDownloadFailed");
                    }
                    if (!append && existingLength > 0) restarted = true;
                    EnsureSpace(partPath, request.Length - (append ? existingLength : 0));
                    if (!append)
                    {
                        await using var reset = File.Create(partPath);
                    }

                    var storedValidator = GetStrongValidator(response);
                    metadata = new PartialMetadata(request.Length, request.Sha256.ToLowerInvariant(), storedValidator);
                    await WriteMetadataAtomicallyAsync(metadataPath, metadata, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await CopyBoundedAsync(response.Content, partPath, append, request.Length, cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException error) when (IsDiskFull(error))
                    {
                        throw new AssetDownloadException("update.diskSpaceInsufficient");
                    }
                    catch (AssetDownloadException error) when (error.Code == "update.assetLengthMismatch")
                    {
                        DeletePartial(partPath, metadataPath);
                        metadata = null;
                        restarted = true;
                        integrityFailure = error;
                        break;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (OperationCanceledException) { continue; }
                    catch (HttpRequestException) { continue; }
                    catch (IOException) { continue; }
                    var actualLength = new FileInfo(partPath).Length;
                    if (actualLength < request.Length) continue;
                    if (!await IsHashAsync(partPath, request.Sha256, cancellationToken).ConfigureAwait(false))
                    {
                        DeletePartial(partPath, metadataPath);
                        metadata = null;
                        restarted = true;
                        integrityFailure = new AssetDownloadException("update.assetHashMismatch");
                        break;
                    }
                    return await CompleteAsync(request, partPath, metadataPath, source.Id, append, restarted, attempts, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        throw integrityFailure ?? new AssetDownloadException("update.allSourcesUnavailable");
    }

    private static void Validate(AssetDownloadRequest request)
    {
        if (request.Length < 0 || request.Length > 20L * 1024 * 1024 * 1024 ||
            request.Sha256.Length != 64 || !request.Sha256.All(Uri.IsHexDigit) || request.Sources.Count == 0 ||
            request.Sources.Count > 8 || request.Sources.Any(source => !source.Uri.IsAbsoluteUri || source.Uri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(source.Uri.UserInfo) || !source.Uri.IsDefaultPort || string.IsNullOrWhiteSpace(source.Id) ||
                source.Uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(source.Uri.Host, out _)))
            throw new AssetDownloadException("update.assetMetadataInvalid");
    }

    private async Task CopyBoundedAsync(HttpContent content, string path, bool append, long maximumLength, CancellationToken token)
    {
        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[BufferSize];
        while (true)
        {
            int read;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(options.IdleTimeout);
                read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            }
            if (read == 0) break;
            if (output.Length + read > maximumLength) throw new AssetDownloadException("update.assetLengthMismatch");
            await budget.ConsumeDownloadAsync(read, token).ConfigureAwait(false);
            EnsureSpace(path, read);
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    private static string? GetStrongValidator(HttpResponseMessage response) =>
        response.Headers.ETag is { IsWeak: false, Tag: { } tag } ? tag : null;

    private void EnsureSpace(string path, long bytes)
    {
        if (freeSpace(path) < bytes) throw new AssetDownloadException("update.diskSpaceInsufficient");
    }

    private async Task<HttpResponseMessage> SendSafeAsync(HttpRequestMessage initial,
        IReadOnlyList<AssetDownloadSource> sources, CancellationToken token)
    {
        var uri = initial.RequestUri!;
        var allowed = new HashSet<string>(sources.Select(source => source.Uri.Host), StringComparer.OrdinalIgnoreCase);
        allowed.UnionWith(["github.com", "api.github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"]);
        for (var redirects = 0; ; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
                !allowed.Contains(uri.Host) || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                IPAddress.TryParse(uri.Host, out _))
                throw new AssetDownloadException("update.sourceUriInvalid");
            using var message = new HttpRequestMessage(HttpMethod.Get, uri);
            message.Headers.UserAgent.ParseAdd("MabiTool-UpdateClient/1.0");
            message.Headers.Accept.ParseAdd("application/octet-stream");
            message.Headers.Range = initial.Headers.Range;
            if (initial.Headers.TryGetValues("If-Range", out var values))
                message.Headers.TryAddWithoutValidation("If-Range", values);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(options.HeaderTimeout);
            var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or > 399) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirects >= 5) throw new AssetDownloadException("update.redirectInvalid");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
    }

    private static bool IsDiskFull(IOException error) => (error.HResult & 0xFFFF) is 28 or 39 or 112;
    private static void DeletePartial(string partPath, string metadataPath)
    {
        if (File.Exists(partPath)) File.Delete(partPath);
        if (File.Exists(metadataPath)) File.Delete(metadataPath);
    }

    private async Task<bool> IsHashAsync(string path, string expected, CancellationToken token)
    {
        UpdatePaths.RejectLinks(path);
        await using var stream = File.OpenRead(path);
        var actual = await budget.CopyAndHashAsync(stream, null, 20L * 1024 * 1024 * 1024, token).ConfigureAwait(false);
        return string.Equals(actual.Hash, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidatePaths(string destination)
    {
        foreach (var path in new[] { destination, destination + ".lock", destination + ".part", destination + ".part.json", destination + ".part.json.tmp" })
            UpdatePaths.RejectLinks(path);
    }

    private static async Task<AssetDownloadResult> CompleteAsync(AssetDownloadRequest request, string partPath, string metadataPath,
        string sourceId, bool resumed, bool restarted, int attempts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        File.Move(partPath, Path.GetFullPath(request.DestinationPath), true);
        if (File.Exists(metadataPath)) File.Delete(metadataPath);
        return Result(sourceId, request, resumed, restarted, false, attempts);
    }

    private static AssetDownloadResult Result(string sourceId, AssetDownloadRequest request, bool resumed, bool restarted, bool cache, int attempts) =>
        new(sourceId, request.Length, request.Sha256.ToLowerInvariant(), resumed, restarted, cache, attempts);

    private static async Task<PartialMetadata?> ReadMetadataAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 4096) return null;
            await using var stream = File.OpenRead(path);
            var metadata = await JsonSerializer.DeserializeAsync<PartialMetadata>(stream, JsonOptions, token).ConfigureAwait(false);
            if (metadata?.Validator is { } validator &&
                (!EntityTagHeaderValue.TryParse(validator, out var parsed) || parsed.IsWeak || parsed.Tag == "*")) return null;
            return metadata;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private static async Task WriteMetadataAtomicallyAsync(string path, PartialMetadata metadata, CancellationToken token)
    {
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions, token).ConfigureAwait(false);
        File.Move(temporaryPath, path, true);
    }

    private sealed record PartialMetadata(long Length, string Sha256, string? Validator);

    public void Dispose()
    {
        client.Dispose();
        slots.Dispose();
        foreach (var gate in cacheGates) gate.Dispose();
    }
}

using System.Text.Json;

namespace MabiTool.Contracts.Generation;

public static partial class ClientGenerator
{
    private static string TypeScriptBinaryEndpoint(EndpointDefinition e)
    {
        var path = JsonSerializer.Serialize(e.Path);
        var id = e.Path.Contains("{id}", StringComparison.Ordinal);
        if (id) path += ".replace('{id}', encodeURIComponent(resourceId(id)))";
        var args = (id ? "id: string, " : "") + (e.BinaryRequest ? "content: Blob | Uint8Array | ReadableStream<Uint8Array>, " : "") +
            (e.Idempotent ? "idempotencyKey: string, " : "") + (e.Conditional ? "ifMatch: string, " : "") +
            "signal?: AbortSignal, onResponse?: (metadata: ApiResponseMetadata) => void";
        return e.BinaryResponse
            ? $"  {e.OperationId}({args}): Promise<BinaryDownload> {{ return this.binaryDownload({path}, {e.MaximumResponseBytes}, signal, onResponse); }}"
            : $"  {e.OperationId}({args}): Promise<{TsType(e.ResponseType)}> {{ return this.binaryUpload({path}, content, idempotencyKey, ifMatch, signal, onResponse); }}";
    }
    private static string CSharpBinaryEndpoint(EndpointDefinition e)
    {
        var path = JsonSerializer.Serialize(e.Path);
        var id = e.Path.Contains("{id}", StringComparison.Ordinal);
        if (id) path += ".Replace(\"{id}\", Uri.EscapeDataString(ResourceId(id)), StringComparison.Ordinal)";
        var args = (id ? "string id, " : "") + (e.BinaryRequest ? "Stream content, " : "") +
            (e.Idempotent ? "string idempotencyKey, " : "") + (e.Conditional ? "string ifMatch, " : "") +
            "CancellationToken cancellationToken = default, Action<ApiResponseMetadata>? onResponse = null";
        var name = char.ToUpperInvariant(e.OperationId[0]) + e.OperationId[1..] + "Async";
        return e.BinaryResponse
            ? $"  public Task<BinaryDownload> {name}({args}) => BinaryDownloadAsync({path}, {e.MaximumResponseBytes}, cancellationToken, onResponse);"
            : $"  public Task<{CsType(e.ResponseType)}> {name}({args}) => BinaryUploadAsync<{CsType(e.ResponseType)}>({path}, content, idempotencyKey, ifMatch, cancellationToken, onResponse);";
    }

    private const string TypeScriptBinaryRuntime = """
export interface BinaryDownload {
  readonly content: ReadableStream<Uint8Array>;
  readonly sizeBytes: UInt64String | null;
  readonly sha256: string | null;
  readonly contentDisposition: string | null;
}
""";
    private const string TypeScriptBinaryHelpers = """
  private async binaryResponse(path: string, init: RequestInit, signal?: AbortSignal): Promise<Response> {
    const headers = new Headers(init.headers);
    headers.set('Authorization', 'Bearer ' + await this.token());
    const response = await this.transport(new URL(path, this.baseUrl), {
      ...init, headers, signal: signal ? AbortSignal.any([signal, AbortSignal.timeout(300000)]) : AbortSignal.timeout(300000),
      redirect: 'error', credentials: 'omit', cache: 'no-store'
    });
    if (!response.ok) {
      let problem: ApiProblem | null = null;
      try {
        if (response.headers.get('content-type')?.split(';')[0] === 'application/problem+json') {
          const value = await readJson(response); if (isProblem(value, response.status)) problem = value;
        } else await response.body?.cancel();
      } catch { if (signal?.aborted) throw signal.reason; }
      throw new MabiToolApiError(response.status, problem);
    }
    return response;
  }
  private async binaryUpload<T>(path: string, content: Blob | Uint8Array | ReadableStream<Uint8Array>, key: string, etag: string,
      signal?: AbortSignal, onResponse?: (metadata: ApiResponseMetadata) => void): Promise<T> {
    const response = await this.binaryResponse(path, {
      method: 'POST', headers: { 'Content-Type': 'application/octet-stream', 'Accept': 'application/json',
        'Idempotency-Key': key, 'If-Match': etag }, body: content as BodyInit,
      ...(content instanceof ReadableStream ? { duplex: 'half' } : {})
    } as RequestInit, signal);
    const result = await readJson(response) as T;
    onResponse?.({ status: response.status, etag: response.headers.get('etag'), location: response.headers.get('location') });
    return result;
  }
  private async binaryDownload(path: string, maximum: number, signal?: AbortSignal,
      onResponse?: (metadata: ApiResponseMetadata) => void): Promise<BinaryDownload> {
    const response = await this.binaryResponse(path, { method: 'GET', headers: { 'Accept': 'application/octet-stream' } }, signal);
    let expected: UInt64String | null = null;
    const reader = response.body?.getReader();
    if (!reader) throw new Error('Local API response is empty');
    try {
      const length = response.headers.get('content-length');
      if (length !== null) { expected = uint64(length); if (BigInt(expected) > BigInt(maximum)) throw new Error('Download exceeds size limit'); }
      onResponse?.({ status: response.status, etag: response.headers.get('etag'), location: response.headers.get('location') });
    } catch (error) { await reader.cancel(); reader.releaseLock(); throw error; }
    let total = 0, closed = false;
    const content = new ReadableStream<Uint8Array>({
      async pull(controller) {
        try {
          const next = await reader.read();
          if (next.done) {
            if (expected !== null && BigInt(total) !== BigInt(expected)) throw new Error('Download length is invalid');
            closed = true; reader.releaseLock(); controller.close(); return;
          }
          total += next.value.byteLength;
          if (total > maximum || expected !== null && BigInt(total) > BigInt(expected)) throw new Error('Download exceeds size limit');
          controller.enqueue(next.value);
        } catch (error) {
          if (!closed) { closed = true; await reader.cancel().catch(() => {}); reader.releaseLock(); }
          controller.error(error);
        }
      },
      async cancel(reason) { if (!closed) { closed = true; await reader.cancel(reason); reader.releaseLock(); } }
    }, { highWaterMark: 0 });
    return { content, sizeBytes: expected, sha256: response.headers.get('x-content-sha256'),
      contentDisposition: response.headers.get('content-disposition') };
  }
""";

    private const string CSharpBinaryRuntime = """
public sealed class BinaryDownload : IAsyncDisposable {
  public Stream Content { get; }
  public UInt64String? SizeBytes { get; }
  public string? Sha256 { get; }
  public string? Name { get; }
  internal BinaryDownload(HttpResponseMessage response, Stream stream, int maximum, CancellationTokenSource deadline) {
    var length = response.Content.Headers.ContentLength;
    if (length is < 0 || length > maximum) throw new InvalidDataException("Download exceeds size limit");
    SizeBytes = length is null ? null : new((ulong)length.Value);
    Sha256 = response.Headers.TryGetValues("X-Content-Sha256", out var values) ? values.SingleOrDefault() : null;
    Name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
    Content = new BoundedBinaryStream(stream, maximum, length, response, deadline);
  }
  public ValueTask DisposeAsync() => Content.DisposeAsync();
}
internal sealed class BoundedBinaryStream(Stream source, long maximum, long? expected,
    HttpResponseMessage response, CancellationTokenSource deadline) : Stream {
  private long total; private int disposed;
  public override bool CanRead => source.CanRead; public override bool CanSeek => false; public override bool CanWrite => false;
  public override long Length => expected ?? throw new NotSupportedException();
  public override long Position { get => total; set => throw new NotSupportedException(); }
  private int Count(int count) {
    total += count;
    if (total > maximum || expected is not null && total > expected || count == 0 && expected is not null && total != expected)
      throw new InvalidDataException("Download length exceeds or differs from its budget");
    return count;
  }
  public override int Read(byte[] buffer, int offset, int count) {
    deadline.Token.ThrowIfCancellationRequested(); if (count == 0) return 0;
    return Count(source.Read(buffer, offset, count));
  }
  public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) {
    if (buffer.Length == 0) return 0;
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
    return Count(await source.ReadAsync(buffer, linked.Token));
  }
  public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
    ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
  protected override void Dispose(bool disposing) {
    if (disposing && Interlocked.Exchange(ref disposed, 1) == 0) { source.Dispose(); response.Dispose(); deadline.Dispose(); }
    base.Dispose(disposing);
  }
  public override async ValueTask DisposeAsync() {
    if (Interlocked.Exchange(ref disposed, 1) != 0) return;
    try { await source.DisposeAsync(); } finally { response.Dispose(); deadline.Dispose(); }
    GC.SuppressFinalize(this);
  }
  public override void Flush() => throw new NotSupportedException();
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
internal sealed class BorrowedStreamContent(Stream content) : HttpContent {
  protected override bool TryComputeLength(out long length) {
    length = content.CanSeek ? content.Length - content.Position : 0; return content.CanSeek;
  }
  protected override Task SerializeToStreamAsync(Stream output, TransportContext? context) => content.CopyToAsync(output, 65536);
  protected override Task SerializeToStreamAsync(Stream output, TransportContext? context, CancellationToken ct) => content.CopyToAsync(output, 65536, ct);
}
""";

    private const string CSharpBinaryHelpers = """
  private async Task<HttpResponseMessage> BinaryResponseAsync(HttpRequestMessage request, CancellationToken ct) {
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(ct));
    var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    if (response.IsSuccessStatusCode) return response;
    using (response) {
      ApiProblem? problem = null;
      try {
        if (response.Content.Headers.ContentType?.MediaType == "application/problem+json") {
          var data = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(ct), ct);
          problem = JsonSerializer.Deserialize<ApiProblem>(data, ContractJson.Options);
          if (problem?.Status != (int)response.StatusCode) problem = null;
        }
      } catch (JsonException) { } catch (InvalidDataException) { }
      throw new MabiToolApiException((int)response.StatusCode, problem);
    }
  }
  private async Task<T> BinaryUploadAsync<T>(string path, Stream content, string key, string etag,
      CancellationToken ct, Action<ApiResponseMetadata>? onResponse) {
    if (!content.CanRead) throw new ArgumentException("Upload stream must be readable", nameof(content));
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(5));
    using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, path));
    request.Content = new BorrowedStreamContent(content);
    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    request.Headers.Add("Idempotency-Key", key); request.Headers.Add("If-Match", etag);
    request.Headers.Accept.ParseAdd("application/json");
    using var response = await BinaryResponseAsync(request, deadline.Token);
    var data = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(deadline.Token), deadline.Token);
    var result = JsonSerializer.Deserialize<T>(data, ContractJson.Options) ?? throw new JsonException("Empty upload response");
    onResponse?.Invoke(new((int)response.StatusCode, response.Headers.ETag?.ToString(), response.Headers.Location?.ToString()));
    return result;
  }
  private async Task<BinaryDownload> BinaryDownloadAsync(string path, int maximum, CancellationToken ct,
      Action<ApiResponseMetadata>? onResponse) {
    var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(5));
    HttpResponseMessage? response = null;
    try {
      using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, path));
      request.Headers.Accept.ParseAdd("application/octet-stream");
      response = await BinaryResponseAsync(request, deadline.Token);
      var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
      onResponse?.Invoke(new((int)response.StatusCode, response.Headers.ETag?.ToString(), response.Headers.Location?.ToString()));
      return new(response, stream, maximum, deadline);
    } catch { response?.Dispose(); deadline.Dispose(); throw; }
  }
""";
}

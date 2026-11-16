namespace MabiTool.Contracts.Generation;

public static partial class ClientGenerator
{
    private const string TypeScriptStream = """
  async *streamEvents(options?: EventStreamOptions, signal?: AbortSignal): AsyncGenerator<StreamMessage> {
    const controller = new AbortController();
    const linked = signal ? AbortSignal.any([signal, controller.signal]) : controller.signal;
    let response: Response | undefined;
    try {
      response = await this.transport(new URL('/api/v1/events', this.baseUrl), {
        method: 'GET', headers: { Accept: 'text/event-stream', Authorization: 'Bearer ' + await this.token(), ...this.eventHeaders(options) },
        signal: linked, redirect: 'error', credentials: 'omit', cache: 'no-store'
      });
      if (!response.ok) {
        let problem: ApiProblem | null = null;
        if (response.headers.get('content-type')?.split(';')[0] === 'application/problem+json') {
          try { const value = await readJson(response); if (isProblem(value, response.status)) problem = value; }
          catch { if (linked.aborted) throw linked.reason; }
        }
        throw new MabiToolApiError(response.status, problem);
      }
      if (response.headers.get('content-type')?.split(';')[0] !== 'text/event-stream') throw new Error('Local API response is not SSE');
      const reader = response.body?.getReader();
      if (!reader) throw new Error('Local API response is empty');
      const decoder = new TextDecoder('utf-8', { fatal: true });
      let line = '', data = '', event = '', id: string | null = null, size = 0, skipLf = false, first = true;
      try {
        for (;;) {
          const part = await reader.read();
          const text = decoder.decode(part.value, { stream: !part.done });
          for (const char of text) {
            if (skipLf) { skipLf = false; if (char === '\n') continue; }
            if (char !== '\r' && char !== '\n') {
              line += char; const point = char.codePointAt(0)!; size += point < 128 ? 1 : point < 2048 ? 2 : point < 65536 ? 3 : 4;
              if (size > 4 * 1024 * 1024) throw new Error('SSE frame exceeds size limit');
              continue;
            }
            skipLf = char === '\r';
            if (first) { line = line.replace(/^\uFEFF/, ''); first = false; }
            if (line === '') {
              if (data !== '') yield { event: event || 'message', id, data: JSON.parse(data.slice(0, -1)) };
              data = ''; event = ''; size = 0;
            } else if (!line.startsWith(':')) {
              const colon = line.indexOf(':'); const field = colon < 0 ? line : line.slice(0, colon);
              let value = colon < 0 ? '' : line.slice(colon + 1); if (value.startsWith(' ')) value = value.slice(1);
              if (field === 'data') data += value + '\n';
              else if (field === 'event') event = value;
              else if (field === 'id' && !value.includes('\0')) id = value;
            }
            line = '';
          }
          if (part.done) break;
        }
      } finally { await reader.cancel().catch(() => {}); reader.releaseLock(); }
    } finally { controller.abort(); if (!response?.body?.locked) await response?.body?.cancel().catch(() => {}); }
  }
""";

    private const string TypeScriptStreamHelpers = """
  private eventHeaders(options?: EventStreamOptions): Record<string, string> {
    const headers: Record<string, string> = {};
    if (options?.topics.length) headers['X-Event-Topics'] = options.topics.join(',');
    if (options?.resourceIds.length) headers['X-Event-Resources'] = options.resourceIds.join(',');
    if (options?.lastEventId != null) headers['Last-Event-ID'] = options.lastEventId;
    return headers;
  }
""";

    private const string CSharpStream = """
  public async IAsyncEnumerable<StreamMessage> StreamEventsAsync(EventStreamOptions? options = null,
      [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    handshake.CancelAfter(TimeSpan.FromSeconds(30));
    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "/api/v1/events"));
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(handshake.Token));
    request.Headers.Accept.ParseAdd("text/event-stream"); AddEventHeaders(request, options);
    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, handshake.Token);
    if (!response.IsSuccessStatusCode) {
      ApiProblem? problem = null;
      if (response.Content.Headers.ContentType?.MediaType == "application/problem+json") {
        var bytes = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(handshake.Token), handshake.Token);
        try { problem = JsonSerializer.Deserialize<ApiProblem>(bytes, ContractJson.Options); } catch (JsonException) { }
        if (problem?.Status != (int)response.StatusCode) problem = null;
      }
      throw new MabiToolApiException((int)response.StatusCode, problem);
    }
    if (response.Content.Headers.ContentType?.MediaType != "text/event-stream") throw new InvalidDataException("Local API response is not SSE");
    handshake.CancelAfter(Timeout.InfiniteTimeSpan);
    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    var block = new byte[16384];
    using var line = new MemoryStream();
    var utf8 = new System.Text.UTF8Encoding(false, true);
    var data = new System.Text.StringBuilder();
    string eventName = ""; string? id = null;
    int size = 0, count; bool skipLf = false, first = true;
    while ((count = await stream.ReadAsync(block, cancellationToken)) != 0) {
      for (var i = 0; i < count; i++) {
        var b = block[i];
        if (skipLf) { skipLf = false; if (b == 10) continue; }
        if (b != 10 && b != 13) {
          if (++size > 4 * 1024 * 1024) throw new InvalidDataException("SSE frame exceeds size limit");
          line.WriteByte(b); continue;
        }
        skipLf = b == 13;
        var text = utf8.GetString(line.GetBuffer(), 0, (int)line.Length); line.SetLength(0);
        if (first) { text = text.TrimStart('\uFEFF'); first = false; }
        if (text.Length == 0) {
          if (data.Length > 0) yield return new(eventName.Length == 0 ? "message" : eventName, id,
              JsonSerializer.Deserialize<JsonElement>(data.ToString(0, data.Length - 1), ContractJson.Options));
          data.Clear(); eventName = ""; size = 0;
        } else if (text[0] != ':') {
          var colon = text.IndexOf(':'); var field = colon < 0 ? text : text[..colon];
          var value = colon < 0 ? "" : text[(colon + 1)..]; if (value.StartsWith(' ')) value = value[1..];
          if (field == "data") data.Append(value).Append('\n');
          else if (field == "event") eventName = value;
          else if (field == "id" && !value.Contains('\0')) id = value;
        }
      }
    }
  }
""";

    private const string CSharpStreamHelpers = """
  private static void AddEventHeaders(HttpRequestMessage request, EventStreamOptions? options) {
    if (options?.Topics.Count > 0) request.Headers.Add("X-Event-Topics", string.Join(',', options.Topics));
    if (options?.ResourceIds.Count > 0) request.Headers.Add("X-Event-Resources", string.Join(',', options.ResourceIds));
    if (options?.LastEventId is not null) request.Headers.Add("Last-Event-ID", options.LastEventId);
  }
  private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken ct) {
    using var buffer = new MemoryStream(); var block = new byte[16384]; int count;
    while ((count = await stream.ReadAsync(block, ct)) != 0) {
      if (buffer.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("Local API response exceeds size limit");
      buffer.Write(block, 0, count);
    }
    return buffer.ToArray();
  }
""";
}

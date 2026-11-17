using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MabiTool.Host.Extensions;

/// <summary>会话结束的原因；<see cref="Restartable"/> 为真时运行时按退避自动重启（崩溃、挂死、超内存），协议违规与主动停止不重启。</summary>
public sealed record ExtensionSessionEnd(string Code, string Reason, bool Restartable, int? ExitCode);

/// <summary>会话持有的宿主资源：事件订阅、输入租约等，由 <see cref="ExtensionCapabilities"/> 管理并在会话结束或撤销授权时释放。</summary>
public sealed class ExtensionSessionResources
{
    public readonly object Gate = new();
    public HashSet<string> Topics { get; } = new(StringComparer.Ordinal);
    public CancellationTokenSource? EventPump { get; set; }
    public ExtensionInputHold? Input { get; set; }
}

/// <summary>
/// 一个扩展进程的一次运行：握手、请求分发、心跳、取消与停止。
/// 限制：单行 1 MiB；扩展发来的消息每秒 200 条（可突发 400），超出即判定违规并结束；同时进行的调用最多 16 个；
/// 握手须在 10 秒内完成；每 10 秒一次心跳，连续两次 5 秒内无响应判定挂死；停止时先请扩展退出，3 秒后强制结束。
/// 结束后到达的消息不再处理；宿主请求超时后才到的响应只记日志。
/// </summary>
public sealed class ExtensionSession : IAsyncDisposable
{
    public const int MaximumInFlight = 16, MessagesPerSecond = 200, MessageBurst = 400;
    public static TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public static TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(10);
    public static TimeSpan PingTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(3), WriteTimeout = TimeSpan.FromSeconds(5);

    private readonly IExtensionProcess process;
    private readonly ExtensionLog log;
    private readonly ExtensionCapabilities broker;
    private readonly TimeProvider time;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Method, CancellationTokenSource Cancel)> calls = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<bool> handshake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ExtensionSessionEnd> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlySet<string> grants;
    private ExtensionSessionEnd? end;
    private ExtensionSessionEnd? stopRequest;
    private int terminating;
    private long nextRequest;
    private double tokens = MessageBurst;
    private long refilledAt;
    private long lateMessages;

    public ExtensionSession(RunnableExtension extension, IExtensionProcess process, ExtensionLog log, ExtensionCapabilities broker, TimeProvider time)
    {
        Extension = extension;
        this.process = process;
        this.log = log;
        this.broker = broker;
        this.time = time;
        grants = extension.Grants;
        refilledAt = time.GetTimestamp();
        StartedAt = time.GetUtcNow();
    }

    public RunnableExtension Extension { get; }
    public string Id => Extension.Id;
    public int ProcessId => process.Id;
    public DateTimeOffset StartedAt { get; }
    public IReadOnlySet<string> Grants => Volatile.Read(ref grants);
    public ExtensionSessionResources Resources { get; } = new();
    public ExtensionLog Log => log;
    public long LateMessages => Interlocked.Read(ref lateMessages);
    /// <summary>会话结束时完成；结果为结束原因。</summary>
    public Task<ExtensionSessionEnd> Completion => ended.Task;
    public bool Ready => handshake.Task.IsCompletedSuccessfully && handshake.Task.Result;
    internal CancellationToken Lifetime => lifetime.Token;

    /// <summary>启动读取、标准错误与心跳循环，等待握手完成；握手失败或超时时返回假，会话随之结束。</summary>
    public async Task<bool> StartAsync()
    {
        _ = Task.Run(ReadErrorsAsync);
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(WatchExitAsync);
        var completed = await Task.WhenAny(handshake.Task, Task.Delay(HandshakeTimeout, time, lifetime.Token).ContinueWith(_ => false, TaskScheduler.Default));
        if (completed != handshake.Task || !await handshake.Task)
        {
            await TerminateAsync(new("extension.handshakeTimeout", "扩展没有在 " + HandshakeTimeout.TotalSeconds + " 秒内完成握手", true, null));
            return false;
        }
        _ = Task.Run(HeartbeatAsync);
        return true;
    }

    /// <summary>更新授权：撤销的权限对应的进行中调用立即以 permissionDenied 结束，持有的资源随之释放，再通知扩展。</summary>
    public async Task UpdateGrantsAsync(IReadOnlySet<string> value)
    {
        Volatile.Write(ref grants, value);
        foreach (var (_, call) in calls)
            if (!ExtensionCapabilities.Allowed(call.Method, value)) call.Cancel.Cancel();
        await broker.GrantsChangedAsync(this);
        if (Ready) await NotifyAsync("grants/changed", new JsonObject { ["grants"] = new JsonArray(value.Order(StringComparer.Ordinal).Select(x => (JsonNode)x!).ToArray()) });
    }

    /// <summary>向扩展发通知；会话已结束时忽略。</summary>
    public Task NotifyAsync(string method, object? parameters) =>
        Volatile.Read(ref terminating) != 0 ? Task.CompletedTask : SendAsync(ExtensionProtocol.Notification(method, parameters));

    /// <summary>请扩展退出：发送 shutdown 并等待 3 秒，仍在运行则强制结束。</summary>
    public async Task StopAsync(string code, string reason)
    {
        if (Volatile.Read(ref terminating) != 0) { await ended.Task; return; }
        Volatile.Write(ref stopRequest, new ExtensionSessionEnd(code, reason, false, null));
        if (Ready)
        {
            try { await RequestAsync("shutdown", new JsonObject { ["reason"] = reason }, StopGrace); }
            catch (Exception error) when (error is TimeoutException or OperationCanceledException or ExtensionCallException) { }
            try { await process.WaitForExitAsync(lifetime.Token).WaitAsync(StopGrace, time); }
            catch (Exception error) when (error is TimeoutException or OperationCanceledException) { }
        }
        await TerminateAsync(new(code, reason, false, null));
    }

    private async Task<JsonElement> RequestAsync(string method, object? parameters, TimeSpan timeout)
    {
        var id = "s:h" + Interlocked.Increment(ref nextRequest);
        var source = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = source;
        try
        {
            await SendAsync(ExtensionProtocol.Request(id, method, parameters));
            return await source.Task.WaitAsync(timeout, time, lifetime.Token);
        }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task SendAsync(byte[] line)
    {
        try { await writeGate.WaitAsync(lifetime.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            // 匿名管道的写入不能被取消；扩展长时间不读标准输入时超时结束进程，写入随管道断开返回。
            var write = WriteCoreAsync(line);
            if (await Task.WhenAny(write, Task.Delay(WriteTimeout, time)) != write)
            {
                _ = TerminateAsync(new("extension.unresponsive", "扩展超过 5 秒没有读取标准输入", true, null));
                return;
            }
            await write;
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally { writeGate.Release(); }
    }

    private async Task WriteCoreAsync(byte[] line)
    {
        await process.Input.WriteAsync(line);
        await process.Input.FlushAsync();
    }

    private async Task ReadLoopAsync()
    {
        var reader = new ExtensionLineReader(process.Output);
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(lifetime.Token);
                if (line is null) break;
                if (Volatile.Read(ref terminating) != 0) return;
                if (!TakeToken()) throw new ExtensionProtocolException("extension.rateLimited", "扩展发送消息超过每秒 " + MessagesPerSecond + " 条");
                if (line.Length == 0) continue;
                Dispatch(ExtensionProtocol.Parse(line));
            }
        }
        catch (ExtensionProtocolException error)
        {
            log.Write("error", "host", error.Code + "：" + error.Message);
            await TerminateAsync(new(error.Code, error.Message, false, null));
            return;
        }
        catch (OperationCanceledException) { return; }
        catch (IOException) { }
        // 标准输出关闭：等进程退出后按退出码结束会话。
        await ExitedAsync();
    }

    private async Task WatchExitAsync()
    {
        try { await process.WaitForExitAsync(lifetime.Token); }
        catch (OperationCanceledException) { return; }
        await ExitedAsync();
    }

    private async Task ExitedAsync()
    {
        int? code = null;
        try { code = await process.WaitForExitAsync(lifetime.Token).WaitAsync(TimeSpan.FromSeconds(2), time); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException) { }
        if (Volatile.Read(ref terminating) != 0) return;
        if (Volatile.Read(ref stopRequest) is { } stop) { await TerminateAsync(stop with { ExitCode = code }); return; }
        var peak = process.PeakMemoryBytes;
        if (peak is { } bytes && bytes >= ExtensionRuntime.MemoryLimitBytes * 95 / 100)
            await TerminateAsync(new("extension.memoryLimit", "扩展进程用量达到内存上限 " + ExtensionRuntime.MemoryLimitBytes / 1024 / 1024 + " MiB 后退出", true, code));
        else if (code == 0)
            await TerminateAsync(new("extension.exited", "扩展进程自行退出（退出码 0）", false, code));
        else
            await TerminateAsync(new("extension.exited", code is null ? "扩展关闭了标准输出" : "扩展进程异常退出，退出码 " + code, true, code));
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            while (await process.Error.ReadLineAsync(lifetime.Token) is { } line)
                if (line.Length > 0) log.Write("info", "stderr", line);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private async Task HeartbeatAsync()
    {
        var misses = 0;
        while (!lifetime.IsCancellationRequested)
        {
            try { await Task.Delay(PingInterval, time, lifetime.Token); }
            catch (OperationCanceledException) { return; }
            try
            {
                await RequestAsync("ping", null, PingTimeout);
                misses = 0;
            }
            catch (TimeoutException)
            {
                if (++misses < 2) continue;
                await TerminateAsync(new("extension.unresponsive", "扩展连续两次没有响应心跳", true, null));
                return;
            }
            catch (Exception error) when (error is OperationCanceledException or ExtensionCallException) { return; }
        }
    }

    private bool TakeToken()
    {
        var now = time.GetTimestamp();
        tokens = Math.Min(MessageBurst, tokens + time.GetElapsedTime(refilledAt, now).TotalSeconds * MessagesPerSecond);
        refilledAt = now;
        if (tokens < 1) return false;
        tokens--;
        return true;
    }

    private void Dispatch(ExtensionMessage message)
    {
        if (!handshake.Task.IsCompleted)
        {
            if (message is not { IsRequest: true, Method: "hello" }) throw new ExtensionProtocolException("extension.protocolViolation", "握手前只能发送 hello 请求");
            _ = HelloAsync(message);
            return;
        }
        if (message.IsResponse)
        {
            if (pending.TryRemove(message.Id!, out var source))
            {
                if (message.Error is { } error) source.TrySetException(new ExtensionCallException(Reason(error), "扩展返回错误"));
                else source.TrySetResult(message.Result!.Value);
            }
            else
            {
                Interlocked.Increment(ref lateMessages);
                log.Write("warning", "host", "收到没有对应请求的响应（已超时或重复），已忽略");
            }
            return;
        }
        if (message.IsNotification)
        {
            switch (message.Method)
            {
                case "log":
                    var level = message.Params.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.String && l.GetString() is "debug" or "info" or "warning" or "error" ? l.GetString()! : "info";
                    var text = message.Params.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : string.Empty;
                    log.Write(level, "extension", text);
                    break;
                case "$/cancel":
                    if (message.Params.TryGetProperty("id", out var target) && Wire(target) is { } key && calls.TryGetValue(key, out var call)) call.Cancel.Cancel();
                    break;
                default:
                    log.Write("warning", "host", "未知通知 " + message.Method + "，已忽略");
                    break;
            }
            return;
        }
        if (message.Method == "hello") { _ = SendAsync(ExtensionProtocol.Failure(message.Id!, "request.invalid", "已完成握手")); return; }
        if (calls.Count >= MaximumInFlight) { _ = SendAsync(ExtensionProtocol.Failure(message.Id!, "extension.tooManyCalls", "同时进行的调用超过 " + MaximumInFlight + " 个")); return; }
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        if (!calls.TryAdd(message.Id!, (message.Method!, cancel))) { cancel.Dispose(); _ = SendAsync(ExtensionProtocol.Failure(message.Id!, "request.invalid", "调用 id 重复")); return; }
        _ = Task.Run(() => CallAsync(message, cancel));
    }

    private async Task CallAsync(ExtensionMessage message, CancellationTokenSource cancel)
    {
        byte[] reply;
        try
        {
            var result = await broker.InvokeAsync(this, message.Method!, message.Params, cancel.Token);
            reply = ExtensionProtocol.Result(message.Id!, result);
        }
        catch (ExtensionCallException error) { reply = ExtensionProtocol.Failure(message.Id!, error.Code, error.Message); }
        catch (ExtensionProtocolException error) { reply = ExtensionProtocol.Failure(message.Id!, "extension.resultTooLarge", error.Message); }
        catch (OperationCanceledException) when (!ExtensionCapabilities.Allowed(message.Method!, Grants))
        { reply = ExtensionProtocol.Failure(message.Id!, "extension.permissionDenied", "权限已被撤销"); }
        catch (OperationCanceledException) { reply = ExtensionProtocol.Failure(message.Id!, "extension.cancelled", "调用已取消"); }
        catch (MabiTool.Infrastructure.OperationException error) { reply = ExtensionProtocol.Failure(message.Id!, error.Code, error.Message); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("error", "host", message.Method + " 调用失败：" + error.GetType().Name);
            reply = ExtensionProtocol.Failure(message.Id!, "extension.callFailed", "宿主处理调用失败");
        }
        finally
        {
            calls.TryRemove(message.Id!, out _);
            cancel.Dispose();
        }
        await SendAsync(reply);
    }

    private async Task HelloAsync(ExtensionMessage message)
    {
        var p = message.Params;
        int? protocol = p.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var parsed) ? parsed : null;
        var id = p.TryGetProperty("extensionId", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
        var version = p.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.String ? ver.GetString() : null;
        ExtensionSessionEnd? failure = protocol != ExtensionProtocol.Version
            ? new("extension.protocolIncompatible", "扩展协议版本 " + (protocol?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "缺失") + " 与宿主支持的 " + ExtensionProtocol.Version + " 不符", false, null)
            : id != Extension.Id || version != Extension.Version
                ? new("extension.identityMismatch", "握手的扩展标识或版本与清单不符", false, null) : null;
        if (failure is not null)
        {
            await SendAsync(ExtensionProtocol.Failure(message.Id!, failure.Code, failure.Reason));
            log.Write("error", "host", failure.Reason);
            handshake.TrySetResult(false);
            await TerminateAsync(failure);
            return;
        }
        await SendAsync(ExtensionProtocol.Result(message.Id!, new JsonObject
        {
            ["protocolVersion"] = ExtensionProtocol.Version,
            ["host"] = new JsonObject { ["product"] = "Moongate", ["version"] = BackendApplication.HostVersion },
            ["grants"] = new JsonArray(Grants.Order(StringComparer.Ordinal).Select(x => (JsonNode)x!).ToArray()),
            ["settings"] = broker.SettingsNode(this),
            ["limits"] = new JsonObject
            {
                ["maximumMessageBytes"] = ExtensionProtocol.MaximumLineBytes, ["maximumInFlight"] = MaximumInFlight,
                ["messagesPerSecond"] = MessagesPerSecond, ["memoryBytes"] = ExtensionRuntime.MemoryLimitBytes, ["cpuPercent"] = ExtensionRuntime.CpuPercent
            }
        }));
        log.Write("info", "host", "握手完成，进程 " + process.Id);
        handshake.TrySetResult(true);
    }

    /// <summary>结束会话：取消进行中的调用与宿主请求，释放会话持有的宿主资源，结束进程树。只执行一次。</summary>
    public async Task TerminateAsync(ExtensionSessionEnd reason)
    {
        if (Interlocked.Exchange(ref terminating, 1) != 0) { await ended.Task; return; }
        end = reason;
        handshake.TrySetResult(false);
        await lifetime.CancelAsync();
        foreach (var (_, call) in calls) { try { call.Cancel.Cancel(); } catch (ObjectDisposedException) { } }
        foreach (var (_, source) in pending) source.TrySetCanceled();
        try { await broker.ReleaseAsync(this); }
        catch (Exception error) when (error is not OutOfMemoryException) { log.Write("error", "host", "释放扩展资源失败：" + error.Message); }
        process.Kill();
        int? exit = reason.ExitCode;
        try { exit ??= await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5), time); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException) { }
        log.Write(reason.Code == "extension.stopped" ? "info" : "warning", "host", reason.Code + "：" + reason.Reason);
        ended.TrySetResult(reason with { ExitCode = exit });
    }

    public async ValueTask DisposeAsync()
    {
        if (end is null) await TerminateAsync(new("extension.stopped", "宿主释放会话", false, null));
        await process.DisposeAsync();
        lifetime.Dispose();
        writeGate.Dispose();
    }

    private static string Reason(JsonElement error) => error.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString()! : "extension.error";

    private static string? Wire(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number when id.TryGetInt64(out var n) => "n:" + n,
        JsonValueKind.String => "s:" + id.GetString(),
        _ => null
    };
}

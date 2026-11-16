using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MabiTool.ModuleSdk;
using Microsoft.Win32.SafeHandles;

namespace MabiTool.Infrastructure.Workers;

public sealed record WorkerProcessOptions(string Executable, string WorkerDirectory, string AssetRoot, string WorkerId)
{
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan CancellationGrace { get; init; } = TimeSpan.FromSeconds(2);
    internal string InactiveRequestCode { get; init; } = "worker.requestInactive";
    /// <summary>通道调试事件的入口及所属模块；为空时不记录。</summary>
    public DiagnosticTrace? Trace { get; init; }
    public string DiagnosticModule { get; init; } = "core";
}

public sealed record WorkerRequest(string Method, JsonElement Arguments, string RunId,
    string BindingRevision, string FencingToken, WorkerAssetReference? ArgumentsAsset = null);
public sealed record WorkerFailure(string Code, string? RunId);

public sealed class WorkerCall
{
    internal WorkerCall(WorkerFrame request) { Request = request; }
    internal WorkerFrame Request { get; }
    internal TaskCompletionSource<WorkerFrame> Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Channel<WorkerFrame> Queue { get; } = Channel.CreateBounded<WorkerFrame>(new BoundedChannelOptions(64)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    internal bool Accepted { get; set; }
    internal bool PortPending { get; set; }
    internal CancellationTokenSource PortCancellation { get; } = new();
    internal Func<WorkerFrame, CancellationToken, Task<JsonElement>>? PortHandler { get; set; }
    internal ulong PortSequence { get; set; }
    internal ulong CommandSequence { get; set; }
    internal Action<JsonElement>? CommandResultValidator { get; set; }
    internal TaskCompletionSource<JsonElement>? PendingCommand { get; set; }
    public Task<WorkerFrame> Completion => Terminal.Task;
    public ChannelReader<WorkerFrame> Events => Queue.Reader;
}

/// <summary>每个实例固定一个真实 Worker；故障清理先于进程终止，不自动重放请求。</summary>
[SupportedOSPlatform("windows")]
public sealed class WorkerSession : IAsyncDisposable
{
    private readonly WorkerProcessOptions options;
    private readonly NamedPipeServerStream pipe;
    private readonly Process process;
    private readonly Action<WorkerFailure> cleanup;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly object gate = new();
    private readonly object logGate = new();
    private readonly StringBuilder diagnostics = new();
    private readonly Task stdout;
    private readonly Task stderr;
    private Task reader = Task.CompletedTask;
    private Task heartbeat = Task.CompletedTask;
    private Task ports = Task.CompletedTask;
    private readonly Channel<(WorkerCall Call, WorkerFrame Frame)> portRequests = Channel.CreateBounded<(WorkerCall, WorkerFrame)>(1);
    private WorkerCall? active;
    private ulong incoming;
    private ulong outgoing;
    private long lastPong;
    // 0 运行；1 正常停止中（已发出 shutdown，等待 Worker 自行退出）；2 已关闭通道。
    private int state;
    private int tornDown;
    private Task? disposal;
    // Worker 及其后代所在的作业；Worker 自行退出后据此回收仍存活的后代进程。为空时正常停止退回直接终止。
    private readonly SafeFileHandle? job;

    private WorkerSession(WorkerProcessOptions options, NamedPipeServerStream pipe, Process process, Action<WorkerFailure> cleanup)
    {
        this.options = options; this.pipe = pipe; this.process = process; this.cleanup = cleanup;
        stdout = DrainAsync(process.StandardOutput);
        stderr = DrainAsync(process.StandardError);
        job = CreateJobObject(0, null);
        if (job.IsInvalid || !AssignProcessToJobObject(job, process.Handle)) { job.Dispose(); job = null; }
    }

    public int ProcessId => process.Id;
    public bool IsAlive => Volatile.Read(ref state) == 0 && !process.HasExited;
    /// <summary>释放完成后的进程退出码；进程在等待期限内未退出时为空。</summary>
    public int? ExitCode { get; private set; }
    /// <summary>故障清理或正常停止超过取消宽限时进程仍在运行、由宿主终止；false 表示进程已自行退出。</summary>
    public bool TerminatedByHost { get; private set; }
    public string DiagnosticTail { get { lock (logGate) return diagnostics.ToString(); } }

    public static async Task<WorkerSession> StartAsync(WorkerProcessOptions options,
        Action<WorkerFailure> cleanup, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cleanup);
        if (string.IsNullOrWhiteSpace(options.WorkerId) || options.WorkerId.Length > 64 ||
            options.WorkerId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Worker 标识无效。");
        foreach (var path in new[] { options.Executable, options.WorkerDirectory, options.AssetRoot })
        {
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Worker 路径必须为绝对路径。");
            LocalDiscoverySecurity.RejectLinks(path);
        }
        if (!File.Exists(options.Executable) || !Directory.Exists(options.WorkerDirectory) || !Directory.Exists(options.AssetRoot) ||
            options.StartupTimeout <= TimeSpan.Zero || options.StartupTimeout > TimeSpan.FromMinutes(1) ||
            options.HeartbeatInterval < TimeSpan.FromMilliseconds(50) || options.HeartbeatTimeout < options.HeartbeatInterval * 2 ||
            options.HeartbeatTimeout > TimeSpan.FromMinutes(1) || options.CancellationGrace <= TimeSpan.Zero || options.CancellationGrace > TimeSpan.FromSeconds(10))
            throw new ArgumentException("Worker 启动配置无效。");
        var name = "mabitool-worker-" + Guid.NewGuid().ToString("N");
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64 * 1024, 64 * 1024);
        var start = new ProcessStartInfo(options.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            WorkingDirectory = options.WorkerDirectory
        };
        foreach (var argument in options.Arguments) start.ArgumentList.Add(argument);
        foreach (var pair in options.EnvironmentVariables) start.Environment[pair.Key] = pair.Value;
        start.Environment["MABITOOL_WORKER_PIPE"] = name;
        start.Environment["MABITOOL_WORKER_TOKEN"] = secret;
        start.Environment["MABITOOL_WORKER_ASSET_ROOT"] = options.AssetRoot;
        start.Environment["MABITOOL_WORKER_HOST_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        using (var host = Process.GetCurrentProcess())
            start.Environment["MABITOOL_WORKER_HOST_START"] = host.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        Process process;
        try { process = Process.Start(start) ?? throw new WorkerException("worker.startFailed"); }
        catch { pipe.Dispose(); throw; }
        var session = new WorkerSession(options, pipe, process, cleanup);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(options.StartupTimeout);
        try
        {
            var connected = pipe.WaitForConnectionAsync(deadline.Token);
            var exited = process.WaitForExitAsync(deadline.Token);
            if (await Task.WhenAny(connected, exited).ConfigureAwait(false) == exited)
                throw new WorkerException("worker.startFailed");
            await connected.ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != process.Id || process.HasExited)
                throw new WorkerException("worker.peerInvalid");
            var hello = await WorkerProtocol.ReadAsync(pipe, deadline.Token).ConfigureAwait(false);
            var p = hello.Payload;
            if (hello.MessageId != "1" || hello.Kind != "hello" || p.EnumerateObject().Count() != 5 ||
                p.GetProperty("workerId").GetString() != options.WorkerId || p.GetProperty("pid").GetInt32() != process.Id ||
                p.GetProperty("token").GetString() != secret) throw new WorkerException("worker.handshakeInvalid");
            if (p.GetProperty("minimumVersion").GetInt32() > 1 || p.GetProperty("maximumVersion").GetInt32() < 1)
                throw new WorkerException("worker.protocolIncompatible");
            session.incoming = 1;
            await session.SendAsync("ready", new { workerId = options.WorkerId }, null, deadline.Token).ConfigureAwait(false);
            session.lastPong = Stopwatch.GetTimestamp();
            session.reader = session.ReadLoopAsync();
            session.heartbeat = session.HeartbeatLoopAsync();
            session.ports = session.PortLoopAsync();
            if (options.Trace?.IsEnabled(DiagnosticEvents.ChannelConnected, options.DiagnosticModule) == true)
                options.Trace.Emit(DiagnosticEvents.ChannelConnected, new DiagnosticFields().Count("pid", process.Id), options.DiagnosticModule);
            return session;
        }
        catch (Exception error)
        {
            var code = error is WorkerException typed ? typed.Code : error is OperationCanceledException ? "worker.startTimeout" : "worker.handshakeInvalid";
            session.Fault(code);
            await session.DisposeAsync().ConfigureAwait(false);
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            throw new WorkerException(code, error);
        }
    }

    public async Task<WorkerCall> BeginAsync(WorkerRequest request, CancellationToken token = default,
        Func<WorkerFrame, CancellationToken, Task<JsonElement>>? portHandler = null)
    {
        if (!WorkerProtocol.IsId(request.RunId) || !WorkerProtocol.IsUInt64(request.BindingRevision) ||
            !WorkerProtocol.IsUInt64(request.FencingToken) || string.IsNullOrWhiteSpace(request.Method) || request.Method.Length > 64 ||
            (request.ArgumentsAsset is null && request.Arguments.ValueKind != JsonValueKind.Object)) throw new ArgumentException("Worker 请求无效。");
        var payload = request.ArgumentsAsset is null
            ? JsonSerializer.SerializeToElement(new { method = request.Method, arguments = request.Arguments.Clone() }, WorkerProtocol.Json)
            : JsonSerializer.SerializeToElement(new { method = request.Method, argumentsAsset = request.ArgumentsAsset }, WorkerProtocol.Json);
        var call = new WorkerCall(new(1, "1", Guid.NewGuid().ToString("N"), "request", request.RunId,
            request.BindingRevision, request.FencingToken, payload)) { PortHandler = portHandler };
        lock (gate)
        {
            if (!IsAlive) throw new WorkerException("worker.disconnected");
            if (active is not null) throw new WorkerException("worker.busy");
            active = call;
        }
        if (options.Trace?.IsEnabled(DiagnosticEvents.ChannelRequest, options.DiagnosticModule) == true)
            options.Trace.Emit(DiagnosticEvents.ChannelRequest, new DiagnosticFields().Value("method", request.Method), options.DiagnosticModule);
        try { await SendAsync("request", payload, call.Request, token).ConfigureAwait(false); return call; }
        catch
        {
            Fault("worker.writeFailed");
            _ = call.Completion.Exception;
            throw;
        }
    }

    public async Task CancelAsync(WorkerCall call, CancellationToken token = default)
    {
        lock (gate)
        {
            if (!ReferenceEquals(active, call)) return;
            call.PortCancellation.Cancel();
            call.PendingCommand?.TrySetException(new WorkerException(options.InactiveRequestCode));
            call.PendingCommand = null;
        }
        try
        {
            await SendAsync("cancel", new { }, call.Request, token, expectedCall: call).ConfigureAwait(false);
            await call.Completion.WaitAsync(options.CancellationGrace, token).ConfigureAwait(false);
        }
        catch (TimeoutException) { Fault("worker.cancelTimeout"); throw new WorkerException("worker.cancelTimeout"); }
        catch (OperationCanceledException) { Fault("worker.cancelInterrupted"); throw; }
    }

    public async Task<JsonElement> CommandAsync(WorkerCall call, Func<string, object> payloadFactory,
        Action<JsonElement> resultValidator, CancellationToken token = default, TimeSpan? commandTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(payloadFactory);
        ArgumentNullException.ThrowIfNull(resultValidator);
        var timeout = commandTimeout ?? options.HeartbeatTimeout;
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        token.ThrowIfCancellationRequested();
        TaskCompletionSource<JsonElement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string id;
        lock (gate)
        {
            if (!ReferenceEquals(active, call) || call.PortCancellation.IsCancellationRequested)
                throw new WorkerException(options.InactiveRequestCode);
            if (call.PendingCommand is not null) throw new WorkerException("worker.commandBusy");
            id = checked(++call.CommandSequence).ToString(CultureInfo.InvariantCulture);
            call.PendingCommand = completion;
            call.CommandResultValidator = resultValidator;
        }
        try
        {
            var payload = payloadFactory(id);
            await SendAsync("command", payload, call.Request, token, call, discardCancelled: true).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout, token).ConfigureAwait(false);
        }
        catch (TimeoutException) { Fault("worker.commandTimeout"); throw new WorkerException("worker.commandTimeout"); }
        catch (OperationCanceledException)
        {
            // 命令可能已受理；撤销整个运行，避免调用方放弃后仍定位。
            await CancelAsync(call).ConfigureAwait(false);
            throw;
        }
        catch (IOException) { Fault("worker.writeFailed"); throw new WorkerException("worker.writeFailed"); }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(call.PendingCommand, completion)) call.PendingCommand = null;
            }
        }
    }

    private async Task SendAsync(string kind, object payload, WorkerFrame? scope, CancellationToken token,
        WorkerCall? expectedCall = null, bool discardCancelled = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await writes.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            WorkerFrame frame;
            lock (gate)
            {
                // 停止开始后只发送 shutdown，避免心跳等帧写入正在退出的 Worker。
                if (Volatile.Read(ref state) != 0 && kind != "shutdown") return;
                if (expectedCall is not null && (!ReferenceEquals(active, expectedCall) ||
                    (discardCancelled && expectedCall.PortCancellation.IsCancellationRequested))) return;
                frame = new(1, checked(++outgoing).ToString(CultureInfo.InvariantCulture), scope?.RequestId,
                    kind, scope?.RunId, scope?.BindingRevision, scope?.FencingToken,
                    payload is JsonElement element ? element : JsonSerializer.SerializeToElement(payload, WorkerProtocol.Json));
            }
            await WorkerProtocol.WriteAsync(pipe, frame, linked.Token).ConfigureAwait(false);
        }
        finally { writes.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            var rateWindow = Stopwatch.GetTimestamp();
            var rateCount = 0;
            while (!lifetime.IsCancellationRequested)
            {
                var frame = await WorkerProtocol.ReadAsync(pipe, lifetime.Token).ConfigureAwait(false);
                if (Stopwatch.GetElapsedTime(rateWindow) >= TimeSpan.FromSeconds(1)) { rateWindow = Stopwatch.GetTimestamp(); rateCount = 0; }
                if (++rateCount > 256) throw new WorkerException("worker.outputRateExceeded");
                if (frame.MessageId != checked(++incoming).ToString(CultureInfo.InvariantCulture)) throw new WorkerException("worker.sequenceInvalid");
                if (frame.Kind == "pong")
                {
                    if (frame.Payload.EnumerateObject().Any()) throw new WorkerException("worker.frameInvalid");
                    Interlocked.Exchange(ref lastPong, Stopwatch.GetTimestamp());
                    continue;
                }
                if (frame.Kind == "bye")
                {
                    if (Volatile.Read(ref state) == 0 || frame.Payload.EnumerateObject().Any()) throw new WorkerException("worker.frameInvalid");
                    continue;
                }
                // 停止开始时运行已结束，Worker 退出前迟到的业务帧不再交付。
                if (Volatile.Read(ref state) != 0) continue;
                lock (gate)
                {
                    // 停止在锁外检查之后开始时，EndActive 已在锁内清空当前运行；锁内复核避免把迟到帧误判为协议错误。
                    if (Volatile.Read(ref state) != 0) continue;
                    var call = active ?? throw new WorkerException("worker.requestIdentityInvalid");
                    if (frame.RequestId != call.Request.RequestId || frame.RunId != call.Request.RunId ||
                        frame.BindingRevision != call.Request.BindingRevision || frame.FencingToken != call.Request.FencingToken)
                        throw new WorkerException("worker.requestIdentityInvalid");
                    if (frame.Kind is "result" or "error" or "cancelled")
                    {
                        if (!call.Accepted && frame.Kind != "error") throw new WorkerException("worker.phaseInvalid");
                        active = null;
                        call.PendingCommand?.TrySetException(new WorkerException(options.InactiveRequestCode));
                        call.PendingCommand = null;
                        call.PortCancellation.Cancel();
                        if (!call.PortPending) call.PortCancellation.Dispose();
                        call.Queue.Writer.TryComplete();
                        call.Terminal.TrySetResult(frame);
                    }
                    else if (frame.Kind == "commandResult")
                    {
                        var payload = frame.Payload;
                        if (!call.Accepted || !payload.TryGetProperty("commandId", out var commandId) ||
                            commandId.ValueKind != JsonValueKind.String ||
                            commandId.GetString() != call.CommandSequence.ToString(CultureInfo.InvariantCulture))
                            throw new WorkerException("worker.commandInvalid");
                        call.CommandResultValidator?.Invoke(payload);
                        if (call.PortCancellation.IsCancellationRequested) continue;
                        var pending = call.PendingCommand ?? throw new WorkerException("worker.commandInvalid");
                        call.PendingCommand = null;
                        pending.TrySetResult(payload.Clone());
                    }
                    else if (frame.Kind == "portRequest")
                    {
                        var payload = frame.Payload;
                        if (!call.Accepted || call.PortHandler is null || call.PortPending ||
                            payload.EnumerateObject().Count() != 3 ||
                            !payload.TryGetProperty("callId", out var callId) || callId.ValueKind != JsonValueKind.String ||
                            callId.GetString() != checked(++call.PortSequence).ToString(CultureInfo.InvariantCulture) ||
                            !payload.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String ||
                            !payload.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Object)
                            throw new WorkerException("worker.portInvalid");
                        call.PortPending = true;
                        if (!portRequests.Writer.TryWrite((call, frame))) throw new WorkerException("worker.portInvalid");
                    }
                    else if (frame.Kind is "accepted" or "event")
                    {
                        if (frame.Kind == "accepted")
                        {
                            if (call.Accepted) throw new WorkerException("worker.phaseInvalid");
                            call.Accepted = true;
                        }
                        else if (!call.Accepted) throw new WorkerException("worker.phaseInvalid");
                        if (!call.Queue.Writer.TryWrite(frame)) throw new WorkerException("worker.outputBackpressure");
                    }
                    else throw new WorkerException("worker.frameInvalid");
                }
            }
        }
        catch (Exception error)
        {
            // 正常停止时 Worker 关闭管道属于预期；协议错误仍立即终止。
            if (Volatile.Read(ref state) == 1 && error is not WorkerException) return;
            Fault(error is WorkerException typed ? typed.Code : "worker.disconnected");
        }
    }

    private async Task PortLoopAsync()
    {
        try
        {
            await foreach (var (call, frame) in portRequests.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                string kind;
                object payload;
                var id = frame.Payload.GetProperty("callId").GetString();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, call.PortCancellation.Token);
                try
                {
                    var result = await call.PortHandler!(frame, linked.Token).ConfigureAwait(false);
                    kind = "portResult"; payload = new { callId = id, result };
                }
                catch (Exception error)
                {
                    var code = error switch
                    {
                        GameInputException input => input.Code,
                        WorkerException worker => worker.Code,
                        OperationCanceledException => "input.runStopped",
                        _ => "worker.portFailed"
                    };
                    kind = "portError"; payload = new { callId = id, code };
                }
                lock (gate)
                {
                    call.PortPending = false;
                    if (!ReferenceEquals(active, call)) { call.PortCancellation.Dispose(); continue; }
                }
                // 与新请求共用写锁，在取得写权后再次核对运行，避免旧回复排到新请求之后。
                await SendAsync(kind, payload, call.Request, lifetime.Token, call, discardCancelled: true).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            if (Volatile.Read(ref state) == 1) return;
            Fault(error is WorkerException typed ? typed.Code : "worker.disconnected");
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(options.HeartbeatInterval, lifetime.Token).ConfigureAwait(false);
                // 正常停止由取消宽限约束，不再发送心跳或判定超时。
                if (Volatile.Read(ref state) != 0) return;
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastPong)) > options.HeartbeatTimeout)
                    throw new WorkerException("worker.heartbeatTimeout");
                using var timeout = new CancellationTokenSource(options.HeartbeatTimeout);
                await SendAsync("ping", new { }, null, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            if (Volatile.Read(ref state) == 1) return;
            Fault(error is WorkerException typed ? typed.Code : "worker.disconnected");
        }
    }

    /// <summary>故障路径：先执行宿主清理，再立即关闭通道并终止进程树；正常停止期间的故障直接终止。</summary>
    private void Fault(string code)
    {
        var previous = Interlocked.CompareExchange(ref state, 2, 0);
        if (previous == 0)
        {
            if (options.Trace?.IsEnabled(DiagnosticEvents.ChannelFault, options.DiagnosticModule) == true)
                options.Trace.Emit(DiagnosticEvents.ChannelFault, new DiagnosticFields().Value("reason", code), options.DiagnosticModule, detail: DiagnosticTail);
            EndActive(code); Teardown();
        }
        else if (previous == 1 && Interlocked.CompareExchange(ref state, 2, 1) == 1) Teardown();
    }

    /// <summary>结束当前运行：先调用宿主清理回调，再以失败终态完成调用并关闭端口队列。</summary>
    private void EndActive(string code)
    {
        WorkerCall? call;
        lock (gate)
        {
            call = active; active = null;
            if (call is not null)
            {
                try { call.PortCancellation.Cancel(); }
                catch (AggregateException) { /* 回调失败仍继续宿主清理与进程回收。 */ }
                if (!call.PortPending) call.PortCancellation.Dispose();
            }
        }
        try { cleanup(new(code, call?.Request.RunId)); }
        catch { /* 清理回调失败仍须关闭通道并回收进程。 */ }
        finally
        {
            var error = new WorkerException(code);
            call?.Queue.Writer.TryComplete(error);
            call?.Terminal.TrySetException(error);
            call?.PendingCommand?.TrySetException(error);
            portRequests.Writer.TryComplete();
        }
    }

    private void Teardown()
    {
        if (Interlocked.Exchange(ref tornDown, 1) != 0) return;
        lifetime.Cancel();
        pipe.Dispose();
        try { if (!process.HasExited) { TerminatedByHost = true; process.Kill(entireProcessTree: true); } }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        // Worker 已退出时其后代不再能按父进程找到，按作业回收。
        if (job is not null) TerminateJobObject(job, 1);
    }

    /// <summary>正常停止：结束当前运行后发送 shutdown，在取消宽限内等待 Worker 自行退出，超时或写入失败再终止进程树。</summary>
    private async Task StopAsync()
    {
        if (job is null) { Fault("worker.stopped"); return; }
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
        EndActive("worker.stopped");
        try
        {
            using var grace = new CancellationTokenSource(options.CancellationGrace);
            await SendAsync("shutdown", new { }, null, grace.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or
            InvalidOperationException or WorkerException) { }
        if (Interlocked.CompareExchange(ref state, 2, 1) == 1) Teardown();
    }

    private async Task DrainAsync(StreamReader stream)
    {
        var buffer = new char[2048];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                lock (logGate)
                {
                    diagnostics.Append(buffer, 0, read);
                    if (diagnostics.Length > 16 * 1024) diagnostics.Remove(0, diagnostics.Length - 16 * 1024);
                }
            }
        }
        catch (IOException) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await StopAsync().ConfigureAwait(false);
        try { await Task.WhenAll(reader, heartbeat, ports, stdout, stderr, process.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or IOException or InvalidOperationException) { }
        try { if (process.HasExited) ExitCode = process.ExitCode; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        if (options.Trace?.IsEnabled(DiagnosticEvents.ChannelClosed, options.DiagnosticModule) == true)
            options.Trace.Emit(DiagnosticEvents.ChannelClosed, new DiagnosticFields().Count("exitCode", ExitCode is { } exit ? unchecked((uint)exit) : 0UL)
                .Flag("terminatedByHost", TerminatedByHost), options.DiagnosticModule);
        await writes.WaitAsync().ConfigureAwait(false);
        writes.Release();
        writes.Dispose();
        job?.Dispose();
        lifetime.Dispose();
        process.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}

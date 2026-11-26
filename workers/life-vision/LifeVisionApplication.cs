using System.Diagnostics;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Workers;

namespace MabiTool.LifeVision.Worker;

/// <summary>
/// life-vision Worker 的协议循环：握手之后读帧，<c>ping</c> 回 <c>pong</c>，<c>shutdown</c> 退出，<c>cancel</c> 取消当前观察并回 <c>cancelled</c>，
/// <c>request</c>（<c>life.vision.fishing.watch</c>）校验后回 <c>accepted</c>，随后按请求周期持续发送 <c>event</c>（每帧一个 <see cref="FishingObservation"/>）。
/// 同一时间只受理一个观察；整帧图像只在进程内存里存在，管道上只有类型化观察，Worker 不写文件、日志、stdout 与 stderr。
/// </summary>
public static class LifeVisionApplication
{
    /// <summary>契约 failure.code 枚举；Worker 只用这些错误码结束请求。</summary>
    internal static readonly HashSet<string> FailureCodes = new(StringComparer.Ordinal)
    {
        "capture.sourceFailed", "input.bindingChanged", "capture.frameInvalid",
        "life.fishing.layoutUnsupported", "life.fishing.componentMissing", "worker.frameInvalid"
    };

    /// <summary>
    /// 运行协议循环直到收到 <c>shutdown</c>、宿主关闭管道或 <paramref name="cancellationToken"/> 取消。
    /// 读帧在调用线程上进行，观察循环在线程池任务里运行，截图由 <paramref name="source"/> 在自己的线程执行，
    /// 因此截图卡住时 ping 仍然得到回应（宿主按 3 秒无帧终止进程）。
    /// </summary>
    /// <param name="assetRoot">含 <c>templates/</c> 的目录，默认是 Worker 输出目录。</param>
    public static async Task RunAsync(IFrameSource source, WorkerPeerConnection peer, IVisionClock clock, string? assetRoot = null,
        CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var gate = new object();
        Watch? active = null;
        Task work = Task.CompletedTask;
        TemplateCatalog? catalog = null;
        var rateStart = Stopwatch.GetTimestamp();
        var rateCount = 0;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                WorkerFrame frame;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(6));
                    frame = await peer.ReadAsync(deadline.Token).ConfigureAwait(false);
                }
                // 宿主关闭管道、进程已退出，或 6 秒（宿主每秒一次心跳）没有任何帧：没有对端了，自行退出。
                catch (Exception error) when (error is EndOfStreamException or IOException or ObjectDisposedException or OperationCanceledException ||
                    error is WorkerException { Code: "worker.peerInvalid" })
                { break; }
                if (Stopwatch.GetElapsedTime(rateStart) >= TimeSpan.FromSeconds(1)) { rateStart = Stopwatch.GetTimestamp(); rateCount = 0; }
                if (++rateCount > 256) throw new WorkerException("worker.outputRateExceeded");
                if (frame.Kind == "ping")
                {
                    if (frame.Payload.EnumerateObject().Any()) throw new WorkerException("worker.frameInvalid");
                    await peer.SendAsync("pong", new { }, null, lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                if (frame.Kind == "shutdown") break;
                if (frame.Kind == "cancel")
                {
                    if (frame.Payload.EnumerateObject().Any()) throw new WorkerException("worker.frameInvalid");
                    lock (gate)
                        if (Matches(active, frame)) active!.Stop.Cancel();
                    continue;
                }
                if (frame.Kind != "request") throw new WorkerException("worker.frameInvalid");

                LifeVisionWatchArguments watch;
                try
                {
                    lock (gate)
                        if (active is not null) throw new WorkerException("worker.frameInvalid");
                    watch = ParseRequest(frame);
                    catalog ??= TemplateCatalog.Load(assetRoot ?? AppContext.BaseDirectory);
                }
                catch (WorkerException error)
                {
                    await peer.SendAsync("error", new { code = FailureCode(error, "worker.frameInvalid") }, frame, lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                await peer.SendAsync("accepted", new { }, frame, lifetime.Token).ConfigureAwait(false);
                var recognizer = new FishingRecognizer(catalog);
                lock (gate)
                {
                    var run = new Watch(frame, CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token));
                    active = run;
                    work = Task.Run(() => RunWatchAsync(run, watch, source, peer, clock, recognizer, lifetime, () =>
                    {
                        lock (gate)
                            if (ReferenceEquals(active, run)) active = null;
                    }), CancellationToken.None);
                }
            }
        }
        finally
        {
            lifetime.Cancel();
            lock (gate) active?.Stop.Cancel();
            try { await work.ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or WorkerException) { }
        }
    }

    private static bool Matches(Watch? active, WorkerFrame frame) => active is not null &&
        active.Scope.RequestId == frame.RequestId && active.Scope.RunId == frame.RunId &&
        active.Scope.BindingRevision == frame.BindingRevision && active.Scope.FencingToken == frame.FencingToken;

    /// <summary>只接受 life.fishing.watch 与 1280×960 客户区；参数超出 schema 范围或字段不全一律是无效帧。</summary>
    private static LifeVisionWatchArguments ParseRequest(WorkerFrame frame)
    {
        var payload = frame.Payload;
        if (payload.EnumerateObject().Count() != 2 || !payload.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String ||
            !payload.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object ||
            method.GetString() != LifeVisionProfiles.WatchMethod || arguments.EnumerateObject().Count() != 7)
            throw new WorkerException("worker.frameInvalid");
        LifeVisionWatchArguments watch;
        try { watch = arguments.Deserialize<LifeVisionWatchArguments>(ContractJson.Options) ?? throw new WorkerException("worker.frameInvalid"); }
        catch (JsonException) { throw new WorkerException("worker.frameInvalid"); }
        if (watch.Profile != LifeVisionProfiles.TwFishing1280x960 || watch.ProcessId <= 0 || watch.WindowIdentity.Value == 0 ||
            watch.ProcessStartTime == default || watch.Dpi is < 48 or > 768 || watch.PeriodMilliseconds is < 50 or > 500)
            throw new WorkerException("worker.frameInvalid");
        if (watch.Client.Width != LifeVisionProfiles.ClientWidth || watch.Client.Height != LifeVisionProfiles.ClientHeight)
            throw new WorkerException("life.fishing.layoutUnsupported");
        return watch;
    }

    private static async Task RunWatchAsync(Watch run, LifeVisionWatchArguments watch, IFrameSource source, WorkerPeerConnection peer,
        IVisionClock clock, FishingRecognizer recognizer, CancellationTokenSource lifetime, Action completed)
    {
        Exception? failure = null;
        try { await ObserveAsync(run, watch, source, peer, clock, recognizer, lifetime.Token).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        // 先让读帧循环看不到本次运行，再释放取消源：cancel 帧只会在持锁时对仍登记的运行调用 Cancel。
        completed();
        run.Stop.Dispose();
        if (lifetime.IsCancellationRequested) return;
        try
        {
            if (failure is null or OperationCanceledException)
                await peer.SendAsync("cancelled", new { }, run.Scope, lifetime.Token).ConfigureAwait(false);
            else if (failure is HostGoneException) lifetime.Cancel();
            else await peer.SendAsync("error", new { code = FailureCode(failure, "capture.sourceFailed") }, run.Scope, lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or WorkerException)
        { lifetime.Cancel(); }
    }

    /// <summary>
    /// 观察循环：截图、识别、发送，然后把截图与识别耗时从周期里扣除再等待。下一帧的计划时刻按周期累加，
    /// 定时器的偏差不会累积；一帧超过周期时下一帧立即开始，不补发。取消后不再输出观察。
    /// </summary>
    private static async Task ObserveAsync(Watch run, LifeVisionWatchArguments watch, IFrameSource source, WorkerPeerConnection peer,
        IVisionClock clock, FishingRecognizer recognizer, CancellationToken lifetime)
    {
        var token = run.Stop.Token;
        ulong frameId = 0;
        var due = clock.NowMilliseconds;
        while (!token.IsCancellationRequested)
        {
            var started = clock.NowMilliseconds;
            RgbImage image;
            try { image = await source.CaptureAsync(watch, token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            { throw error is WorkerException { Code: var code } && FailureCodes.Contains(code) ? error : new WorkerException("capture.sourceFailed", error); }
            var captured = clock.NowMilliseconds;
            token.ThrowIfCancellationRequested();
            FishingObservation observation;
            try { observation = recognizer.Recognize(image, captured); }
            catch (Exception error) when (error is not OperationCanceledException)
            { throw error is WorkerException { Code: var code } && FailureCodes.Contains(code) ? error : new WorkerException("capture.frameInvalid", error); }
            var recognized = clock.NowMilliseconds;
            observation = observation with
            {
                FrameId = new UInt64String(++frameId),
                CaptureMilliseconds = captured - started,
                RecognizeMilliseconds = recognized - captured
            };
            var payload = JsonSerializer.SerializeToUtf8Bytes(new LifeVisionEvent(observation), ContractJson.Options);
            // 写帧用进程生命周期的令牌：取消请求不能打断写到一半的帧，否则会留下半个帧破坏后续的 cancelled。
            try { await peer.SendEncodedAsync("event", payload, run.Scope, lifetime).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or WorkerException)
            { throw new HostGoneException(error); }
            due += watch.PeriodMilliseconds;
            var wait = due - clock.NowMilliseconds;
            if (wait <= 0) due = clock.NowMilliseconds;
            else await clock.DelayAsync(wait, token).ConfigureAwait(false);
        }
    }

    private static string FailureCode(Exception error, string fallback) =>
        error is WorkerException { Code: var code } && FailureCodes.Contains(code) ? code : fallback;

    private sealed class Watch(WorkerFrame scope, CancellationTokenSource stop)
    {
        public WorkerFrame Scope { get; } = scope;
        public CancellationTokenSource Stop { get; } = stop;
    }

    /// <summary>事件无法写出：宿主已断开，Worker 随即退出，不再尝试回复。</summary>
    private sealed class HostGoneException(Exception inner) : Exception("宿主已断开", inner);
}

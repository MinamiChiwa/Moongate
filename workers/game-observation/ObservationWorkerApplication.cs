using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Workers;

namespace MabiTool.Observation.Worker;

[SupportedOSPlatform("windows")]
public static class ObservationWorkerApplication
{
    public static async Task RunAsync(IObservationCaptureSource source, CancellationToken ct = default)
    {
        WorkerMemoryTrace.StartFromEnvironment();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var peer = await WorkerPeerConnection.ConnectFromEnvironmentAsync("game-observation", lifetime.Token);
        var assetRoot = Environment.GetEnvironmentVariable("MABITOOL_WORKER_ASSET_ROOT")
            ?? throw new WorkerException("worker.assetInvalid");
        Environment.SetEnvironmentVariable("MABITOOL_WORKER_ASSET_ROOT", null);
        // Host 以启动环境声明可受理 captureDiagnostics 事件；未声明的旧 Host 不会收到该事件。
        var reportDiagnostics = Environment.GetEnvironmentVariable(ObservationCaptureCounters.EnvironmentVariable) == "1";
        Environment.SetEnvironmentVariable(ObservationCaptureCounters.EnvironmentVariable, null);
        using var assets = new InventoryAssetStore(assetRoot);
        var gate = new object();
        WorkerFrame? active = null;
        CancellationTokenSource? stop = null;
        ObservationCaptureRun? captureRun = null;
        Task work = Task.CompletedTask;
        Task commandWork = Task.CompletedTask;
        var pendingCommands = 0;
        var rateStart = Stopwatch.GetTimestamp();
        var rateCount = 0;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(6));
                var frame = await peer.ReadAsync(deadline.Token);
                if (Stopwatch.GetElapsedTime(rateStart) >= TimeSpan.FromSeconds(1)) { rateStart = Stopwatch.GetTimestamp(); rateCount = 0; }
                if (++rateCount > 256) throw new WorkerException("worker.outputRateExceeded");
                if (frame.Kind == "ping")
                {
                    if (frame.Payload.EnumerateObject().Any()) throw new WorkerException("worker.frameInvalid");
                    await peer.SendAsync("pong", new { }, null, lifetime.Token); continue;
                }
                if (frame.Kind == "shutdown") break;
                if (frame.Kind == "cancel")
                {
                    if (frame.Payload.EnumerateObject().Any()) throw new WorkerException("worker.frameInvalid");
                    lock (gate)
                        if (Matches(active, frame)) stop?.Cancel();
                    continue;
                }
                if (frame.Kind == "command")
                {
                    ObservationCaptureRun current;
                    CancellationToken commandToken;
                    lock (gate)
                    {
                        current = Matches(active, frame) && captureRun is not null && pendingCommands < 2
                            ? captureRun : throw new WorkerException("worker.commandInvalid");
                        commandToken = stop!.Token;
                        var previousCommand = commandWork;
                        pendingCommands++;
                        commandWork = Task.Run(async () =>
                        {
                            try
                            {
                                await previousCommand.ConfigureAwait(false);
                                commandToken.ThrowIfCancellationRequested();
                                await current.ConfigureAsync(frame, commandToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (commandToken.IsCancellationRequested) { }
                            catch (Exception error)
                            { await peer.SendAsync("error", new { code = Code(error) }, frame, lifetime.Token).ConfigureAwait(false); }
                            finally { lock (gate) pendingCommands--; }
                        }, CancellationToken.None);
                    }
                    continue;
                }
                if (frame.Kind != "request") throw new WorkerException("worker.frameInvalid");
                lock (gate)
                    if (active is not null) throw new WorkerException("worker.busy");
                try
                {
                    var payload = frame.Payload;
                    if (payload.EnumerateObject().Count() != 2 || !payload.TryGetProperty("method", out var method) ||
                        method.ValueKind != JsonValueKind.String || !payload.TryGetProperty("arguments", out var arguments) ||
                        arguments.ValueKind != JsonValueKind.Object) throw new WorkerException("worker.frameInvalid");
                    if (method.GetString() == "observation.channels")
                    {
                        CaptureChannelQuery? query = null;
                        if (arguments.EnumerateObject().Any())
                        {
                            query = arguments.Deserialize<CaptureChannelQuery>(ContractJson.Options) ?? throw new WorkerException("worker.frameInvalid");
                            if (query.ProcessId <= 0 || query.ProcessStartTime.Offset != TimeSpan.Zero) throw new WorkerException("worker.frameInvalid");
                        }
                        await peer.SendAsync("accepted", new { }, frame, lifetime.Token);
                        var channels = source.Channels(query);
                        if (channels.Count > 128) throw new WorkerException("worker.frameInvalid");
                        await peer.SendAsync("result", new { channels }, frame, lifetime.Token); continue;
                    }
                    if (method.GetString() == "observation.channels.detect")
                    {
                        var detect = arguments.Deserialize<ChannelDetectionRequest>(ContractJson.Options) ?? throw new WorkerException("worker.frameInvalid");
                        if (detect.ProcessId <= 0 || detect.ProcessStartTime.Offset != TimeSpan.Zero ||
                            detect.DurationMs is < ChannelTrafficDetector.MinimumDurationMs or > ChannelTrafficDetector.MaximumDurationMs)
                            throw new WorkerException("worker.frameInvalid");
                        await peer.SendAsync("accepted", new { }, frame, lifetime.Token);
                        lock (gate)
                        {
                            stop?.Dispose();
                            stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                            active = frame; captureRun = null;
                            var currentStop = stop;
                            work = Task.Run(async () =>
                            {
                                ChannelDetectionProgress? result = null; Exception? failure = null;
                                try
                                {
                                    result = await source.DetectChannelsAsync(detect, value => peer.SendAsync("event", new { detection = value }, frame, lifetime.Token),
                                        currentStop.Token).ConfigureAwait(false);
                                }
                                catch (Exception error) { failure = error; }
                                lock (gate) if (ReferenceEquals(active, frame)) active = null;
                                if (lifetime.IsCancellationRequested) return;
                                if (currentStop.IsCancellationRequested)
                                    await peer.SendAsync("cancelled", new { }, frame, lifetime.Token).ConfigureAwait(false);
                                else if (failure is not null)
                                    await peer.SendAsync("error", new { code = Code(failure) }, frame, lifetime.Token).ConfigureAwait(false);
                                else await peer.SendAsync("result", new { detection = result }, frame, lifetime.Token).ConfigureAwait(false);
                            }, CancellationToken.None);
                        }
                        continue;
                    }
                    if (method.GetString() is "inventory.prepare" or "inventory.catalog")
                    {
                        var client = arguments.Deserialize<InventoryClientRequest>(ContractJson.Options)
                            ?? throw new WorkerException("worker.frameInvalid");
                        if (string.IsNullOrWhiteSpace(client.GameSessionId) || client.GameSessionId.Length > 128 ||
                            client.Region != "TW" || client.ProcessId <= 0 || client.ProcessStartTime == default ||
                            client.ProcessStartTime.Offset != TimeSpan.Zero || frame.BindingRevision == "0")
                            throw new WorkerException("worker.frameInvalid");
                        await peer.SendAsync("accepted", new { }, frame, lifetime.Token);
                        lock (gate)
                        {
                            stop?.Dispose();
                            stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                            active = frame; captureRun = null;
                            var currentStop = stop;
                            var jobMethod = method.GetString()!;
                            work = Task.Run(async () =>
                            {
                                object? result = null; Exception? failure = null;
                                WorkerMemoryTrace.Mark(jobMethod == "inventory.prepare" ? "layout.prepareStarted" : "catalog.started");
                                try
                                {
                                    result = jobMethod == "inventory.prepare"
                                        ? new { prepared = await source.PrepareInventoryAsync(client, currentStop.Token).ConfigureAwait(false) }
                                        : (object)new { catalogAsset = await assets.WriteCatalogAsync(
                                            await source.ReadInventoryCatalogAsync(client, currentStop.Token).ConfigureAwait(false), currentStop.Token).ConfigureAwait(false) };
                                }
                                catch (Exception error) { failure = error; }
                                WorkerMemoryTrace.Mark(jobMethod == "inventory.prepare" ? "layout.prepareFinished" : "catalog.finished");
                                lock (gate) if (ReferenceEquals(active, frame)) active = null;
                                if (lifetime.IsCancellationRequested) return;
                                if (currentStop.IsCancellationRequested)
                                    await peer.SendAsync("cancelled", new { }, frame, lifetime.Token).ConfigureAwait(false);
                                else if (failure is not null)
                                    await peer.SendAsync("error", new { code = Code(failure) }, frame, lifetime.Token).ConfigureAwait(false);
                                else await peer.SendAsync("result", result!, frame, lifetime.Token).ConfigureAwait(false);
                            }, CancellationToken.None);
                        }
                        continue;
                    }
                    if (method.GetString() != "observation.capture") throw new WorkerException("worker.methodUnsupported");
                    var request = arguments.Deserialize<ObservationCaptureRequest>(ContractJson.Options)
                        ?? throw new WorkerException("worker.frameInvalid");
                    if (string.IsNullOrWhiteSpace(request.GameSessionId) || request.GameSessionId.Length > 128 ||
                        request.Region != "TW" || request.ProcessId <= 0 || request.ProcessStartTime.Offset != TimeSpan.Zero ||
                        request.ChannelId is not null && !WorkerProtocol.IsId(request.ChannelId) || request.Profile != MabiTool.Providers.TwObservation.TwGameProtocol.CaptureProfile &&
                        !MabiTool.Providers.TwObservation.Combat.Tw20261110CombatProtocol.IsCaptureProfile(request.Profile) || frame.BindingRevision == "0")
                        throw new WorkerObservationException("observation.profileUnavailable");
                    if (!request.TryKnownIdentity(out _)) throw new WorkerException("worker.frameInvalid");
                    await peer.SendAsync("accepted", new { }, frame, lifetime.Token);
                    lock (gate)
                    {
                        stop?.Dispose();
                        stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        active = frame;
                        var currentStop = stop;
                        captureRun = new(peer, source, request, frame,
                            (report, token) => assets.PrepareInventoryAsync(report, token), reportDiagnostics);
                        captureRun.Initialize();
                        var currentRun = captureRun;
                        work = Task.Run(async () =>
                        {
                            try { await CaptureAsync(peer, currentRun, frame, currentStop, lifetime.Token,
                                () => { lock (gate) if (ReferenceEquals(active, frame)) active = null; }); }
                            finally { lock (gate) if (ReferenceEquals(active, frame)) active = null; }
                        }, CancellationToken.None);
                    }
                }
                catch (Exception error) when (error is WorkerException or WorkerObservationException or JsonException or
                    DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or IOException)
                { await peer.SendAsync("error", new { code = Code(error) }, frame, lifetime.Token); }
            }
        }
        finally
        {
            lifetime.Cancel();
            lock (gate) stop?.Cancel();
            try { await work.ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or IOException or WorkerException) { }
            try { await commandWork.ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or IOException or WorkerException) { }
            stop?.Dispose();
        }
    }
    private static bool Matches(WorkerFrame? active, WorkerFrame frame) => active is not null &&
        active.RequestId == frame.RequestId && active.RunId == frame.RunId &&
        active.BindingRevision == frame.BindingRevision && active.FencingToken == frame.FencingToken;

    private static async Task CaptureAsync(WorkerPeerConnection peer, ObservationCaptureRun run,
        WorkerFrame scope, CancellationTokenSource stop, CancellationToken lifetime, Action completed)
    {
        Exception? failure = null;
        try { await run.RunAsync(stop, lifetime).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        completed();
        if (lifetime.IsCancellationRequested) return;
        if (failure is OperationCanceledException && stop.IsCancellationRequested || failure is null && stop.IsCancellationRequested)
            await peer.SendAsync("cancelled", new { }, scope, lifetime);
        else if (failure is not null) await peer.SendAsync("error", new { code = Code(failure) }, scope, lifetime);
        else await peer.SendAsync("result", new { }, scope, lifetime);
    }
    private static string Code(Exception error) => error switch
    {
        WorkerException value => value.Code,
        WorkerObservationException value => value.Code,
        DllNotFoundException or BadImageFormatException or EntryPointNotFoundException => "observation.npcapUnavailable",
        _ => "observation.captureFailed"
    };
}

using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Game;

/// <summary>运行状态总览的观察侧读数：只在锁内复制已有字段，不启动 Worker 或发送命令。</summary>
public sealed partial class GameObservationService
{
    private ulong workerStarts, workerFaults, failedStops;
    private string? lastFault;
    private void RecordFault(string code, bool stop = false)
    {
        lastFault = code;
        if (stop) failedStops++; else workerFaults++;
    }

    internal DiagnosticProbeReading CaptureProbe()
    {
        lock (gate)
        {
            var now = Mono();
            var reading = new DiagnosticProbeReading(state, reason)
                .Flag("installed", options() is not null).Value("profile", profile).Value("stateReason", reason).Value("lastFault", lastFault)
                .Flag("interests.music", appliedInterests.MusicEnabled).Flag("interests.combat", appliedInterests.Combat is not null)
                .Flag("interests.inventory", appliedInterests.Inventory is not null)
                .Count("subscriptions", TotalDemands).Count("subscriptions.music", demands.Count)
                .Count("subscriptions.combat", combatDemands.Count).Count("subscriptions.inventory", inventoryDemands.Count)
                .Count("worker.starts", workerStarts).Count("worker.faults", workerFaults).Count("worker.failedStops", failedStops)
                .Count("reports.accepted", sequence).Count("reports.generations", generation).Count("revision", revision)
                .Milliseconds("lastFrameAgeMs", lastArrivalMono is { } last ? now - last : null)
                .Milliseconds("lastArrivalIntervalMs", arrivalIntervalMs);
            if (workerPid is { } pid) reading.Count("worker.pid", pid);
            if (worker is not null && state == "starting") reading.Milliseconds("startingMs", clock.GetElapsedTime(startingAt).TotalMilliseconds);
            foreach (var kind in WorkerEndKinds) reading.Count("worker.ends." + kind, workerEnds.GetValueOrDefault(kind));
            reading.Value("worker.lastEnd", lastWorkerEnd);
            if (lastExitCode is { } exit) reading.Count("worker.lastExitCode", unchecked((uint)exit));
            if (lastWorkerEnd is not null) reading.Flag("worker.lastTerminatedByHost", lastTerminatedByHost);
            AppendCaptureCounters(reading);
            return reading;
        }
    }

    // Worker 结束分类：正常停止、心跳超时、协议错误（帧/序列/阶段/身份/命令/速率/背压）、通道断开及其他错误码。
    private static readonly string[] WorkerEndKinds = ["normal", "heartbeatTimeout", "protocolError", "disconnected", "other"];
    private static readonly HashSet<string> ProtocolErrors = new(StringComparer.Ordinal)
    {
        "worker.frameInvalid", "worker.sequenceInvalid", "worker.phaseInvalid", "worker.requestIdentityInvalid",
        "worker.commandInvalid", "worker.outputRateExceeded", "worker.outputBackpressure", "worker.portInvalid",
        "worker.handshakeInvalid", "worker.protocolIncompatible"
    };
    private readonly Dictionary<string, ulong> workerEnds = new(StringComparer.Ordinal);
    private string? lastWorkerEnd;
    private int? lastExitCode;
    private bool lastTerminatedByHost;
    internal static string WorkerEndKind(string? code) => code switch
    {
        null => "normal",
        "worker.heartbeatTimeout" => "heartbeatTimeout",
        "worker.disconnected" => "disconnected",
        _ when ProtocolErrors.Contains(code) => "protocolError",
        _ => "other"
    };
    private void RecordWorkerEnd(string? code, int? exitCode, bool terminatedByHost)
    {
        var kind = WorkerEndKind(code);
        workerEnds[kind] = workerEnds.GetValueOrDefault(kind) + 1;
        lastWorkerEnd = kind; lastExitCode = exitCode; lastTerminatedByHost = terminatedByHost;
    }

    // Worker 上报的捕获计数：只保留最新一份，新捕获开始时清空；旧 Worker 不上报时探针省略相应键。
    private ObservationCaptureCounters? captureCounters;
    private ulong captureCounterReports;
    private void ResetCaptureCounters() { lock (gate) { captureCounters = null; captureCounterReports = 0; } }
    private bool AcceptCaptureDiagnostics(WorkerFrame frame, WorkerSession session, string id)
    {
        if (!frame.Payload.TryGetProperty("captureDiagnostics", out var value)) return false;
        if (frame.Payload.EnumerateObject().Count() != 1) throw new WorkerException("worker.frameInvalid");
        ObservationCaptureCounters? counters;
        try { counters = value.Deserialize<ObservationCaptureCounters>(ContractJson.Options); }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException) { throw new WorkerException("worker.frameInvalid", error); }
        if (!ValidCounters(counters)) throw new WorkerException("worker.frameInvalid");
        ObservationCaptureCounters? previous;
        lock (gate)
        {
            if (worker != session || captureId != id) return true;
            previous = captureCounters; captureCounters = counters; captureCounterReports++;
        }
        if ((previous is null || previous.Connections.Opened != counters.Connections.Opened || previous.Connections.Closed != counters.Connections.Closed) &&
            trace?.IsEnabled(DiagnosticEvents.ConnectionChanged) == true)
            trace.Emit(DiagnosticEvents.ConnectionChanged, new DiagnosticFields().Count("current", counters.Connections.Current.Value)
                .Count("opened", counters.Connections.Opened.Value).Count("closed", counters.Connections.Closed.Value));
        return true;
    }
    internal static bool ValidCounters([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] ObservationCaptureCounters? value)
    {
        static bool Code(string key) => key.Length is > 0 and <= 10 && key.All(char.IsAsciiDigit) && (key == "0" || key[0] != '0') &&
            ulong.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var code) && code <= int.MaxValue;
        return value is { SchemaVersion: 1, Connections: not null, Packets: not null, Queues: not null, Reassembly.Resets: not null,
                Frames: { Rejected: not null, Types: not null } } &&
            value.Connections.Current.Value <= value.Connections.Opened.Value &&
            value.Reassembly.Resets.Count <= ObservationCaptureCounters.ResetCauses.Count &&
            value.Reassembly.Resets.Keys.All(ObservationCaptureCounters.ResetCauses.Contains) &&
            value.Frames.Rejected.Keys.All(ObservationCaptureCounters.RejectionBranches.Contains) &&
            value.Frames.Types.Count <= ObservationCaptureCounters.MaximumFrameTypes && value.Frames.Types.Keys.All(Code) &&
            (value.Devices is null || value.Devices.Count <= ObservationCaptureCounters.MaximumDevices && value.Devices.All(device => device is not null &&
                WorkerProtocol.IsId(device.ChannelId) && ObservationCaptureCounters.DeviceKinds.Contains(device.Kind) && device.LinkType >= -1 &&
                (device.Issue is null || DiagnosticSanitizer.IsProbeValue(device.Issue))) && value.Devices.Count(device => device.Primary) <= 1);
    }
    private void AppendCaptureCounters(DiagnosticProbeReading reading)
    {
        if (captureCounters is not { } value) return;
        static ulong U(UInt64String number) => number.Value;
        reading.Count("workerCounters.reports", captureCounterReports)
            .Count("connections.current", U(value.Connections.Current)).Count("connections.opened", U(value.Connections.Opened))
            .Count("connections.closed", U(value.Connections.Closed))
            .Count("packets.received", U(value.Packets.Received)).Count("packets.bytes", U(value.Packets.Bytes))
            .Count("packets.filtered", U(value.Packets.Filtered)).Count("packets.truncated", U(value.Packets.Truncated))
            .Count("packets.driverDropped", U(value.Packets.DriverDropped)).Count("packets.readLimitHits", U(value.Packets.ReadLimitHits))
            .Count("queues.combat.depth", U(value.Queues.CombatDepth)).Count("queues.combat.peak", U(value.Queues.CombatPeak))
            .Count("queues.inventory.depth", U(value.Queues.InventoryDepth)).Count("queues.inventory.peak", U(value.Queues.InventoryPeak))
            .Count("queues.inventoryLane.rejected", U(value.Queues.InventoryLaneRejected))
            .Count("reassembly.segments", U(value.Reassembly.Segments)).Count("reassembly.bytes", U(value.Reassembly.Bytes))
            .Count("reassembly.outOfOrder", U(value.Reassembly.OutOfOrder)).Count("reassembly.retransmitted", U(value.Reassembly.Retransmitted))
            .Count("reassembly.gaps", value.Reassembly.Resets.Where(pair => pair.Key is "gapTimeout" or "pendingCapacity" or "captureDrops")
                .Aggregate(0UL, (sum, pair) => sum + pair.Value.Value));
        foreach (var (cause, count) in value.Reassembly.Resets.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            reading.Count("reassembly.resets." + cause, U(count));
        reading.Count("frames.total", U(value.Frames.Total)).Count("frames.dispatched", U(value.Frames.Dispatched))
            .Count("frames.decompressFailures", U(value.Frames.DecompressFailures))
            .Count("frames.rejected", value.Frames.Rejected.Values.Aggregate(0UL, (sum, count) => sum + count.Value));
        foreach (var (branch, count) in value.Frames.Rejected.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            reading.Count("frames.rejected." + branch, U(count));
        foreach (var (code, count) in value.Frames.Types.OrderBy(pair => int.Parse(pair.Key, System.Globalization.CultureInfo.InvariantCulture)))
            reading.Count("frames.types.op" + code, U(count));
        reading.Count("frames.types.other", U(value.Frames.OtherFrames));
    }

    internal DiagnosticProbeReading MusicProbe()
    {
        lock (gate)
        {
            var value = latest;
            var quality = value is null ? "none" : Mono() - value.MonotonicMs > value.ValidForMs ? "stale" : value.Quality.RawValue;
            var reading = new DiagnosticProbeReading(demands.Count == 0 ? "idle" : value is null ? "waiting" : "observing")
                .Count("subscriptions", demands.Count).Flag("interestApplied", appliedInterests.MusicEnabled)
                .Value("quality", quality).Flag("identityPresent", value?.Value.Identity is not null)
                .Flag("concertPresent", value?.Value.Concert is not null).Flag("musicPresent", value?.Value.Music is not null);
            if (value is not null) reading.Milliseconds("observationAgeMs", Mono() - value.MonotonicMs);
            return reading;
        }
    }

    /// <summary>共享捕获上的战斗兴趣、首个帧拒绝分支与缺口计数。</summary>
    internal void AppendCombatCapture(DiagnosticProbeReading reading)
    {
        lock (gate)
        {
            reading.Count("capture.subscriptions", combatDemands.Count).Value("capture.interestProfile", appliedInterests.Combat?.Profile)
                .Value("capture.frameRejection", frameRejection?.Branch)
                .Count("capture.gaps", combatGaps.Count + combatGapsTruncated).Count("capture.gapsTruncated", combatGapsTruncated)
                .Count("capture.gapDroppedEvents", combatGaps.Aggregate(0UL, (sum, gap) => sum + gap.DroppedEvents));
            if (frameRejection is { } rejection) reading.Milliseconds("capture.frameRejectionAgeMs", rejection.FrameAgeMs);
            reading.Count("capture.frameRejections", frameRejections);
            foreach (var (branch, count) in frameRejectionBranches.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                reading.Count("capture.frameRejections." + branch, count);
            foreach (var group in combatGaps.GroupBy(gap => gap.Reason, StringComparer.Ordinal))
                reading.Count("capture.gapReasons." + group.Key, group.Count());
        }
    }

    /// <summary>共享捕获上的库存兴趣、布局准备与帧种类/原因计数；不含身份与条目内容。</summary>
    internal void AppendInventoryCapture(DiagnosticProbeReading reading)
    {
        lock (gate)
        {
            reading.Count("capture.subscriptions", inventoryDemands.Count).Flag("capture.layoutPrepared", inventoryProfile is not null)
                .Value("capture.interestProfile", appliedInterests.Inventory?.Profile)
                .Count("capture.frames", inventoryFrameCounts.Values.Sum()).Count("capture.framesTruncated", inventoryFramesTruncated);
            if (inventoryProfile is { } prepared)
                reading.Flag("capture.fullInventory", prepared.FullInventory).Flag("capture.incrementalInventory", prepared.IncrementalInventory);
            if (lastInventoryFrame is { } last)
                reading.Value("capture.lastFrameKind", last.Kind.ToLowerInvariant()).Flag("capture.lastFrameIdentity", last.IdentityPresent)
                    .Value("capture.lastFrameReason", last.Reason).Milliseconds("capture.lastFrameAgeMs", Mono() - last.HostMonotonicMs);
            // 计数键为“种类”或“种类:原因码”，分别按种类和原因汇总。
            foreach (var group in inventoryFrameCounts.GroupBy(pair => pair.Key.Split(':')[0].ToLowerInvariant(), StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
                reading.Count("capture.frames." + group.Key, group.Sum(pair => pair.Value));
            foreach (var group in inventoryFrameCounts.Where(pair => pair.Key.Contains(':')).GroupBy(pair => pair.Key[(pair.Key.IndexOf(':') + 1)..], StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal))
                reading.Count("capture.frameReasons." + group.Key, group.Sum(pair => pair.Value));
        }
    }
}

internal sealed class ObservationCaptureDiagnosticProbe(GameObservationService observation) : IDiagnosticProbe
{
    public string Id => "observation.capture";
    public string Module => "game-access";
    public DiagnosticProbeReading Capture() => observation.CaptureProbe();
}

internal sealed class MusicDiagnosticProbe(GameObservationService observation) : IDiagnosticProbe
{
    public string Id => "music";
    public string Module => "music-library";
    public DiagnosticProbeReading Capture() => observation.MusicProbe();
}

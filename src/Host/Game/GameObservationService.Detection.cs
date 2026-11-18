using System.Runtime.Versioning;
using MabiTool.Infrastructure;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Workers;

namespace MabiTool.Host.Game;

/// <summary>
/// 手动调试的网卡检测：独立短时 Worker 在候选网卡上同时监听游戏连接下行流量，进度由
/// <see cref="Detection"/> 读取。同一时间只运行一次；时长 5–60 秒，Worker 侧另有 16 张网卡、200,000 包与
/// 每句柄 1 MiB 缓冲上限；宿主在时长加 15 秒后强制结束。结束、取消或失败均关闭 Worker 与全部句柄。
/// 检测与常驻捕获可同时运行。结果只供确认，保存由设置接口完成。
/// </summary>
public sealed partial class GameObservationService
{
    private CaptureChannelDetection? detection;
    private CancellationTokenSource? detectionStop;
    private Task detectionTask = Task.CompletedTask;
    private (string Owner, string Key, string Hash)? detectionReceipt;
    private bool DetectionRunning { get { lock (gate) return detection?.State == "running"; } }

    public CaptureChannelDetection Detection()
    {
        lock (gate) return detection ?? new(system.InstanceId, null, "idle", null, 0, 0, [], null, null, null, null);
    }

    public async Task<CaptureChannelDetection> StartDetectionAsync(string owner, CaptureChannelDetectionRequest request, string? key, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new OperationException("observation.workerUnavailable", "观察组件需要 Windows");
        Authorize(owner);
        if (key is null) throw new OperationException("idempotency.required", "网卡检测需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z") || request.DurationSeconds is < 5 or > 60)
            throw new OperationException("request.invalid", "检测时长或幂等键无效");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, ContractJson.Options))));
        lock (gate)
        {
            if (detectionReceipt is { } receipt && receipt.Owner == owner && receipt.Key == key)
                return receipt.Hash == hash ? Detection() : throw new OperationException("idempotency.conflict", "幂等键已用于其他检测");
            if (detection?.State == "running") throw new OperationException("observation.channelDetecting", "网卡检测正在进行");
        }
        var binding = controls.Bindings.Current?.Session ?? throw new OperationException("input.bindingUnavailable", "请先选择游戏窗口");
        if (binding.BindingRevision != request.BindingRevision) throw new OperationException("input.bindingChanged", "游戏绑定修订已变化");
        if (binding.Region != "TW") throw new OperationException("observation.profileUnavailable", "当前地区尚未验证");
        var configuration = Configuration();
        var id = Guid.NewGuid().ToString("N");
        var stop = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        // 检测时长之外留出 Worker 启动握手（至多 60 秒）的余量。
        stop.CancelAfter(TimeSpan.FromSeconds(request.DurationSeconds + 60));
        IDisposable? hold = null; IAsyncDisposable? module = null; WorkerSession? session = null; WorkerCall? request0;
        try
        {
            hold = barrier.EnterActivity(owner, "game.observation.detection");
            module = await modules.AcquireAsync("game-access", "observation-detection-" + id, ct);
            session = await WorkerSession.StartAsync(configuration, _ => { }, ct);
            request0 = await session.BeginAsync(new("observation.channels.detect", JsonSerializer.SerializeToElement(new
            {
                processId = binding.ProcessId, processStartTime = binding.ProcessStartTime, durationMs = request.DurationSeconds * 1000
            }, ContractJson.Options), id, binding.BindingRevision.ToString(), "0"), ct);
        }
        catch (Exception error)
        {
            stop.Dispose();
            if (session is not null) await session.DisposeAsync();
            if (module is not null) await module.DisposeAsync();
            hold?.Dispose();
            throw error is WorkerException failure ? new OperationException("observation.captureFailed", failure.Code) : error;
        }
        lock (gate)
        {
            detectionStop?.Dispose(); detectionStop = stop; detectionReceipt = (owner, key, hash);
            detection = new(system.InstanceId, id, "running", binding.BindingRevision, request.DurationSeconds, 0, [], null, null, clock.GetUtcNow(), null);
            revision++;
            detectionTask = RunDetectionAsync(id, session, request0, module, hold, stop.Token);
        }
        Notify();
        return Detection();
    }

    public async Task<CaptureChannelDetection> CancelDetectionAsync(string owner)
    {
        Authorize(owner);
        Task running;
        lock (gate) { if (detection?.State == "running") detectionStop?.Cancel(); running = detectionTask; }
        await running.ConfigureAwait(false);
        return Detection();
    }

    [SupportedOSPlatform("windows")]
    private async Task RunDetectionAsync(string id, WorkerSession session, WorkerCall request, IAsyncDisposable module, IDisposable hold, CancellationToken stop)
    {
        string finalState = "failed"; string? code = null; ChannelProgress? last = null;
        try
        {
            using var cancel = stop.Register(() => _ = CancelQuietlyAsync(session, request));
            await foreach (var frame in request.Events.ReadAllAsync(CancellationToken.None))
            {
                if (frame.Kind != "event") continue;
                if (frame.Payload.EnumerateObject().Count() != 1 || !frame.Payload.TryGetProperty("detection", out var value))
                    throw new WorkerException("worker.frameInvalid");
                last = Validate(value.Deserialize<ChannelProgress>(ContractJson.Options));
                Apply(id, last, "running", null);
            }
            var terminal = await request.Completion;
            if (terminal.Kind == "result" && terminal.Payload.TryGetProperty("detection", out var result))
            {
                last = Validate(result.Deserialize<ChannelProgress>(ContractJson.Options));
                finalState = "completed"; code = last.Reason;
            }
            else if (terminal.Kind == "cancelled") finalState = "cancelled";
            else code = Failure(terminal).Code;
        }
        catch (Exception error) when (error is WorkerException or JsonException or IOException or OperationCanceledException or InvalidOperationException)
        { code = error is WorkerException failure ? failure.Code : "observation.captureFailed"; }
        finally
        {
            try { await session.DisposeAsync(); }
            finally
            {
                try { await module.DisposeAsync(); } finally { hold.Dispose(); }
            }
        }
        Apply(id, last, finalState, code, end: true);
        Notify();
    }
    [SupportedOSPlatform("windows")]
    private static async Task CancelQuietlyAsync(WorkerSession session, WorkerCall request)
    {
        try { await session.CancelAsync(request); }
        catch (Exception error) when (error is WorkerException or IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    private void Apply(string id, ChannelProgress? value, string nextState, string? code, bool end = false)
    {
        lock (gate)
        {
            if (detection?.Id != id) return;
            detection = detection with
            {
                State = nextState, ElapsedSeconds = value?.ElapsedSeconds ?? detection.ElapsedSeconds,
                Channels = value?.Channels.Select(entry => new CaptureChannelDetectionEntry(entry.ChannelId, entry.Description, entry.Opened,
                    new((ulong)entry.Packets), new((ulong)entry.PayloadBytes), entry.Issue)).ToArray() ?? detection.Channels,
                WinnerChannelId = nextState == "completed" ? value?.WinnerChannelId : null,
                Reason = code, EndedAt = end ? clock.GetUtcNow() : null
            };
            revision++;
        }
        Notify();
    }
    private static ChannelProgress Validate(ChannelProgress? value)
    {
        if (value is null || !double.IsFinite(value.ElapsedSeconds) || value.ElapsedSeconds is < 0 or > 120 || value.Channels is null || value.Channels.Count > 16 ||
            value.Channels.Any(entry => entry is null || !WorkerProtocol.IsId(entry.ChannelId) || entry.Description is null or { Length: > 512 } ||
                entry.Packets is < 0 or > 200_000 || entry.PayloadBytes < 0 || entry.Issue is { Length: > 128 }) ||
            value.WinnerChannelId is not null && value.Channels.All(entry => entry.ChannelId != value.WinnerChannelId) ||
            value.Reason is { Length: > 128 })
            throw new WorkerException("worker.frameInvalid");
        return value;
    }
    private sealed record ChannelProgressEntry(string ChannelId, string Description, bool Opened, long Packets, long PayloadBytes, string? Issue);
    private sealed record ChannelProgress(double ElapsedSeconds, IReadOnlyList<ChannelProgressEntry> Channels, string? WinnerChannelId, string? Reason, bool Final);
}

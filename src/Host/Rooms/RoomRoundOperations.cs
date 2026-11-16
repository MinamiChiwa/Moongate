using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Host.Diagnostics;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Rooms;

/// <summary>
/// 轮次命令按受理顺序在后台执行；执行阶段的拒绝写入 Operation 结果，同时以 rooms 模块的 warning 记入诊断（错误码与操作 ID），
/// 排查时能看到“已受理但未生效”的原因。
/// </summary>
public sealed class RoomRoundOperations(OperationStore operations, RoomSessionManager rooms,
    ActivityBarrier activities, ModuleRuntime modules, DiagnosticRecorder? recorder = null) : BackgroundService
{
    public const int MaximumPendingCommands = 32;
    private sealed class Job(string id, string owner, string? roundId, RoomRoundRequest? prepare,
        RoomRoundControlRequest? control, string etag, IDisposable activity) : IDisposable
    {
        public string Id { get; } = id;
        public string Owner { get; } = owner;
        public string? RoundId { get; } = roundId;
        public RoomRoundRequest? Prepare { get; } = prepare;
        public RoomRoundControlRequest? Control { get; } = control;
        public string ETag { get; } = etag;
        public CancellationTokenSource Stop { get; } = new();
        public void Dispose() { Stop.Dispose(); activity.Dispose(); }
    }

    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);
    private readonly Channel<Job> queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(MaximumPendingCommands)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private bool accepting;

    public override Task StartAsync(CancellationToken ct) { accepting = true; return base.StartAsync(ct); }

    public Task<OperationSnapshot> PrepareAsync(AuthSession session, RoomRoundRequest request,
        string? key, string? etag, CancellationToken ct) =>
        AdmitAsync(session, null, Freeze(request), null, key, etag, ct);

    public Task<OperationSnapshot> ControlAsync(AuthSession session, string roundId, RoomRoundControlRequest request,
        string? key, string? etag, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(roundId) || roundId.Length > 128 ||
            roundId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new OperationException("request.invalid", "轮次 ID 无效");
        if (request.Action.KnownValue is null || request.Reason is { Length: > 256 } || request.Reason?.Any(char.IsControl) == true)
            throw new OperationException("request.invalid", "轮次动作或原因无效");
        return AdmitAsync(session, roundId, null, request with { }, key, etag, ct);
    }

    private async Task<OperationSnapshot> AdmitAsync(AuthSession session, string? roundId, RoomRoundRequest? prepare,
        RoomRoundControlRequest? control, string? key, string? etag, CancellationToken ct)
    {
        IDisposable? activity = null;
        await admission.WaitAsync(ct);
        try
        {
            var accepted = await operations.AdmitRoomRoundAsync(session.ClientId, roundId, prepare, control, key, etag,
                () => RoomSessionManager.RoundETag(rooms.GetRound(session.ClientId)), () =>
                {
                    if (!accepting) throw new OperationException("room.serverUnavailable", "轮次控制正在关闭");
                    if (jobs.Count >= MaximumPendingCommands) throw new OperationException("resource.capacityExceeded", "轮次命令队列已满");
                    rooms.ValidateRoundCommand(session.ClientId, roundId, prepare, control, etag!);
                    activity = activities.EnterActivity(session.ClientId, "room.round");
                }, ct);
            if (accepted.Created)
            {
                var job = new Job(accepted.Snapshot.Id, session.ClientId, roundId, prepare, control, etag!, activity!);
                activity = null; jobs.Add(job.Id, job);
                if (!queue.Writer.TryWrite(job)) throw new InvalidOperationException("已受理轮次命令未能进入保留队列");
            }
            return accepted.Snapshot;
        }
        finally { activity?.Dispose(); admission.Release(); }
    }

    public async Task<OperationSnapshot> CancelAsync(string id, AuthSession session, string? reason, CancellationToken ct)
    {
        await admission.WaitAsync(ct);
        try
        {
            if (!OperationStore.IsRoomRoundOperation(operations.Get(id, session.ClientId, session.Owner).Kind))
                throw new OperationException("resource.notFound", "轮次操作不存在");
            var result = await operations.CancelAsync(id, session.ClientId, session.Owner, reason, ct);
            if (result.State.KnownValue == OperationState.Cancelled && jobs.TryGetValue(id, out var job)) job.Stop.Cancel();
            return result;
        }
        finally { admission.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync())
        {
            using var hostStop = stoppingToken.Register(job.Stop.Cancel);
            try
            {
                if (await operations.BeginRoomRoundAsync(job.Id) is null) continue;
                await using var demand = await modules.AcquireAsync("rooms", job.Id, job.Stop.Token);
                var result = await rooms.ExecuteRoundCommandAsync(job.Owner, job.RoundId, job.Prepare, job.Control,
                    job.ETag, ct => operations.CommitRoomRoundAsync(job.Id, ct), job.Stop.Token);
                await operations.FinishRoomRoundAsync(job.Id, result);
            }
            catch (OperationCanceledException)
            {
                await operations.FinishRoomRoundAsync(job.Id, Result(job),
                    ApiErrors.Create("operation.interrupted", "后端停止了轮次命令", job.Id), interrupted: true);
            }
            catch (Exception error)
            {
                var code = error switch
                {
                    OperationException e => e.Code, ModuleRuntimeException e => e.Code,
                    ActivityBarrierException e => e.Code,
                    IOException or TimeoutException or System.Net.WebSockets.WebSocketException => "room.commandUnconfirmed",
                    _ => "operation.executionFailed"
                };
                var unconfirmed = operations.Get(job.Id, job.Owner, false).Phase == "applying" &&
                    error is not OperationException { Details: not null };
                recorder?.Record(DiagnosticSanitizer.Create(DateTimeOffset.UtcNow, "warning", "rooms", code, operationId: job.Id));
                await operations.FinishRoomRoundAsync(job.Id, Result(job),
                    ApiErrors.Create(code, "轮次命令未完成", job.Id, (error as OperationException)?.Details), unconfirmed: unconfirmed);
            }
            finally
            {
                hostStop.Dispose();
                await admission.WaitAsync();
                try { jobs.Remove(job.Id); job.Dispose(); }
                finally { admission.Release(); }
            }
        }
    }

    private RoomRoundCommandResult Result(Job job) => new(job.Prepare is not null ? "prepare" : job.Control!.Action.RawValue,
        false, rooms.GetRound(job.Owner));

    public override async Task StopAsync(CancellationToken ct)
    {
        await admission.WaitAsync(ct);
        try
        {
            accepting = false; queue.Writer.TryComplete();
            foreach (var job in jobs.Values) job.Stop.Cancel();
        }
        finally { admission.Release(); }
        await base.StopAsync(ct);
    }

    private static RoomRoundRequest Freeze(RoomRoundRequest request)
    {
        if (request.Mode is not ("emote" or "ensemble" or "independent") || request.ParticipantIds is null ||
            request.ParticipantIds.Count is < 1 or > 24 || request.CountdownMs is < 1000 or > 60000)
            throw new OperationException("request.invalid", "轮次模式、参与成员或倒数无效");
        var participants = request.ParticipantIds.ToArray();
        if (participants.Distinct(StringComparer.Ordinal).Count() != participants.Length ||
            participants.Any(id => string.IsNullOrEmpty(id) || id.Length > 128 || id.Any(char.IsControl)))
            throw new OperationException("request.invalid", "参与成员 ID 无效或重复");
        var rule = request.StopRule?.KnownValue ?? (request.StopRule is null
            ? request.StopAfterMs is null ? MusicStopRule.Manual : MusicStopRule.Duration : (MusicStopRule?)null);
        if (rule is null || rule == MusicStopRule.Duration && request.StopAfterMs is null or < 1 or > 86400000 ||
            rule != MusicStopRule.Duration && request.StopAfterMs is not null ||
            request.TailHoldMs is < 0 or > 10000 || rule != MusicStopRule.FirstCycle && request.TailHoldMs is not null ||
            request.Mode == "emote" && rule != MusicStopRule.Manual)
            throw new OperationException("request.invalid", "停止规则、时长或尾音无效");
        return request with { ParticipantIds = Array.AsReadOnly(participants), StopRule = rule.Value,
            TailHoldMs = rule == MusicStopRule.FirstCycle ? request.TailHoldMs ?? 0 : null };
    }
}

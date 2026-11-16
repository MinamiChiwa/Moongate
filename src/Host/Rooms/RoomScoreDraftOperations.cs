using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.MusicLibrary.Domain;

namespace MabiTool.Host.Rooms;

public sealed class RoomScoreDraftOperations(OperationStore operations, RoomSessionManager rooms, ActivityBarrier activities,
    ModuleRuntime modules) : BackgroundService
{
    public const int MaximumPendingCommands = 32;
    private sealed class Job(string id, string owner, string command, RoomScoreDraftRequest? publish,
        RoomScoreMappingRequest? map, string etag, IDisposable activity) : IDisposable
    {
        public string Id { get; } = id;
        public string Owner { get; } = owner;
        public string Command { get; } = command;
        public RoomScoreDraftRequest? Publish { get; } = publish;
        public RoomScoreMappingRequest? Map { get; } = map;
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

    public Task<OperationSnapshot> PublishAsync(AuthSession session, RoomScoreDraftRequest request, string? key, string? etag, CancellationToken ct)
    {
        ValidateSelection(request.Score);
        if (request.Assignments is null || request.Assignments.Count is < 1 or > 24 || request.Assignments.Any(item => item is null ||
            string.IsNullOrEmpty(item.MemberId) || item.MemberId.Length > 128 || item.MemberId.Any(char.IsControl) ||
            item.PartId is not null && (item.PartId.Length is < 1 or > 128 || item.PartId.Any(char.IsControl))) ||
            request.Assignments.Select(item => item.MemberId).Distinct(StringComparer.Ordinal).Count() != request.Assignments.Count)
            throw new OperationException("request.invalid", "声部成员分工无效或重复");
        foreach (var item in request.Assignments) ValidateInstrument(item.InstrumentId);
        return AdmitAsync(session, "publish", request with { Score = request.Score with { }, Assignments = Array.AsReadOnly(request.Assignments.Select(item => item with { }).ToArray()) }, null, key, etag, ct);
    }

    public Task<OperationSnapshot> MapAsync(AuthSession session, RoomScoreMappingRequest request, string? key, string? etag, CancellationToken ct)
    {
        if (request.Score is not null) ValidateSelection(request.Score);
        ValidateInstrument(request.InstrumentId);
        return AdmitAsync(session, "map", null, request with { Score = request.Score is null ? null : request.Score with { } }, key, etag, ct);
    }
    public Task<OperationSnapshot> ClearAsync(AuthSession session, string? key, string? etag, CancellationToken ct) =>
        AdmitAsync(session, "clear", null, null, key, etag, ct);

    private async Task<OperationSnapshot> AdmitAsync(AuthSession session, string command, RoomScoreDraftRequest? publish,
        RoomScoreMappingRequest? map, string? key, string? etag, CancellationToken ct)
    {
        IDisposable? activity = null; await admission.WaitAsync(ct);
        try
        {
            _ = rooms.GetScoreDraft(session.ClientId);
            var accepted = await operations.AdmitRoomScoreDraftAsync(session.ClientId, command, publish, map, key, etag,
                () => RoomSessionManager.ScoreDraftETag(rooms.GetScoreDraft(session.ClientId, false)), () =>
                {
                    if (!accepting) throw new OperationException("room.serverUnavailable", "草稿控制正在关闭");
                    if (jobs.Count >= MaximumPendingCommands) throw new OperationException("resource.capacityExceeded", "草稿命令队列已满");
                    rooms.ValidateScoreDraftCommand(session.ClientId, command, etag!);
                    activity = activities.EnterActivity(session.ClientId, "room.scoreDraft");
                }, ct);
            if (accepted.Created)
            {
                var job = new Job(accepted.Snapshot.Id, session.ClientId, command, publish, map, etag!, activity!);
                activity = null; jobs.Add(job.Id, job);
                if (!queue.Writer.TryWrite(job)) throw new InvalidOperationException("已受理草稿命令未能进入保留队列");
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
            if (!OperationStore.IsRoomScoreDraftOperation(operations.Get(id, session.ClientId, session.Owner).Kind))
                throw new OperationException("resource.notFound", "草稿操作不存在");
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
                if (await operations.BeginRoomScoreDraftAsync(job.Id) is null) continue;
                await using var roomDemand = await modules.AcquireAsync("rooms", job.Id, job.Stop.Token);
                await using var musicDemand = job.Publish is not null || job.Map?.Score is not null
                    ? await modules.AcquireAsync("music-library", job.Id, job.Stop.Token) : null;
                var result = await rooms.ExecuteScoreDraftCommandAsync(job.Owner, job.Command, job.Publish, job.Map, job.ETag,
                    ct => operations.CommitRoomScoreDraftAsync(job.Id, ct), job.Stop.Token);
                await operations.FinishRoomScoreDraftAsync(job.Id, result);
            }
            catch (OperationCanceledException) when (job.Stop.IsCancellationRequested)
            {
                await operations.FinishRoomScoreDraftAsync(job.Id, Result(job),
                    ApiErrors.Create("operation.interrupted", "后端停止了乐谱草稿命令", job.Id), interrupted: true);
            }
            catch (Exception error)
            {
                var code = error switch
                {
                    OperationException e => e.Code, ModuleRuntimeException e => e.Code, ActivityBarrierException e => e.Code,
                    IOException or TimeoutException or OperationCanceledException or System.Net.WebSockets.WebSocketException => "room.commandUnconfirmed",
                    _ => "operation.executionFailed"
                };
                var unconfirmed = operations.Get(job.Id, job.Owner, false).Phase == "applying" && error is not OperationException { Details: not null };
                await operations.FinishRoomScoreDraftAsync(job.Id, Result(job),
                    ApiErrors.Create(code, "乐谱草稿命令未完成", job.Id, (error as OperationException)?.Details), unconfirmed: unconfirmed);
            }
            finally
            {
                hostStop.Dispose(); await admission.WaitAsync();
                try { jobs.Remove(job.Id); job.Dispose(); } finally { admission.Release(); }
            }
        }
    }

    private RoomScoreDraftCommandResult Result(Job job) => new(job.Command, false, rooms.GetScoreDraft(job.Owner, false));
    public override async Task StopAsync(CancellationToken ct)
    {
        await admission.WaitAsync(ct);
        try { accepting = false; queue.Writer.TryComplete(); foreach (var job in jobs.Values) job.Stop.Cancel(); }
        finally { admission.Release(); }
        await base.StopAsync(ct);
    }

    private static void ValidateSelection(MusicScoreSelection? selection)
    {
        if (selection is null || selection.ScopeKey is not { Length: 64 } || selection.ScopeKey.Any(ch => ch is not (>= 'A' and <= 'F' or >= '0' and <= '9')) ||
            string.IsNullOrEmpty(selection.ScoreId) || selection.ScoreId.Length > 128 || selection.ScoreId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '_' or ':' or '-')) ||
            selection.PartId is not null && (selection.PartId.Length is < 1 or > 128 || selection.PartId.Any(char.IsControl)))
            throw new OperationException("request.invalid", "乐谱来源、ID 或声部无效");
    }
    private static void ValidateInstrument(string? id)
    {
        if (id is not null && !MusicLibraryEditor.InstrumentCatalog.Any(item => item.Id == id))
            throw new OperationException("request.invalid", "乐器目录中不存在该乐器");
    }
}

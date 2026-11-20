using MabiTool.Client;
using MabiTool.Contracts;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 乐谱库、染色、模块和更新的写操作（合奏在 LiveBackend.Rooms.cs）。
// 每个意图只发送一次；需要修订的请求先读取当前修订，被后端拒绝时把错误码显示在活动列。
public sealed partial class LiveBackend
{
    private static readonly PerformancePhase[] ActivePhases =
        [PerformancePhase.Preparing, PerformancePhase.Armed, PerformancePhase.Executing, PerformancePhase.Performing, PerformancePhase.Waiting];

    private readonly ScoreDurationCache durations = new();
    private string? colorOperationId;
    private string? colorRunId;
    private ColorRun? colorRun;

    private static bool IsLive(PerformanceQueueState? session) => session is not null
        && session.Phase is not (PerformancePhase.Stopped or PerformancePhase.Failed or PerformancePhase.Cancelled or PerformancePhase.Interrupted);

    private async Task<string?> ActAsync(string intent, string? argument, MabiToolClient api, State current,
        CancellationToken token)
    {
        var key = IdempotencyKey();
        PageState page;
        lock (gate) page = pages;
        var game = current.Binding?.Binding?.Session;
        GameSession Bound() => game ?? throw new InvalidOperationException(Pick("尚未绑定游戏窗口", "尚未綁定遊戲視窗"));

        switch (intent)
        {
            case "music.enqueue" when argument is not null:
            {
                var item = new PlaylistItem(Guid.NewGuid().ToString("N"), argument, null, DateTimeOffset.UtcNow.Ticks.ToString("D20"));
                if (!IsLive(page.Session))
                    await api.CreatePerformanceSessionAsync(new(null, "solo", Bound().Id, [item], false, PerformanceRepeatMode.Off), key, token);
                else
                    await CommandAsync(api, page.Session!.Id, new("edit", Items: [.. page.Session.Items.Select(entry => entry.Item), item]), token);
                return null;
            }
            case "music.dequeue" when argument is not null && IsLive(page.Session):
                await CommandAsync(api, page.Session!.Id,
                    new("edit", Items: page.Session.Items.Select(entry => entry.Item).Where(entry => entry.Id != argument).ToList()), token);
                return null;
            case "music.start" when IsLive(page.Session):
                await CommandAsync(api, page.Session!.Id, new("arm"), token);
                return null;
            case "music.next" when IsLive(page.Session):
                await CommandAsync(api, page.Session!.Id, new("advance", 1), token);
                return null;
            case "music.stop" when IsLive(page.Session):
                await CommandAsync(api, page.Session!.Id, new("stop", StopReason: PerformanceStopReason.ManualStop), token);
                return null;

            case "color.start":
            {
                // 寻色使用后端的当前草稿；页面在开始前已把待保存的改动写入草稿，这里重新读取。
                var library = await api.GetColorLibraryAsync(token);
                var session = Bound();
                var operation = await api.StartColorRunAsync(new(session.Id, session.BindingRevision, library.Draft), key, token);
                lock (gate)
                {
                    colorOperationId = operation.Id;
                    colorRunId = operation.ResourceRefs.FirstOrDefault(reference =>
                        reference.Type.Contains("color", StringComparison.OrdinalIgnoreCase))?.Id;
                    colorRun = null;
                }
                StartColorPresence();
                return null;
            }
            case "color.stop":
            {
                string? operationId;
                lock (gate) operationId = colorOperationId;
                // 取消后不再输入；颜色是否保持原样以停止后的重新读取为准。
                if (operationId is not null) await api.CancelOperationAsync(operationId, new("user"), token);
                return null;
            }
            case "color.select" when argument is not null:
            {
                var run = ActiveColorRun();
                var batch = run.Candidates ?? throw new InvalidOperationException(Pick("目前没有候选组合", "目前沒有候選組合"));
                await api.SelectColorCandidateAsync(run.Id, new(batch.Id, argument, batch.Interaction.Revision), token);
                return null;
            }
            case "color.retry":
            {
                var run = ActiveColorRun();
                var interaction = run.Interaction ?? throw new InvalidOperationException(Pick("目前不能重试", "目前不能重試"));
                await api.RetryColorRunAsync(run.Id, new(interaction.Revision), token);
                return null;
            }

            case "modules.toggle" when argument is not null:
            {
                string? tag = null;
                var configuration = await api.GetModuleConfigurationAsync(argument, token, metadata => tag = metadata.ETag);
                await api.ConfigureModuleAsync(argument, new(!configuration.Enabled), key, tag ?? string.Empty, token);
                return null;
            }

            case "update.download" or "update.install":
            {
                var plan = page.Update?.Available?.PlanId ?? throw new InvalidOperationException(Pick("目前没有可用的更新", "目前沒有可用的更新"));
                string? tag = null;
                await api.GetUpdatePlanAsync(plan, token, metadata => tag = metadata.ETag);
                if (intent == "update.download") await api.DownloadUpdatePlanAsync(plan, key, tag ?? string.Empty, token);
                else await api.ApplyUpdatePlanAsync(plan, key, tag ?? string.Empty, token);
                return null;
            }
        }
        return Pick($"后端没有对应这个操作的接口：{intent}", $"後端沒有對應這個操作的介面：{intent}");
    }

    private static async Task CommandAsync(MabiToolClient api, string sessionId, PerformanceSessionCommand command,
        CancellationToken token)
    {
        string? tag = null;
        await api.GetPerformanceSessionAsync(sessionId, token, metadata => tag = metadata.ETag);
        await api.CommandPerformanceSessionAsync(sessionId, command, tag ?? string.Empty, token);
    }

    private ColorRun ActiveColorRun()
    {
        lock (gate) return colorRun ?? throw new InvalidOperationException(Pick("目前没有进行中的寻色", "目前沒有進行中的尋色"));
    }

    // 寻色进行中随页面刷新读取运行状态；交互租约由独立的续约循环维持（StartColorPresence），不受这一轮其他读取的快慢影响。
    private async Task RefreshColorRunAsync(MabiToolClient api, CancellationToken token)
    {
        string? runId;
        lock (gate)
        {
            runId = colorRunId;
            if (runId is null || !Elapsed(lastColorRunRead, StatusInterval)) return;
            lastColorRunRead = Now;
        }
        try
        {
            var run = await api.GetColorRunAsync(runId, token);
            lock (gate) colorRun = run;
            Notify();
        }
        // 后端已没有这次寻色（宿主重启或记录已清理）：停止按每轮刷新查询它。
        catch (MabiToolApiException error) when (error.Status == 404) { lock (gate) if (colorRunId == runId) colorRunId = null; }
        catch (MabiToolApiException) { }
    }

    public void RequestScoreDetails(IReadOnlyList<string> scoreIds)
    {
        if (durations.Want(scoreIds)) Signal();
    }

    // 队列里的乐谱逐首查询一次时长；查不到的记为未知，不再重复查询。
    private async Task RefreshDurationsAsync(MabiToolClient api, PerformanceQueueState? session, CancellationToken token)
    {
        if (session is null) return;
        foreach (var scoreId in session.Items.Select(entry => entry.Item.ScoreId).Distinct().Take(24))
            if (!durations.Contains(scoreId))
                durations.Record(scoreId, await QueryDurationAsync(api, scoreId, token));
    }

    // 乐谱表当前页的时长：每轮最多一批，查到后通知页面。
    private async Task PumpScoreDurationsAsync(MabiToolClient api, CancellationToken token)
    {
        var batch = durations.NextBatch();
        if (batch.Count == 0) return;
        foreach (var scoreId in batch)
            durations.Record(scoreId, await QueryDurationAsync(api, scoreId, token));
        Notify();
    }

    private static async Task<double?> QueryDurationAsync(MabiToolClient api, string scoreId, CancellationToken token)
    {
        try { return (await api.QueryMusicTimelineAsync(new(scoreId, null), token)).FullCycleMs; }
        catch (MabiToolApiException) { return null; }
    }
}

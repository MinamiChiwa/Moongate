using System.Diagnostics;
using System.Globalization;
using MabiTool.Client;
using MabiTool.Contracts;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// DPS检测页的范围、目标、定时测量与保存记录。范围与所选目标是界面的显示选择，记在这里；
// 普通分段直接使用后端快照，其余范围由后端按范围与目标重新计算，页面不自行累计。
public sealed partial class LiveBackend
{
    // 范围与目标：整个前端只有这一份选择（主页面、明细窗口与浮窗共用），读取结果按发出时的选择认领。
    private readonly CombatSelection selection = new();
    private string? seenMeasurementResult;
    // 上一次读到的进行中测量 ID（空串表示没有）；首次读取之前为空，启动时已在进行的测量不触发切换。
    private string? seenMeasurementRun;
    // 开始观测是异步受理的 Operation：记下它，例行刷新时读取结果；等待识别角色时可取消，失败时页面写明原因。
    private string? startingObservation;
    private OperationSnapshot? startingResult;
    private CombatAutoObservationState? autoObservation;

    /// <summary>常驻自动观测的状态说明；观测进行中为空。</summary>
    /// <param name="waiting">抓包运行中没有数据时的统一提示（<see cref="CaptureText.Waiting"/>）；为空时用默认的等待识别角色说明。</param>
    internal static MabiTool.Desktop.Pages.Combat.AutoObservationView? AutoView(CombatAutoObservationState? value, CombatWorkspace? workspace, string? waiting = null)
    {
        if (value is null) return null;
        if (workspace?.Observation.State == "observing" && value.State is "observing" or "waiting" && value.ObservationId == workspace.Observation.Id)
            return new(value.Enabled, string.Empty, false);
        var (text, problem) = value.State switch
        {
            "waiting" => (waiting ?? Pick("等待识别角色，请切换一次地图", "等待識別角色，請切換一次地圖"), false),
            "observing" => (string.Empty, false),
            "failed" => (Pick("自动观测未开始：", "自動觀測未開始：") + Localization.CodeText.Reason(value.Reason), true),
            _ => value.Reason switch
            {
                "combat.autoDisabled" => (Pick("自动观测已关闭", "自動觀測已關閉"), false),
                "game.bindingRequired" => (Pick("绑定游戏窗口后自动观测", "綁定遊戲視窗後自動觀測"), true),
                null => (string.Empty, false),
                var other => (Pick("未抓包：", "未抓包：") + (CaptureText.Reason(other) ?? Localization.CodeText.Reason(other)), true)
            }
        };
        return new(value.Enabled, text, problem);
    }

    /// <summary>开始观测的结果：等待中（未结束、观测尚未进行）或失败（Operation 以失败、中断结束）；观测已开始时为空。</summary>
    internal static MabiTool.Desktop.Pages.Combat.ObservationStartView? StartView(OperationSnapshot? operation, CombatWorkspace? workspace, string? waiting = null)
    {
        if (operation is null) return null;
        if (workspace?.Observation is { State: "observing" } live && live.Id == operation.Id) return null;
        var state = operation.State.KnownValue;
        if (state is OperationState.Failed or OperationState.Interrupted)
            return new(false, StartFailure(operation.Problem?.Code));
        if (state is OperationState.Succeeded or OperationState.Cancelled or OperationState.CancelRequested) return null;
        return new(true, waiting ?? Pick("等待识别角色，请切换一次地图", "等待識別角色，請切換一次地圖"));
    }

    /// <summary>开始观测失败的原因：常见错误码给出短句，其余原样显示。</summary>
    internal static string StartFailure(string? code) => code switch
    {
        null or "" => Pick("开始观测失败", "開始觀測失敗"),
        var other => Pick("开始观测失败：", "開始觀測失敗：") + Localization.CodeText.Reason(other)
    };

    private async Task<OperationSnapshot?> RefreshStartAsync(MabiToolClient api, CombatWorkspace? workspace, CancellationToken token)
    {
        string? id;
        lock (gate) id = startingObservation;
        if (id is null) return null;
        OperationSnapshot? operation;
        try { operation = await api.GetOperationAsync(id, token); }
        catch (MabiToolApiException error)
        {
            // 后端已没有这个 Operation（宿主重启、记录已清理）：不再按每轮刷新反复查询。
            if (error.Status == 404)
                lock (gate) if (startingObservation == id) { startingObservation = null; startingResult = null; }
            return null;
        }
        lock (gate)
        {
            if (startingObservation != id) return startingResult;
            startingResult = operation;
            // 观测已开始或正常结束后不再跟踪；失败结果保留到下一次开始。
            if (StartView(operation, workspace) is null) { startingObservation = null; startingResult = null; }
        }
        return operation;
    }

    private void RememberStart(OperationSnapshot operation)
    {
        lock (gate) { startingObservation = operation.Id; startingResult = operation; }
    }

    private string? PendingStart()
    {
        lock (gate) return startingResult is { } value && StartView(value, null) is { Waiting: true } ? value.Id : null;
    }

    private Task SendCombatPageIntentAsync(string intent, string? argument, CancellationToken cancellationToken)
    {
        switch (intent)
        {
            case "combat.scope":
                selection.SelectScope(argument);
                Signal(); Notify();
                return Task.CompletedTask;
            case "combat.scope.live":
                selection.BackToLive();
                Signal(); Notify();
                return Task.CompletedTask;
            case "combat.auto":
            {
                var write = AutoObservationWrite(argument == "on");
                return SendAsync(async (api, _, token) =>
                {
                    var next = await write(api, token);
                    lock (gate) autoObservation = next;
                    return next.Enabled ? Pick("已开启自动观测", "已開啟自動觀測") : Pick("已关闭自动观测", "已關閉自動觀測");
                }, cancellationToken);
            }
            case "combat.target":
                selection.SelectTarget(argument);
                Signal(); Notify();
                return Task.CompletedTask;
            // 明细窗口自己的选择：不改主页面的选择，不唤醒整轮刷新。
            case "combat.detail.scope":
            case "combat.detail.scope.live":
            case "combat.detail.target":
                return SelectDetailAsync(intent, argument);
        }
        // 测量开始后自动切到这次测量：只在发出请求后用户没有另选范围时生效。
        var issuedAt = selection.Version;
        MeasurementCall? measurementCall = null;
        Func<MabiToolClient, CancellationToken, Task<CombatMeasurementState>>? settingsWrite = null;
        return SendAsync(async (api, current, token) =>
        {
            switch (intent)
            {
                case "combat.measure.toggle":
                case "combat.measure.cancel":
                case "combat.measure.start":
                {
                    measurementCall ??= new(intent, current.Measurement, current.Workspace?.Observation is { State: "observing" } live ? live.Id : null);
                    var state = await measurementCall.ExecuteAsync(api, token);
                    if (measurementCall.Cancelling) return Pick("已取消定时测量，不保存", "已取消定時測量，不儲存");
                    // 开始即切到这次测量：页面立刻清空上一次的结果，预备与计时期间按这次测量显示。
                    lock (gate)
                    {
                        var next = FollowMeasurement(selection.Scope, seenMeasurementRun ?? string.Empty, seenMeasurementResult, state);
                        if (next.Scope != selection.Scope) selection.Follow(issuedAt, next.Scope);
                        (seenMeasurementRun, seenMeasurementResult) = (next.SeenRun, next.SeenResult);
                    }
                    var settings = measurementCall.Settings;
                    return state.Phase == "running"
                        ? Pick($"定时测量 {CombatMapper.DurationText(settings.DurationMs)} 开始计时", $"定時測量 {CombatMapper.DurationText(settings.DurationMs)} 開始計時")
                        : Pick($"定时测量 {CombatMapper.DurationText(settings.DurationMs)}，等待首次伤害后开始计时",
                            $"定時測量 {CombatMapper.DurationText(settings.DurationMs)}，等待首次傷害後開始計時");
                }
                case "combat.measure.settings" when argument is not null:
                {
                    var parts = argument.Split(',');
                    if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var duration) ||
                        !ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
                        throw new InvalidOperationException("request.invalid");
                    // 预备倒计时已停用，设定只保存时长。
                    settingsWrite ??= MeasurementSettingsWrite(new(duration, 0), revision);
                    await settingsWrite(api, token);
                    return null;
                }
                case "combat.save" when argument is not null:
                {
                    var range = RangeQuery(current, selection.Scope, null)
                        ?? throw new InvalidOperationException(Pick("当前范围没有可保存的数据", "目前範圍沒有可儲存的資料"));
                    var saved = await api.SaveCombatRangeAsync(new(range, argument), IdempotencyKey(), token);
                    return Pick($"已保存记录“{saved.Record?.Name ?? argument}”", $"已儲存紀錄「{saved.Record?.Name ?? argument}」");
                }
            }
            return null;
        }, cancellationToken, CombatRequestTimeout);
    }

    /// <summary>当前范围对应的查询；当前分段且不选目标时不需要查询（直接用分段摘要），返回空。</summary>
    private static CombatRangeQuery? RangeQuery(State current, string scope, string? target, bool forDisplay = false)
    {
        var workspace = current.Workspace;
        if (scope == "segment")
        {
            if (workspace?.Encounter is not { } encounter) return null;
            return forDisplay && target is null ? null : new("encounter", encounter.Id, TargetId: target);
        }
        if (scope.StartsWith("measurement:", StringComparison.Ordinal)) return new("encounter", scope["measurement:".Length..], TargetId: target);
        if (scope.StartsWith("measurement", StringComparison.Ordinal)) return null;
        return workspace is null ? null : new("observation", ObservationId: workspace.Observation.Id, TargetId: target);
    }

    /// <summary>
    /// 每秒刷新：读取定时测量状态；测量刚完成时把范围切到这次结果。再按范围与目标读取后端重新统计的结果，
    /// 目标已不在范围内时回到“全部”。
    /// </summary>
    private async Task<(CombatRangeSummary? Range, string? Problem, CombatMeasurementState? Measurement, long At, CombatSelectionSnapshot Selection)> RefreshRangeAsync(
        MabiToolClient api, CombatWorkspace? workspace, CombatEncounterPage history, CancellationToken token)
    {
        var issuedAt = selection.Version;
        try { var auto = await api.GetCombatAutoObservationAsync(token); lock (gate) autoObservation = auto; }
        catch (MabiToolApiException) { }
        CombatMeasurementState? measurement = null; long at = 0;
        try { measurement = await api.GetCombatMeasurementAsync(token); at = Stopwatch.GetTimestamp(); }
        catch (MabiToolApiException) { }
        if (measurement is not null)
            lock (gate)
            {
                var next = FollowMeasurement(selection.Scope, seenMeasurementRun, seenMeasurementResult, measurement);
                // 新测量开始或出结果时切到它；这一轮读取期间用户另选了范围时以用户的选择为准。
                if (next.Scope != selection.Scope) selection.Follow(issuedAt, next.Scope);
                (seenMeasurementRun, seenMeasurementResult) = (next.SeenRun, next.SeenResult);
            }
        // 这一轮读取针对的选择；结果带着它返回，选择已经改变时页面不采用。
        var chosen = selection.Snapshot();
        var (scope, target) = (chosen.Scope, chosen.Target);
        var current = Read().State with { Workspace = workspace, History = history.Items };
        var query = RangeQuery(current, scope, target, forDisplay: true);
        if (query is null) return (null, null, measurement, at, chosen);
        // 整场范围要等这次观察有了第一段战斗记录才有结果（观测开始前、等待识别角色时都没有），此前不查询。
        if (query.Scope == "observation" && !HasSegments(workspace, history.Items)) return (null, null, measurement, at, chosen);
        // 同一查询刚得到“不存在”时，等分段或战斗记录变化后再查，不按每轮刷新反复请求。
        var memo = string.Join('|', query.Scope, query.EncounterId, query.ObservationId, query.TargetId, workspace?.Encounter?.Id, history.Revision);
        lock (gate) if (missingRange is { } missing && missing.Key == memo) return (null, missing.Problem, measurement, at, chosen);
        try { return (await api.QueryCombatRangeAsync(query, token), null, measurement, at, chosen); }
        catch (MabiToolApiException error) when (error.Problem?.Code == "combat.entityOutOfScope" && target is not null)
        {
            selection.DropTarget(target);
            var all = chosen with { Target = null, Version = selection.Version };
            if (RangeQuery(current, scope, null, forDisplay: true) is not { } whole) return (null, null, measurement, at, all);
            try { return (await api.QueryCombatRangeAsync(whole, token), null, measurement, at, all); }
            catch (MabiToolApiException again) { return (null, again.Problem?.Code ?? $"http.{again.Status}", measurement, at, all); }
        }
        catch (MabiToolApiException error) when (error.Status == 404)
        {
            // 进行中的测量在第一次检查点写入前查不到：不记为“不存在”，下一轮照常再查。
            if (measurement?.MeasurementId is { } live && query.EncounterId == live) return (null, null, measurement, at, chosen);
            // 整场范围在观察还没有战斗记录时为空，不算错误；记录已删除时写明。
            var problem = query.Scope == "observation" ? null : error.Problem?.Code ?? "resource.notFound";
            lock (gate) missingRange = (memo, problem);
            return (null, problem, measurement, at, chosen);
        }
        catch (MabiToolApiException error) { return (null, error.Problem?.Code ?? $"http.{error.Status}", measurement, at, chosen); }
    }

    /// <summary>
    /// 测量状态到达时的范围：新的测量进入预备或计时时切到这次测量（不再显示上一次的结果），新的结果出现时切到该结果；
    /// 首次读取（<paramref name="seenRun"/>、<paramref name="seenResult"/> 为空）只记下当前值，不切换。
    /// </summary>
    internal static (string Scope, string SeenRun, string SeenResult) FollowMeasurement(string scope, string? seenRun, string? seenResult,
        CombatMeasurementState measurement)
    {
        if (measurement.MeasurementId is { } running && seenRun is not null && running != seenRun) scope = "measurement:" + running;
        if (measurement.LastResultId is { } finished && seenResult is not null && finished != seenResult) scope = "measurement:" + finished;
        return (scope, measurement.MeasurementId ?? string.Empty, measurement.LastResultId ?? seenResult ?? string.Empty);
    }

    private (string Key, string? Problem)? missingRange;
    private string? missingTimeline;

    /// <summary>这次观察是否已有自动分段（整场范围的来源）：当前分段或历史里同一观察、不是保存记录的战斗。</summary>
    internal static bool HasSegments(CombatWorkspace? workspace, IReadOnlyList<CombatEncounter> history) =>
        workspace is not null && (workspace.Encounter is not null
            || history.Any(item => item.ObservationId == workspace.Observation.Id && item.Record is null));

    private string AutoSignature()
    {
        lock (gate) return autoObservation is { } value ? $"{value.Enabled}:{value.State}:{value.Reason}:{value.ObservationId}" : string.Empty;
    }
}

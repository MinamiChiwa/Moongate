using System.Globalization;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Life;

/// <summary>
/// 生活页与生活浮窗共用的队列规则：待执行任务、整队花费上限（与后端同一算法：各项剩余命令次数 × 目录单价，目录没写花费的按 0）、
/// 开始或继续前的确认文字、领取的发出方式与状态文字。业务判定在后端，这里只按快照排版与组织请求。
/// </summary>
public static class LifeQueueLogic
{
    public static string CommandOf(string kind) => kind switch
    {
        "gather" => "execute_gathering", "alter" => "execute_altering", "collect" => "complete_altering_work", _ => "execute_crafting"
    };

    public static LifeCommandInfo? Info(LifeSnapshot snapshot, string kind) => snapshot.Commands.FirstOrDefault(x => x.Command == CommandOf(kind));

    /// <summary>队列里待执行（含正在执行）的任务，按执行顺序；立即执行的单项不算。</summary>
    public static IReadOnlyList<LifeTask> Pending(LifeSnapshot? snapshot) =>
        snapshot?.Tasks?.Tasks.Where(x => !x.Immediate && x.State.KnownValue is LifeTaskState.Pending or LifeTaskState.Running).ToList() ?? [];

    /// <summary>命令行每次采集以 10 个为目标，实得可能更多。</summary>
    public const int GatherPerCall = 10;

    /// <summary>剩余命令次数（与后端相同）：采集按剩余数量 ÷ 10 向上取整估算，加工每项一次，制作与领取一次。</summary>
    public static int RemainingCalls(LifeTask task) => task.Kind switch
    {
        "craft" or "collect" => task.Done >= task.Count ? 0 : 1,
        "gather" => GatherCalls(task.Count - task.Done),
        _ => Math.Max(0, task.Count - task.Done)
    };

    /// <summary>采集剩余 <paramref name="remaining"/> 个时估算的命令次数。</summary>
    public static int GatherCalls(int remaining) => remaining <= 0 ? 0 : (int)(((long)remaining + GatherPerCall - 1) / GatherPerCall);

    /// <summary>整队花费上限：与后端核对的数值相同。</summary>
    public static int Ceiling(LifeSnapshot snapshot) =>
        (int)Math.Min(int.MaxValue, Pending(snapshot).Sum(task => (long)RemainingCalls(task) * (Info(snapshot, task.Kind)?.CostAmount ?? 0)));

    /// <summary>队列正在执行命令（有当前任务），或旧版单项动作正在进行。</summary>
    public static bool Executing(LifeSnapshot? snapshot) =>
        snapshot?.Tasks?.CurrentTaskId is not null || snapshot?.Action?.Phase.KnownValue == LifeActionPhase.Running;

    public static string Currency(LifeSnapshot snapshot) =>
        snapshot.Tasks?.CostCurrency ?? snapshot.Commands.Select(x => x.CostCurrency).FirstOrDefault(x => x is not null) ?? "精靈之翼";

    /// <summary>
    /// 开始或继续前确认框的文字：逐项写单次花费与次数（采集写目标数量与估算的调用次数），最后写整队上限、会自动移动角色与失败时的处理。
    /// </summary>
    public static IReadOnlyList<string> StartLines(LifeSnapshot snapshot)
    {
        var lines = new List<string>();
        var number = 0;
        var pending = Pending(snapshot);
        foreach (var task in pending)
        {
            number++;
            var info = Info(snapshot, task.Kind);
            var calls = RemainingCalls(task);
            var head = $"{number}. {Title(task)}";
            string cost;
            if (task.Kind == "collect") cost = Pick("不花费精靈之翼", "不花費精靈之翼");
            else if (task.Kind == "gather")
            {
                var plan = task.Done > 0
                    ? Pick($"目标 {task.Count} 个（已采集 {task.Done} 个），约 {calls} 次调用", $"目標 {task.Count} 個（已採集 {task.Done} 個），約 {calls} 次呼叫")
                    : Pick($"目标 {task.Count} 个，约 {calls} 次调用", $"目標 {task.Count} 個，約 {calls} 次呼叫");
                cost = info?.CostAmount is { } each
                    ? plan + Pick($" × 每次 {info.CostText} = {calls * (long)each} {info.CostCurrency}", $" × 每次 {info.CostText} = {calls * (long)each} {info.CostCurrency}")
                    : plan + Pick("；游戏目录没有写明花费，实际花费以游戏返回为准", "；遊戲目錄沒有寫明花費，實際花費以遊戲回傳為準");
            }
            else if (info?.CostAmount is { } amount)
                cost = task.Kind == "craft"
                    ? Pick($"制作 {task.Count} 次是一条命令，花费 {info.CostText}（制作尚未经实机确认）", $"製作 {task.Count} 次是一條命令，花費 {info.CostText}（製作尚未經實機確認）")
                    : Pick($"剩余 {calls} 轮 × 每次 {info.CostText} = {calls * (long)amount} {info.CostCurrency}", $"剩餘 {calls} 輪 × 每次 {info.CostText} = {calls * (long)amount} {info.CostCurrency}");
            else cost = Pick("游戏目录没有写明花费，实际花费以游戏返回为准", "遊戲目錄沒有寫明花費，實際花費以遊戲回傳為準");
            lines.Add(head + "：" + cost);
        }
        if (pending.Any(x => x.Kind == "gather"))
            lines.Add(Pick("采集每次以 10 个为目标，实得多于 10 个时调用次数更少。", "採集每次以 10 個為目標，實得多於 10 個時呼叫次數更少。"));
        lines.Add(Pick($"整队花费上限：{Ceiling(snapshot)} {Currency(snapshot)}", $"整隊花費上限：{Ceiling(snapshot)} {Currency(snapshot)}"));
        lines.Add(Pick("执行会自动移动角色。", "執行會自動移動角色。"));
        lines.Add(Pick("遇到失败、阻塞或拒绝时整个队列暂停，不跳过、不自动重试；处理后点“继续”。", "遇到失敗、阻塞或拒絕時整個佇列暫停，不跳過、不自動重試；處理後點「繼續」。"));
        return lines;
    }

    /// <summary>队列运行中加入任务时的确认文字（这项任务的花费并入已确认的上限）。</summary>
    public static IReadOnlyList<string> AddLines(LifeSnapshot snapshot, string kind, string title, int count)
    {
        var info = Info(snapshot, kind);
        var calls = kind switch { "craft" or "collect" => 1, "gather" => GatherCalls(count), _ => count };
        var lines = new List<string> { KindName(kind) + "：" + title };
        if (kind == "collect") lines.Add(Pick("领取没有精靈之翼花费。", "領取沒有精靈之翼花費。"));
        else if (kind == "gather" && info?.CostAmount is { } each)
            lines.Add(Pick($"目标 {count} 个，约 {calls} 次调用，每次花费 {info.CostText}，并入队列的花费上限 {calls * (long)each} {info.CostCurrency}",
                $"目標 {count} 個，約 {calls} 次呼叫，每次花費 {info.CostText}，併入佇列的花費上限 {calls * (long)each} {info.CostCurrency}"));
        else if (info?.CostAmount is { } amount)
            lines.Add(Pick($"每次花费 {info.CostText}，共 {calls} 次，并入队列的花费上限 {calls * (long)amount} {info.CostCurrency}",
                $"每次花費 {info.CostText}，共 {calls} 次，併入佇列的花費上限 {calls * (long)amount} {info.CostCurrency}"));
        else lines.Add(Pick("游戏目录没有写明花费，实际花费以游戏返回为准。", "遊戲目錄沒有寫明花費，實際花費以遊戲回傳為準。"));
        lines.Add(Pick("队列正在执行，这项任务排到最后，按顺序执行，会自动移动角色。", "佇列正在執行，這項任務排到最後，按順序執行，會自動移動角色。"));
        if (kind == "craft") lines.Add(Pick("制作执行尚未经实机确认。", "製作執行尚未經實機確認。"));
        return lines;
    }

    /// <summary>
    /// 领取一个设施的已完成加工：队列空闲或暂停时立即执行（后端插到最前单独执行，结束后队列回到原状态），队列正在执行时排到当前任务之后。
    /// </summary>
    public static Task<BackendReply> CollectAsync(IDesktopBackend backend, LifeSnapshot snapshot, string facility) =>
        Executing(snapshot)
            ? backend.AddLifeTaskAsync(new("collect", null, facility, 1, null, "next"), CancellationToken.None)
            : backend.StartLifeActionAsync(new("collect", null, facility, 1, null, Info(snapshot, "collect")?.CostText), CancellationToken.None);

    public static string KindName(string kind) => kind switch
    {
        "gather" => Pick("采集", "採集"), "alter" => Pick("加工", "加工"), "collect" => Pick("领取", "領取"), "craft" => Pick("制作", "製作"),
        _ => Pick("生活动作", "生活動作")
    };

    /// <summary>任务的一行标题：种类、目标与数量。</summary>
    public static string Title(LifeTask task) => task.Kind switch
    {
        "gather" => Pick($"采集 {task.Target} × {task.Count} 个", $"採集 {task.Target} × {task.Count} 個"),
        "alter" => Pick($"加工 {task.Target} × {task.Count} 项", $"加工 {task.Target} × {task.Count} 項"),
        "collect" => Pick($"领取 {task.Target}", $"領取 {task.Target}"),
        "craft" => Pick($"制作 {task.Target}{Variant(task.Variant)} × {task.Count} 次", $"製作 {task.Target}{Variant(task.Variant)} × {task.Count} 次"),
        _ => task.Target
    };

    public static string Variant(int? variant) => variant is { } value ? Pick($"（版本 {value}）", $"（版本 {value}）") : string.Empty;

    /// <summary>队列状态的一句话：运行中、即将暂停、已暂停（原因）或空闲。</summary>
    public static string StateText(LifeSnapshot? snapshot)
    {
        if (snapshot?.Tasks is not { } queue) return Pick("未读取", "未讀取");
        if (queue.CurrentTaskId is not null && queue.State.KnownValue != LifeQueueState.Running)
            return Pick("正在立即执行一项任务", "正在立即執行一項任務");
        return queue.State.KnownValue switch
        {
            LifeQueueState.Running => queue.PauseCode == "life.pauseRequested" ? Pick("这一次调用结束后暂停", "這一次呼叫結束後暫停") : Pick("执行中", "執行中"),
            LifeQueueState.Paused => Pick("已暂停", "已暫停"),
            _ => Pending(snapshot).Count > 0 ? Pick("等待开始", "等待開始") : Pick("空闲", "空閒")
        };
    }

    /// <summary>暂停原因（含 blocked 的界面种类）与停在哪一项；没有暂停时为空。</summary>
    public static string PauseText(LifeSnapshot? snapshot)
    {
        if (snapshot?.Tasks is not { State.KnownValue: LifeQueueState.Paused } queue || queue.PauseCode is null) return string.Empty;
        var reason = queue.PauseCode switch
        {
            "gameCli.blocked" => LifeText.Blocked(queue.PauseDetail),
            _ => LifeText.Reason(queue.PauseCode)
        };
        var task = queue.Tasks.FirstOrDefault(x => x.Id == queue.PauseTaskId);
        var where = task is null ? string.Empty
            : task.Kind == "gather" ? Pick($"（停在：{Title(task)}，{GatherProgress(task)}）", $"（停在：{Title(task)}，{GatherProgress(task)}）")
            : Pick($"（停在：{Title(task)}，已完成 {Done(task)}）", $"（停在：{Title(task)}，已完成 {Done(task)}）");
        var unknown = task?.StateUnknown == true ? Pick("；游戏里的结果未知，已重新读取", "；遊戲裡的結果未知，已重新讀取") : string.Empty;
        return reason + where + unknown;
    }

    /// <summary>已完成的部分：加工写已排队项数/总项数，制作与领取写是否完成（采集见 <see cref="GatherProgress"/>）。</summary>
    public static string Done(LifeTask task) => task.Kind is "craft" or "collect"
        ? (task.Done >= task.Count ? Pick("已完成", "已完成") : Pick("未完成", "未完成"))
        : string.Create(CultureInfo.InvariantCulture, $"{task.Done}/{task.Count}");

    /// <summary>采集的进度：已采集数量与目标数量（已采集可能超过目标）。</summary>
    public static string GatherProgress(LifeTask task) => Pick($"已采集 {task.Done} / {task.Count} 个", $"已採集 {task.Done} / {task.Count} 個");

    /// <summary>总队列行与生活浮窗的进度文字：采集写已采集数量，加工写项数，制作与领取完成时写“完成”。</summary>
    public static string ProgressText(LifeTask task) => task.Kind switch
    {
        "gather" => GatherProgress(task),
        "alter" => Done(task),
        _ => task.Done >= task.Count ? Pick("完成", "完成") : string.Empty
    };

    /// <summary>是否显示进度条：采集总是显示（按数量），加工多于一项时显示。</summary>
    public static bool ShowsProgress(LifeTask task) => task.Kind == "gather" || task.Kind == "alter" && task.Count > 1;

    /// <summary>进度条的比例：已完成数量 ÷ 总数（采集按数量，超过目标时为满）。</summary>
    public static double Fraction(LifeTask task) => task.Count <= 0 ? 0 : Math.Clamp(task.Done / (double)task.Count, 0, 1);
}

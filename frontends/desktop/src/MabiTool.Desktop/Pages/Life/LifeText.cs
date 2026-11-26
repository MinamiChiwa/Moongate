using System.Globalization;
using MabiTool.Contracts;
using MabiTool.Desktop.Localization;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Life;

/// <summary>
/// “生活”页的界面文字：游戏拒绝与阻塞原因、归属读不到的原因、执行动作与结果的说明。原因码只显示中文；本页没有文案的码回落到
/// <see cref="CodeText.Reason"/>（通用原因，未登记的记入诊断页的待补清单）。游戏里的物品、设施与技能名称不经过这里，按原文显示。
/// </summary>
public static class LifeText
{
    private static readonly Dictionary<string, (string Hans, string Hant)> Reasons = new(StringComparer.Ordinal)
    {
        ["gameCli.blocked"] = ("游戏里有界面或状态需要你处理，处理后请重新执行", "遊戲裡有介面或狀態需要你處理，處理後請重新執行"),
        ["gameCli.overweight"] = ("背包超重，请先整理", "背包超重，請先整理"),
        ["gameCli.toolMissing"] = ("没有所需的工具", "沒有所需的工具"),
        ["gameCli.toolBroken"] = ("工具耐久为 0，已不能使用", "工具耐久為 0，已不能使用"),
        ["gameCli.requiredConsumableMissing"] = ("缺少采集所需的消耗品", "缺少採集所需的消耗品"),
        ["gameCli.interruptedByAttack"] = ("被攻击打断", "被攻擊打斷"),
        ["gameCli.noRoute"] = ("找不到前往目的地的路线", "找不到前往目的地的路線"),
        ["gameCli.notInField"] = ("需要在野外才能执行", "需要在野外才能執行"),
        ["gameCli.insufficientLivingSkillLevel"] = ("生活技能等级不足", "生活技能等級不足"),
        ["gameCli.notFound"] = ("游戏里找不到这个项目", "遊戲裡找不到這個項目"),
        ["gameCli.notEnoughCurrency"] = ("精靈之翼不足，没有扣费", "精靈之翼不足，沒有扣費"),
        ["gameCli.costPaymentFailed"] = ("扣除花费失败", "扣除花費失敗"),
        ["gameCli.notEnoughIngredient"] = ("材料不足", "材料不足"),
        ["gameCli.ingredientLocked"] = ("材料被锁定", "材料被鎖定"),
        ["gameCli.insufficientFacilityLevel"] = ("设施等级不足", "設施等級不足"),
        ["gameCli.insufficientTransferCost"] = ("转移费用不足", "轉移費用不足"),
        ["gameCli.insufficientDecorScore"] = ("装饰分数不足", "裝飾分數不足"),
        ["gameCli.queueFull"] = ("加工队列已满", "加工佇列已滿"),
        ["gameCli.notAvailable"] = ("这个项目现在不能执行", "這個項目現在不能執行"),
        ["gameCli.requiresUserInteraction"] = ("这个配方需要在游戏里亲自开始", "這個配方需要在遊戲裡親自開始"),
        ["gameCli.facilityNotFound"] = ("找不到对应的设施", "找不到對應的設施"),
        ["gameCli.componentNotFound"] = ("找不到设施界面", "找不到設施介面"),
        ["gameCli.timeout"] = ("游戏里的动作超时", "遊戲裡的動作逾時"),
        ["gameCli.canceled"] = ("动作被新的命令取代", "動作被新的命令取代"),
        ["gameCli.craftingLocked"] = ("制作系统尚未解锁", "製作系統尚未解鎖"),
        ["gameCli.ambiguousRecipe"] = ("有多个同名配方，请选择配方版本", "有多個同名配方，請選擇配方版本"),
        ["gameCli.invalidCount"] = ("次数超过游戏允许的上限", "次數超過遊戲允許的上限"),
        ["gameCli.noAltering"] = ("没有加工中的项目", "沒有加工中的項目"),
        ["gameCli.noCompletedWork"] = ("没有已完成的加工", "沒有已完成的加工"),
        ["gameCli.noCompletedWorkAtFacility"] = ("该设施没有已完成的加工", "該設施沒有已完成的加工"),
        ["gameCli.notCompletedYet"] = ("该加工还没有完成", "該加工還沒有完成"),
        ["gameCli.invalidBody"] = ("游戏认为请求内容无效", "遊戲認為請求內容無效"),
        ["gameCli.unparseable"] = ("游戏返回的内容无法解析", "遊戲回傳的內容無法解析"),
        ["gameCli.unexpectedResponse"] = ("游戏返回了没有预期的结果", "遊戲回傳了沒有預期的結果"),
        ["life.stoppedByUser"] = ("已按你的要求停止", "已依你的要求停止"),
        ["life.pausedByUser"] = ("已按你的要求暂停", "已依你的要求暫停"),
        ["life.pauseRequested"] = ("这一次调用结束后暂停", "這一次呼叫結束後暫停"),
        ["life.costLimitReached"] = ("花费将超过已确认的上限，继续前请确认新的上限", "花費將超過已確認的上限，繼續前請確認新的上限"),
        ["life.hostRestarted"] = ("工具重启后暂停，请确认后手动继续", "工具重啟後暫停，請確認後手動繼續"),
        ["life.queueFull"] = ("生活队列已满，请先清理", "生活佇列已滿，請先清理"),
        ["life.taskRunning"] = ("这项任务正在执行，请先停止当前", "這項任務正在執行，請先停止目前"),
        ["life.queueRunning"] = ("生活队列正在执行", "生活佇列正在執行"),
        ["life.nothingToRun"] = ("队列里没有待执行的任务", "佇列裡沒有待執行的任務"),
        ["life.actionRunning"] = ("生活队列正在执行，请等待结束或先暂停", "生活佇列正在執行，請等待結束或先暫停"),
        ["life.costChanged"] = ("花费与确认时不同，请按新的花费重新确认", "花費與確認時不同，請依新的花費重新確認"),
        ["life.nothingToCollect"] = ("该设施没有已完成的加工", "該設施沒有已完成的加工"),
        ["life.fishingNotSupported"] = ("钓鱼类采集物请使用专注钓鱼", "釣魚類採集物請使用專注釣魚"),
        ["life.connectorUnavailable"] = ("游戏连接器未连接", "遊戲連接器未連接"),
        ["life.commandUnavailable"] = ("游戏目录里没有这条指令", "遊戲目錄裡沒有這條指令"),
        ["revision.stale"] = ("队列已经变化，已按最新内容显示，请再操作一次", "佇列已經變化，已依最新內容顯示，請再操作一次"),
        ["input.activityLeaseRequired"] = ("游戏活动权限已被撤销（紧急停止或超时），已暂停", "遊戲活動權限已被撤銷（緊急停止或逾時），已暫停"),
        ["life.stoppedInGame"] = ("游戏里提前结束了动作", "遊戲裡提前結束了動作"),
        ["life.fishingStarted"] = ("这是钓鱼类采集物，命令行只会开启悠闲钓鱼，已立即停止；钓鱼请使用专注钓鱼", "這是釣魚類採集物，命令列只會開啟悠閒釣魚，已立即停止；釣魚請使用專注釣魚"),
        ["life.clientLoading"] = ("客户端表读取中", "用戶端表讀取中"),
        ["life.clientUnavailable"] = ("客户端表读取失败", "用戶端表讀取失敗"),
        ["life.clientDirectoryUnknown"] = ("未找到台服游戏目录", "未找到台服遊戲目錄"),
        ["life.gatherNotInClient"] = ("客户端表里没有找到掉落该物品的采集点", "用戶端表裡沒有找到掉落該物品的採集點"),
        ["life.gatherMultiple"] = ("客户端表里该物品在多个生活技能的采集点掉落", "用戶端表裡該物品在多個生活技能的採集點掉落"),
        ["life.alterNotInClient"] = ("客户端表里没有找到该加工配方", "用戶端表裡沒有找到該加工配方"),
        ["life.alterMultiple"] = ("客户端表里该配方对应多个加工设施", "用戶端表裡該配方對應多個加工設施"),
        ["life.craftNotInClient"] = ("客户端表里没有找到以该物品为结果的配方（装备等没有固定结果的配方也读不到）", "用戶端表裡沒有找到以該物品為結果的配方（裝備等沒有固定結果的配方也讀不到）"),
        ["life.craftMultiple"] = ("客户端表里该配方对应多个制作设施", "用戶端表裡該配方對應多個製作設施"),
        ["life.skillTableMissing"] = ("客户端缺少生活技能表", "用戶端缺少生活技能表"),
        ["life.skillLayoutUnsupported"] = ("客户端生活技能表的结构与已核对的版本不符", "用戶端生活技能表的結構與已核對的版本不符"),
        ["life.itemTableMissing"] = ("客户端缺少道具表", "用戶端缺少道具表"),
        ["life.itemLayoutUnsupported"] = ("客户端道具表的结构与已核对的版本不符", "用戶端道具表的結構與已核對的版本不符"),
        ["life.facilityTableMissing"] = ("客户端缺少设施表", "用戶端缺少設施表"),
        ["life.facilityLayoutUnsupported"] = ("客户端设施表的结构与已核对的版本不符", "用戶端設施表的結構與已核對的版本不符"),
        ["life.gatherTableMissing"] = ("客户端缺少采集点表", "用戶端缺少採集點表"),
        ["life.gatherLayoutUnsupported"] = ("客户端采集点表的结构与已核对的版本不符", "用戶端採集點表的結構與已核對的版本不符"),
        ["life.alterTableMissing"] = ("客户端缺少加工配方表", "用戶端缺少加工配方表"),
        ["life.alterLayoutUnsupported"] = ("客户端加工配方表的结构与已核对的版本不符", "用戶端加工配方表的結構與已核對的版本不符"),
        ["life.craftTableMissing"] = ("客户端缺少制作配方表", "用戶端缺少製作配方表"),
        ["life.craftLayoutUnsupported"] = ("客户端制作配方表的结构与已核对的版本不符", "用戶端製作配方表的結構與已核對的版本不符")
    };

    private static readonly Dictionary<string, (string Hans, string Hant)> FishingPhases = new(StringComparer.Ordinal)
    {
        ["starting"] = ("正在启动", "正在啟動"), ["preparing-mode"] = ("正在确认钓鱼模式", "正在確認釣魚模式"), ["dismounting"] = ("正在下坐骑", "正在下坐騎"),
        ["casting"] = ("抛竿中", "拋竿中"), ["waiting-bite"] = ("等待上钩", "等待上鉤"), ["fishing"] = ("钓鱼中", "釣魚中"), ["reeling"] = ("收竿中", "收竿中"),
        ["recasting"] = ("重新抛竿", "重新拋竿"), ["leaving"] = ("正在离开钓鱼姿态", "正在離開釣魚姿態"),
        ["succeeded"] = ("已结束", "已結束"), ["failed"] = ("已失败", "已失敗"), ["interrupted"] = ("已中断", "已中斷"), ["cancelled"] = ("已取消", "已取消")
    };

    private static readonly Dictionary<string, (string Hans, string Hant)> FishingEnds = new(StringComparer.Ordinal)
    {
        ["duration-reached"] = ("已到设定时长", "已到設定時長"), ["user-stop"] = ("已按你的要求收竿结束", "已依你的要求收竿結束"),
        ["backpack-full"] = ("背包已满，已停止", "背包已滿，已停止"), ["cancelled"] = ("已立即停止", "已立即停止"), ["emergency-stop"] = ("紧急停止", "緊急停止"),
        ["host-stopped"] = ("宿主已停止", "宿主已停止"), ["backend-restarted"] = ("后端重启，运行已中断", "後端重啟，執行已中斷"),
        ["interface-unrecognized"] = ("无法识别游戏画面", "無法辨識遊戲畫面"), ["dismount-not-observed"] = ("没有观察到下坐骑", "沒有觀察到下坐騎"),
        ["cast-preflight-retry-limit"] = ("抛竿前检查多次失败", "拋竿前檢查多次失敗"), ["hook-not-recognized"] = ("多轮没有识别到上钩", "多輪沒有辨識到上鉤")
    };

    /// <summary>钓鱼运行的阶段文字；未登记的阶段写“处理中”（不显示英文代码）。</summary>
    public static string FishingPhase(string phase) =>
        FishingPhases.TryGetValue(phase, out var text) ? Pick(text.Hans, text.Hant) : Pick("处理中", "處理中");

    /// <summary>钓鱼运行的结束原因：运行给出的 kebab 原因，或失败时的原因码（经通用原因文案）。</summary>
    public static string FishingEnd(string reason) =>
        FishingEnds.TryGetValue(reason, out var text) ? Pick(text.Hans, text.Hant) : Reason(reason);

    /// <summary>运行中值得提示的原因（不是终态错误）。</summary>
    public static string? FishingNote(string? reason) => reason switch
    {
        "cast_retry_no_response" => Pick("抛竿后 20 秒没有反应，已换向轻移重抛", "拋竿後 20 秒沒有反應，已換向輕移重拋"),
        _ => null
    };

    /// <summary>原因码的中文说明；本页没有文案时用通用原因文案。</summary>
    public static string Reason(string? code) =>
        code is not null && Reasons.TryGetValue(code, out var text) ? Pick(text.Hans, text.Hant) : CodeText.Reason(code);

    /// <summary>游戏给出的 snake_case 原因（<c>not_enough_ingredient</c>）规范为 <c>gameCli.notEnoughIngredient</c> 后取中文说明。</summary>
    public static string GameReason(string reason)
    {
        var builder = new System.Text.StringBuilder("gameCli.");
        var upper = false;
        foreach (var ch in reason.Trim())
        {
            if (ch is '_' or '-' or ' ') { upper = builder.Length > "gameCli.".Length; continue; }
            if (!char.IsAsciiLetterOrDigit(ch)) continue;
            builder.Append(upper ? char.ToUpperInvariant(ch) : builder.Length == "gameCli.".Length ? char.ToLowerInvariant(ch) : ch);
            upper = false;
        }
        return Reason(builder.ToString());
    }

    /// <summary>本页登记了文案的原因码（测试核对简繁都有文字）。</summary>
    public static IReadOnlyCollection<string> KnownReasons => Reasons.Keys;

    /// <summary><c>blocked</c> 带的界面或状态种类：已知种类写具体做法，其余写通用说明（不显示英文代码）。</summary>
    public static string Blocked(string? kind) => kind switch
    {
        "unknown_modal" => Pick("游戏画面上有窗口挡住，请关闭后重试", "遊戲畫面上有視窗擋住，請關閉後重試"),
        _ => Pick("游戏里有界面或状态需要你处理，处理后请重新执行", "遊戲裡有介面或狀態需要你處理，處理後請重新執行")
    };

    public static string KindName(string kind) => kind switch
    {
        "gather" => Pick("采集", "採集"),
        "alter" => Pick("加工排队", "加工排隊"),
        "collect" => Pick("领取加工", "領取加工"),
        "craft" => Pick("制作", "製作"),
        _ => Pick("生活动作", "生活動作")
    };

    /// <summary>占用活动锁的所有者：按所有者标识的前缀换成功能名，没有对应时写“其他功能”。</summary>
    public static string Owner(string? ownerId)
    {
        var prefix = ownerId is null ? string.Empty : ownerId.Split('-', ':')[0];
        return prefix switch
        {
            "performance" => Pick("游戏内演奏", "遊戲內演奏"),
            "rooms" or "room" or "ensemble" or "synchronization" => Pick("合奏", "合奏"),
            "color" => Pick("染色", "染色"),
            "life" => Pick("生活", "生活"),
            _ => Pick("其他功能", "其他功能")
        };
    }

    /// <summary>剩余时间：不满一小时写 mm:ss，否则 h:mm:ss；未知写“未知”。</summary>
    public static string Duration(double? seconds)
    {
        if (seconds is not { } value || double.IsNaN(value)) return Pick("未知", "未知");
        var total = (long)Math.Ceiling(Math.Max(0, value));
        var time = TimeSpan.FromSeconds(total);
        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes:00}:{time.Seconds:00}");
    }

    public static string Number(int? value) => value is { } number ? number.ToString("N0", CultureInfo.InvariantCulture) : Pick("未知", "未知");

    /// <summary>“全部领取”停用时的原因（页面与生活浮窗共用）。</summary>
    public static string NothingToCollect => Pick("没有已完成的加工", "沒有已完成的加工");

    /// <summary>槽位圆点的悬停提示（页面与生活浮窗共用）：加工品名与状态；<paramref name="finishIn"/> 是该项完成还需的秒数（见 <see cref="LifeCountdown.Remaining"/>）。</summary>
    public static string WorkTip(string name, LifeDotState state, double? finishIn) => name + "：" + state switch
    {
        LifeDotState.Done => Pick("已完成，点按领取", "已完成，點按領取"),
        LifeDotState.Scheduled => finishIn is { } queued
            ? Pick($"排队中，约 {Duration(queued)} 后完成", $"排隊中，約 {Duration(queued)} 後完成") : Pick("排队中，尚未开始", "排隊中，尚未開始"),
        _ => finishIn is { } remaining
            ? remaining <= 0 ? Pick("即将完成", "即將完成") : Pick("剩余 ", "剩餘 ") + Duration(remaining)
            : Pick("剩余时间未知", "剩餘時間未知")
    };
}

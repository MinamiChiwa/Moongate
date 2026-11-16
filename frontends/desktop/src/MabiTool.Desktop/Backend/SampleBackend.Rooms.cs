using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using MabiTool.Desktop.Pages.Dyeing;
using MabiTool.Desktop.Pages.Rooms;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 样本后端的合奏与染色历史：在内存里按后端的规则响应，供界面预览与测试。
// 合奏的房间内容先组成房间协议同样的 JSON，再用页面的解析器读取。
public sealed partial class SampleBackend
{
    private const string LocalMemberId = "m1";

    /// <summary>设为假时合奏页显示连接房间的表单。</summary>
    public bool InRoom { get; set; } = true;

    /// <summary>不在房间时房间服务让本机退出的原因（kicked、roomClosed），用于显示被移出与房间关闭的提示。</summary>
    public string? ExitReason { get; set; }

    /// <summary>设为假时样本以成员身份（芙蕾雅）显示合奏页：成员表不能编辑，没有开始等房主按钮。</summary>
    public bool IsRoomOwner { get; set; } = true;

    /// <summary>测试用：房间连接中断（重连前）。</summary>
    internal bool RoomDisconnected { get; set; }

    /// <summary>测试用：就绪请求被后端拒绝时的错误码。</summary>
    internal string? ReadinessRejectCode { get; set; }

    /// <summary>测试用：本机设置读取失败时的错误码。</summary>
    internal string? PreferencesLoadFailure { get; set; }

    /// <summary>测试用：剪贴板不可用。</summary>
    internal bool ClipboardFails { get; set; }

    /// <summary>测试用：没有任何染色记录。</summary>
    internal bool EmptyColorHistory { get; set; }

    /// <summary>保存本机设置的请求（设置与 If-Match），按发送顺序记录。</summary>
    public List<(RoomPreferences Value, string ETag)> SavedRoomPreferences { get; } = [];

    /// <summary>开轮请求，按发送顺序记录。</summary>
    public List<RoomRoundRequest> RoundRequests { get; } = [];

    /// <summary>复制到剪贴板的邀请。</summary>
    public List<string> CopiedInvitations { get; } = [];

    /// <summary>染色历史的读取请求（分页标记），按发送顺序记录。</summary>
    public List<string?> ColorHistoryQueries { get; } = [];

    private sealed record SampleMember(string Id, string Name, int Group, bool Initiator, string Instrument, bool Ready,
        bool Connected = true, bool Selected = true);

    // 名单按组排列；乐谱使用者固定在组内首位，与房间协议一致。
    private readonly List<SampleMember> roomMembers =
    [
        new("m1", "琉特", 1, true, "魯特琴", true),
        new("m2", "芙蕾雅", 1, false, "豎琴", true),
        new("m3", "小麥田", 1, false, "長笛", false, Connected: false),
        new("m4", "北境旅人", 2, true, "小鼓", false),
        new("m5", "艾菈", 2, false, "", true, Selected: false)
    ];

    private string roomMode = "ensemble";
    private string roomLeader = LocalMemberId;
    private string? roundPhase;
    private bool operateGame = true;
    private RoomTiming? roomTiming;
    private RoomFeedback? roomFeedback;
    private string? fallbackInvitation;
    private bool invitationCopied;

    /// <summary>测试用：本轮阶段（preparing、committed、performing、interrupted、cancelled、stopped），为空表示没有本轮。</summary>
    internal string? RoundPhase
    {
        get => roundPhase;
        set
        {
            roundPhase = value;
            roomTiming = SampleTiming();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 样本的时间基准：开轮时距同步开始为本轮倒计时；已开始的阶段从开始后 12 秒算起，指定时长时另有统一停止。
    /// </summary>
    private RoomTiming? SampleTiming() => roundPhase switch
    {
        "preparing" or "committed" => new(1000, 23.4, 1.8, samplePreferences.TimingOffsetMs, samplePreferences.CountdownMs + 1500, null, Stopwatch.GetTimestamp()),
        "performing" or "interrupted" => new(1000, 23.4, 1.8, samplePreferences.TimingOffsetMs, -12000,
            samplePreferences.MusicStopRule.RawValue == "duration" ? samplePreferences.MusicDurationMs - 12000 : null, Stopwatch.GetTimestamp()),
        _ => new(1000, 23.4, 1.8, samplePreferences.TimingOffsetMs, null, null, Stopwatch.GetTimestamp())
    };

    private RoomCapture SampleCaptureView()
    {
        var status = GetCaptureStatus();
        var identified = status.Running && InRoom && status.Character is not null;
        // 昵称取全局抓包识别的本机身份（与侧栏同一状态源），与是否在房间无关；乐谱只在房间内识别。
        return new(status, status.Character,
            identified ? new ObservedMusic(new(7), new(4100200300), new(4100200300), "夜風與營火", false, [0, 1], null, 184.5, 176.25, null,
                "capture", false, null) : null);
    }

    /// <summary>本机停止与切换操作游戏的请求，按发送顺序记录。</summary>
    public List<string> RoomControlRequests { get; } = [];

    private RoomPreferences samplePreferences = RoomPreferences.Default with
    {
        Revision = new(3), PublicProvider = "community", Nickname = "琉特", OverlayOpacity = 0.75,
        TimingOffsetMs = -40, MusicStopRule = MusicStopRule.FirstCycle,
        Bindings = new Dictionary<string, ModeInputPreferences>
        {
            ["emote"] = new(new(LocalBindingKind.ChatDraft), null, false),
            ["independent"] = new(new(LocalBindingKind.Key, 0x20), new(LocalBindingKind.Key, 0x1B), false),
            ["ensemble"] = new(new(LocalBindingKind.Button, X: 800, Y: 450, ClientWidth: 1600, ClientHeight: 900, Dpi: 144),
                new(LocalBindingKind.Key, RoomLocalSettings.SpaceKey), false)
        },
        StartInputKinds = new Dictionary<string, int> { ["emote"] = 2, ["independent"] = 0, ["ensemble"] = 1 }
    };

    private static string ETagOf(RoomPreferences value) => $"\"room-preferences-{value.Revision}\"";

    private string SelfId => IsRoomOwner ? LocalMemberId : "m2";

    public RoomsPageData GetRooms()
    {
        if (Offline) return RoomsPageData.Offline;
        var room = InRoom ? RoomJson.Parse(JsonSerializer.SerializeToElement(RoomNode())) : null;
        return new(true, !InRoom ? "disconnected" : RoomDisconnected ? "reconnecting" : "connected", InRoom ? SelfId : null, room,
            new(Pick("本机服务未启动", "本機服務未啟動"), false, null, null, ["ws://192.168.1.10:38472/ws"],
                Pick("未建立公网连接", "未建立公網連線"), null),
            samplePreferences, ETagOf(samplePreferences), !operateGame, new(100, 50, 1600, 900, 144), BoundWindowLabel(), "F9", roomFeedback,
            InRoom && roundPhase is "preparing" or "committed" ? "prep-1" : null, SampleCaptureView().Nickname,
            ["魯特琴", "長笛", "小提琴", "曼陀林", "豎琴", "小鼓", "烏克麗麗"], InRoom ? fallbackInvitation : null, InRoom && invitationCopied,
            InRoom && !RoomDisconnected ? roomTiming ??= SampleTiming() : null, operateGame, InRoom ? null : ExitReason, SampleCaptureView());
    }

    // 房间协议同样的 JSON：成员、总指挥、识别到的角色与本轮。
    private JsonObject RoomNode()
    {
        var members = new JsonArray();
        foreach (var group in roomMembers.GroupBy(member => member.Group).OrderBy(group => group.Key))
        {
            var order = 0;
            foreach (var member in group.OrderBy(member => member.Initiator ? 0 : 1).ThenBy(member => roomMembers.IndexOf(member)))
                members.Add(new JsonObject
                {
                    ["id"] = member.Id, ["nickname"] = member.Name, ["role"] = member.Id == LocalMemberId ? "host" : "member",
                    ["ensembleGroup"] = member.Group, ["ensembleOrder"] = ++order, ["ensembleRole"] = member.Initiator ? "initiator" : "joiner",
                    ["connected"] = member.Connected, ["ready"] = member.Ready, ["selected"] = member.Selected, ["instrument"] = member.Instrument,
                    ["capabilities"] = new JsonObject { ["clockRoundTripMs"] = 23.4 },
                    ["music"] = member.Id == roomLeader
                        ? new JsonObject { ["title"] = "夜風與營火", ["wholeScoreDurationSeconds"] = 184.5, ["selectedDurationSeconds"] = 176.25 }
                        : null
                });
        }
        var round = roundPhase is null ? null : new JsonObject
        {
            ["id"] = "round-1", ["mode"] = roomMode, ["phase"] = roundPhase, ["leaderId"] = roomMode == "emote" ? null : roomLeader,
            ["countdownMs"] = samplePreferences.CountdownMs + 1500, ["requestedCountdownMs"] = samplePreferences.CountdownMs,
            ["participantIds"] = new JsonArray(roomMembers.Where(member => member.Selected).Select(member => (JsonNode)member.Id).ToArray()),
            ["startAction"] = new JsonObject { ["id"] = "start-1", ["targetServerMs"] = 1000.0 },
            ["stopRule"] = roomMode == "emote" ? "manual" : samplePreferences.MusicStopRule.RawValue,
            ["tailHoldMs"] = samplePreferences.MusicTailHoldMs, ["cycleStopStatus"] = "waiting-for-score", ["stopCommitted"] = false,
            ["interruption"] = roundPhase is "interrupted" or "cancelled"
                ? new JsonObject { ["memberId"] = "m4", ["source"] = roundPhase == "cancelled" ? "host-cancel" : "game-observation" }
                : null,
            ["receipts"] = roundPhase is "performing" or "stopped" or "interrupted"
                ? new JsonArray(
                    new JsonObject { ["actionId"] = "start-1", ["memberId"] = "m1", ["status"] = "executed", ["simulated"] = false },
                    new JsonObject { ["actionId"] = "start-1", ["memberId"] = "m4", ["status"] = "missed", ["simulated"] = false })
                : new JsonArray()
        };
        return new JsonObject
        {
            ["code"] = "K7M2QX", ["hostId"] = LocalMemberId, ["leaderId"] = roomLeader, ["mode"] = roomMode, ["members"] = members,
            ["observedCharacters"] = new JsonArray(
                new JsonObject { ["characterId"] = "4100200300", ["realmId"] = 3, ["nickname"] = "琉特" },
                new JsonObject { ["characterId"] = "4100200301", ["realmId"] = 3, ["nickname"] = "北境旅人" }),
            ["performance"] = round
        };
    }

    // 合奏意图：按房间服务的规则改内存里的房间；被拒绝时错误显示在反馈横幅与活动胶囊。
    private void ApplyRoomIntent(string intent, string? argument)
    {
        var parts = (argument ?? string.Empty).Split('\n');
        string Part(int index) => index < parts.Length ? parts[index].Trim() : string.Empty;
        void Done(string text) => roomFeedback = new(text, RoomTone.Success);
        var index = roomMembers.FindIndex(member => member.Id == Part(0));
        switch (intent)
        {
            case "rooms.feedback-clear":
                roomFeedback = null;
                break;
            case "rooms.create" or "rooms.join":
                InRoom = true;
                if (intent == "rooms.create" && Part(3) is "emote" or "ensemble") roomMode = Part(3);
                Done(Pick("房间已创建，复制邀请给好友。", "房間已建立，複製邀請給好友。"));
                break;
            case "rooms.leave":
                InRoom = false;
                fallbackInvitation = null;
                invitationCopied = false;
                roomFeedback = null;
                break;
            case "rooms.reconnect":
                RoomDisconnected = false;
                Done(Pick("房间已重新连接，成员列表已更新。", "房間已重新連線，成員列表已更新。"));
                break;
            case "rooms.copy-invite":
            {
                var text = RoomInvitationText.Format(samplePreferences.CommunityEndpoint, "K7M2QX");
                if (ClipboardFails)
                {
                    fallbackInvitation = text;
                    invitationCopied = false;
                    roomFeedback = new(Pick("剪贴板不可用，请选中下方完整邀请后复制。", "剪貼簿不可用，請選取下方完整邀請後複製。"), RoomTone.Success);
                    break;
                }
                CopiedInvitations.Add(text);
                invitationCopied = true;
                fallbackInvitation = null;
                Done(Pick("邀请已复制", "邀請已複製"));
                break;
            }
            case "rooms.mode" when IsRoomOwner && roundPhase is null or "cancelled" or "stopped":
                roomMode = Part(0);
                break;
            case "rooms.ready" when Part(0) is "on" or "off":
            {
                if (Part(0) == "on" && ReadinessRejectCode is { } code)
                {
                    roomFeedback = new(RoomReadinessText.Reason(code) ?? code, RoomTone.Error);
                    notice = Pick($"请求未完成：{code}", $"請求未完成：{code}");
                    break;
                }
                var self = roomMembers.FindIndex(member => member.Id == SelfId);
                roomMembers[self] = roomMembers[self] with { Ready = Part(0) == "on" };
                Done(Part(0) == "on" ? Pick("已就绪", "已就緒") : Pick("已取消就绪", "已取消就緒"));
                break;
            }
            case "rooms.start" when IsRoomOwner:
                RoundRequests.Add(RoomRequests.Round(RoomJson.Parse(JsonSerializer.SerializeToElement(RoomNode()))!,
                    RoomLocalSettings.Effective(samplePreferences)));
                roundPhase = "committed";
                roomTiming = SampleTiming();
                break;
            case "rooms.leader" when index >= 0 && IsRoomOwner && roundPhase is null or "cancelled" or "stopped":
                roomLeader = Part(0);
                Done(Pick($"已将 {roomMembers[index].Name} 设为总指挥。", $"已將 {roomMembers[index].Name} 設為總指揮。"));
                break;
            case "rooms.kick" when index >= 0 && IsRoomOwner && Part(0) != LocalMemberId:
            {
                var name = roomMembers[index].Name;
                roomMembers.RemoveAt(index);
                // 被移出的总指挥由房主接任。
                if (roomLeader == Part(0)) roomLeader = LocalMemberId;
                Done(Pick($"已将 {name} 移出房间。", $"已將 {name} 移出房間。"));
                break;
            }
            case "rooms.local-stop":
            {
                RoomControlRequests.Add(intent);
                var music = roomMode != "emote" && roundPhase is "committed" or "performing";
                if (music) roundPhase = "interrupted";
                var (text, tone) = RoomReadinessText.LocalStop(music ? "stopScheduled" : "cancelled", music);
                roomFeedback = new(text, tone);
                break;
            }
            case "rooms.game-operation" when Part(0) is "on" or "off":
                RoomControlRequests.Add($"{intent}:{Part(0)}");
                operateGame = Part(0) == "on";
                // 仅就绪未开轮时切换会撤回本机就绪。
                if (roundPhase is null or "cancelled" or "stopped")
                {
                    var self = roomMembers.FindIndex(member => member.Id == SelfId);
                    roomMembers[self] = roomMembers[self] with { Ready = false };
                }
                Done(operateGame ? Pick("已开启操作游戏，开始时会按设置操作游戏。", "已開啟操作遊戲，開始時會按設定操作遊戲。")
                    : Pick("已关闭操作游戏，本机只模拟倒计时和执行过程。", "已關閉操作遊戲，本機只模擬倒數和執行過程。"));
                break;
            case "rooms.cancel":
                roundPhase = "cancelled";
                break;
            case "rooms.stop-all":
                roundPhase = "stopped";
                break;
            case "rooms.continue":
                roundPhase = "performing";
                break;
            case "rooms.roster" when index >= 0 && IsRoomOwner:
                EditSampleMember(index, Part(1), Part(2));
                break;
            case "rooms.move" when index >= 0 && IsRoomOwner && int.TryParse(Part(1), out var group):
                if (group != roomMembers[index].Group) EditSampleMember(index, "group", Part(1));
                EditSampleMember(roomMembers.FindIndex(member => member.Id == Part(0)), "order", Part(2));
                break;
        }
    }

    // 与房间服务相同：改组时若目标组已有乐谱使用者则改为参与者并排在末尾；设为乐谱使用者时同组原乐谱使用者改为参与者；
    // 顺序在组内重排；成员编辑后合奏就绪全部撤销。
    private void EditSampleMember(int index, string field, string value)
    {
        var member = roomMembers[index];
        switch (field)
        {
            case "group" when int.TryParse(value, out var group) && group is >= 1 and <= 24:
                roomMembers.RemoveAt(index);
                roomMembers.Add(member with { Group = group, Initiator = member.Initiator && !roomMembers.Any(other => other.Group == group && other.Initiator) });
                break;
            case "order" when int.TryParse(value, out var order) && order is >= 1 and <= 24:
            {
                var group = roomMembers.Where(other => other.Group == member.Group && other.Id != member.Id)
                    .OrderBy(other => other.Initiator ? 0 : 1).ThenBy(other => roomMembers.IndexOf(other)).ToList();
                group.Insert(Math.Clamp(order - 1, 0, group.Count), member);
                roomMembers.RemoveAll(other => other.Group == member.Group);
                roomMembers.AddRange(group);
                break;
            }
            case "role":
                for (var i = 0; i < roomMembers.Count; i++)
                    if (roomMembers[i].Group == member.Group)
                        roomMembers[i] = roomMembers[i] with { Initiator = value == "initiator" ? i == index : i != index && roomMembers[i].Initiator };
                break;
            case "instrument":
                roomMembers[index] = member with { Instrument = value };
                break;
            case "selected":
                roomMembers[index] = member with { Selected = value == "true" };
                return;
            default:
                return;
        }
        if (roomMode == "ensemble")
            for (var i = 0; i < roomMembers.Count; i++) roomMembers[i] = roomMembers[i] with { Ready = false };
    }

    public Task<RoomPreferencesReply> LoadRoomPreferencesAsync(CancellationToken cancellationToken)
    {
        UiIntents.Add(("rooms.preferences-load", null));
        return Task.FromResult(PreferencesLoadFailure is { } code
            ? new RoomPreferencesReply(null, string.Empty, Pick($"请求未完成：{code}", $"請求未完成：{code}"))
            : new RoomPreferencesReply(samplePreferences, ETagOf(samplePreferences), string.Empty));
    }

    // 按后端的规则：修订与 If-Match 都要与当前一致，数值在校验范围内；通过后修订加一。
    public Task<BackendReply> SaveRoomPreferencesAsync(RoomPreferences preferences, string etag, CancellationToken cancellationToken)
    {
        SavedRoomPreferences.Add((preferences, etag));
        if (RejectCode is { } code) return Task.FromResult(Reject(code));
        if (etag != ETagOf(samplePreferences) || preferences.Revision != samplePreferences.Revision)
            return Task.FromResult(Reject("revision.stale"));
        if (preferences.CountdownMs is < 1000 or > 60000 || preferences.TimingOffsetMs is < -2000 or > 2000
            || preferences.MusicDurationMs is < 1 or > 86_400_000 || preferences.MusicTailHoldMs is < 0 or > 10000)
            return Task.FromResult(Reject("request.invalid"));
        samplePreferences = preferences with { Revision = new(preferences.Revision.Value + 1) };
        notice = Pick("已保存合奏本机设置", "已儲存合奏本機設定");
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new BackendReply(true, notice));
    }

    // 23 条样本历史，按后端的页大小一页读完。
    public Task<ColorHistoryReply> QueryColorHistoryAsync(string? beforeOperationId, CancellationToken cancellationToken)
    {
        ColorHistoryQueries.Add(beforeOperationId);
        List<ColorHistoryItem> all = EmptyColorHistory ? [] : SampleHistory();
        var start = beforeOperationId is null ? 0 : all.FindIndex(item => item.OperationId == beforeOperationId) + 1;
        var page = all.Skip(start).Take(ColorHistoryMapper.PageSize).ToList();
        var next = start + page.Count < all.Count ? page[^1].OperationId : null;
        return Task.FromResult(new ColorHistoryReply(true, string.Empty, page.Select(ColorHistoryMapper.Map).ToList(), next));
    }

    private static List<ColorHistoryItem> SampleHistory()
    {
        var source = new ObservationSource("sample", "1", "TW", null);
        var completed = new DateTimeOffset(2026, 11, 10, 13, 0, 0, TimeSpan.Zero);
        OpenEnum<OperationState>[] states = [OperationState.Succeeded, OperationState.Cancelled, OperationState.Failed,
            new("expired")];
        return Enumerable.Range(0, 23).Select(index =>
        {
            var regions = new List<ColorRegion>
            {
                new("region-1", "#1F3A6B", [], ColorMatchMode.ExactHex, 3, 1, true),
                new("region-2", "#F2E9D8", ["#E6DCC2"], ColorMatchMode.Similar, 3, 2, true),
                new("region-3", "#8C2F45", [], ColorMatchMode.Similar, 12, 3, index % 3 != 2)
            };
            var profile = new ColorProfile("winter", 1, index % 2 == 0 ? "冬夜禮服" : "樂團制服白", ColorStrategy.Precise, regions, new(1));
            // 每 4 条里有一条没有实测（只显示目标色），一条有色码但后端没有给出色差（不显示色差）。
            var best = index % 4 == 3 ? null : new ColorMeasurement(
                new Dictionary<string, string> { ["region-1"] = "#213D70", ["region-2"] = "#E6DCC2", ["region-3"] = "#A8455A" },
                completed, source, new(1), ObservationQuality.Confirmed)
            {
                DeltaByRegion = index % 4 == 1 ? new Dictionary<string, double?>()
                    : new Dictionary<string, double?> { ["region-1"] = 1.4, ["region-2"] = 4.8, ["region-3"] = 9.6 }
            };
            var id = "color-op-" + (100 - index).ToString("D3", CultureInfo.InvariantCulture);
            return new ColorHistoryItem("run-" + id, id, "sample", states[index % states.Length], completed.AddMinutes(-37 * index),
                profile, null, best, best);
        }).ToList();
    }
}

using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Controls;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Controls.Tables;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Overlay;
using MabiTool.Desktop.Pages.Rooms;
using Xunit;
using RoomMember = MabiTool.Desktop.Pages.Rooms.RoomMember;
using static MabiTool.Desktop.Tests.TestUi;

namespace MabiTool.Desktop.Tests;

/// <summary>
/// 合奏页：房间协议解析、开轮与就绪请求、邀请与秒数输入、默认值与绑定规则、拖动落位与按钮坐标换算、
/// 阶段与按钮的映射、时间基准的换算，以及页面上的开始、房主免就绪、指定总指挥、移出成员、本机停止、操作游戏、
/// 按键录入、加入、成员编辑、拖动、复制邀请与信息浮窗。
/// </summary>
public sealed class RoomsTests
{
    private const string RoomJsonText = """
        {
          "code": "AB12CD", "hostId": "h", "leaderId": "a", "mode": "ensemble",
          "observedCharacters": [ { "characterId": "4100200300", "realmId": 3, "nickname": "琉特" } ],
          "members": [
            { "id": "h", "nickname": "房主", "role": "host", "ensembleGroup": 1, "ensembleOrder": 1, "ensembleRole": "initiator",
              "connected": true, "ready": true, "instrument": "魯特琴", "capabilities": { "clockRoundTripMs": 12.5 } },
            { "id": "a", "nickname": "成員甲", "role": "member", "ensembleGroup": 2, "ensembleOrder": 2, "ensembleRole": "joiner",
              "connected": false, "ready": false, "selected": false, "instrument": "",
              "ensembleFeedback": { "state": "wrong", "text": "加入顺序不符", "actualOrder": 3 },
              "music": { "title": "夜風與營火", "wholeScoreDurationSeconds": 184.5, "selectedDurationSeconds": null } }
          ],
          "performance": { "id": "r1", "mode": "ensemble", "phase": "interrupted", "leaderId": "a", "participantIds": ["h"],
            "countdownMs": 6500, "requestedCountdownMs": 5000, "stopRule": "duration", "tailHoldMs": 0,
            "startAction": { "id": "s1", "targetServerMs": 10 }, "stopAction": { "id": "t1", "targetServerMs": 20 },
            "interruption": { "memberId": "a", "evidenceSource": "player-report" },
            "receipts": [ { "actionId": "s1", "memberId": "h", "status": "executed", "simulated": true } ] }
        }
        """;

    private static RoomState ParseSample() => RoomJson.Parse(JsonDocument.Parse(RoomJsonText).RootElement)!;

    [AvaloniaFact]
    public void RoomJsonReadsProtocolFields()
    {
        var room = ParseSample();
        Assert.Equal(("AB12CD", "h", "a", "ensemble"), (room.Code, room.HostId, room.LeaderId, room.Mode));
        var host = room.Members[0];
        Assert.True(host.IsHost && host.Selected && host.Initiator && host.Connected && host.Ready);
        Assert.Equal(12.5, host.ClockRoundTripMs);
        var member = room.Members[1];
        Assert.False(member.Selected);
        Assert.Equal((2, 2, ""), (member.Group, member.Order, member.Instrument));
        Assert.Equal(new RoomScore("夜風與營火", 184.5, null), member.Music);
        // 区服在房间协议里是数字，匹配角色时按文字带回。
        Assert.Equal(new RoomCharacter("4100200300", "3", "琉特"), Assert.Single(room.Characters));
        var round = room.Round!;
        Assert.Equal(("interrupted", "duration", 6500, 5000), (round.Phase, round.StopRule, round.CountdownMs, round.RequestedCountdownMs));
        Assert.True(round.HasStop && round.StopCommitted && round.Active);
        Assert.Equal("s1", round.StartActionId);
        Assert.Equal("player-report", round.Interruption!.EvidenceSource);
    }

    [AvaloniaFact]
    public void RoundRequestCarriesModeParticipantsCountdownAndStopParameters()
    {
        var room = ParseSample() with
        {
            Members = [.. ParseSample().Members, new RoomMember("b", "成員乙", false, true, true, true, 1, 2, false, "")]
        };
        var preferences = RoomPreferences.Default with { CountdownMs = 8000, MusicDurationMs = 45000, MusicTailHoldMs = 1500 };

        var duration = RoomRequests.Round(room, preferences with { MusicStopRule = MusicStopRule.Duration });
        Assert.Equal(("ensemble", 8000, "duration", (int?)45000, (int?)null),
            (duration.Mode, duration.CountdownMs, duration.StopRule?.RawValue, duration.StopAfterMs, duration.TailHoldMs));
        // 只带勾选参与的成员。
        Assert.Equal(["h", "b"], duration.ParticipantIds);

        var cycle = RoomRequests.Round(room, preferences with { MusicStopRule = MusicStopRule.FirstCycle });
        Assert.Equal(("first-cycle", (int?)null, (int?)1500), (cycle.StopRule?.RawValue, cycle.StopAfterMs, cycle.TailHoldMs));

        var manual = RoomRequests.Round(room, preferences with { MusicStopRule = MusicStopRule.Manual });
        Assert.Equal(("manual", (int?)null, (int?)null), (manual.StopRule?.RawValue, manual.StopAfterMs, manual.TailHoldMs));

        // 表情动作不带任何停止参数。
        var emote = RoomRequests.Round(room with { Mode = "emote" }, preferences with { MusicStopRule = MusicStopRule.Duration });
        Assert.Equal(("emote", 8000), (emote.Mode, emote.CountdownMs));
        Assert.Null(emote.StopRule);
        Assert.Null(emote.StopAfterMs);
        Assert.Null(emote.TailHoldMs);
    }

    [AvaloniaFact]
    public void ReadinessAndCharacterRequestsLeaveMeasurementsToBackend()
    {
        var preferences = RoomPreferences.Default with { Revision = new(7), TimingOffsetMs = -40 };
        var request = RoomRequests.Readiness(true, "joiner", true, new UInt64String(12), preferences);
        Assert.Equal((true, true, "joiner", -40), (request.Ready, request.Simulation, request.MusicRole, request.TimingOffsetMs));
        Assert.Null(request.ReadyMode);
        Assert.Equal((new UInt64String(12), new UInt64String(7)), (request.ExpectedBindingRevision!.Value, request.ExpectedPreferencesRevision!.Value));
        Assert.False(request.CanStart || request.CanStop || request.ChatDraftConfirmed);
        Assert.Equal((0d, 0), (request.ClockRoundTripMs, request.WindowActivationMs));

        Assert.Equal(new RoomCharacterBindingRequest("m4", "4100200300", "3"), RoomRequests.Character("m4", "4100200300", "3"));
        // 取消匹配：编号与区服同时为空，两个 null 都显式发送；区服按十进制数字字符串发送。
        Assert.Equal(new RoomCharacterBindingRequest("m4", null, null), RoomRequests.Character("m4", "", "3"));
        Assert.Equal("""{"memberId":"m4","characterId":null,"realmId":null}""",
            JsonSerializer.Serialize(RoomRequests.Character("m4", null, null), ContractJson.Options));
        var observed = ParseSample().Characters[0];
        Assert.Equal("""{"memberId":"m4","characterId":"4100200300","realmId":"3"}""",
            JsonSerializer.Serialize(RoomRequests.Character("m4", observed.CharacterId, observed.RealmId), ContractJson.Options));
    }

    [AvaloniaFact]
    public void InvitationAcceptsFullInviteOrSixCharacterCode()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        Assert.Equal("MabiSync/1|wss://47.111.3.134/|K7M2QX", RoomInvitationText.Format("wss://47.111.3.134/", "K7M2QX"));
        Assert.Equal(("wss://example.invalid/ws", "AB12CD"), RoomInvitationText.Parse(" MabiSync/1|wss://example.invalid/ws|ab12cd ", out _));
        Assert.Equal(((string?)null, "AB12CD"), RoomInvitationText.Parse("ab12cd", out _));
        foreach (var bad in new[] { "ABC", "MabiSync/2|wss://x/|AB12CD", "MabiSync/1|http://x/|AB12CD", "MabiSync/1|wss://x/|AB12" })
        {
            Assert.Null(RoomInvitationText.Parse(bad, out var error));
            Assert.StartsWith("邀请不完整", error);
        }
    }

    [AvaloniaFact]
    public void SecondsAndOffsetInputsFollowOriginalRanges()
    {
        Assert.Equal(2500, RoomLocalSettings.ParseCountdown("2.5", out _));
        Assert.Equal(60000, RoomLocalSettings.ParseCountdown("60", out _));
        foreach (var bad in new[] { "0.5", "61", "1.2345", "abc", "-3" })
            Assert.Null(RoomLocalSettings.ParseCountdown(bad, out _));
        Assert.Equal(1, RoomLocalSettings.ParseDuration("0.001", out _));
        Assert.Null(RoomLocalSettings.ParseDuration("0", out _));
        Assert.Equal((0, 10000), (RoomLocalSettings.ParseTail("0", out _), RoomLocalSettings.ParseTail("10", out _)));
        Assert.Null(RoomLocalSettings.ParseTail("10.5", out var tailError));
        Assert.NotNull(tailError);
        Assert.Equal((-2000, 2000), (RoomLocalSettings.ParseOffset("-2000", out _), RoomLocalSettings.ParseOffset("+2000", out _)));
        Assert.Null(RoomLocalSettings.ParseOffset("12.5", out _));
        Assert.Equal("1.5", RoomLocalSettings.Seconds(1500));
    }

    [AvaloniaFact]
    public void UnsavedPreferencesTakeOriginalDefaultsAndSavedOnesStayAsIs()
    {
        var fresh = RoomLocalSettings.Effective(RoomPreferences.Default);
        Assert.Equal("community", fresh.PublicProvider);
        Assert.Equal(RoomStartKind.ChatDraft, RoomLocalSettings.StartKind(fresh, "emote"));
        Assert.Equal(RoomStartKind.Button, RoomLocalSettings.StartKind(fresh, "ensemble"));
        Assert.Equal(new InputBindingPreference(LocalBindingKind.Key, 0x20), RoomLocalSettings.Stop(fresh, "ensemble"));
        Assert.Equal(LocalBindingKind.ChatDraft, RoomLocalSettings.Start(fresh, "emote")!.Kind.KnownValue);

        var saved = RoomPreferences.Default with { Revision = new(2) };
        Assert.Same(saved, RoomLocalSettings.Effective(saved));
    }

    [AvaloniaFact]
    public void StartAndStopMustDifferForScoreUsersOnly()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var space = new InputBindingPreference(LocalBindingKind.Key, 0x20);
        var same = RoomLocalSettings.WithStop(RoomLocalSettings.WithStart(RoomPreferences.Default, "ensemble", space), "ensemble", space);
        Assert.False(same.Bindings["ensemble"].StopIsToggle);
        Assert.StartsWith("开始和停止需要使用不同", RoomLocalSettings.BindingError(same, "ensemble", joiner: false));
        Assert.Null(RoomLocalSettings.BindingError(same, "ensemble", joiner: true));
        Assert.Null(RoomLocalSettings.BindingError(same, "emote", joiner: false));

        // 换开始方式：预填指令写入聊天草稿；换到游戏按钮时原按键绑定不沿用。
        var chat = RoomLocalSettings.WithStartKind(same, "emote", RoomStartKind.ChatDraft);
        Assert.Equal((2, LocalBindingKind.ChatDraft), (chat.StartInputKinds["emote"], chat.Bindings["emote"].Start!.Kind.KnownValue));
        var button = RoomLocalSettings.WithStartKind(same, "ensemble", RoomStartKind.Button);
        Assert.Null(button.Bindings["ensemble"].Start);
        Assert.Equal(space, button.Bindings["ensemble"].Stop);
    }

    [AvaloniaFact]
    public void DropPositionBecomesGroupAndOrder()
    {
        // 第 1 组三人、第 2 组两人。
        int[] groups = [1, 1, 1, 2, 2];
        // 第 1 组第 2 位向下拖过第 2 组首位：进入第 2 组第 2 位。
        Assert.Equal((2, 2), RosterDrop.Resolve(groups, 1, 3));
        // 第 2 组首位向上拖过第 1 组末位：进入第 1 组第 3 位。
        Assert.Equal((1, 3), RosterDrop.Resolve(groups, 3, 2));
        // 组内调换。
        Assert.Equal((1, 1), RosterDrop.Resolve(groups, 2, 0));
        // 原位或越界没有变化。
        Assert.Null(RosterDrop.Resolve(groups, 2, 2));
        Assert.Null(RosterDrop.Resolve(groups, 0, 9));
    }

    [AvaloniaFact]
    public void PickedPointBecomesClientCoordinates()
    {
        var client = new RoomGameWindow(100, 50, 1600, 900, 144);
        Assert.Equal(new InputBindingPreference(LocalBindingKind.Button, X: 800, Y: 450, ClientWidth: 1600, ClientHeight: 900, Dpi: 144),
            ButtonPick.ToBinding(client, new PixelPoint(900, 500)));
        Assert.Null(ButtonPick.ToBinding(client, new PixelPoint(99, 500)));
        Assert.Null(ButtonPick.ToBinding(client, new PixelPoint(1700, 500)));
    }

    [AvaloniaFact]
    public void RejectionsMapToShortSentences()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        ApiProblem Problem(string code, string? protocol = null) => new("about:blank", "x", 409, code, "x", "t",
            protocol is null ? new Dictionary<string, JsonElement>() : new() { ["protocolCode"] = JsonSerializer.SerializeToElement(protocol) }, false);
        Assert.StartsWith("有选中的成员未就绪", RoomReadinessText.Failure(Problem("room.commandRejected", "MEMBER_NOT_READY"), 409));
        Assert.StartsWith("总指挥需加入本轮", RoomReadinessText.Failure(Problem("room.commandRejected", "LEADER_NOT_PARTICIPATING"), 409));
        Assert.Equal("该操作由房主执行", RoomReadinessText.Failure(Problem("permission.denied"), 403));
        Assert.Equal("未知原因", RoomReadinessText.Failure(Problem("room.somethingNew"), 409));
        Assert.Equal("后端内部出错", RoomReadinessText.Failure(null, 502));

        // 指定总指挥与移出成员。
        Assert.StartsWith("房主退出请点「关闭房间」", RoomReadinessText.Failure(Problem("room.commandRejected", "CANNOT_KICK_HOST"), 409, "rooms.kick"));
        Assert.Equal("该成员已离开房间", RoomReadinessText.Failure(Problem("resource.notFound"), 404, "rooms.kick"));
        Assert.Equal("该成员已离开房间", RoomReadinessText.Failure(Problem("resource.notFound"), 404, "rooms.leader"));
        Assert.Equal("找不到所选内容", RoomReadinessText.Failure(Problem("resource.notFound"), 404, "rooms.start"));
        Assert.Equal("本轮仍在进行，请先取消或全停", RoomReadinessText.Failure(Problem("room.activityActive"), 409, "rooms.leader"));
        Assert.Equal("该操作由房主执行", RoomReadinessText.Failure(Problem("permission.denied"), 403, "rooms.kick"));
        // 房主免就绪开轮前准备本机输入失败。
        Assert.Equal("尚未绑定游戏窗口", RoomReadinessText.Failure(Problem("input.bindingUnavailable"), 409, "rooms.start"));

        // 轮次命令受理后在执行阶段被拒：结果来自 Operation，成功为空，失败给出原因，仍在执行时说明待确认。
        OperationSnapshot Operation(OperationState state, ApiProblem? problem = null) => new("op-" + new string('a', 32), "room.round.prepare", "o", [],
            state, "x", new(1), null, new(DateTimeOffset.UnixEpoch, null, null), null, problem, new(CancellationState.None, null), EffectStatus.None);
        Assert.Null(RoomReadinessText.RoundOutcome(Operation(OperationState.Succeeded)));
        Assert.Equal(("本轮仍在进行，请先取消或全停", RoomTone.Error),
            RoomReadinessText.RoundOutcome(Operation(OperationState.Failed, Problem("room.activityActive")), "rooms.start"));
        Assert.Equal(("有选中的成员未就绪", RoomTone.Error),
            RoomReadinessText.RoundOutcome(Operation(OperationState.Failed, Problem("room.memberNotReady", "MEMBER_NOT_READY")), "rooms.start"));
        Assert.Equal(RoomTone.Warning, RoomReadinessText.RoundOutcome(Operation(OperationState.Running))!.Value.Tone);

        // 抓包识别的被拒原因按抓包说明；轮询输入占用时只查询 Operation 标识，合奏本机准备的 room-… 不查询。
        Assert.Equal("所选网卡不可用，请重新选择", RoomReadinessText.Failure(Problem("observation.channelUnavailable"), 409, "rooms.capture-start"));
        Assert.Equal("请先选择游戏窗口", RoomReadinessText.Failure(Problem("input.bindingUnavailable"), 409, "rooms.capture-start"));
        Assert.True(LiveBackend.IsOperationId("op-" + new string('0', 32)));
        Assert.False(LiveBackend.IsOperationId("room-" + new string('0', 32)));
        Assert.False(LiveBackend.IsOperationId("op-" + new string('G', 32)));

        // 本机停止的三种结果各一句。
        Assert.Equal(("已发出本机停止，等房主决定", RoomTone.Success), RoomReadinessText.LocalStop("stopScheduled", true));
        Assert.Equal(RoomTone.Warning, RoomReadinessText.LocalStop("stopScheduled", false).Tone);
        Assert.Equal(("本机停止失败，请在游戏中手动停止", RoomTone.Error), RoomReadinessText.LocalStop("stopRejected", true));
        Assert.Equal(("已取消本机任务", RoomTone.Success), RoomReadinessText.LocalStop("cancelled", false));
    }

    [AvaloniaFact]
    public void TimingCountsDownOnTheLocalMonotonicClock()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        long At(double seconds) => (long)(seconds * Stopwatch.Frequency);
        // 收到响应时：距开始 3 秒、距停止 33 秒；服务器此刻 1000，个人偏移 −40。
        var timing = new RoomTiming(1000, 20, 2, -40, 3000, 33000, 0);
        Assert.Equal(1000, timing.StartRemaining(At(2))!.Value, 3);
        Assert.Equal(31000, timing.StopRemaining(At(2))!.Value, 3);
        Assert.Equal(4000 - 40 - (1000 + 2000), timing.Remaining(4000, At(2)), 3);
        Assert.Null((timing with { StopRemainingMs = null }).StopRemaining(At(2)));

        // 开始前：蓝色的距离同步开始，按本轮倒计时算比例。
        var sample = ParseSample();
        var committed = sample.Round! with { Phase = "committed", Receipts = [] };
        var starting = RoomClock.Countdown(committed, timing, At(1))!;
        Assert.Equal((false, "距离同步开始", "2.0 秒", false), (starting.Stopping, starting.Label, starting.Value, starting.Calibrating));
        Assert.Equal(2000d / 6500, starting.Fraction, 6);

        // 开始后且已确认停止：绿色的距离统一停止，总长是停止与开始的间隔。
        var stopping = RoomClock.Countdown(committed with { Phase = "performing" }, timing, At(5))!;
        Assert.Equal((true, "距离统一停止", "28.0 秒"), (stopping.Stopping, stopping.Label, stopping.Value));
        Assert.Equal(28000d / 30000, stopping.Fraction, 6);
        Assert.Null(RoomClock.Countdown(committed with { Phase = "performing", HasStop = false }, timing, At(5)));
        Assert.Null(RoomClock.Countdown(committed with { Phase = "stopped" }, timing, At(5)));

        // 没有校时样本：只说明正在校时。
        var calibrating = RoomClock.Countdown(committed, null, 0)!;
        Assert.Equal((true, "正在校时"), (calibrating.Calibrating, calibrating.Value));
        Assert.Equal("正在校时", RoomClock.ClockText(null));
        Assert.Equal("网络往返 20.0 ms · 校时估计误差 ±2.0 ms", RoomClock.ClockText(timing));

        // 乐谱进度：还没有观测的起奏时刻时从同步开始 + 1.5 秒（游戏的起奏偏移）起算；按完整乐谱时长循环计轮，总指挥一轮且已安排停止时不循环。
        var score = new RoomScore("夜風與營火", 184.5, null);
        Assert.Equal(((double?)null, "一轮 184.5 秒 · 等待开始"), (RoomClock.ScoreProgress(committed, score, timing, At(4))!.Fraction,
            RoomClock.ScoreProgress(committed, score, timing, At(4))!.Text));
        var looping = RoomClock.ScoreProgress(committed with { Phase = "performing" }, score, timing, At(3 + 1.5 + 184.5 + 12.5))!;
        Assert.Equal("12.5 / 184.5 秒 · 第 2 轮 · 估计进度", looping.Text);
        Assert.Equal(12.5 / 184.5, looping.Fraction!.Value, 6);
        var ending = RoomClock.ScoreProgress(committed with { Phase = "performing", StopRule = "first-cycle" }, score, timing, At(3 + 1.5 + 190))!;
        Assert.Equal(("184.5 / 184.5 秒 · 第 1 轮 · 估计进度", 1d), (ending.Text, ending.Fraction!.Value));
        Assert.Equal("一轮 184.5 秒 · 等待演奏", RoomClock.ScoreProgress(committed, score, null, 0)!.Text);
        Assert.Null(RoomClock.ScoreProgress(committed, score with { WholeSeconds = null }, timing, 0));

        // 阶段文字：本机演奏将在 x 秒后触发；模拟时注明模拟；表情为本机表情。
        var room = sample with { Round = committed with { ParticipantIds = ["h"] } };
        var preferences = RoomLocalSettings.Effective(RoomPreferences.Default);
        Assert.Equal("本机演奏将在 2.0 秒后触发", RoomFlow.Describe(Data(room) with { Timing = timing }, "ensemble", preferences, now: At(1)).Phase);
        Assert.Equal("本机模拟演奏将在 2.0 秒后触发",
            RoomFlow.Describe(Data(room) with { Timing = timing, Simulation = true }, "ensemble", preferences, now: At(1)).Phase);
        var emote = room with { Mode = "emote", Round = room.Round! with { Mode = "emote" } };
        Assert.Equal("本机表情将在 0.0 秒后触发", RoomFlow.Describe(Data(emote) with { Timing = timing }, "emote", preferences, now: At(4)).Phase);
        Assert.Equal("任务已安排", RoomFlow.Describe(Data(room), "ensemble", preferences).Phase);
    }

    private static RoomsPageData Data(RoomState? room, string? self = "h", string session = "connected") =>
        new(true, session, self, room, RoomServerView.Unknown, RoomPreferences.Default, "\"e\"", false, new(0, 0, 100, 100, 96), "琉特", "F9", null);

    [AvaloniaFact]
    public void PhaseNextStepAndButtonsFollowRoomState()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var preferences = RoomLocalSettings.Effective(RoomPreferences.Default) with
        {
            Bindings = new Dictionary<string, ModeInputPreferences>
            {
                ["emote"] = new(new(LocalBindingKind.ChatDraft), null, false), ["independent"] = new(null, null, false),
                ["ensemble"] = new(new(LocalBindingKind.Button, X: 1, Y: 1, ClientWidth: 10, ClientHeight: 10, Dpi: 96), new(LocalBindingKind.Key, 0x20), false)
            }
        };
        var room = ParseSample() with { Round = null };

        // 未入房。
        var outside = RoomFlow.Describe(Data(null, null, "disconnected"), "ensemble", preferences);
        Assert.Equal(("连接房间", "创建房间，或粘贴邀请加入。", "创建房间，或接受好友邀请"), (outside.Phase, outside.NextStep, outside.RoleSummary));
        Assert.False(outside.ShowReady || outside.ShowStart);

        // 房主免就绪：不显示就绪；房主自己未就绪、勾选的其他成员都就绪（未勾选的成员不阻塞）时全员已就绪，开始可用。
        var hostUnready = room with { Members = [room.Members[0] with { Ready = false }, room.Members[1]] };
        var host = RoomFlow.Describe(Data(hostUnready), "ensemble", preferences);
        Assert.Equal(("全员已就绪", "点击开始"), (host.Phase, host.NextStep));
        Assert.True(host.ShowStart && host.CanStart && !host.ShowReady);
        Assert.Equal("房主 · 第 1 组 · 乐谱使用者 · 第 1 位 · 乐器：魯特琴", host.RoleSummary);

        // 勾选的成员未就绪：下一步列出名字；开始按原工具仍可点击，点击时说明等待谁就绪，不发送。
        var waiting = room with { Members = [room.Members[0], room.Members[1] with { Selected = true }] };
        var blocked = RoomFlow.Describe(Data(waiting), "ensemble", preferences);
        Assert.Equal(("等待全员就绪", "未就绪：成員甲"), (blocked.Phase, blocked.NextStep));
        Assert.True(blocked.CanStart);
        Assert.Equal("等待就绪：成員甲。", blocked.StartIssue);
        Assert.Empty(host.StartIssue);

        // 开始请求等待后端执行结果期间：开始与就绪停用并说明原因。
        var busy = RoomFlow.Describe(Data(hostUnready) with { RoundBusy = true }, "ensemble", preferences);
        Assert.False(busy.CanStart);
        Assert.Equal("等待上次操作的结果…", busy.StartHint);
        Assert.False(RoomFlow.Describe(Data(hostUnready, "a") with { RoundBusy = true }, "ensemble", preferences).CanReady);
        // 倒计时等参数有误时停用并说明。
        Assert.StartsWith("倒计时或时长填写有误", RoomFlow.Describe(Data(hostUnready), "ensemble", preferences, settingsInvalid: true).StartHint);

        // 成员视角：参与者显示参与者提示，不显示开始。
        var member = RoomFlow.Describe(Data(waiting, "a"), "ensemble", preferences);
        Assert.Equal(("未就绪", "确认本机设置后点击就绪"), (member.Phase, member.NextStep));
        Assert.True(member.Joiner && !member.ShowStart && member.ShowReady && !member.ReadyOn);
        Assert.False(member.CanEditRoster);

        // 中断：房主看到全员停止与继续（已安排停止时为按原计划停止），成员等待房主。
        var interrupted = ParseSample();
        var hostInterrupted = RoomFlow.Describe(Data(interrupted), "ensemble", preferences);
        Assert.Equal(("演奏中断", RoomTone.Error), (hostInterrupted.Phase, hostInterrupted.PhaseTone));
        Assert.Equal("请选择全员停止或继续", hostInterrupted.NextStep);
        Assert.True(hostInterrupted.ShowContinue && hostInterrupted.ShowStopAll && !hostInterrupted.ShowCancel);
        Assert.Equal("继续，按原计划停止", hostInterrupted.ContinueLabel);
        Assert.Contains("成員甲 手动报告已提前停止", hostInterrupted.MusicStatus);
        Assert.False(hostInterrupted.CanReady || hostInterrupted.CanEditRoster);
        Assert.Equal("你正在旁观本轮。", RoomFlow.Describe(Data(interrupted, "a"), "ensemble", preferences).NextStep);

        // 表情本轮准备中：取消本轮可见；本机有待触发任务时可取消本机任务。
        var emote = interrupted with { Mode = "emote", Round = interrupted.Round! with { Mode = "emote", Phase = "committed", Receipts = [] } };
        var emoteStatus = RoomFlow.Describe(Data(emote) with { LocalPreparationId = "p" }, "emote", preferences);
        Assert.Equal("任务已安排", emoteStatus.Phase);
        Assert.True(emoteStatus.ShowCancel && emoteStatus.ShowLocalStop && emoteStatus.CanLocalStop && !emoteStatus.ShowStopAll);
        Assert.Equal("取消本机任务", emoteStatus.LocalStopLabel);
        Assert.False(RoomFlow.Describe(Data(emote), "emote", preferences).CanLocalStop);

        // 合奏进行中：参与本轮的成员可本机停止。
        var performing = interrupted with { Round = interrupted.Round! with { Phase = "performing" } };
        var localStop = RoomFlow.Describe(Data(performing), "ensemble", preferences);
        Assert.Equal(("本机停止", true, true), (localStop.LocalStopLabel, localStop.ShowLocalStop, localStop.CanLocalStop));

        // 房间连接中断。
        var lost = RoomFlow.Describe(Data(room, session: "reconnecting"), "ensemble", preferences);
        Assert.Equal(("房间连接已中断", "连接已断开，可重连或关闭房间"), (lost.Phase, lost.NextStep));
        Assert.False(lost.CanReady || lost.CanStart);
    }

    [AvaloniaFact]
    public void StartSendsTheFullRoundRequestOnceEveryoneSelectedIsReady()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "rooms");
        try
        {
            var model = (RoomsPageModel)Page<RoomsPage>(window).DataContext!;
            // 小麥田已离线、北境旅人未就绪：点击开始只说明等待谁就绪，不发送开轮请求。
            Assert.Contains("小麥田", Named<TextBlock>(window, "NextStepText").Text);
            Click(window, Named<Button>(window, "StartButton"));
            LayoutProbe.Settle();
            Assert.Empty(backend.RoundRequests);
            Assert.Equal("等待就绪：小麥田、北境旅人。", Named<TextBlock>(window, "FeedbackText").Text);
            model.EditCell(model.Rows.Single(row => row.Id == "m3"), "selected", "false");
            model.EditCell(model.Rows.Single(row => row.Id == "m4"), "selected", "false");
            LayoutProbe.Settle();
            Assert.Equal(("rooms.roster", "m4\nselected\nfalse"), backend.UiIntents[^1]);
            Assert.True(Named<Button>(window, "StartButton").IsEnabled);
            Click(window, Named<Button>(window, "StartButton"));
            LayoutProbe.Settle();
            var request = Assert.Single(backend.RoundRequests);
            // 尾音保留的缺省值是 0 秒。
            Assert.Equal(("ensemble", 5000, "first-cycle", (int?)0), (request.Mode, request.CountdownMs, request.StopRule?.RawValue, request.TailHoldMs));
            Assert.Equal(["m1", "m2"], request.ParticipantIds);
            Assert.StartsWith("本机演奏将在 ", Named<TextBlock>(window, "PhaseText").Text);
            Assert.True(Named<Button>(window, "CancelButton").IsVisible);
            Assert.True(Named<DockPanel>(window, "CountdownPanel").IsEffectivelyVisible);
            Assert.Equal("距离同步开始", Named<TextBlock>(window, "CountdownLabel").Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RecognizedSelfAndScoreAreReadOnlyAndTheShellShowsTheCaptureState()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        // 合奏页不再有抓包卡与成员核对；本机动作里只读显示识别到的本机与当前乐谱，左上角显示角色名。
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "rooms");
        try
        {
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Control>(), control => control.Name is "CaptureStatusText" or "CaptureSettingsButton");
            Assert.Equal("已识别本机：琉特", Named<TextBlock>(window, "ObservedSelfText").Inlines!.Text);
            Assert.Equal("当前乐谱：夜風與營火（184.5 秒）", Named<TextBlock>(window, "ObservedScoreText").Inlines!.Text);
            Assert.Equal("琉特", Named<TextBlock>(window, "CaptureLineText").Text);
        }
        finally { window.Close(); }

        // 抓包中但尚未识别角色：左上角提示切换地图，合奏页不显示识别行。
        var pending = LayoutProbe.Open(new SampleBackend { CaptureCharacter = null }, 1280, 860, "rooms");
        try
        {
            Assert.Equal("未识别角色，请切换一次地图", Named<TextBlock>(pending, "CaptureLineText").Text);
            Assert.False(Named<StackPanel>(pending, "ObservedPanel").IsVisible);
        }
        finally { pending.Close(); }

        // 未抓包：左上角写明原因，点击打开设置页的抓包区。
        var failed = LayoutProbe.Open(new SampleBackend { CaptureState = "faulted", CaptureReason = "observation.channelNotFound" }, 1280, 860, "rooms");
        try
        {
            Assert.StartsWith("未抓包：找不到游戏网卡", Named<TextBlock>(failed, "CaptureLineText").Text);
            Click(failed, Named<Button>(failed, "CaptureChip"));
            LayoutProbe.Settle();
            Assert.True(Named<Card>(failed, "CapturePanel").IsEffectivelyVisible);
            Assert.Single(failed.GetVisualDescendants().OfType<MabiTool.Desktop.Pages.Settings.SettingsPage>());
        }
        finally { failed.Close(); }
    }

    [AvaloniaFact]
    public void CardsCollapseWithArrowTogglesAndTheChoiceIsSavedInPreferences()
    {
        CardStateStore.Restore(null);
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "rooms");
        var path = Path.Combine(Path.GetTempPath(), $"moongate-preferences-{Guid.NewGuid():N}.json");
        try
        {
            // 缺省只收起执行记录与诊断。
            Assert.False(Named<TextBlock>(window, "ClockText").IsEffectivelyVisible);
            Assert.True(Named<TextBlock>(window, "InputState").IsEffectivelyVisible);
            Click(window, Named<Card>(window, "DiagnosticsPanel").Toggle);
            Click(window, Named<Card>(window, "LocalPanel").Toggle);
            LayoutProbe.Settle();
            Assert.True(Named<TextBlock>(window, "ClockText").IsEffectivelyVisible);
            Assert.False(Named<TextBlock>(window, "InputState").IsEffectivelyVisible);
            Assert.True(Named<Card>(window, "LocalPanel").Toggle.IsEffectivelyVisible);
            // 只有这两张卡片的状态被记下：本机动作收起，执行记录与诊断展开；其余取缺省（展开）。
            Assert.Equal(["rooms/diagnostics", "rooms/local"], CardStateStore.All.Keys.Order());
            Assert.False(CardStateStore.IsExpanded("rooms", "local"));
            Assert.True(CardStateStore.IsExpanded("rooms", "diagnostics", false));

            new MabiTool.Desktop.Infrastructure.DesktopPreferenceStore(path).Save(MabiTool.Desktop.Infrastructure.DesktopPreferences.Current);
            var saved = new MabiTool.Desktop.Infrastructure.DesktopPreferenceStore(path).Load().Cards!;
            Assert.Equal((new CardState(false), new CardState(true), 2), (saved["rooms/local"], saved["rooms/diagnostics"], saved.Count));
        }
        finally
        {
            window.Close();
            CardStateStore.Restore(null);
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void HostStartsWithoutReadyingAndRoomControlsReachTheBackend()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "rooms");
        try
        {
            var model = (RoomsPageModel)Page<RoomsPage>(window).DataContext!;
            Assert.False(Named<Button>(window, "ReadyButton").IsVisible);

            // 操作游戏：关闭后标题显示模拟模式，再打开。
            Click(window, Named<CheckBox>(window, "OperateGameBox"));
            LayoutProbe.Settle();
            Assert.Equal(("rooms.game-operation", "off"), backend.UiIntents[^1]);
            Assert.Equal("模拟模式", model.LocalSummary);
            Assert.False(Named<CheckBox>(window, "OperateGameBox").IsChecked);
            Click(window, Named<CheckBox>(window, "OperateGameBox"));
            LayoutProbe.Settle();
            Assert.Equal(("rooms.game-operation", "on"), backend.UiIntents[^1]);
            Assert.True(model.OperateGame);

            // 指定总指挥：下拉改变即发送一次。
            model.Conductor = model.ConductorChoices.Single(member => member.Id == "m4");
            LayoutProbe.Settle();
            Assert.Equal(("rooms.leader", "m4"), backend.UiIntents[^1]);
            Assert.Equal("北境旅人", ((RoomConductorChoice)Named<ComboBox>(window, "ConductorBox").SelectedItem!).Nickname);
            Assert.Equal(1, backend.UiIntents.Count(item => item.Intent == "rooms.leader"));

            // 移出房间：被移出的总指挥由房主接任。
            model.Kick(model.Rows.Single(row => row.Id == "m4"));
            LayoutProbe.Settle();
            Assert.Equal(("rooms.kick", "m4"), backend.UiIntents[^1]);
            Assert.DoesNotContain(model.Rows, row => row.Id == "m4");
            Assert.Equal("m1", model.Conductor!.Id);

            // 房主自己未就绪也能开始：只看勾选参与的其他成员。
            model.EditCell(model.Rows.Single(row => row.Id == "m3"), "selected", "false");
            LayoutProbe.Settle();
            Assert.False(model.Data.Self!.Ready);
            Assert.True(Named<Button>(window, "StartButton").IsEnabled);
            Click(window, Named<Button>(window, "StartButton"));
            LayoutProbe.Settle();
            Assert.Single(backend.RoundRequests);

            // 合奏进行中的本机停止：发送一次，房主看到演奏中断与反馈。
            backend.RoundPhase = "performing";
            LayoutProbe.Settle();
            Assert.True(Named<Button>(window, "LocalStopButton").IsEnabled);
            Click(window, Named<Button>(window, "LocalStopButton"));
            LayoutProbe.Settle();
            Assert.Equal(("rooms.local-stop", (string?)null), backend.UiIntents[^1]);
            Assert.Equal("演奏中断", Named<TextBlock>(window, "PhaseText").Text);
            Assert.Equal("已发出本机停止，等房主决定", Named<TextBlock>(window, "FeedbackText").Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void HostSettingsSaveOnCommitAndRejectOutOfRangeSeconds()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "rooms");
        try
        {
            var model = (RoomsPageModel)Page<RoomsPage>(window).DataContext!;
            model.StopRuleCommand.Execute("duration");
            LayoutProbe.Settle();
            Assert.Equal("duration", backend.SavedRoomPreferences[^1].Value.MusicStopRule.RawValue);
            Assert.True(Named<TextBox>(window, "DurationBox").IsEffectivelyVisible);

            model.DurationText = "0";
            model.CommitNumber("duration");
            LayoutProbe.Settle();
            Assert.Single(backend.SavedRoomPreferences);
            Assert.True(model.HasDurationError);
            model.DurationText = "45.5";
            model.CommitNumber("duration");
            LayoutProbe.Settle();
            Assert.Equal(45500, backend.SavedRoomPreferences[^1].Value.MusicDurationMs);
            Assert.False(model.HasDurationError);

            model.CountdownText = "2.5";
            model.CommitNumber("countdown");
            LayoutProbe.Settle();
            Assert.Equal(2500, backend.SavedRoomPreferences[^1].Value.CountdownMs);
            // 每次保存都带读取到的修订与 If-Match。
            Assert.All(backend.SavedRoomPreferences.Select((saved, index) => (saved, index)),
                item => Assert.Equal($"\"room-preferences-{3 + item.index}\"", item.saved.ETag));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void KeyCaptureSavesAtOnceRevokesReadinessAndFlagsSameStartAndStop()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "rooms");
        try
        {
            var model = (RoomsPageModel)Page<RoomsPage>(window).DataContext!;
            Assert.Equal("Space", model.StopKeyText);
            Click(window, Named<Button>(window, "StopKeyButton"));
            Assert.True(model.RecordingStop);
            window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
            Assert.True(model.RecordingStop);
            window.KeyPress(Key.F5, RawInputModifiers.Control, PhysicalKey.F5, null);
            LayoutProbe.Settle();
            Assert.False(model.RecordingStop);
            Assert.Equal(new InputBindingPreference(LocalBindingKind.Key, 0x74, Control: true), backend.SavedRoomPreferences[^1].Value.Bindings["ensemble"].Stop);
            Assert.Equal("Ctrl + F5", model.StopKeyText);
            // 本机已就绪：改动后撤销就绪。
            Assert.Equal(("rooms.ready", "off"), backend.UiIntents[^1]);

            // 开始也改成 Ctrl+F5：说明不能相同，就绪停用。
            model.StartKindCommand.Execute("key");
            LayoutProbe.Settle();
            model.RecordCommand.Execute("start");
            window.KeyPress(Key.F5, RawInputModifiers.Control, PhysicalKey.F5, null);
            LayoutProbe.Settle();
            Assert.True(Named<TextBlock>(window, "BindingError").IsEffectivelyVisible);
            Assert.False(Named<Button>(window, "ReadyButton").IsEnabled);

            // Esc 取消录入，原设置不变。
            var count = backend.SavedRoomPreferences.Count;
            model.RecordCommand.Execute("stop");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            LayoutProbe.Settle();
            Assert.Equal(count, backend.SavedRoomPreferences.Count);
            Assert.False(model.IsRecording);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PickedButtonIsSavedAndCancelKeepsTheOldBinding()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "rooms");
        try
        {
            var model = (RoomsPageModel)Page<RoomsPage>(window).DataContext!;
            model.ApplyPicked("start", null);
            LayoutProbe.Settle();
            Assert.Empty(backend.SavedRoomPreferences);
            Assert.Contains("已取消位置记录", model.FeedbackText);
            var binding = new InputBindingPreference(LocalBindingKind.Button, X: 10, Y: 20, ClientWidth: 1280, ClientHeight: 720, Dpi: 96);
            model.ApplyPicked("start", binding);
            LayoutProbe.Settle();
            Assert.Equal(binding, backend.SavedRoomPreferences[^1].Value.Bindings["ensemble"].Start);
            Assert.Equal("已记录 (10, 20) · 1280×720 · DPI 96", model.StartButtonText);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PickingActivatesTheGameFirstAndStillPicksWhenNotSwitched()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "rooms");
        try
        {
            var model = (RoomsPageModel)Page<RoomsPage>(window).DataContext!;
            var binding = new InputBindingPreference(LocalBindingKind.Button, X: 10, Y: 20, ClientWidth: 1280, ClientHeight: 720, Dpi: 96);
            string? before = null;
            // 样本宿主没有真实的游戏窗口：激活没切到前台，拾取层照常显示，反馈注明原因。
            await model.PickAsync("start", _ =>
            {
                before = backend.UiIntents[^1].Intent;
                return Task.FromResult<InputBindingPreference?>(null);
            });
            Assert.Equal("game.activate", before);
            Assert.Equal("已取消位置记录，原按钮设置已保留。未能切到游戏窗口（模拟宿主没有真实的游戏窗口）。", model.FeedbackText);

            // 切到前台后记录位置并保存。
            backend.Activation = new GameWindowActivation(true, true, true, true, null, string.Empty);
            await model.PickAsync("start", _ => Task.FromResult<InputBindingPreference?>(binding));
            LayoutProbe.Settle();
            Assert.Equal(binding, backend.SavedRoomPreferences[^1].Value.Bindings["ensemble"].Start);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ExitNoticeShowsWhyTheHostRemovedThisMemberUntilTheNextRoom()
    {
        var backend = new SampleBackend { InRoom = false, ExitReason = "kicked" };
        var window = LayoutProbe.Open(backend, 1280, 860, "rooms");
        try
        {
            var notice = Named<Border>(window, "ExitNotice");
            Assert.True(notice.IsEffectivelyVisible);
            Assert.Equal(["你已被房主移出房间。"], notice.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text));

            backend.ExitReason = "roomClosed";
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.Equal(["房主已关闭房间，你已退出。"], notice.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text));

            // 重新进入房间后提示消失。
            backend.InRoom = true;
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.False(notice.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void JoinFormSendsProviderNicknameAndInvitationOnlyWhenPressed()
    {
        var backend = new SampleBackend { InRoom = false };
        var window = LayoutProbe.Open(backend, 1280, 860, "rooms");
        try
        {
            var model = (RoomsPageModel)Page<RoomsPage>(window).DataContext!;
            Assert.Equal("琉特", Named<TextBox>(window, "NicknameBox").Text);
            Assert.False(Named<Card>(window, "RosterPanel").IsVisible);
            Named<TextBox>(window, "InvitationBox").Text = "MabiSync/1|wss://example.invalid/ws|AB12CD";
            LayoutProbe.Settle();
            Assert.DoesNotContain(backend.UiIntents, item => item.Intent.StartsWith("rooms.join"));
            Click(window, Named<Button>(window, "JoinButton"));
            Assert.Equal(("rooms.join", (string?)"community\n琉特\nMabiSync/1|wss://example.invalid/ws|AB12CD\nws://192.168.1.10:38472/ws"),
                backend.UiIntents.Last(item => item.Intent == "rooms.join"));

            // 样本加入后进入房间；回到房间外继续检查联机方式。
            backend.InRoom = false;
            backend.RaiseChanged();
            LayoutProbe.Settle();
            // 联机方式改动即保存；Cloudflare 时按钮为“加入邀请”。
            model.ProviderCommand.Execute("cloudflare");
            LayoutProbe.Settle();
            Assert.Equal("cloudflare", backend.SavedRoomPreferences[^1].Value.PublicProvider);
            Assert.Equal("加入邀请", model.JoinLabel);
            Click(window, Named<Button>(window, "CreateButton"));
            Assert.StartsWith("cloudflare\n琉特\n", backend.UiIntents.Last(item => item.Intent == "rooms.create").Argument);
            Assert.EndsWith("\nensemble", backend.UiIntents.Last(item => item.Intent == "rooms.create").Argument);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RosterCellsSubmitOneFieldAndRejectOutOfRangeNumbers()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "rooms");
        try
        {
            var model = (RoomsPageModel)Page<RoomsPage>(window).DataContext!;
            var row = model.Rows.Single(item => item.Id == "m2");
            var before = backend.UiIntents.Count;
            model.EditCell(row, "group", "25");
            LayoutProbe.Settle();
            Assert.Equal(before, backend.UiIntents.Count);
            Assert.True(model.FeedbackError);
            model.EditCell(model.Rows.Single(item => item.Id == "m2"), "group", "2");
            Assert.Equal(("rooms.roster", "m2\ngroup\n2"), backend.UiIntents[^1]);
            model.Rows.Single(item => item.Id == "m4").RoleIndex = 1;
            Assert.Equal(("rooms.roster", "m4\nrole\njoiner"), backend.UiIntents[^1]);
            model.Rows.Single(item => item.Id == "m1").Instrument = model.Rows[0].Instruments[0];
            Assert.Equal(("rooms.roster", "m1\ninstrument\n"), backend.UiIntents[^1]);
            Assert.Equal("合奏成员 · 5 人 · 本轮 4 人", Named<Card>(window, "RosterPanel").Title);
        }
        finally { window.Close(); }

        // 成员不能编辑，表格只读。
        var member = new SampleBackend { IsRoomOwner = false };
        var other = LayoutProbe.Open(member, 1280, 860, "rooms");
        try
        {
            var model = (RoomsPageModel)Page<RoomsPage>(other).DataContext!;
            var count = member.UiIntents.Count;
            model.EditCell(model.Rows[0], "group", "3");
            model.Rows[0].Selected = false;
            Assert.Equal(count, member.UiIntents.Count);
            Assert.All(model.Rows, item => Assert.True(item.ReadOnly));
            Assert.False(Named<Button>(other, "StartButton").IsVisible);
        }
        finally { other.Close(); }
    }

    [AvaloniaFact]
    public void DraggingARowIntoAnotherGroupSubmitsGroupAndOrder()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "rooms");
        try
        {
            var list = Named<MabiTool.Desktop.Controls.Tables.DataTable>(window, "EnsembleRows").List;
            Control Row(int index) => list.ContainerFromIndex(index)!;
            Row(4).BringIntoView();
            LayoutProbe.Render(window);
            Point NameCell(int index)
            {
                // 文字列由行自己画出：取昵称所在列在这一行里的中心。
                var row = Row(index).GetVisualDescendants().OfType<MabiTool.Desktop.Controls.Tables.TableRowPresenter>().First();
                var nickname = ((RosterRow)Row(index).DataContext!).Nickname;
                if (row.TextCellBounds(text => text == nickname) is { } bounds) return row.TranslatePoint(bounds.Center, window)!.Value;
                // 昵称在模板列里时仍是真实的文字块。
                return Center(Row(index).GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == nickname), window);
            }
            // 拖到表外松开：回到原位，不发送。
            var count = backend.UiIntents.Count;
            var start = NameCell(1);
            ShellSidebarTests.DragTo(window, start, new Point(start.X, start.Y - 600));
            Assert.Equal(count, backend.UiIntents.Count);

            // 第 1 组第 2 位（芙蕾雅）拖过第 2 组首位（北境旅人）：进入第 2 组第 2 位。
            start = NameCell(1);
            var target = NameCell(3);
            ShellSidebarTests.DragTo(window, start, new Point(start.X, target.Y + 4));
            Assert.Equal(("rooms.move", "m2\n2\n2"), backend.UiIntents[^1]);
            var model = (RoomsPageModel)Page<RoomsPage>(window).DataContext!;
            Assert.Equal(["m1", "m3", "m4", "m2", "m5"], model.Rows.Select(row => row.Id));
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CopyInviteFallsBackToSelectableTextWhenClipboardFails()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "rooms");
        try
        {
            Click(window, Named<Button>(window, "CopyInviteButton"));
            Assert.Equal(["MabiSync/1|wss://47.111.3.134/|K7M2QX"], backend.CopiedInvitations);
            Assert.True(Named<TextBlock>(window, "CopiedText").IsEffectivelyVisible);
        }
        finally { window.Close(); }

        var failing = new SampleBackend { ClipboardFails = true };
        window = LayoutProbe.Open(failing, 1280, 860, "rooms");
        try
        {
            Click(window, Named<Button>(window, "CopyInviteButton"));
            Assert.Empty(failing.CopiedInvitations);
            Assert.Equal("MabiSync/1|wss://47.111.3.134/|K7M2QX", Named<TextBox>(window, "FallbackInvitationBox").Text);
            Assert.True(Named<TextBox>(window, "FallbackInvitationBox").IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void InterruptedRoundOffersContinueAndStopAll()
    {
        var backend = new SampleBackend { RoundPhase = "interrupted" };
        var window = LayoutProbe.Open(backend, 1280, 860, "rooms");
        try
        {
            Assert.Equal("演奏中断", Named<TextBlock>(window, "PhaseText").Text);
            Assert.True(Named<Button>(window, "StopAllButton").IsVisible);
            Click(window, Named<Button>(window, "ContinueButton"));
            Assert.Equal(("rooms.continue", (string?)null), backend.UiIntents[^1]);
            Assert.Equal("本轮进行中", Named<TextBlock>(window, "PhaseText").Text);
            // 进行中锁定模式、时间微调与成员编辑。
            Assert.False(Named<Slider>(window, "OffsetSlider").IsEnabled);
            Assert.False(((RoomsPageModel)Page<RoomsPage>(window).DataContext!).CanEditRoster);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void NicknameFollowsTheCaptureIdentityReadOnlyUntilCustomIsChosen()
    {
        RoomsPreference.Restore(null);
        var backend = new SampleBackend { InRoom = false, CaptureCharacter = "北境旅人" };
        var window = LayoutProbe.Open(backend, 1280, 860, "rooms");
        try
        {
            var model = (RoomsPageModel)Page<RoomsPage>(window).DataContext!;
            var box = Named<TextBox>(window, "NicknameBox");
            var note = Named<TextBlock>(window, "NicknameNote");
            // 缺省为抓包昵称：取全局抓包识别的本机身份，只读，随识别更新。
            Assert.Equal(RoomsPreference.CaptureNickname, model.NicknameSource.Value);
            Assert.Equal("北境旅人", box.Text);
            Assert.True(box.IsReadOnly);
            backend.CaptureCharacter = "芙蕾雅";
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.Equal("芙蕾雅", box.Text);

            // 未识别：输入框为空、下方写明原因，加入不发送。
            backend.CaptureCharacter = null;
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.Equal(string.Empty, box.Text ?? string.Empty);
            Assert.True(note.IsEffectivelyVisible);
            Assert.StartsWith("未识别：", note.Text);
            Named<TextBox>(window, "InvitationBox").Text = "AB12CD";
            Click(window, Named<Button>(window, "JoinButton"));
            Assert.DoesNotContain(backend.UiIntents, item => item.Intent == "rooms.join");

            // 切到自定义昵称：可编辑，显示保存的值；改动离开输入框时保存到本机设置。
            model.NicknameSource = model.NicknameSources.Single(option => option.Value == RoomsPreference.CustomNickname);
            LayoutProbe.Settle();
            Assert.False(box.IsReadOnly);
            Assert.False(note.IsVisible);
            Assert.Equal("琉特", box.Text);
            box.Text = "自定昵称";
            model.CommitConnection();
            LayoutProbe.Settle();
            Assert.Equal("自定昵称", backend.SavedRoomPreferences[^1].Value.Nickname);
            Click(window, Named<Button>(window, "JoinButton"));
            Assert.StartsWith("community\n自定昵称\n", backend.UiIntents.Last(item => item.Intent == "rooms.join").Argument);
        }
        finally
        {
            RoomsPreference.Restore(null);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void GameWindowRowShowsTheBoundWindowRatherThanThePlayer()
    {
        Assert.Equal("Mabinogi Mobile（MabinogiMobile.exe · PID 12840）", LiveBackend.WindowLabel("Mabinogi Mobile", "MabinogiMobile", 12840));
        Assert.Equal("MabinogiMobile.exe（PID 7）", LiveBackend.WindowLabel(" ", "MabinogiMobile.exe", 7));
        var backend = new SampleBackend();
        using var model = new RoomsPageModel(backend);
        Assert.Equal("游戏窗口：Mabinogi Mobile（MabinogiMobile.exe · PID 12840）", model.GameWindowText);
        Assert.DoesNotContain("琉特", model.GameWindowText);
    }

    [AvaloniaFact]
    public void RosterRowsAndOpenDropDownsSurviveBackendRefreshes()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "rooms");
        try
        {
            var model = (RoomsPageModel)Page<RoomsPage>(window).DataContext!;
            var rows = model.Rows;
            var combo = Named<DataTable>(window, "EnsembleRows").GetVisualDescendants().OfType<TableChoice>()
                .First(box => box.DataContext is RosterRow && box.ItemCount > 2);
            combo.IsDropDownOpen = true;
            LayoutProbe.Settle();
            // 后端例行刷新（每秒一次，内容不变）：行与列表都是原来的，行内下拉保持打开。
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.Same(rows, model.Rows);
            Assert.True(combo.IsDropDownOpen);
            // 某位成员就绪状态变化：同一成员仍是同一个行对象，内容就地更新。
            var first = rows[0];
            backend.RoundPhase = "committed";
            LayoutProbe.Settle();
            Assert.Same(first, model.Rows.First(row => row.Id == first.Id));
            combo.IsDropDownOpen = false;
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RosterChoiceOpensTheSharedPopupAndSubmitsTheChosenValue()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "rooms");
        try
        {
            var table = Named<DataTable>(window, "EnsembleRows");
            var model = (RoomsPageModel)Page<RoomsPage>(window).DataContext!;
            TableChoice Choice(string id) => table.GetVisualDescendants().OfType<TableChoice>()
                .First(box => box.DataContext is RosterRow row && row.Id == id && box.ItemCount > 2);
            var row = model.Rows.Single(item => item.Id == "m2");
            var choice = Choice("m2");
            // 行内只画当前值；读屏是组合框，名称为列名，值为当前乐器。
            Assert.Equal(row.Instrument, choice.Text);
            var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(choice);
            Assert.Equal(("乐器", row.Instrument), (peer.GetName(), ((Avalonia.Automation.Provider.IValueProvider)peer).Value));

            // 点按打开整表共用的浮层；Esc 关闭，不发送。
            var before = backend.UiIntents.Count;
            Click(window, choice);
            Assert.True(choice.IsDropDownOpen);
            Assert.Same(choice, table.ChoicePopup.Anchor);
            PressKey(window, Key.Escape, PhysicalKey.Escape);
            Assert.False(choice.IsDropDownOpen);
            Assert.Equal(before, backend.UiIntents.Count);

            // 键盘：空格打开，↓ 移到下一项，回车选中，发出与原下拉相同的意图。
            var index = row.Instruments.ToList().IndexOf(row.Instrument);
            var expected = row.Instruments[index + 1];
            choice.Focus();
            PressKey(window, Key.Space, PhysicalKey.Space);
            Assert.True(choice.IsDropDownOpen);
            PressKey(window, Key.Down, PhysicalKey.ArrowDown);
            PressKey(window, Key.Enter, PhysicalKey.Enter);
            Assert.False(choice.IsDropDownOpen);
            Assert.Equal(("rooms.roster", $"m2\ninstrument\n{expected}"), backend.UiIntents[^1]);
            LayoutProbe.Settle();
            Assert.Same(choice, Choice("m2"));
            Assert.Equal(expected, choice.Text);
        }
        finally { window.Close(); }

        // 成员不能编辑：选择框停用，点按不打开。
        var member = new SampleBackend { IsRoomOwner = false };
        var other = LayoutProbe.Open(member, 1280, 860, "rooms");
        try
        {
            var locked = Named<DataTable>(other, "EnsembleRows").GetVisualDescendants().OfType<TableChoice>().First();
            Assert.False(locked.IsEffectivelyEnabled);
            Click(other, locked);
            Assert.False(locked.IsDropDownOpen);
        }
        finally { other.Close(); }
    }

    [AvaloniaFact]
    public void RosterInputsEditInTheSharedEditorAndSubmitOnEnterOrLeave()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "rooms");
        try
        {
            var table = Named<DataTable>(window, "EnsembleRows");
            var model = (RoomsPageModel)Page<RoomsPage>(window).DataContext!;
            TableInput Cell(string id, string field) => table.GetVisualDescendants().OfType<TableInput>()
                .Single(cell => cell.DataContext is RosterRow row && row.Id == id && (string?)cell.Tag == field);
            var editor = table.InputEditor.Box;
            var group = Cell("m2", "group");
            var order = Cell("m2", "order");
            Assert.Equal(("1", "2"), (group.Text, order.Text));
            var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(group);
            Assert.Equal(("组", "1"), (peer.GetName(), ((Avalonia.Automation.Provider.IValueProvider)peer).Value));
            var before = backend.UiIntents.Count;

            // 点按：整表共用的输入框放进这一格；Esc 放弃输入，文字回到原样，焦点留在格上，不发送。
            Click(window, group);
            Assert.True(group.IsEditing);
            Assert.Same(group, editor.Parent);
            editor.Text = "3";
            PressKey(window, Key.Escape, PhysicalKey.Escape);
            Assert.False(group.IsEditing);
            Assert.True(group.IsFocused);
            Assert.Equal("1", group.Text);
            Assert.Equal(before, backend.UiIntents.Count);

            // 越界：回车进入编辑并全选，输入 25 回车不发送并提示范围，格与输入框的文字还原为当前组号。
            PressKey(window, Key.Enter, PhysicalKey.Enter);
            Assert.True(group.IsEditing);
            Assert.Equal(editor.Text!.Length, editor.SelectionEnd - editor.SelectionStart);
            window.KeyTextInput("25");
            PressKey(window, Key.Enter, PhysicalKey.Enter);
            Assert.Equal(before, backend.UiIntents.Count);
            Assert.True(model.FeedbackError);
            Assert.Equal(("1", "1"), (group.Text, editor.Text));

            // Tab 与原来每格一个 TextBox 时相同：焦点离开表格（表体列表的 Tab 只停一次），离开的一格按离开提交（文字已还原为当前值，不发送）。
            PressKey(window, Key.Tab, PhysicalKey.Tab);
            Assert.False(group.IsEditing);
            Assert.Null(editor.Parent);
            Assert.False(table.IsKeyboardFocusWithin);
            Assert.Equal(before, backend.UiIntents.Count);

            // 点按顺序格，输入框移过去；后端例行刷新不打断输入；Shift+Tab 离开即提交。
            Click(window, order);
            Assert.Same(order, editor.Parent);
            editor.Text = "1";
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.Same(order, editor.Parent);
            Assert.Equal("1", editor.Text);
            PressKey(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
            Assert.Equal(("rooms.roster", "m2\norder\n1"), backend.UiIntents[^1]);
            Assert.False(order.IsEditing);

            // 合法值回车提交一次，输入框留在格里继续编辑。
            Click(window, group);
            var row = (RosterRow)group.DataContext!;
            editor.Text = "2";
            PressKey(window, Key.Enter, PhysicalKey.Enter);
            Assert.Equal(("rooms.roster", $"{row.Id}\ngroup\n2"), backend.UiIntents[^1]);
            Assert.True(editor.IsFocused);
        }
        finally { window.Close(); }

        // 成员不能编辑：格只读，点按不进入编辑。
        var member = new SampleBackend { IsRoomOwner = false };
        var other = LayoutProbe.Open(member, 1280, 860, "rooms");
        try
        {
            var table = Named<DataTable>(other, "EnsembleRows");
            var locked = table.GetVisualDescendants().OfType<TableInput>().First();
            Assert.True(locked.IsReadOnly);
            Click(other, locked);
            Assert.False(locked.IsEditing);
        }
        finally { other.Close(); }
    }

    [AvaloniaFact]
    public void ScoreBarFollowsTheObservedStartAndTheStopBarNeverReturnsAfterTheScoreEnds()
    {
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        long At(double seconds) => (long)(seconds * Stopwatch.Frequency);
        var stopping = new RoomCountdown(true, "距离统一停止", 0.5, "92.0 秒", false);
        var starting = stopping with { Stopping = false, Label = "距离同步开始" };
        Assert.Null(RoomFlow.SingleBar(stopping, new RoomScoreProgress(0.5, "92.0 / 184.5 秒")));
        Assert.Same(stopping, RoomFlow.SingleBar(stopping, null));
        Assert.Same(starting, RoomFlow.SingleBar(starting, new RoomScoreProgress(0.1, "")));

        // 总指挥一轮：房间服务绑定的起奏时刻为服务器 5040（同步开始后约 1.6 秒），统一停止在一轮结束 189540（尾音保留 0）。
        // 收到响应时服务器此刻 1000、个人偏移 −40，距停止 188500 ms。
        var sample = ParseSample();
        var round = sample.Round! with { Phase = "performing", StopRule = "first-cycle", ParticipantIds = ["h", "a"], Receipts = [],
            Interruption = null, Cycle = new RoomCycle(184.5, 189540) };
        var room = sample with { Round = round };
        var timing = new RoomTiming(1000, 20, 2, -40, 3400, 188500, 0);
        var preferences = RoomLocalSettings.Effective(RoomPreferences.Default);
        RoomStatus Status(RoomState state, double seconds) => RoomFlow.Describe(Data(state) with { Timing = timing }, "ensemble", preferences, now: At(seconds));

        // 起奏后 6 秒：只有乐谱进度条；总指挥的识别结果消失（已停止演奏）后仍按绑定的乐谱显示。
        var playing = Status(room, 10);
        Assert.Null(playing.Countdown);
        Assert.Equal("6.0 / 184.5 秒 · 第 1 轮 · 估计进度", playing.ScoreProgress!.Text);
        var silent = room with { Members = [.. room.Members.Select(member => member with { Music = null })] };
        Assert.Equal(1d, Status(silent, 200).ScoreProgress!.Fraction);

        // 停止时刻过后：任何情况下都不再出现停止倒计时，阶段文字给出收尾状态。
        Assert.Null(RoomClock.Countdown(round, timing, At(190)));
        Assert.Null(Status(silent with { Round = round with { Cycle = null } }, 190).Countdown);
        Assert.Equal(("正在执行停止 · 还有 2 人未停", RoomTone.Info), (Status(silent, 190).Phase, Status(silent, 190).PhaseTone));
        RoomState With(params RoomReceipt[] receipts) => silent with { Round = round with { Receipts = receipts } };
        var executed = new RoomReceipt("t1", "h", "executed", false, null);
        Assert.Equal(("已执行停止 · 还有 1 人未停", RoomTone.Success), (Status(With(executed), 190).Phase, Status(With(executed), 190).PhaseTone));
        Assert.Equal("已执行停止", Status(With(executed, executed with { MemberId = "a" }), 190).Phase);
        var failed = Status(With(executed with { Status = "failed", Detail = "input-failed" }), 190);
        Assert.StartsWith("停止失败：", failed.Phase);
        Assert.Equal(RoomTone.Error, failed.PhaseTone);
        Assert.Null(Status(With(executed), 190).Countdown);

        // 信息浮窗与合奏页取同一份收尾状态。
        Assert.Equal(Status(With(executed), 190).Phase, RoomFlow.WrapUp(round with { Receipts = [executed] }, "h", timing, At(190))!.Value.Text);
    }

    [AvaloniaFact]
    public void InformationWidgetSwapsStartForStopWhileTheRoundRuns()
    {
        var backend = new SampleBackend { RoundPhase = "committed" };
        using var model = new OverlayWidgetModel("rooms.readiness", backend);
        // 房主：倒计时内“停止”取消本轮，开始后“停止”通知全员停止；没有进行中的本轮时回到“开始”。
        Assert.False(model.ShowRoomStart);
        Assert.True(model.ShowRoomStop);
        Assert.Equal("rooms.cancel", model.RoomStopIntent);
        backend.RoundPhase = "performing";
        Assert.Equal("rooms.stop-all", model.RoomStopIntent);
        model.IntentCommand.Execute(model.RoomStopIntent);
        Assert.Equal(("rooms.stop-all", (string?)null), backend.UiIntents[^1]);
        backend.RoundPhase = null;
        Assert.True(model.ShowRoomStart);
        Assert.False(model.ShowRoomStop);

        // 成员：本轮进行中“就绪”的位置变为本机停止。
        var member = new SampleBackend { IsRoomOwner = false, RoundPhase = "performing" };
        using var joined = new OverlayWidgetModel("rooms.readiness", member);
        Assert.True(joined.ShowRoomLocalStop);
        Assert.False(joined.ShowRoomReady);
    }

    [AvaloniaFact]
    public void InformationWidgetShowsMembersAndForwardsReady()
    {
        var backend = new SampleBackend { IsRoomOwner = false };
        using var model = new OverlayWidgetModel("rooms.readiness", backend);
        Assert.Equal(5, model.RoomGroups.Sum(group => group.Members.Count));
        Assert.Equal(("第 1 组", "2", "芙蕾雅"), (model.RoomGroups[0].Title, model.RoomGroups[0].Members[1].Order, model.RoomGroups[0].Members[1].Nickname));
        Assert.True(model.ShowRoomReady && !model.ShowRoomStart);
        model.IntentCommand.Execute(model.RoomReadyIntent);
        Assert.Equal(("rooms.ready", "off"), backend.UiIntents[^1]);
        Assert.False(model.RoomStatus.ReadyOn);
        Assert.Equal("rooms.ready:on", model.RoomReadyIntent);

        backend.InRoom = false;
        backend.RaiseChanged();
        Assert.True(model.NotInRoom);
        Assert.Empty(model.RoomGroups);
    }

    [AvaloniaFact]
    public void InformationWidgetCountsDownWhileShown()
    {
        var backend = new SampleBackend { RoundPhase = "committed" };
        using var model = new OverlayWidgetModel("rooms.readiness", backend);
        Assert.True(model.HasRoomCountdown);
        Assert.Equal("距离同步开始", model.RoomCountdownLabel);
        Assert.True(model.RoomCountdownStarting);
        Assert.EndsWith(" 秒", model.RoomCountdownValue);
        Assert.True(model.HasRoomScoreProgress);
        // 定时器只在元件显示且本轮进行中时运行。
        Assert.False(model.ClockRunning);
        model.SetShown(true);
        Assert.True(model.ClockRunning);
        backend.RoundPhase = null;
        Assert.False(model.ClockRunning);
        Assert.False(model.HasRoomCountdown);
        backend.RoundPhase = "performing";
        Assert.True(model.ClockRunning);
        model.Dispose();
        Assert.False(model.ClockRunning);
    }
}

using System.Globalization;
using System.Text.Json;

namespace MabiTool.Desktop.Pages.Rooms;

/// <summary>反馈横幅的语气，对应原工具的操作完成、需要处理、请留意、操作反馈。</summary>
public enum RoomTone { Info, Success, Warning, Error }

/// <summary>最近一次操作的结果，显示在状态卡顶部的反馈横幅。</summary>
public sealed record RoomFeedback(string Text, RoomTone Tone);

/// <summary>总指挥（或任一成员）被识别到的乐谱：标题原文、完整乐谱与当前音轨的秒数。</summary>
public sealed record RoomScore(string Title, double? WholeSeconds, double? SelectedSeconds);

/// <summary>
/// 房间协议里的一位成员。<see cref="Instrument"/> 为空表示未指定。
/// <see cref="ClockRoundTripMs"/> 取成员就绪时上报的能力，只用于诊断。
/// </summary>
public sealed record RoomMember(string Id, string Nickname, bool IsHost, bool Connected, bool Ready, bool Selected,
    int Group, int Order, bool Initiator, string Instrument, double? ClockRoundTripMs = null, RoomScore? Music = null);

/// <summary>房间识别到的游戏角色，供房主为成员匹配；区服按房间协议的数字原样转成文字。</summary>
public sealed record RoomCharacter(string CharacterId, string RealmId, string Nickname);

/// <summary>本轮的中断或取消原因，字段与房间协议的 <c>interruption</c> 相同。</summary>
public sealed record RoomInterruption(string? MemberId, string? EvidenceSource, string? Source, string? Detail);

/// <summary>一条执行回执。</summary>
public sealed record RoomReceipt(string ActionId, string MemberId, string Status, bool Simulated, string? Detail);

/// <summary>
/// 本轮（房间协议的 <c>performance</c>）。阶段为 cancelled 或 stopped 时本轮已经结束，其余阶段视为进行中。
/// <see cref="HasStop"/> 表示已安排统一停止，<see cref="StopCommitted"/> 表示各成员已确认停止动作。
/// <see cref="Cycle"/> 是房间服务为总指挥一轮绑定的乐谱（<c>performance.cycleScore</c>），绑定前为空。
/// </summary>
public sealed record RoomRound(string Id, string Mode, string Phase, string? LeaderId, IReadOnlyList<string> ParticipantIds,
    int CountdownMs, int RequestedCountdownMs, string StopRule, int TailHoldMs, bool HasStop, bool StopCommitted,
    string? CycleStopStatus, string? StartActionId, RoomInterruption? Interruption, IReadOnlyList<RoomReceipt> Receipts,
    string? StopActionId = null, RoomCycle? Cycle = null)
{
    public bool Active => Phase is not ("cancelled" or "stopped");
}

/// <summary>
/// 总指挥本轮的乐谱时间轴：整谱秒数与一轮结束的服务器时刻。结束时刻 = 抓包观测的起奏时刻 + 整谱时长；
/// 起奏时刻取合奏新建帧的收包时刻加帧内的起奏偏移（游戏在新建合奏后约 1.5 秒才起奏）。
/// </summary>
public sealed record RoomCycle(double DurationSeconds, double EstimatedEndServerMs)
{
    public double StartServerMs => EstimatedEndServerMs - DurationSeconds * 1000;
}

/// <summary>房间协议原样的房间内容，解析成页面需要的字段。</summary>
public sealed record RoomState(string Code, string? HostId, string? LeaderId, string Mode, IReadOnlyList<RoomMember> Members,
    IReadOnlyList<RoomCharacter> Characters, RoomRound? Round);

/// <summary>解析 <c>RoomLifecycleSnapshot.Room</c>（房间协议原样的 JSON）。缺少的字段按房间协议的默认值处理。</summary>
public static class RoomJson
{
    public static RoomState? Parse(JsonElement? room, string? fallbackCode = null)
    {
        if (room is not { ValueKind: JsonValueKind.Object } raw ||
            !raw.TryGetProperty("members", out var members) || members.ValueKind != JsonValueKind.Array) return null;
        var hostId = Text(raw, "hostId");
        var list = members.EnumerateArray().Where(member => member.ValueKind == JsonValueKind.Object).Select(member =>
        {
            var id = Text(member, "id") ?? string.Empty;
            RoomScore? music = null;
            if (member.TryGetProperty("music", out var score) && score.ValueKind == JsonValueKind.Object)
                music = new(Text(score, "title") ?? string.Empty, Number(score, "wholeScoreDurationSeconds"),
                    Number(score, "selectedDurationSeconds"));
            double? clock = member.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object
                ? Number(capabilities, "clockRoundTripMs") : null;
            return new RoomMember(id, Text(member, "nickname") ?? string.Empty,
                Text(member, "role") == "host" || (hostId is not null && id == hostId), Flag(member, "connected"), Flag(member, "ready"),
                !member.TryGetProperty("selected", out var selected) || selected.ValueKind != JsonValueKind.False,
                Integer(member, "ensembleGroup") ?? 1, Integer(member, "ensembleOrder") ?? 1, Text(member, "ensembleRole") == "initiator",
                Text(member, "instrument")?.Trim() ?? string.Empty, clock, music);
        }).ToList();
        var characters = raw.TryGetProperty("observedCharacters", out var observed) && observed.ValueKind == JsonValueKind.Array
            ? observed.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object && Text(item, "characterId") is not null)
                .Select(item => new RoomCharacter(Text(item, "characterId")!, Raw(item, "realmId"), Text(item, "nickname") ?? string.Empty))
                .ToList()
            : [];
        return new(Text(raw, "code") ?? fallbackCode ?? string.Empty, hostId, Text(raw, "leaderId"), Text(raw, "mode") ?? "ensemble",
            list, characters, ParseRound(raw.TryGetProperty("performance", out var performance) ? performance : default));
    }

    private static RoomRound? ParseRound(JsonElement round)
    {
        if (round.ValueKind != JsonValueKind.Object || Text(round, "id") is not { } id) return null;
        var ids = round.TryGetProperty("participantIds", out var participants) && participants.ValueKind == JsonValueKind.Array
            ? participants.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList()
            : [];
        RoomInterruption? interruption = round.TryGetProperty("interruption", out var detail) && detail.ValueKind == JsonValueKind.Object
            ? new(Text(detail, "memberId"), Text(detail, "evidenceSource"), Text(detail, "source"), Text(detail, "detail"))
            : null;
        var receipts = round.TryGetProperty("receipts", out var reports) && reports.ValueKind == JsonValueKind.Array
            ? reports.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(item => new RoomReceipt(
                Text(item, "actionId") ?? string.Empty, Text(item, "memberId") ?? string.Empty, Text(item, "status") ?? string.Empty,
                Flag(item, "simulated"), Text(item, "detail"))).ToList()
            : [];
        var hasStop = round.TryGetProperty("stopAction", out var stop) && stop.ValueKind == JsonValueKind.Object;
        var start = round.TryGetProperty("startAction", out var action) && action.ValueKind == JsonValueKind.Object ? Text(action, "id") : null;
        var cycle = round.TryGetProperty("cycleScore", out var bound) && bound.ValueKind == JsonValueKind.Object &&
                    Number(bound, "durationSeconds") is { } seconds && double.IsFinite(seconds) && seconds > 0 &&
                    Number(bound, "estimatedEndServerMs") is { } end && double.IsFinite(end)
            ? new RoomCycle(seconds, end) : null;
        return new(id, Text(round, "mode") ?? "ensemble", Text(round, "phase") ?? "preparing", Text(round, "leaderId"), ids,
            Integer(round, "countdownMs") ?? 0, Integer(round, "requestedCountdownMs") ?? 0, Text(round, "stopRule") ?? "manual",
            Integer(round, "tailHoldMs") ?? 0, hasStop, !round.TryGetProperty("stopCommitted", out var committed) || committed.ValueKind != JsonValueKind.False,
            Text(round, "cycleStopStatus"), start, interruption, receipts, hasStop ? Text(stop, "id") : null, cycle);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Raw(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        }
        : string.Empty;

    internal static string Seconds(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
}

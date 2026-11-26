using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.Rooms.Client;
using MabiTool.Modules.Rooms.Domain;

namespace MabiTool.Host.Rooms;

public sealed partial class RoomSessionManager
{
    public static string RoundETag(RoomRoundSnapshot value)
    {
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { value.ServerEpoch, value.RoomCode, value.MemberId }, ContractJson.Options))))[..32];
        return $"\"room-round-{value.BackendEpoch}-{identity}-{value.Revision}\"";
    }

    public RoomRoundSnapshot GetRound(string owner)
    {
        if (!entries.TryGetValue(owner, out var entry))
            return new(backendEpoch, null, null, null, new(0), null, "disconnected", null, null, [], null, 0,
                null, null, false, null, null, null, null, [], null);
        lock (entry.RoundGate)
        {
            var timing = Timing(entry);
            var identity = entry.Client.Session;
            var room = entry.Client.Room;
            var round = Field(room, "performance");
            var members = Field(room, "members") is { ValueKind: JsonValueKind.Array } rows
                ? rows.EnumerateArray().Select(row => new { id = Text(row, "id"), ready = Field(row, "ready"),
                    connected = Field(row, "connected"), capabilities = Field(row, "capabilities"),
                    role = Text(row, "ensembleRole"), group = Field(row, "ensembleGroup"), order = Field(row, "ensembleOrder"),
                instrument = Text(row, "instrument") }).ToArray() : null;
            // 校时采样和高频角色投影不改变控制修订；轮次及准备条件改变才使旧写入失效。
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { identity?.ServerEpoch, identity?.Code, identity?.MemberId, connected = entry.Client.IsConnected,
                leader = Text(room, "leaderId"), mode = Text(room, "mode"), members, round,
                scoreDraft = entry.Client.ScoreDraft, scoreMappings = entry.Client.ScoreMappings }, ContractJson.Options))));
            if (fingerprint != entry.RoundFingerprint)
            { entry.RoundFingerprint = fingerprint; entry.RoundRevision = checked(entry.RoundRevision + 1); }
            var receipts = Field(round, "receipts") is { ValueKind: JsonValueKind.Array } reports
                ? reports.EnumerateArray().Select(row => new RoomRoundReceipt(Text(row, "actionId")!, Text(row, "memberId")!,
                    Text(row, "status")!, Number(row, "actualServerMs"), Boolean(row, "simulated"),
                    Boolean(row, "gameResponseVerified"), Text(row, "detail"))).ToArray() : [];
            OpenEnum<MusicStopRule>? stopRule = Text(round, "stopRule") is { } rule ? new OpenEnum<MusicStopRule>(rule) : null;
            return new(backendEpoch, identity?.ServerEpoch, identity?.Code, identity?.MemberId, new(entry.RoundRevision),
                Text(round, "id"), !entry.Client.IsConnected ? "disconnected" : Text(round, "phase") ?? "idle",
                Text(round, "mode") ?? Text(room, "mode"), round is null ? Text(room, "leaderId") : Text(round, "leaderId"),
                Field(round, "participantIds") is { ValueKind: JsonValueKind.Array } ids ? ids.EnumerateArray().Select(x => x.GetString()!).ToArray() : [],
                stopRule, (int)(Number(round, "tailHoldMs") ?? 0), ReadAction(Field(round, "startAction")),
                ReadAction(Field(round, "stopAction")), Boolean(round, "stopCommitted"), Text(round, "cycleStopStatus"),
                Text(round, "stopTimingSource"), Text(round, "stopTimingUnavailable"), Field(round, "interruption"), receipts, entry.LocalInput?.Snapshot,
                Text(round, "scoreDraftId"), Field(round, "scoreDraftRevision") is { } draftRevision ? draftRevision.Deserialize<UInt64String>(ContractJson.Options) : null,
                timing?.Invoke(ReadAction(Field(round, "startAction")), ReadAction(Field(round, "stopAction"))));
        }
    }

    /// <summary>校时估计及本轮起止剩余时间；与控制修订无关，不参与指纹。</summary>
    private static Func<RoomRoundAction?, RoomRoundAction?, RoomRoundTiming>? Timing(Entry entry)
    {
        if (!entry.Client.IsConnected || entry.Clock.Estimate is not { } estimate) return null;
        var local = entry.Monotonic.NowMs; var server = local + estimate.OffsetMs; var offset = entry.Scheduler.TimingOffsetMs;
        double? Remaining(RoomRoundAction? action) => action is null ? null
            : entry.Scheduler.LocalDeadline(action.Id) is { } deadline ? deadline - local : action.TargetServerMs + offset - server;
        return (start, stop) => new(server, estimate.RoundTripMs, estimate.UncertaintyMs, estimate.AgeMs, offset, Remaining(start), Remaining(stop));
    }

    public void ValidateRoundCommand(string owner, string? roundId, RoomRoundRequest? prepare, RoomRoundControlRequest? control, string etag)
    {
        var entry = Find(owner); var current = GetRound(owner);
        if (RoundETag(current) != etag) throw new OperationException("revision.stale", "轮次或准备条件已变化，请读取最新状态");
        if (!authorized(owner)) throw new OperationException("permission.denied", "房间控制权限已撤销");
        if (!entry.Client.IsConnected || entry.Client.Session is null) throw new OperationException("room.commandUnconfirmed", "房间当前未连接");
        if (prepare is not null)
        {
            if (!entry.Client.IsRoomOwner) throw new OperationException("permission.denied", "只有房主可以准备轮次");
            if (current.Id is not null && current.Phase is not ("cancelled" or "stopped"))
                throw new OperationException("room.activityActive", "当前轮次尚未结束");
            if (prepare.StopRule?.KnownValue == MusicStopRule.FirstCycle && !entry.Client.SupportsFirstCycleStop ||
                prepare.TailHoldMs is > 0 && !entry.Client.SupportsFirstCycleTail)
                throw new OperationException("room.commandUnsupported", "房间服务尚未支持所选停止规则");
            if (prepare.Mode != "emote" && entry.Client.ScoreDraft is { } draft)
            {
                var mappings = entry.Client.ScoreMappings;
                if (prepare.ParticipantIds.Any(id => !draft.Assignments.Any(item => item.MemberId == id) ||
                    !mappings.Any(item => item.MemberId == id && item.DraftRevision == draft.Revision && item.State == "mapped")))
                    throw new OperationException("room.scoreMappingRequired", "参与成员尚未完成本轮乐谱映射");
            }
        }
        else
        {
            if (roundId != current.Id || current.Phase is "stopped" or "cancelled")
                throw new OperationException("room.planStale", "该轮次已结束或被新轮次取代");
            if (control?.Action.KnownValue == RoomRoundControl.Interrupt)
            {
                if (current.MemberId != current.LeaderId) throw new OperationException("permission.denied", "只有总指挥可以报告本轮手工中断");
            }
            else if (!entry.Client.IsRoomOwner) throw new OperationException("permission.denied", "该轮次控制需要房主权限");
            if (control?.Action.KnownValue == RoomRoundControl.Continue && current.Phase != "interrupted")
                throw new OperationException("room.interruptionUnavailable", "本轮没有待处理的中断");
        }
    }

    public async Task<RoomRoundCommandResult> ExecuteRoundCommandAsync(string owner, string? roundId,
        RoomRoundRequest? prepare, RoomRoundControlRequest? control, string etag,
        Func<CancellationToken, Task<bool>> commit, CancellationToken ct)
    {
        var entry = Find(owner);
        await entry.Gate.WaitAsync(ct);
        try
        {
            ValidateRoundCommand(owner, roundId, prepare, control, etag);
            ct.ThrowIfCancellationRequested();
            if (prepare is not null)
            {
                await entry.Client.RefreshClockAsync(ct);
                if (prepare.Mode != "emote" && prepare.ParticipantIds.Contains(entry.Client.Session!.MemberId)) EnsureScoreReady(entry, owner);
                ValidateRoundCommand(owner, roundId, prepare, control, etag);
            }
            ct.ThrowIfCancellationRequested();
            if (!await commit(ct)) throw new OperationCanceledException(ct);
            ct.ThrowIfCancellationRequested();
            if (prepare is not null)
            {
                var configured = await ConfigureHostInputAsync(entry, owner, prepare, ct);
                try
                {
                    await entry.Client.PrepareRoundAsync(prepare, ct);
                    if (configured)
                        lock (entry.ReadinessGate) entry.HostConfiguredRound = Text(Field(entry.Client.Room, "performance"), "id");
                }
                catch when (configured)
                {
                    lock (entry.ReadinessGate)
                    {
                        entry.HostConfigured = false; entry.HostConfiguredRound = null;
                        entry.Client.CancelLocalPreparations(); entry.Scheduler.CancelAll(); ReleaseReadiness(entry);
                    }
                    throw;
                }
            }
            else await entry.Client.ControlRoundAsync(roundId!, control!, ct);
            Notify(owner);
            return new(prepare is null ? control!.Action.RawValue : "prepare", true, GetRound(owner));
        }
        catch (RoomInputException error) { throw InputFailure(error); }
        catch (RoomRequestException error)
        {
            var code = error.Code switch
            {
                "FORBIDDEN" => "permission.denied", "STALE_SESSION" => "room.planStale",
                "SESSION_ACTIVE" or "USE_STOP_ALL" => "room.activityActive", "NO_INTERRUPTION" => "room.interruptionUnavailable",
                "MEMBER_NOT_READY" => "room.memberNotReady", "STOP_RULE_UNSUPPORTED" => "room.commandUnsupported", _ => "room.commandRejected"
            };
            throw new OperationException(code, "房间服务拒绝了本次轮次命令",
                new Dictionary<string, JsonElement> { ["protocolCode"] = JsonSerializer.SerializeToElement(error.Code) });
        }
        finally { entry.Gate.Release(); }
    }

    /// <summary>
    /// 原工具开轮前由房主发送 configure-input 上报本机能力；服务端免除房主就绪，但仍核对其开始/停止能力。
    /// 房主已按本轮模式就绪时沿用现有准备；否则按保存偏好的个人偏移和当前分工准备本机输入，开轮即视为确认聊天草稿。
    /// </summary>
    private async Task<bool> ConfigureHostInputAsync(Entry entry, string owner, RoomRoundRequest prepare, CancellationToken ct)
    {
        var self = entry.Client.Session!.MemberId;
        if (!prepare.ParticipantIds.Contains(self)) return false;
        var member = Field(entry.Client.Room, "members") is { ValueKind: JsonValueKind.Array } rows
            ? rows.EnumerateArray().FirstOrDefault(row => Text(row, "id") == self) : default;
        var readyMode = Text(Field(member, "capabilities"), "readyMode");
        if (Boolean(member, "ready") && (readyMode is null || readyMode == prepare.Mode) &&
            entry.LocalInput?.Snapshot is { State: "prepared" or "armed", Mode: var mode } && mode == prepare.Mode) return false;
        var role = prepare.Mode == "ensemble" && Text(member, "ensembleRole") == "joiner" ? "joiner" : "initiator";
        var offset = preferences(owner).TimingOffsetMs;
        var confirmed = false;
        lock (entry.ReadinessGate) { entry.ReadinessActivity ??= activities.EnterActivity(owner, "room.ready"); entry.ReadinessPending = true; }
        try
        {
            var capabilities = entry.LocalInput?.Arm(new(true, Simulated(owner), false, false, offset, role, prepare.Mode, ChatDraftConfirmed: true), prepare.Mode)
                ?? throw new OperationException("input.actionMappingUnavailable", "宿主尚未装配本地轮次输入");
            await PrepareGameMusicAsync(entry, prepare.Mode, ct);
            await entry.Client.ConfigureInputAsync(capabilities.Simulation, capabilities.CanStart, capabilities.CanStop, offset, role, prepare.Mode,
                capabilities.RoundTripMs, capabilities.ActivationMs);
            confirmed = true;
            return true;
        }
        finally
        {
            lock (entry.ReadinessGate)
            {
                entry.ReadinessPending = false;
                if (confirmed) { entry.HostConfigured = true; entry.HostConfiguredRound = null; }
                else { entry.Client.CancelLocalPreparations(); entry.Scheduler.CancelAll(); ReleaseReadiness(entry); }
            }
        }
    }

    /// <summary>
    /// 合奏或独立演奏就绪时（连接器可用、本机不是模拟）：预热游戏乐谱列表，并按分配给本人的乐器（房间成员乐器，其次本机乐谱映射的乐器）
    /// 在持有本轮活动租约的情况下换乐器；乐器不在背包、名称有歧义或游戏拒绝换乐器时本机无法就绪。连接器不可用时按原流程就绪。
    /// </summary>
    private async Task PrepareGameMusicAsync(Entry entry, string mode, CancellationToken ct)
    {
        if (inputPorts?.Music is not { Available: true } music || mode == "emote" || entry.LocalInput?.ActivityProof is not { } proof) return;
        var self = entry.Client.Session?.MemberId;
        var member = self is null || Field(entry.Client.Room, "members") is not { ValueKind: JsonValueKind.Array } rows ? default
            : rows.EnumerateArray().FirstOrDefault(row => Text(row, "id") == self);
        string? title; string? catalogInstrument;
        lock (entry.ScoreGate)
        {
            title = entry.LocalScore is { State: "mapped", Score: { } mapped } ? mapped.Entry.RawTitle : null;
            catalogInstrument = entry.LocalScore?.InstrumentId;
        }
        var wanted = Text(member, "instrument") is { Length: > 0 } text ? text
            : catalogInstrument is null ? null : MabiTool.Modules.MusicLibrary.Domain.MusicLibraryEditor.InstrumentCatalog
                .FirstOrDefault(x => x.Id == catalogInstrument)?.DisplayNames.GetValueOrDefault("zh-TW");
        _ = await music.WarmAsync(title, ct);
        var result = await music.PrepareInstrumentAsync(proof, wanted, ct);
        if (result.ErrorCode is null) return;
        throw new OperationException(result.ErrorCode, result.ErrorCode switch
        {
            "room.instrumentNotOwned" => "分配的乐器不在背包里",
            "room.instrumentAmbiguous" => "分配的乐器名称有歧义，背包里有多件同名候选",
            _ => "游戏没有换上分配的乐器"
        }, result.GameCode is null ? null : new Dictionary<string, JsonElement> { ["gameCode"] = JsonSerializer.SerializeToElement(result.GameCode) });
    }

    private static JsonElement? Field(JsonElement? value, string name) => value is { ValueKind: JsonValueKind.Object } row &&
        row.TryGetProperty(name, out var field) && field.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? field : null;
    private static string? Text(JsonElement? value, string name) => Field(value, name) is { ValueKind: JsonValueKind.String } field ? field.GetString() : null;
    private static double? Number(JsonElement? value, string name) => Field(value, name) is { ValueKind: JsonValueKind.Number } field ? field.GetDouble() : null;
    private static bool Boolean(JsonElement? value, string name) => Field(value, name) is { ValueKind: JsonValueKind.True };
    private static RoomRoundAction? ReadAction(JsonElement? value) => value is null ? null :
        new(Text(value, "id")!, Text(value, "sessionId")!, Text(value, "kind")!, Number(value, "targetServerMs")!.Value, Text(value, "serverEpoch")!);
}

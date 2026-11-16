using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Rooms;

public sealed partial class RoomSessionManager
{
    public static string ScoreDraftETag(RoomScoreDraftSnapshot value)
    {
        var identity = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { value.ServerEpoch, value.RoomCode, value.MemberId }, ContractJson.Options)))[..32];
        return $"\"room-score-{value.BackendEpoch}-{identity}-{value.Revision}\"";
    }

    public RoomScoreDraftSnapshot GetScoreDraft(string owner, bool refresh = true)
    {
        if (!entries.TryGetValue(owner, out var entry)) return new(backendEpoch, null, null, null, false, new(0), null, [], null);
        if (refresh) RefreshLocal(entry, owner);
        lock (entry.ScoreGate)
        {
            var session = entry.Client.Session;
            var draft = entry.Client.ScoreDraft; var members = entry.Client.ScoreMappings;
            var supported = entry.Client.IsConnected && entry.Client.SupportsScoreDraft;
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { session?.ServerEpoch, session?.Code, session?.MemberId, supported, draft, members,
                serverRevision = entry.Client.ScoreDraftRevision, local = entry.LocalScore }, ContractJson.Options)));
            if (fingerprint != entry.ScoreFingerprint)
            { entry.ScoreFingerprint = fingerprint; entry.ScoreRevision = checked(entry.ScoreRevision + 1); }
            return new(backendEpoch, session?.ServerEpoch, session?.Code, session?.MemberId, supported, new(entry.ScoreRevision), draft, members, entry.LocalScore);
        }
    }

    public void ValidateScoreDraftCommand(string owner, string command, string etag)
    {
        var entry = Find(owner); var current = GetScoreDraft(owner, false);
        if (!authorized(owner)) throw new OperationException("permission.denied", "房间控制权限已撤销");
        if (ScoreDraftETag(current) != etag) throw new OperationException("revision.stale", "草稿或成员映射已变化，请读取最新状态");
        if (!entry.Client.IsConnected || entry.Client.Session is null) throw new OperationException("room.commandUnconfirmed", "房间当前未连接");
        if (!entry.Client.SupportsScoreDraft) throw new OperationException("room.scoreDraftUnsupported", "房间服务尚未支持轮次乐谱草稿");
        if (command != "map" && !entry.Client.IsRoomOwner) throw new OperationException("permission.denied", "只有房主可以修改共享草稿");
        if (command != "publish" && current.Draft is null) throw new OperationException("room.scoreDraftStale", "当前没有可操作的乐谱草稿");
        if (command == "map" && !current.Draft!.Assignments.Any(item => item.MemberId == current.MemberId))
            throw new OperationException("room.scoreUnassigned", "本轮尚未为本人分配乐谱");
        var round = GetRound(owner);
        if (round.Mode != "emote" && round.Id is not null && round.Phase is not ("cancelled" or "stopped" or "preparing" or "committed"))
            throw new OperationException("room.activityActive", "请先结束当前轮次再修改乐谱草稿");
    }

    public async Task<RoomScoreDraftCommandResult> ExecuteScoreDraftCommandAsync(string owner, string command,
        RoomScoreDraftRequest? publish, RoomScoreMappingRequest? map, string etag, Func<CancellationToken, Task<bool>> commit, CancellationToken ct)
    {
        var entry = Find(owner); await entry.Gate.WaitAsync(ct);
        try
        {
            RefreshScoreSource(entry, owner);
            ValidateScoreDraftCommand(owner, command, etag);
            MusicResolvedScore? resolved = null; RoomLocalScoreMapping? local = null;
            var draft = entry.Client.ScoreDraft;
            var expectedRevision = entry.Client.ScoreDraftRevision;
            if (publish is not null)
            {
                resolved = ReadScore(owner, publish.Score);
                var members = Field(entry.Client.Room, "members")!.Value.EnumerateArray().Select(row => Text(row, "id")).ToHashSet();
                if (publish.Assignments.Any(item => !members.Contains(item.MemberId))) throw new OperationException("room.scoreUnassigned", "草稿包含不在房间中的成员");
            }
            else if (map is not null) local = ResolveMapping(owner, entry.Client.Session!.MemberId, draft!, map);
            ct.ThrowIfCancellationRequested();
            ValidateScoreDraftCommand(owner, command, etag);
            if (!await commit(ct)) throw new OperationCanceledException(ct);
            ct.ThrowIfCancellationRequested();
            if (publish is not null) await entry.Client.PublishScoreDraftAsync(publish.Score, resolved!, publish.Assignments, expectedRevision, ct);
            else if (map is not null)
            {
                CancelScorePreparation(entry);
                lock (entry.ScoreGate)
                {
                    entry.MappingPending = true; entry.LocalScore = local;
                    entry.LocalScoreDraftId = draft!.Id; entry.LocalScoreDraftRevision = draft.Revision;
                }
                try { await entry.Client.MapScoreDraftAsync(draft!, ProtocolMapping(entry, draft!, local!), ct); }
                catch
                {
                    lock (entry.ScoreGate) entry.LocalScore = local! with { State = "pending", Reason = "room.commandUnconfirmed" };
                    throw;
                }
                finally { lock (entry.ScoreGate) entry.MappingPending = false; }
            }
            else await entry.Client.ClearScoreDraftAsync(draft!, ct);
            ReconcileScore(entry, owner); Notify(owner);
            return new(command, true, GetScoreDraft(owner, false));
        }
        catch (MabiTool.Modules.Rooms.Client.RoomRequestException error)
        {
            var code = error.Code switch
            {
                "FORBIDDEN" => "permission.denied", "STALE_SCORE_DRAFT" => "room.scoreDraftStale",
                "SCORE_DRAFT_UNSUPPORTED" => "room.scoreDraftUnsupported", "SCORE_DRAFT_UNASSIGNED" => "room.scoreUnassigned",
                "SESSION_ACTIVE" => "room.activityActive", _ => "room.commandRejected"
            };
            throw new OperationException(code, "房间服务拒绝了本次乐谱草稿命令",
                new Dictionary<string, JsonElement> { ["protocolCode"] = JsonSerializer.SerializeToElement(error.Code) });
        }
        finally { entry.Gate.Release(); Notify(owner); }
    }

    private MusicResolvedScore ReadScore(string owner, MusicScoreSelection selection) =>
        (scoreReader ?? throw new OperationException("storage.unavailable", "乐谱草稿需要持久化音乐库")).Read(owner, selection);

    private RoomLocalScoreMapping ResolveMapping(string owner, string memberId, RoomScoreDraft draft, RoomScoreMappingRequest request)
    {
        MusicResolvedScore? score = null; string? reason;
        try
        {
            if (request.Score is null) reason = "room.scoreMappingRequired";
            else
            {
                score = ReadScore(owner, request.Score);
                var assignment = draft.Assignments.Single(item => item.MemberId == memberId);
                reason = score.UnavailableReason ?? draft.Score.UnavailableReason ??
                    (score.ContentSha256 is null || draft.Score.ContentSha256 is null ? "room.scoreContentUnknown" :
                    !string.Equals(score.ContentSha256, draft.Score.ContentSha256, StringComparison.OrdinalIgnoreCase) ? "room.scoreContentMismatch" :
                    request.Score.PartId != assignment.PartId ? "room.scorePartMismatch" :
                    assignment.InstrumentId is not null && request.InstrumentId != assignment.InstrumentId ? "room.scoreInstrumentMismatch" :
                    assignment.PartId is not null && score.Timeline.SelectedPartCycleMs is null ? "room.scorePartUnknown" :
                    score.Timeline.Quality is ObservationQuality.Conflict or ObservationQuality.Stale or ObservationQuality.Unknown ? "room.scoreContentUnknown" : null);
            }
        }
        catch (OperationException error)
        { reason = error.Code == "revision.stale" ? "room.scoreSourceChanged" : error.Code; }
        return new(request.Score, request.InstrumentId, score, reason is null ? "mapped" : "pending", reason);
    }

    private static RoomScoreMemberMapping ProtocolMapping(Entry entry, RoomScoreDraft draft, RoomLocalScoreMapping local) =>
        new(entry.Client.Session!.MemberId, local.State, local.Reason, draft.Revision, local.Score?.ContentSha256, local.Selection?.PartId, local.InstrumentId);

    private void EnsureScoreReady(Entry entry, string owner)
    {
        ReconcileScore(entry, owner); RefreshScoreSource(entry, owner);
        if (entry.Client.ScoreDraft is not { } draft) return;
        RoomLocalScoreMapping? local; lock (entry.ScoreGate) local = entry.LocalScore;
        var memberId = entry.Client.Session!.MemberId;
        if (!entry.Client.SupportsScoreDraft) throw new OperationException("room.scoreDraftUnsupported", "房间服务尚未支持草稿");
        if (!draft.Assignments.Any(item => item.MemberId == memberId)) throw new OperationException("room.scoreUnassigned", "本轮尚未为本人分配乐谱");
        if (local?.State != "mapped" || !entry.Client.ScoreMappings.Any(item => item.MemberId == memberId && item.State == "mapped" && item.DraftRevision == draft.Revision))
            throw new OperationException(local?.Reason ?? "room.scoreMappingRequired", "本人乐谱映射尚未完成");
    }

    private bool RefreshScoreSource(Entry entry, string owner)
    {
        RoomLocalScoreMapping? local;
        lock (entry.ScoreGate) local = entry.LocalScore;
        if (local is not { State: "mapped", Selection: not null, Score: not null }) return false;
        string? reason;
        try
        {
            var current = ReadScore(owner, local.Selection);
            reason = current.UnavailableReason ?? (current.Fingerprint != local.Score.Fingerprint ? "room.scoreSourceChanged" : null);
        }
        catch (OperationException error) { reason = error.Code == "revision.stale" ? "room.scoreSourceChanged" : error.Code; }
        if (reason is null) return false;
        lock (entry.ScoreGate)
        {
            if (entry.LocalScore != local) return false;
            entry.LocalScore = local with { State = "pending", Reason = reason };
        }
        CancelScorePreparation(entry); QueueScoreWithdrawal(entry, owner); return true;
    }

    private void ReconcileScore(Entry entry, string owner)
    {
        var session = entry.Client.Session; var draft = entry.Client.ScoreDraft;
        var identity = session is null ? null : session.ServerEpoch + ":" + session.Code + ":" + session.MemberId;
        var changed = false;
        lock (entry.ScoreGate)
        {
            if (identity != entry.ScoreRoomIdentity)
            {
                changed = entry.LocalScore is not null; entry.LocalScore = null; entry.ScoreRoomIdentity = identity;
                entry.LocalScoreDraftId = null; entry.LocalScoreDraftRevision = null;
            }
            if (entry.LocalScore is { State: "mapped" } local)
            {
                var reason = !entry.Client.IsConnected ? "room.scoreMappingRequired" :
                    draft?.Id != entry.LocalScoreDraftId || draft?.Revision != entry.LocalScoreDraftRevision ? "room.scoreDraftStale" :
                    !entry.MappingPending && !entry.Client.ScoreMappings.Any(item => item.MemberId == session!.MemberId && item.State == "mapped" && item.DraftRevision == draft!.Revision)
                        ? entry.Client.ScoreMappings.FirstOrDefault(item => item.MemberId == session!.MemberId)?.Reason ?? "room.scoreMappingRequired" : null;
                if (reason is not null) { entry.LocalScore = local with { State = "pending", Reason = reason }; changed = true; }
            }
        }
        if (changed) { CancelScorePreparation(entry); QueueScoreWithdrawal(entry, owner); }
    }

    private static void CancelScorePreparation(Entry entry)
    {
        if (entry.LocalInput?.Snapshot.Mode == "emote") return;
        entry.Client.CancelLocalPreparations(); entry.Scheduler.CancelAll();
        lock (entry.ReadinessGate) ReleaseReadiness(entry);
    }

    private void QueueScoreWithdrawal(Entry entry, string owner)
    {
        lock (entry.ScoreGate)
        {
            if (entry.ScoreWithdrawalPending || !entry.Client.IsConnected || entry.Client.ScoreDraft is not { } draft ||
                !draft.Assignments.Any(item => item.MemberId == entry.Client.Session?.MemberId)) return;
            entry.ScoreWithdrawalPending = true;
        }
        _ = WithdrawAsync();
        async Task WithdrawAsync()
        {
            try
            {
                await entry.Gate.WaitAsync();
                try
                {
                    RoomLocalScoreMapping? local; lock (entry.ScoreGate) local = entry.LocalScore;
                    if (entry.Client.IsConnected && entry.Client.ScoreDraft is { } draft && local?.State != "mapped" &&
                        draft.Assignments.Any(item => item.MemberId == entry.Client.Session?.MemberId))
                        await entry.Client.MapScoreDraftAsync(draft, ProtocolMapping(entry, draft,
                            local ?? new(null, null, null, "pending", "room.scoreMappingRequired")), CancellationToken.None);
                }
                finally { entry.Gate.Release(); }
            }
            catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or OperationCanceledException or ObjectDisposedException) { }
            finally { lock (entry.ScoreGate) entry.ScoreWithdrawalPending = false; Notify(owner); }
        }
    }
}

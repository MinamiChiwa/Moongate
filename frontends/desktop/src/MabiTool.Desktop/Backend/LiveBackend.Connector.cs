using MabiTool.Client;
using MabiTool.Contracts;

namespace MabiTool.Desktop.Backend;

// 游戏连接器：快照随五秒级例行刷新读取（界面有可见处时），写操作的回应直接作为最新快照，后端没有这个接口（旧宿主）时本次连接不再探测。
// 乐谱可演奏性只在连接器已连接时向后端查询：页面登记当前显示的乐谱，队列里的乐谱由适配层自己登记；
// 连接器快照修订、乐谱来源与演奏会话的最近错误都没变时，已查到的结果不重复查询，“未知”的结果每五秒再查一次。
public sealed partial class LiveBackend
{
    private GameConnectorView? connector;
    private string? connectorStamp;
    private bool connectorUnsupported;
    private long lastConnectorRead;

    /// <summary>一次可演奏性查询最多带的乐谱数（后端上限）。</summary>
    private const int SupportBatch = 200;

    private sealed record SupportEntry(ScoreSupportView View, long At, string Key);

    private readonly Dictionary<string, SupportEntry> supportKnown = new(StringComparer.Ordinal);
    private List<string> supportWanted = [];
    private bool supportUnsupported;
    private long supportFailedUntil;
    private int supportVersion;

    /// <summary>可演奏性缓存的版本：内容变化时加一，乐谱库页的数据映射据此重算。</summary>
    private int SupportVersion
    {
        get { lock (gate) return supportVersion; }
    }

    public GameConnectorView GetGameConnector()
    {
        if (!Read().State.Connected) return GameConnectorView.Offline;
        lock (gate) return connector ?? GameConnectorView.NotRead;
    }

    /// <summary>新连接：连接器与可演奏性的缓存作废，下一轮重新读取。</summary>
    private void ResetConnector()
    {
        lock (gate)
        {
            connector = null; connectorStamp = null; connectorUnsupported = false; lastConnectorRead = 0;
            supportKnown.Clear(); supportUnsupported = false; supportFailedUntil = 0; supportVersion++;
            musicSyncAt = 0; musicSyncStamp = null; musicSyncUnsupported = false;
        }
    }

    private async Task RefreshConnectorAsync(MabiToolClient api, CancellationToken token)
    {
        lock (gate)
        {
            if (connectorUnsupported) return;
            if ((due & Due.Connector) == 0 && !(AnySurface && Elapsed(lastConnectorRead, SlowInterval))) return;
            due &= ~Due.Connector;
            lastConnectorRead = Now;
        }
        GameConnectorSnapshot? snapshot = null;
        var unsupported = false;
        try { snapshot = await api.GetGameConnectorAsync(token); }
        catch (MabiToolApiException error) { unsupported = error.Status == 404; }
        ApplyConnector(snapshot, unsupported);
    }

    // 只有胶囊与卡片显示的内容变化时才通知界面；修订变化（可演奏性需要重新核对）不通知，由下一轮查询处理。
    private void ApplyConnector(GameConnectorSnapshot? snapshot, bool unsupported)
    {
        var next = snapshot is not null ? GameConnectorView.From(snapshot) : unsupported ? GameConnectorView.Unsupported : GameConnectorView.NotRead;
        var stamp = snapshot is null ? null : snapshot.BackendEpoch + "|" + snapshot.Revision;
        bool changed, restamped;
        lock (gate)
        {
            changed = !Equals(connector, next);
            restamped = stamp != connectorStamp;
            connector = next;
            connectorStamp = stamp;
            if (unsupported) connectorUnsupported = true;
            // 离开已连接：之前的可演奏性不再适用，全部回到未知。
            if (next.State != ConnectorState.Connected && supportKnown.Count > 0) { supportKnown.Clear(); supportVersion++; changed = true; }
            // 状态变化通常伴随能力表（performance.play 等）的原因变化：下一轮页面刷新一并重读能力表；背包快照不因此重读。
            if (changed) pageStateDue = true;
        }
        if (changed || restamped) Signal();
        if (changed) Notify();
    }

    public async Task<BackendReply> CommandGameConnectorAsync(string action, bool riskAcknowledged, string? gameDirectory,
        CancellationToken cancellationToken)
    {
        var reply = await ReplyAsync(async (api, token) =>
        {
            // 写操作的回应就是写入后的快照：直接采用，胶囊与各处卡片随即更新，不等下一次例行读取。
            var snapshot = await api.CommandGameConnectorAsync(new(action, riskAcknowledged, gameDirectory), IdempotencyKey(), token);
            ApplyConnector(snapshot, false);
            return ConnectorText.Done(action);
        }, cancellationToken);
        // 被拒绝时状态可能已经变了（例如游戏刚启动）：重读一次。
        if (!reply.Ok) MarkDue(Due.Connector, immediate: false);
        return reply;
    }

    public void RequestScoreSupport(IReadOnlyList<string> scoreIds)
    {
        bool fresh;
        lock (gate)
        {
            var ids = scoreIds.Distinct(StringComparer.Ordinal).Take(SupportBatch).ToList();
            fresh = ids.Any(id => !supportKnown.ContainsKey(id));
            supportWanted = [.. ids.Concat(supportWanted).Distinct(StringComparer.Ordinal).Take(SupportBatch)];
        }
        if (fresh) Signal();
    }

    public ScoreSupportView GameScoreSupportOf(string scoreId)
    {
        lock (gate)
            return connector is { State: ConnectorState.Connected } && supportKnown.TryGetValue(scoreId, out var entry)
                ? entry.View : ScoreSupportView.Unknown;
    }

    // 调用方持有 gate。失效键：连接器快照修订、乐谱来源与演奏会话的最近错误（拒绝记入否定缓存后，后端的答案会变）。
    private string SupportKey() =>
        string.Join('|', connectorStamp, MusicSourceStamp(), pages.Session?.LastError);

    // 调用方持有 gate。要查询的乐谱与最早的查询时刻：没有连接或没有要查的返回空；
    // 有待查的乐谱时取查询失败的退避时刻，只剩“未知”的结果等重查时间。
    private (List<string> Ids, long At)? SupportPlan(long now)
    {
        if (supportUnsupported || connector is not { State: ConnectorState.Connected, Read: ConnectorRead.Loaded }) return null;
        var key = SupportKey();
        var wanted = supportWanted.Concat(pages.Session?.Items.Select(entry => entry.Item.ScoreId) ?? []).Distinct(StringComparer.Ordinal);
        List<string> pending = [];
        var retryAt = long.MaxValue;
        foreach (var id in wanted)
        {
            if (!supportKnown.TryGetValue(id, out var entry) || entry.Key != key) pending.Add(id);
            else if (entry.View.State == ScoreSupportState.Unknown)
            {
                var at = entry.At + Span(SlowInterval);
                if (at <= now) pending.Add(id);
                else retryAt = Math.Min(retryAt, at);
            }
        }
        if (pending.Count > 0) return (pending.Take(SupportBatch).ToList(), Math.Max(now, supportFailedUntil));
        return retryAt == long.MaxValue ? null : (new List<string>(), retryAt);
    }

    private async Task RefreshScoreSupportAsync(MabiToolClient api, CancellationToken token)
    {
        List<string> ids;
        string key;
        lock (gate)
        {
            if (SupportPlan(Now) is not { Ids.Count: > 0 } plan || plan.At > Now) return;
            ids = plan.Ids;
            key = SupportKey();
        }
        GameScoreSupportPage? page = null;
        var unsupported = false;
        try { page = await api.QueryGameScoreSupportAsync(new(ids), token); }
        catch (MabiToolApiException error) { unsupported = error.Status == 404; }
        bool changed = false;
        lock (gate)
        {
            if (page is null)
            {
                if (unsupported) supportUnsupported = true;
                else supportFailedUntil = Now + Span(SlowInterval);
                return;
            }
            supportFailedUntil = 0;
            var answers = page.Items.GroupBy(item => item.ScoreId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First());
            // 浏览过的乐谱很多时只保留当前登记的，避免缓存无限增长。
            if (supportKnown.Count > 4 * SupportBatch)
            {
                var keep = supportWanted.Concat(ids).ToHashSet(StringComparer.Ordinal);
                foreach (var stale in supportKnown.Keys.Where(id => !keep.Contains(id)).ToList()) supportKnown.Remove(stale);
            }
            foreach (var id in ids)
            {
                var view = answers.TryGetValue(id, out var item) ? MapSupport(item) : ScoreSupportView.Unknown;
                if (!supportKnown.TryGetValue(id, out var previous) || previous.View != view) changed = true;
                supportKnown[id] = new(view, Now, key);
            }
            if (changed) supportVersion++;
        }
        if (changed) Notify();
    }

    private static ScoreSupportView MapSupport(GameScoreSupport item) => new(item.State.KnownValue switch
    {
        GameScoreSupportState.Supported => ScoreSupportState.Supported,
        GameScoreSupportState.UserComposed => ScoreSupportState.UserComposed,
        _ => ScoreSupportState.Unknown
    }, item.Reason);

    // 调用方持有 gate。可演奏性的下一次到期时刻（NextWork 用）；没有要做的时为空。
    private long? SupportDueAt(long now) => SupportPlan(now) is { } plan ? plan.At : null;
}

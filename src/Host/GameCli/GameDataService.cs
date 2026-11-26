using System.Globalization;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Providers.TwCli;

namespace MabiTool.Host.GameCli;

/// <summary>
/// 读取节奏：<see cref="ReadBudget"/> 读取等待多久仍无结果就返回旧数据（通道被占用）；<see cref="RefreshGap"/> 用户刷新时同一主题两次实际读取的最小间隔；
/// <see cref="EmptyConfirm"/> 空任务列表持续多久（且至少两次读取）才确认任务已清空；<see cref="IntervalScale"/> 缩放各主题的最小读取间隔（测试用）。
/// </summary>
public sealed record GameDataTimings(TimeSpan ReadBudget, TimeSpan RefreshGap, TimeSpan EmptyConfirm, double IntervalScale = 1)
{
    public static GameDataTimings Default { get; } = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20));
}

/// <summary>
/// 游戏数据读取：角色属性、货币、任务、成长指南、环境、附近玩家。读取由查询驱动（没有查询就不调用命令行），按主题最小间隔缓存，
/// 同一主题的并发读取合并为一次，所有调用以数据读取优先级进入串行通道（执行类指令与演奏控制读取先出队）。
/// 命令行不可用或读取失败时按字段回落抓包（昵称、地图名、货币），都没有时来源为无；读取等待超过预算时返回上次缓存并标为旧数据。
/// </summary>
public sealed class GameDataService : IDisposable
{
    public static readonly IReadOnlyDictionary<string, TimeSpan> Intervals = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
    {
        [GameDataTopics.Environment] = TimeSpan.FromSeconds(5),
        [GameDataTopics.Nearby] = TimeSpan.FromSeconds(5),
        [GameDataTopics.Profile] = TimeSpan.FromSeconds(30),
        [GameDataTopics.Currencies] = TimeSpan.FromSeconds(30),
        [GameDataTopics.Missions] = TimeSpan.FromSeconds(30),
        [GameDataTopics.GrowthGuide] = TimeSpan.FromSeconds(60)
    };

    private static readonly TimeSpan Validity = TimeSpan.FromSeconds(30);

    private sealed class TopicState
    {
        public readonly object Sync = new();
        public object? Value;
        public Task? Inflight;
        public long Stamp;
        public bool HasStamp;
        public string? ConnectorKey;
        public string? SourceKey;
    }

    private sealed class MissionHold
    {
        public IReadOnlyList<GameMission>? Items;
        public DateTimeOffset ReadAt;
        public int EmptyReads;
        public long FirstEmpty;
    }

    private readonly GameConnectorService connector;
    private readonly Func<GameCaptureFacts> capture;
    private readonly StateEventHub? hub;
    private readonly string backendEpoch;
    private readonly TimeProvider clock;
    private readonly DiagnosticTrace? trace;
    private readonly GameDataTimings timings;
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<string, TopicState> topics = GameDataTopics.All.ToDictionary(x => x, _ => new TopicState(), StringComparer.Ordinal);
    private readonly MissionHold daily = new(), weekly = new();
    private readonly Dictionary<string, ulong> published = new(StringComparer.Ordinal);

    public GameDataService(GameConnectorService connector, Func<GameCaptureFacts>? capture, StateEventHub? hub, string backendEpoch,
        TimeProvider? time = null, DiagnosticTrace? trace = null, GameDataTimings? timings = null)
    {
        this.connector = connector; this.capture = capture ?? (() => GameCaptureFacts.None); this.hub = hub; this.backendEpoch = backendEpoch;
        clock = time ?? TimeProvider.System; this.trace = trace; this.timings = timings ?? GameDataTimings.Default;
    }

    public void Dispose() { stop.Cancel(); stop.Dispose(); }

    public GameCaptureFacts CaptureFacts() => capture();

    // ───────────────────────── 查询 ─────────────────────────

    public async Task<GameDataSnapshot> QueryAsync(GameDataQuery query, CancellationToken ct)
    {
        if (query?.Topics is not { Count: > 0 and <= 6 } requested || requested.Any(x => x is null || !GameDataTopics.All.Contains(x)))
            throw new OperationException("request.invalid", "数据主题须为 profile、currencies、missions、growth-guide、environment 或 nearby");
        var wanted = requested.ToHashSet(StringComparer.Ordinal);
        var refresh = query.Refresh;
        async Task<T?> Run<T>(string topic, Func<CancellationToken, Task<T>> read, Func<string, T> placeholder) where T : class =>
            wanted.Contains(topic) ? await ObtainAsync(topic, refresh, read, placeholder, ct).ConfigureAwait(false) : null;
        var profile = Run(GameDataTopics.Profile, ReadProfileAsync, reason => new GameProfileData(Held(reason), null));
        var currencies = Run(GameDataTopics.Currencies, ReadCurrenciesAsync, reason => new GameCurrenciesData(Held(reason), null));
        var missions = Run(GameDataTopics.Missions, ReadMissionsAsync, reason => new GameMissionsData(new(Held(reason), null), new(Held(reason), null)));
        var growth = Run(GameDataTopics.GrowthGuide, ReadGrowthAsync, reason => new GameGrowthGuideData(Held(reason), false, null, null));
        var environment = Run(GameDataTopics.Environment, ReadEnvironmentAsync, reason => new GameEnvironmentData(Held(reason), null));
        var nearby = Run(GameDataTopics.Nearby, ReadNearbyAsync, reason => new GameNearbyData(Held(reason), null));
        await Task.WhenAll(profile, currencies, missions, growth, environment, nearby).ConfigureAwait(false);
        return new(backendEpoch, WithCaptureNickname(await profile), await currencies, await missions, await growth, await environment, await nearby);
    }

    /// <summary>命令行没有昵称字段：命令行来源的角色信息在返回时补上抓包当前识别的本机昵称（缓存的读取结果也随之保持最新）。</summary>
    private GameProfileData? WithCaptureNickname(GameProfileData? data)
    {
        if (data is not { Profile: { } profile } || data.Status.Source.KnownValue != GameDataSource.Connector) return data;
        var nickname = capture().Nickname is { Length: > 0 } name ? name : null;
        return profile.Nickname == nickname ? data : data with { Profile = profile with { Nickname = nickname } };
    }

    /// <summary>通道占用、读取还没有结果时：没有任何内容，来源为无，原因为通道占用。</summary>
    private static GameDataStatus Held(string reason) => new(GameDataSource.None, reason, null, false, []);

    private async Task<T> ObtainAsync<T>(string topic, bool refresh, Func<CancellationToken, Task<T>> read, Func<string, T> placeholder, CancellationToken ct) where T : class
    {
        var state = topics[topic];
        Task running;
        lock (state.Sync)
        {
            var key = ConnectorKey();
            var interval = refresh ? timings.RefreshGap : Intervals[topic] * timings.IntervalScale;
            var fresh = state.Value is not null && state.HasStamp && state.ConnectorKey == key && clock.GetElapsedTime(state.Stamp) < interval;
            if (fresh) return (T)state.Value!;
            state.Inflight ??= Task.Run(() => RunAsync(state, topic, read));
            running = state.Inflight;
        }
        try { await running.WaitAsync(timings.ReadBudget, clock, ct).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            // 通道被占用：返回上次缓存并标为旧数据，读取在后台继续。
            lock (state.Sync)
                return state.Value is T cached ? Stale(cached, "gameCli.busy") : placeholder("gameCli.busy");
        }
        lock (state.Sync) return state.Value is T value ? value : placeholder("gameCli.readFailed");
    }

    private async Task RunAsync<T>(TopicState state, string topic, Func<CancellationToken, Task<T>> read) where T : class
    {
        T? next = null;
        var key = ConnectorKey();
        try { next = await read(stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            trace?.Emit(DiagnosticEvents.GameCliData, new DiagnosticFields().Value("topic", topic).Value("source", "error").Value("reason", error.GetType().Name));
        }
        lock (state.Sync)
        {
            if (next is not null)
            {
                var source = SourceOf(next);
                if (source != state.SourceKey)
                {
                    state.SourceKey = source;
                    trace?.Emit(DiagnosticEvents.GameCliData, new DiagnosticFields().Value("topic", topic).Value("source", source.Split('|')[0]).Value("reason", source.Split('|')[1]));
                }
                state.Value = next;
            }
            // 被紧急停止等中断的读取没有取得新内容：不记读取时刻，下一次查询立即重读。
            state.Stamp = clock.GetTimestamp(); state.HasStamp = next is not null && !IsInterrupted(next) && !stop.IsCancellationRequested; state.ConnectorKey = key;
            state.Inflight = null;
        }
    }

    private static bool IsInterrupted(object value) => SourceOf(value).EndsWith("|gameCli.interrupted", StringComparison.Ordinal);

    private string ConnectorKey()
    {
        var current = connector.Current;
        return current.BackendEpoch + "|" + current.Revision;
    }

    private static string SourceOf(object value)
    {
        GameDataStatus? status = value switch
        {
            GameProfileData data => data.Status, GameCurrenciesData data => data.Status, GameGrowthGuideData data => data.Status,
            GameEnvironmentData data => data.Status, GameNearbyData data => data.Status, GameMissionsData data => data.Daily.Status, _ => null
        };
        return (status?.Source.RawValue ?? "none") + "|" + (status?.Reason ?? "-");
    }

    private static T Stale<T>(T value, string reason) where T : class => value switch
    {
        GameProfileData data => (T)(object)(data with { Status = data.Status with { Stale = true, Reason = reason } }),
        GameCurrenciesData data => (T)(object)(data with { Status = data.Status with { Stale = true, Reason = reason } }),
        GameGrowthGuideData data => (T)(object)(data with { Status = data.Status with { Stale = true, Reason = reason } }),
        GameEnvironmentData data => (T)(object)(data with { Status = data.Status with { Stale = true, Reason = reason } }),
        GameNearbyData data => (T)(object)(data with { Status = data.Status with { Stale = true, Reason = reason } }),
        GameMissionsData data => (T)(object)new GameMissionsData(StaleList(data.Daily, reason), StaleList(data.Weekly, reason)),
        _ => value
    };

    private static GameMissionList StaleList(GameMissionList list, string reason) =>
        list.Items is null ? list : list with { Status = list.Status with { Stale = true, Reason = reason } };

    // ───────────────────────── 单次调用 ─────────────────────────

    private enum CallKind { Fresh, Interrupted, Failed }

    private sealed record Call<T>(CallKind Kind, T? Value, string? Reason, TwCliResult? Result) where T : class;

    private async Task<Call<T>> CallAsync<T>(string command, Func<JsonElement?, T?> parse, CancellationToken ct) where T : class
    {
        var result = await connector.Cli.ExecuteAsync(command, null, ct, TwCliPriority.Background).ConfigureAwait(false);
        switch (result.Kind)
        {
            case TwCliResultKind.Ok:
                return parse(result.Body) is { } value ? new(CallKind.Fresh, value, null, result) : new(CallKind.Failed, null, "gameCli.readFailed", result);
            case TwCliResultKind.Interrupted: return new(CallKind.Interrupted, null, "gameCli.interrupted", result);
            case TwCliResultKind.Rejected: return new(CallKind.Failed, null, TwCliCodes.FromGame(result.Error), result);
            case TwCliResultKind.NotConnected:
                return new(CallKind.Failed, null, result.Reason == "option_off" ? "gameCli.expired" : "gameCli.gameNotRunning", result);
            case TwCliResultKind.UnknownCommand or TwCliResultKind.Unavailable: return new(CallKind.Failed, null, "gameCli.commandMissing", result);
            default: return new(CallKind.Failed, null, "gameCli.readFailed", result);
        }
    }

    private GameDataStatus Fresh(IReadOnlyList<GameDataConflict>? conflicts = null) =>
        new(GameDataSource.Connector, null, clock.GetUtcNow(), false, conflicts ?? []);

    private static GameDataStatus Nothing(string reason) => new(GameDataSource.None, reason, null, false, []);

    private static GameDataStatus Captured(string reason, GameCaptureFacts facts) =>
        new(GameDataSource.Capture, reason, facts.ObservedAt, facts.Stale, []);

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);

    // ───────────────────────── 各主题 ─────────────────────────

    private static readonly IReadOnlyList<string> ProfileCommands = ["get_my_info"];
    private static readonly IReadOnlyList<string> CurrencyCommands = ["get_currencies"];
    private static readonly IReadOnlyList<string> MissionCommands = ["get_daily_missions", "get_weekly_missions"];
    private static readonly IReadOnlyList<string> GrowthCommands = ["get_growth_guide"];

    private GameProfileData? Last(string topic) { lock (topics[topic].Sync) return topics[topic].Value as GameProfileData; }

    private async Task<GameProfileData> ReadProfileAsync(CancellationToken ct)
    {
        var facts = capture();
        GameProfileData Fallback(string reason) => facts.Nickname is { Length: > 0 } nickname
            ? new(Captured(reason, facts), new(nickname, null, null, null, null, [], [], null, []))
            : new(Nothing(reason), null);
        var ready = GameCliReadiness.Check(connector, ProfileCommands);
        if (!ready.Ready) return Fallback(ready.Reason);
        var call = await CallAsync("get_my_info", TwCliReaders.Profile, ct).ConfigureAwait(false);
        if (call.Value is { } profile)
        {
            return new(Fresh(), profile with { Nickname = facts.Nickname is { Length: > 0 } nickname ? nickname : null });
        }
        var previous = Last(GameDataTopics.Profile);
        if (call.Kind == CallKind.Interrupted) return previous ?? new(Nothing(call.Reason!), null);
        return previous is { Status.Source.KnownValue: GameDataSource.Connector, Profile: not null }
            ? previous with { Status = previous.Status with { Stale = true, Reason = call.Reason } } : Fallback(call.Reason!);
    }

    private async Task<GameCurrenciesData> ReadCurrenciesAsync(CancellationToken ct)
    {
        var facts = capture();
        GameCurrenciesData Fallback(string reason) => facts.Wallet.Count > 0
            ? new(Captured(reason, facts with { ObservedAt = facts.ObservedAt ?? clock.GetUtcNow() }), facts.Wallet) : new(Nothing(reason), null);
        var ready = GameCliReadiness.Check(connector, CurrencyCommands);
        if (!ready.Ready) return Fallback(ready.Reason);
        var call = await CallAsync("get_currencies", TwCliReaders.Currencies, ct).ConfigureAwait(false);
        if (call.Value is { } items) return new(Fresh(), items);
        GameCurrenciesData? previous; lock (topics[GameDataTopics.Currencies].Sync) previous = topics[GameDataTopics.Currencies].Value as GameCurrenciesData;
        if (call.Kind == CallKind.Interrupted) return previous ?? new(Nothing(call.Reason!), null);
        return previous is { Status.Source.KnownValue: GameDataSource.Connector, Items: not null }
            ? previous with { Status = previous.Status with { Stale = true, Reason = call.Reason } } : Fallback(call.Reason!);
    }

    private async Task<GameEnvironmentData> ReadEnvironmentAsync(CancellationToken ct)
    {
        var facts = capture();
        GameEnvironmentData Fallback(string reason) => facts.MapName is { Length: > 0 } map
            ? new(Captured(reason, facts), new(null, map, null, null, null, null, null)) : new(Nothing(reason), null);
        var ready = GameCliReadiness.Check(connector, GameDataCapabilities.EnvironmentCommands);
        if (!ready.Ready) return Fallback(ready.Reason);
        var call = await CallAsync("get_current_environment", TwCliReaders.Environment, ct).ConfigureAwait(false);
        if (call.Value is { } environment)
        {
            List<GameDataConflict> conflicts = [];
            if (facts.MapName is { Length: > 0 } && environment.Area is { Length: > 0 } && !Same(facts.MapName, environment.Area))
                conflicts.Add(new("area", environment.Area, facts.MapName));
            var data = new GameEnvironmentData(Fresh(conflicts), environment);
            await PublishEnvironmentAsync(data).ConfigureAwait(false);
            return data;
        }
        GameEnvironmentData? previous; lock (topics[GameDataTopics.Environment].Sync) previous = topics[GameDataTopics.Environment].Value as GameEnvironmentData;
        if (call.Kind == CallKind.Interrupted) return previous ?? new(Nothing(call.Reason!), null);
        return previous is { Status.Source.KnownValue: GameDataSource.Connector, Value: not null }
            ? previous with { Status = previous.Status with { Stale = true, Reason = call.Reason } } : Fallback(call.Reason!);
    }

    private async Task<GameNearbyData> ReadNearbyAsync(CancellationToken ct)
    {
        var ready = GameCliReadiness.Check(connector, GameDataCapabilities.NearbyCommands);
        if (!ready.Ready) return new(Nothing(ready.Reason), null);
        var call = await CallAsync("get_near_pcs", TwCliReaders.Nearby, ct).ConfigureAwait(false);
        if (call.Value is { } players)
        {
            var data = new GameNearbyData(Fresh(), players);
            await PublishNearbyAsync(data).ConfigureAwait(false);
            return data;
        }
        GameNearbyData? previous; lock (topics[GameDataTopics.Nearby].Sync) previous = topics[GameDataTopics.Nearby].Value as GameNearbyData;
        if (call.Kind == CallKind.Interrupted) return previous ?? new(Nothing(call.Reason!), null);
        return previous is { Status.Source.KnownValue: GameDataSource.Connector, Players: not null }
            ? previous with { Status = previous.Status with { Stale = true, Reason = call.Reason } } : new(Nothing(call.Reason!), null);
    }

    private async Task<GameGrowthGuideData> ReadGrowthAsync(CancellationToken ct)
    {
        var ready = GameCliReadiness.Check(connector, GrowthCommands);
        if (!ready.Ready) return new(Nothing(ready.Reason), false, null, null);
        var call = await CallAsync("get_growth_guide", TwCliReaders.GrowthGuide, ct).ConfigureAwait(false);
        if (call.Value is { } items) return new(Fresh(), false, null, items);
        // 游戏内容指南未解锁：游戏回答了，原因与解锁条件原文交给界面。
        if (call.Result is { Kind: TwCliResultKind.Rejected, Error: "guide_locked" } locked)
            return new(Fresh() with { Reason = "gameCli.guideLocked" }, true, TwCliReaders.UnlockHint(locked.Root) ?? locked.Message, null);
        GameGrowthGuideData? previous; lock (topics[GameDataTopics.GrowthGuide].Sync) previous = topics[GameDataTopics.GrowthGuide].Value as GameGrowthGuideData;
        if (call.Kind == CallKind.Interrupted) return previous ?? new(Nothing(call.Reason!), false, null, null);
        return previous is { Status.Source.KnownValue: GameDataSource.Connector, Items: not null }
            ? previous with { Status = previous.Status with { Stale = true, Reason = call.Reason } } : new(Nothing(call.Reason!), false, null, null);
    }

    private async Task<GameMissionsData> ReadMissionsAsync(CancellationToken ct)
    {
        var ready = GameCliReadiness.Check(connector, MissionCommands);
        if (!ready.Ready)
        {
            daily.Items = null; weekly.Items = null;
            return new(new(Nothing(ready.Reason), null), new(Nothing(ready.Reason), null));
        }
        var dailyList = await ReadMissionListAsync("get_daily_missions", daily, ct).ConfigureAwait(false);
        var weeklyList = await ReadMissionListAsync("get_weekly_missions", weekly, ct).ConfigureAwait(false);
        return new(dailyList, weeklyList);
    }

    /// <summary>
    /// 一类任务的读取：非空结果直接采用；空列表而上次有任务时保留上次内容并标旧数据，空列表持续至少 <see cref="GameDataTimings.EmptyConfirm"/> 且至少两次读取才确认已清空；
    /// 读取失败或被拒绝时保留上次内容并标旧数据，没有上次内容时来源为无。
    /// </summary>
    private async Task<GameMissionList> ReadMissionListAsync(string command, MissionHold hold, CancellationToken ct)
    {
        var call = await CallAsync(command, TwCliReaders.Missions, ct).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (call.Value is { } items)
        {
            if (items.Count > 0) { hold.Items = items; hold.ReadAt = now; hold.EmptyReads = 0; return new(new(GameDataSource.Connector, null, now, false, []), items); }
            if (hold.Items is { Count: > 0 } kept)
            {
                if (hold.EmptyReads++ == 0) hold.FirstEmpty = clock.GetTimestamp();
                if (hold.EmptyReads < 2 || clock.GetElapsedTime(hold.FirstEmpty) < timings.EmptyConfirm)
                    return new(new(GameDataSource.Connector, "gameCli.emptyHeld", hold.ReadAt, true, []), kept);
            }
            hold.Items = []; hold.ReadAt = now; hold.EmptyReads = 0;
            return new(new(GameDataSource.Connector, null, now, false, []), []);
        }
        if (call.Kind == CallKind.Interrupted && hold.Items is null) return new(Nothing(call.Reason!), null);
        return hold.Items is { } held
            ? new(new(GameDataSource.Connector, call.Reason, hold.ReadAt, true, []), held)
            : new(Nothing(call.Reason!), null);
    }

    // ───────────────────────── 展示快照发布与主题状态 ─────────────────────────

    /// <summary>展示主题状态：环境与附近随连接器状态与目录得出（环境在命令行不可用而抓包有地图时降级）。其余主题没有状态。</summary>
    public (CapabilityStatus Status, string? Reason)? TopicStatus(string topic)
    {
        var commands = topic switch { "environment" => GameDataCapabilities.EnvironmentCommands, "nearby" => GameDataCapabilities.NearbyCommands, _ => null };
        if (commands is null) return null;
        var ready = GameCliReadiness.Check(connector, commands);
        if (ready.Ready) return (CapabilityStatus.Available, null);
        if (ready.Unknown) return (CapabilityStatus.Unknown, ready.Reason);
        if (topic == "environment" && capture().MapName is { Length: > 0 }) return (CapabilityStatus.Degraded, ready.Reason);
        return (CapabilityStatus.Unavailable, ready.Reason);
    }

    private ObservationSource Source() => new(GameConnectorCapabilities.Provider, connector.Current.Cli.Version ?? "0", "TW", null);

    private async Task PublishEnvironmentAsync(GameEnvironmentData data)
    {
        if (hub is null || data.Value is not { } value) return;
        var snapshot = new EnvironmentSnapshot(value.ErinnTime, null, value.Weather, value.Channel, value.Area, ObservationQuality.Confirmed,
            ["gameTimeMinutes"]);
        await PublishAsync("game.environment.changed", "environment-presentation", "environment:tw-cli", snapshot).ConfigureAwait(false);
    }

    private async Task PublishNearbyAsync(GameNearbyData data)
    {
        if (hub is null) return;
        await PublishAsync("game.nearby.changed", "nearby-presentation", "nearby:tw-cli", data).ConfigureAwait(false);
    }

    private async Task PublishAsync<T>(string eventType, string resourceType, string resourceId, T value) where T : notnull
    {
        var now = clock.GetUtcNow();
        ulong revision;
        lock (published) { published.TryGetValue(resourceId, out var last); revision = published[resourceId] = last + 1; }
        var observation = new Observation<T>(value, Source(), now, backendEpoch, clock.GetTimestamp() * (1000d / clock.TimestampFrequency),
            Validity.TotalMilliseconds, new(1), new(revision), ObservationQuality.Confirmed);
        try
        {
            await hub!.PublishVolatileAsync(new(eventType + "-" + revision.ToString(CultureInfo.InvariantCulture), resourceType, resourceId, new(revision), eventType,
                JsonSerializer.SerializeToElement(observation, ContractJson.Options)), now, stop.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationException or OperationCanceledException or ObjectDisposedException) { }
    }
}

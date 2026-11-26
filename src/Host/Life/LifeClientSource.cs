using MabiTool.Contracts;
using MabiTool.Host.GameCli;
using MabiTool.Providers.TwObservation.ItemCatalog;

namespace MabiTool.Host.Life;

/// <summary>
/// 归属分类的数据来源：按连接器记住的台服游戏目录在后台读取一次客户端表（游戏会话更换时重读，读取期间继续用上一份），
/// 读取失败一分钟后才重试。读不到时状态为 Unavailable 并带原因码，分类全部落到“其他”。
/// </summary>
public sealed class LifeClientSource(GameConnectorService connector, TimeProvider clock)
{
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
    private readonly object gate = new();
    private LifeClientTables? tables;
    private (string Directory, string? Session)? source, attempted;
    private Task? loading;
    private string? failure;
    private long failedAt;

    /// <summary>测试与离线工具注入的读取函数；默认读取游戏目录里的客户端资源包。</summary>
    internal Func<string, CancellationToken, LifeClientTables> Reader { get; set; } = (directory, ct) => ClientLifeCatalog.Extract(directory, ct);

    /// <summary>读取完成（成功或失败）时触发；快照据此重新分组。</summary>
    public event Action? Changed;

    public (OpenEnum<LifeClientState> State, string? Reason, LifeClientTables? Tables) Current()
    {
        var directory = connector.Current.Game.Directory;
        var session = connector.Cli.SessionKey;
        lock (gate)
        {
            if (directory is null) return tables is not null ? (LifeClientState.Ready, null, tables) : (LifeClientState.Unavailable, "life.clientDirectoryUnknown", null);
            var stale = source != (directory, session);
            if (stale && loading is null && (attempted != (directory, session) || failure is null || clock.GetElapsedTime(failedAt) >= RetryInterval))
            {
                attempted = (directory, session); failure = null;
                loading = Task.Run(() => Load(directory, session));
            }
            if (tables is not null) return (LifeClientState.Ready, null, tables);
            return loading is not null || failure is null ? (LifeClientState.Loading, null, null) : (LifeClientState.Unavailable, failure, null);
        }
    }

    private void Load(string directory, string? session)
    {
        LifeClientTables? result = null; string? error = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            result = Reader(directory, deadline.Token);
        }
        catch (Exception value) when (value is InvalidDataException or IOException or UnauthorizedAccessException or OperationCanceledException or
            InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        { error = "life.clientUnavailable"; }
        lock (gate)
        {
            loading = null;
            if (result is not null) { tables = result; source = (directory, session); failure = null; }
            else { failure = error; failedAt = clock.GetTimestamp(); }
        }
        Changed?.Invoke();
    }
}

/// <summary>
/// 确定采集物、加工配方与制作配方的归属，不按名称关键字猜测。采集物依次取用户 MabiAgent 写好的采集归类、客户端表，
/// 都读不到或客户端表对应多个非钓鱼技能时返回空归属与原因码（界面归入“其他”）；加工与制作设施取客户端表。
/// </summary>
public static class LifeClassifier
{
    public const string SourceMabiAgent = "mabiagent", SourceClient = "client";

    /// <param name="Source">归属来源：<see cref="SourceMabiAgent"/> 或 <see cref="SourceClient"/>，读不到时为空。</param>
    public sealed record Placement(string? Group, string? Reason, bool Fishing = false, string? Source = null);

    public static Placement Gather(OpenEnum<LifeClientState> state, string? clientReason, LifeClientTables? tables, string name)
    {
        if (MabiAgentGatherSkills.Items.TryGetValue(name, out var known)) return new(known.Name, null, known.IsFishing, SourceMabiAgent);
        if (NotReady(state, clientReason, tables, "gather") is { } reason) return new(null, reason);
        if (!tables!.GatherSkills.TryGetValue(name, out var skills)) return new(null, "life.gatherNotInClient");
        if (skills.Count == 1) return new(skills.First().Name, null, skills.First().IsFishing, SourceClient);
        // 钓鱼点的奖励里常带普通材料（礦石、藥草等）：同时掉落于钓鱼点与恰好一个非钓鱼技能采集点的物品可以在该技能的采集点采到，
        // 命令行采集时也会去最近的采集点（只有“只能钓到”的物品才会开启钓鱼），所以归该技能、不算钓鱼类。
        var others = skills.Where(skill => !skill.IsFishing).ToList();
        return others.Count == 1 ? new(others[0].Name, null, false, SourceClient) : new(null, "life.gatherMultiple");
    }

    public static Placement Alter(OpenEnum<LifeClientState> state, string? clientReason, LifeClientTables? tables, string name) =>
        Facility(state, clientReason, tables, "alter", tables?.AlterFacilities, name);

    public static Placement Craft(OpenEnum<LifeClientState> state, string? clientReason, LifeClientTables? tables, string name) =>
        Facility(state, clientReason, tables, "craft", tables?.CraftFacilities, name);

    private static Placement Facility(OpenEnum<LifeClientState> state, string? clientReason, LifeClientTables? tables, string kind,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? map, string name)
    {
        if (NotReady(state, clientReason, tables, kind) is { } reason) return new(null, reason);
        if (!map!.TryGetValue(name, out var facilities)) return new(null, "life." + kind + "NotInClient");
        return facilities.Count == 1 ? new(facilities.First(), null, false, SourceClient) : new(null, "life." + kind + "Multiple");
    }

    private static string? NotReady(OpenEnum<LifeClientState> state, string? clientReason, LifeClientTables? tables, string kind)
    {
        if (state.KnownValue == LifeClientState.Loading) return "life.clientLoading";
        if (tables is null || state.KnownValue != LifeClientState.Ready) return clientReason ?? "life.clientUnavailable";
        return tables.Unsupported.GetValueOrDefault(kind);
    }
}

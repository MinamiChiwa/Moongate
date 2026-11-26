using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Host.GameCli;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.MusicLibrary.Application;
using MabiTool.Providers.TwCli;

namespace MabiTool.Host.Life;

/// <summary>
/// “生活”模块：经游戏连接器读取可采物、加工配方与队列、制作配方，并按宿主维护的总队列依次执行采集、加工排队、领取与制作。
/// 读取只在界面请求时进行（队列正在执行命令时拒绝，避免与执行类命令争用命令行单通道）；执行持有当前游戏会话的 <c>activity-control</c> 租约
/// （所有者 <c>life</c>，与合奏、演奏、染色、专注钓鱼互斥），逐轮发出、遇任何拒绝、阻塞、不足或中断即暂停整队，不自动重发；
/// 队列在宿主持久保存，宿主重启后暂停。分类先取 MabiAgent 采集归类，再取客户端表，读不到的归入“其他”。
/// </summary>
public sealed partial class LifeService : IDisposable
{
    /// <summary>
    /// 数量上限：加工排队的项数（旧版单项接口的采集轮数同样以它为上限）、制作次数、采集的目标数量。
    /// 命令行的 <c>execute_gathering</c> 每次以 <see cref="GatherPerCall"/> 个为目标采集，实得可能更多（2026-11-27 用户实测确认）。
    /// </summary>
    public const int MaximumRounds = 50, MaximumCraftCount = 999, MaximumGatherCount = 500, GatherPerCall = 10;
    public const string OwnerId = "life";
    private static readonly string[] Sections = ["gather", "alter", "works", "craft", "all"];
    private static readonly string[] Kinds = ["gather", "alter", "collect", "craft"];

    private readonly GameConnectorService connector;
    private readonly GameControlContext controls;
    private readonly SystemInfo info;
    private readonly LifeClientSource client;
    private readonly TimeProvider clock;
    private readonly DiagnosticTrace? trace;
    private readonly TwCliLifeControl life;
    private readonly HostPerformanceLeases leases;
    private readonly LifeQueueStore store;
    private readonly bool ownsStore;
    private readonly object gate = new();
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly HashSet<string> knownFishing = new(StringComparer.Ordinal);
    // 排队时游戏以 queue_full 拒绝的设施；重新读到配方后以配方的原因为准。
    private readonly HashSet<string> fullFacilities = new(StringComparer.Ordinal);
    private ulong revision;
    private LifeSectionStatus gatherStatus = NotRead, alterStatus = NotRead, worksStatus = NotRead, craftStatus = NotRead;
    private string? gatherQuery, alterQuery, craftQuery;
    private IReadOnlyList<TwCliGatherable> gatherItems = [];
    private IReadOnlyList<TwCliAlterable> alterRecipes = [];
    private TwCliAlterQueue? queue;
    private TwCliCraftList? crafts;
    private bool disposed;

    private static LifeSectionStatus NotRead => new(LifeReadState.NotRead, null, null);

    public LifeService(GameConnectorService connector, GameControlContext controls, SystemInfo info, LifeClientSource client, TimeProvider? time = null,
        DiagnosticTrace? trace = null, LifeQueueStore? store = null)
    {
        this.connector = connector; this.controls = controls; this.info = info; this.client = client;
        clock = time ?? TimeProvider.System; this.trace = trace;
        ownsStore = store is null;
        this.store = store ?? new LifeQueueStore(null);
        life = new(connector.Cli, connector.MusicAvailability, proof => HostPerformanceLeases.Validate(controls, proof));
        leases = new(controls);
        Restore(this.store.Read());
        client.Changed += Bump;
    }

    public static string ETag(LifeSnapshot value) => "\"life-" + value.BackendEpoch + "-" + value.Revision + "\"";

    private void Bump() { lock (gate) revision = checked(revision + 1); }

    // ───────────────────────── 快照 ─────────────────────────

    public LifeSnapshot Current()
    {
        var status = connector.Current;
        var (clientState, clientReason, tables) = client.Current();
        var catalog = status.Status.KnownValue == GameConnectorStatus.Connected ? life.CachedCatalog() : null;
        var occupied = Occupancy();
        lock (gate)
        {
            var gather = gatherItems.Select(item =>
            {
                var place = LifeClassifier.Gather(clientState, clientReason, tables, item.Name);
                return new LifeGatherItem(item.Name, item.ToolOk, place.Group, place.Reason, place.Fishing || knownFishing.Contains(item.Name), place.Source);
            }).ToArray();
            var alter = alterRecipes.Select(item =>
            {
                var place = LifeClassifier.Alter(clientState, clientReason, tables, item.Name);
                return new LifeAlterRecipe(item.Name, item.Alterable, Int(item.ProducedPerWork), item.Reason, Materials(item.Missing), place.Group, place.Reason);
            }).ToArray();
            var craft = (crafts?.Items ?? []).Select(item =>
            {
                var place = LifeClassifier.Craft(clientState, clientReason, tables, item.Name);
                return new LifeCraftRecipe(item.Name, item.Variant, item.Craftable, Int(item.ProducedPerCraft), item.SuccessRate, item.CriticalSuccessRate, item.Reason,
                    Materials(item.Ingredients), place.Group, place.Reason);
            }).ToArray();
            var works = queue is null ? null : new LifeAlterQueue(queue.CompletedCount,
                queue.Works.Select(work => new LifeAlterWork(work.Name, work.Facility, work.State, work.Completed, work.RemainingSeconds)).ToArray());
            return new(info.InstanceId, new(revision), status.Status, status.Reason, clientState, clientReason,
                gatherStatus, gatherQuery, gather, alterStatus, alterQuery, alter, worksStatus, works, craftStatus, craftQuery, crafts?.Unlocked, craft,
                Commands(status, catalog), ActionLocked(), occupied, QueueLocked(catalog), Facilities(alter, works));
        }
    }

    // 加工设施：配方归属的设施与加工队列里出现的设施，按先后去重；槽位已满取配方的 queue_full 或排队被 queue_full 拒绝。
    private IReadOnlyList<LifeFacility> Facilities(IReadOnlyList<LifeAlterRecipe> recipes, LifeAlterQueue? works)
    {
        var names = new List<string>();
        foreach (var name in recipes.Select(x => x.Group).Concat(works?.Works.Select(x => x.Facility) ?? []))
            if (name is not null && !names.Contains(name, StringComparer.Ordinal)) names.Add(name);
        return names.Select(name =>
        {
            var byRecipes = recipes.Any(x => x.Group == name && x.Reason == "queue_full");
            var byExecute = fullFacilities.Contains(name);
            return new LifeFacility(name, byRecipes || byExecute, byRecipes ? "recipes" : byExecute ? "execute" : null);
        }).ToArray();
    }

    private static IReadOnlyList<LifeMaterial> Materials(IReadOnlyList<TwCliMaterial> list) => list.Select(item => new LifeMaterial(item.Name, Int(item.Required), Int(item.Owned))).ToArray();

    // 契约里的数量用 32 位整数（64 位整数在 JSON 里必须用字符串）；游戏给出的数量远小于上限，越界时取边界值。
    private static int? Int(long? value) => value is null ? null : (int)Math.Clamp(value.Value, int.MinValue, int.MaxValue);

    private static readonly string[] ExecuteCommands = [TwCliLifeControl.Gather, TwCliLifeControl.Alter, TwCliLifeControl.Collect, TwCliLifeControl.Craft];
    private static readonly string[] AllCommands = [.. TwCliLifeControl.GatherCommands, .. TwCliLifeControl.AlterCommands, .. TwCliLifeControl.CraftCommands];

    private static string CommandOf(string kind) => kind switch
    {
        "gather" => TwCliLifeControl.Gather, "alter" => TwCliLifeControl.Alter, "collect" => TwCliLifeControl.Collect, _ => TwCliLifeControl.Craft
    };

    private static IReadOnlyList<LifeCommandInfo> Commands(GameConnectorSnapshot status, TwCliCatalog? catalog)
    {
        var connected = status.Status.KnownValue == GameConnectorStatus.Connected;
        return AllCommands.Select(command =>
        {
            var moves = ExecuteCommands.Contains(command, StringComparer.Ordinal);
            if (!connected) return new LifeCommandInfo(command, false, status.Reason, null, null, null, moves);
            if (catalog is null || catalog.Loading) return new LifeCommandInfo(command, false, "gameCli.catalogLoading", null, null, null, moves);
            if (!catalog.Has(command)) return new LifeCommandInfo(command, false, "gameCli.commandMissing", null, null, null, moves);
            var cost = moves ? TwCliLifeCost.FromNote(catalog.Commands[command].Note) : null;
            return new LifeCommandInfo(command, true, null, Int(cost?.Amount), cost?.Currency, cost?.Text, moves);
        }).ToArray();
    }

    /// <summary>一次执行的单价（目录备注里的花费）；目录没有写明花费时为空。</summary>
    private static TwCliCost? UnitCost(TwCliCatalog? catalog, string kind) =>
        catalog is not null && !catalog.Loading && catalog.Has(CommandOf(kind)) ? TwCliLifeCost.FromNote(catalog.Commands[CommandOf(kind)].Note) : null;

    private LifeOccupancy? Occupancy()
    {
        var session = controls.Bindings.Current?.Session ?? controls.Bindings.Recent?.Session;
        if (session is null) return null;
        var resource = "activity-control:" + session.Id;
        foreach (var lease in controls.Leases.Snapshot())
            if (lease.Resources.Contains(resource, StringComparer.Ordinal) || lease.Resources.Contains("activity-control", StringComparer.Ordinal))
                return new(lease.OwnerId, lease.OwnerId == OwnerId);
        return null;
    }

    // ───────────────────────── 读取 ─────────────────────────

    /// <summary>读取一块或全部：队列正在执行命令时拒绝；读取失败的块清空数据并写明原因，不保留旧结果。</summary>
    public async Task<LifeSnapshot> RefreshAsync(LifeRefreshRequest request, CancellationToken ct = default)
    {
        if (request is null || !Sections.Contains(request.Section, StringComparer.Ordinal) || request.Query is { Length: > 128 } || request.Query?.Any(char.IsControl) == true)
            throw new OperationException("request.invalid", "读取请求无效");
        lock (gate) if (runner is not null) throw new OperationException("life.actionRunning", "生活队列正在执行，请等待结束或先暂停");
        var query = string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim();
        lock (gate)
            switch (request.Section)
            {
                case "gather": gatherQuery = query; break;
                case "alter": alterQuery = query; break;
                case "craft": craftQuery = query; break;
            }
        await ReadAsync(request.Section == "all" ? ["gather", "alter", "works", "craft"] : [request.Section], ct).ConfigureAwait(false);
        return Current();
    }

    private async Task ReadAsync(IReadOnlyList<string> sections, CancellationToken ct)
    {
        await readGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var availability = life.Availability;
            foreach (var section in sections)
            {
                ct.ThrowIfCancellationRequested();
                if (!availability.Available) { Fail(section, availability.Reason); continue; }
                switch (section)
                {
                    case "gather": Apply(section, await life.ReadGatherablesAsync(gatherQuery, ct).ConfigureAwait(false), value => gatherItems = value, () => gatherItems = []); break;
                    case "alter":
                        Apply(section, await life.ReadAlterablesAsync(alterQuery, ct).ConfigureAwait(false), value => { alterRecipes = value; fullFacilities.Clear(); }, () => alterRecipes = []);
                        break;
                    case "works": Apply(section, await life.ReadWorksAsync(ct).ConfigureAwait(false), value => queue = value, () => queue = null); break;
                    case "craft": Apply(section, await life.ReadCraftablesAsync(craftQuery, ct).ConfigureAwait(false), value => crafts = value, () => crafts = null); break;
                }
            }
        }
        finally { readGate.Release(); }
    }

    private void Apply<T>(string section, TwCliRead<T> read, Action<T> store, Action clear)
    {
        lock (gate)
        {
            if (read.Ok) { store(read.Value!); SetStatus(section, new(LifeReadState.Loaded, null, clock.GetUtcNow())); }
            else { clear(); SetStatus(section, new(LifeReadState.Failed, read.Code ?? "gameCli.failed", clock.GetUtcNow())); }
            revision = checked(revision + 1);
        }
    }

    private void Fail(string section, string reason)
    {
        lock (gate)
        {
            switch (section)
            {
                case "gather": gatherItems = []; break;
                case "alter": alterRecipes = []; break;
                case "works": queue = null; break;
                case "craft": crafts = null; break;
            }
            SetStatus(section, new(LifeReadState.Failed, reason, clock.GetUtcNow()));
            revision = checked(revision + 1);
        }
    }

    private void SetStatus(string section, LifeSectionStatus status)
    {
        switch (section)
        {
            case "gather": gatherStatus = status; break;
            case "alter": alterStatus = status; break;
            case "works": worksStatus = status; break;
            case "craft": craftStatus = status; break;
        }
    }

    /// <summary>
    /// 加工队列里加工中的项目按上次读取推算已经到点（任务之间据此补读一次队列）。设施内逐项加工，
    /// 排队未开始（NotStarted）的项目给出的剩余秒数是它自己的加工时长，不随时间减少，不参与判断。
    /// </summary>
    private bool WorksDue()
    {
        lock (gate)
        {
            if (queue is null || worksStatus.ReadAt is not { } readAt) return false;
            var elapsed = (clock.GetUtcNow() - readAt).TotalSeconds;
            return queue.Works.Any(work => work.Completed != true && work.State != "NotStarted" && work.RemainingSeconds is { } seconds && seconds <= elapsed);
        }
    }

    public void Dispose()
    {
        Runner? current;
        lock (gate) { if (disposed) return; disposed = true; current = runner; }
        client.Changed -= Bump;
        if (current is not null)
        {
            current.Cancellation.Cancel();
            current.ReleaseLease();
            try { current.Task?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        }
        // 共用的存储（宿主装配、测试模拟重启）由装配方释放；这里只等最后一次保存写完。
        if (ownsStore) store.Dispose();
        else try { store.FlushAsync().Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
    }
}

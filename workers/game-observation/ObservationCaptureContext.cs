using MabiTool.Contracts;
using MabiTool.Providers.TwObservation.Inventory;

namespace MabiTool.Observation.Worker;

/// <summary>捕获与类型化兴趣切换共用一次串行边界；结束回调先于兴趣撤销。</summary>
public sealed class ObservationCaptureContext
{
    private readonly object gate = new();
    private readonly Dictionary<long, Action<ObservationCaptureInterests, ObservationCaptureInterests>> handlers = [];
    private readonly IReadOnlyDictionary<string, ObservationCombatProfile> profiles;
    private readonly Action<GameCaptureReport> music;
    private readonly Action<CombatCaptureReport> combat;
    private readonly Action<InventoryCaptureReport> inventory;
    public ClientCaptureLayout? InventoryLayout { get; private set; }
    public ObservationInventoryProfile? InventoryProfile { get; private set; }
    /// <summary>本次捕获运行的累计诊断计数，随上下文创建而清零。</summary>
    public CaptureDiagnostics Diagnostics { get; } = new();
    private ObservationCaptureInterests current = new(true, null);
    private long nextHandler;
    public ObservationCaptureContext(IReadOnlyList<ObservationCombatProfile> supported,
        Action<GameCaptureReport> publishMusic, Action<CombatCaptureReport> publishCombat,
        ObservationInventoryProfile? inventoryProfile = null, ClientCaptureLayout? inventoryLayout = null,
        Action<InventoryCaptureReport>? publishInventory = null)
    {
        profiles = supported.ToDictionary(x => x.Id, StringComparer.Ordinal); music = publishMusic; combat = publishCombat;
        InventoryProfile = inventoryProfile; InventoryLayout = inventoryLayout; inventory = publishInventory ?? (_ => { });
    }
    public ObservationCaptureInterests Current { get { lock (gate) return current; } }
    public void InstallInventoryLayout(ObservationInventoryProfile profile, ClientCaptureLayout layout)
    {
        lock (gate)
        {
            if (current.Inventory is not null && InventoryProfile != profile) throw new WorkerObservationException("inventory.interestActive");
            if (profile.AssemblySha256 != layout.AssemblyHash || profile.MetadataSha256 != layout.MetadataHash)
                throw new WorkerObservationException("inventory.buildUnsupported");
            InventoryProfile = profile; InventoryLayout = layout;
        }
    }
    public ObservationCombatProfile? Profile(ObservationCombatInterest? value) => value is null ? null : profiles.GetValueOrDefault(value.Profile);
    public bool CanConfigure(ObservationCaptureInterests value)
    {
        if (value.Inventory is { } requestedInventory && (InventoryProfile?.Id != requestedInventory.Profile || InventoryLayout is null)) return false;
        if (value.Combat is not { } requested) return true;
        return Profile(requested) is { } configured &&
            (!requested.DamageEnabled || configured.Damage) && (!requested.SelfEffectsEnabled || configured.SelfEffects) &&
            (!requested.MonsterEffectsEnabled || configured.MonsterEffects);
    }
    public void Configure(ObservationCaptureInterests value, Action? beforeCommit = null)
    {
        lock (gate)
        {
            if (!CanConfigure(value)) throw new WorkerObservationException(value.Inventory is not null ? "inventory.buildUnsupported" : "combat.buildUnsupported");
            foreach (var handler in handlers.Values) handler(current, value);
            beforeCommit?.Invoke();
            current = value;
        }
    }
    public IDisposable OnChanging(Action<ObservationCaptureInterests, ObservationCaptureInterests> handler)
    {
        lock (gate) { var id = ++nextHandler; handlers.Add(id, handler); return new Registration(this, id); }
    }
    public void Execute(Action action) { lock (gate) action(); }
    /// <summary>状态报告携带本机身份，不依赖模块兴趣始终发送；音乐字段由解析侧按音乐兴趣填写。</summary>
    public void Publish(GameCaptureReport value) { lock (gate) music(value); }
    public void Publish(CombatCaptureReport value) { lock (gate) if (current.Combat?.Id == value.InterestId) combat(value); }
    /// <summary>兴趣在串行边界内核对；入队（含整帧编码）在边界外执行，库存解析通道发布大帧时不阻塞捕获线程。</summary>
    public void Publish(InventoryCaptureReport value)
    {
        bool active; lock (gate) active = current.Inventory?.Id == value.InterestId;
        if (active) inventory(value);
    }
    private sealed class Registration(ObservationCaptureContext context, long id) : IDisposable
    {
        public void Dispose() { lock (context.gate) context.handlers.Remove(id); }
    }
}

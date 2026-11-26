// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
// 身份关联来源 MabiAgent LoginIdentity；字段类型和位置从当前客户端元数据读取。
using MabiTool.Contracts;

namespace MabiTool.Providers.TwObservation.Inventory;

/// <summary>
/// 按连接重放库存协议：登录个人数据（20590）给出完整背包，身份关联 170631 给出账户与角色名后发出完整同步帧。
/// 170631 在 <see cref="IdentityWindowMs"/> 内没有到达而 <paramref name="localName"/> 能给出该角色的本机昵称时，
/// 以“按角色归属”的身份（账户 ID 为 <see cref="CharacterAccountPrefix"/> 加角色 ID）发出完整同步，公共保管箱归到这个身份下。
/// 登录数据读出账户主角色（<see cref="LoginWallet.MasterCharacterId"/>）且角色名称已知时，登录数据到达即发出完整同步：账户为
/// <paramref name="accounts"/> 里此前 170631 关联的游戏账户编号，没有时为 <see cref="MasterAccountPrefix"/> 加主角色 ID（由读模型并到已知账户）。
/// 按 <c>master-</c> 同步后匹配的 170631 改用游戏账户编号并发一帧空增量；账户来自此前记下的编号而 170631 不同时撤销为 <c>inventory.identityConflict</c>。
/// </summary>
/// <param name="localName">同一游戏进程已确认的本机昵称（角色 ID → 昵称）；没有或角色不同为空。</param>
/// <param name="accounts">同一游戏进程各连接共用的账户主角色对应；为空时只在本连接内按 170631 关联。</param>
public sealed class InventoryProtocolObserver(string session, ObservationSource source, ClientCaptureLayout layout,
    Action<InventoryFrame> publish, Func<ulong> nextGeneration, Func<ulong, string?>? localName = null, InventoryAccountMemory? accounts = null)
{
    /// <summary>
    /// 等待身份关联的时长。台服 2026-11-15 五份实采中 170631 在 20590 之后 3.4–16.4 s 到达（同一连接），留出约一倍余量。
    /// </summary>
    public const double IdentityWindowMs = 30000;
    /// <summary>没有取得游戏账户时按角色归属的账户 ID 前缀。</summary>
    public const string CharacterAccountPrefix = "character-";
    /// <summary>只读出账户主角色、还不知道游戏账户编号时的账户 ID 前缀（与 <c>InventoryResources.MasterAccountPrefix</c> 一致）。</summary>
    public const string MasterAccountPrefix = "master-";
    private readonly ClientInventoryDecoder decoder = layout.CreateDecoder();
    private readonly WalletDecoder wallet = WalletProtocol.CreateDecoder(layout.Opcodes, layout.Resources);
    private readonly Dictionary<(string Category, InventoryContainer Container, string Key), InventoryItem> records = [];
    private DecodedInventoryLogin? pending;
    private InventoryIdentity? identity;
    private IReadOnlyList<InventoryCoverage> coverage = [];
    private double pendingAt;
    private ulong sequence;
    public ulong Generation { get; private set; }
    private readonly HashSet<uint> observed = [.. layout.Opcodes.Values, layout.Protocol.Login, layout.Protocol.Profile];
    /// <summary>当前客户端布局登记的全部库存协议号（登录、身份关联和增量）。</summary>
    public IReadOnlySet<uint> Opcodes => observed;
    public bool Observes(int code) => code >= 0 && observed.Contains((uint)code);
    public void Accept(int code, byte[] body, double mono, DateTimeOffset utc)
    {
        try
        {
            if ((uint)code == layout.Protocol.Login)
            {
                Reset("inventory.awaitingIdentity", mono, utc); Generation = nextGeneration(); sequence = 0;
                Emit(InventoryFrameKind.Invalidated, mono, utc, [], [], "inventory.awaitingIdentity");
                // 登录数据读不通时还没有等待中的数据，Reset 不会发帧：在这里发出失效帧，宿主日志才看得到解码失败。
                try { pending = decoder.DecodeLogin(body); }
                catch (Exception error) when (error is InvalidDataException or OverflowException or ArgumentException)
                { Emit(InventoryFrameKind.Invalidated, mono, utc, [], [], "inventory.protocolInvalid"); return; }
                pendingAt = mono;
                ResolveByMaster(mono, utc); return;
            }
            if ((uint)code == layout.Protocol.Profile)
            {
                if (pending is null && !byMaster) return;
                if (!layout.Methods.TryGetValue(layout.Protocol.Profile, out var profileMethod)) throw new InvalidDataException("inventory.identityUnsupported");
                // 同一时刻会收到多名公会成员的 170631；读不出的、属于其他角色的都只跳过，不撤销等待中的登录数据。
                InventoryIdentity joined;
                try { joined = decoder.DecodeIdentity(body, profileMethod); }
                catch (InvalidDataException) { return; }
                if (pending is null)
                {
                    // 已按账户主角色发出完整同步：同一角色的 170631 核对账户，不同即撤销并停用该对应。
                    if (joined.CharacterId != identity!.CharacterId) return;
                    if (identity.AccountId.StartsWith(MasterAccountPrefix, StringComparison.Ordinal))
                    {
                        // 只凭主角色归户的同步拿到游戏账户编号：改用编号，空增量把身份交给读模型记下别名。
                        if (master is { } named) accounts?.Learn(named, joined);
                        identity = identity with { AccountId = joined.AccountId }; byMaster = false;
                        Emit(InventoryFrameKind.Delta, mono, utc, [], []); return;
                    }
                    if (joined.AccountId != identity.AccountId)
                    {
                        if (master is { } conflicted) accounts?.Forget(conflicted);
                        Reset("inventory.identityConflict", mono, utc); return;
                    }
                    if (master is { } confirmed) accounts?.Learn(confirmed, joined);
                    byMaster = false; return;
                }
                if (joined.CharacterId != pending.CharacterId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) return;
                if (pending.Wallet?.MasterCharacterId is { } learned) accounts?.Learn(learned, joined);
                Complete(joined, mono, utc); return;
            }
            if (!Observes(code)) return;
            // 钱包资源：等待身份期间与逐件增量一起按序暂存；本连接还没有登录数据时无法归属到角色，跳过。
            if (wallet.Handles(code))
            {
                if (pending is not null)
                {
                    if (queued.Count >= MaximumQueuedUpdates) { Reset("inventory.streamGap", mono, utc); return; }
                    queued.Add((code, body, mono, utc)); return;
                }
                if (identity is not null) ApplyUpdate(code, body, mono, utc);
                return;
            }
            // 装备槽、负重与钱包的复制帧（布局登记了协议号但没有逐件方法）不改变条目：跳过，不撤销当前库存。
            // 台服 2026-11-15 实采中这类帧每分钟数十次，此前一律按 inventory.updateUnsupported 撤销，完整同步发出后即被作废。
            if (!layout.Methods.ContainsKey((uint)code)) return;
            // 等待身份期间的增量按序暂存（至多 MaximumQueuedUpdates 帧），完整同步发出后依次叠加；超出则撤销并等待下一次完整同步。
            if (pending is not null)
            {
                if (queued.Count >= MaximumQueuedUpdates) { Reset("inventory.streamGap", mono, utc); return; }
                queued.Add((code, body, mono, utc)); return;
            }
            if (identity is null) { Reset("inventory.awaitingFull", mono, utc); return; }
            ApplyUpdate(code, body, mono, utc);
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException or ArgumentException)
        { Reset("inventory.protocolInvalid", mono, utc); }
    }
    /// <summary>等待身份期间暂存的增量帧上限。</summary>
    public const int MaximumQueuedUpdates = 512;
    private readonly List<(int Code, byte[] Body, double Mono, DateTimeOffset Utc)> queued = [];
    private void ApplyUpdate(int code, byte[] body, double mono, DateTimeOffset utc)
    {
        if (wallet.Handles(code))
        {
            // 无法识别的 MoneyType、负值或长度不符的负载只跳过，不撤销库存。
            if (wallet.Decode(code, body) is { } change) Emit(InventoryFrameKind.Delta, mono, utc, [], [], resources: [change]);
            return;
        }
        {
            if (!layout.Methods.TryGetValue((uint)code, out var method)) throw new InvalidDataException("inventory.updateUnsupported");
            var update = decoder.DecodeMethod(body, method); var next = new Dictionary<(string, InventoryContainer, string), InventoryItem>(records);
            var changes = new List<InventoryDelta>();
            foreach (var change in update.Changes)
            {
                var item = change.Item; var container = item.Container.KnownValue!.Value;
                var ambiguousStorage = method.Name.Contains("Storage", StringComparison.Ordinal) &&
                    !method.Name.Contains("AccountStorage", StringComparison.Ordinal) && !method.Name.Contains("CharacterStorage", StringComparison.Ordinal) &&
                    !method.Parameters.Any(p => p.Split('.').Last().StartsWith("Stored", StringComparison.Ordinal));
                if (ambiguousStorage)
                {
                    var matches = next.Values.Where(i => i.Category == item.Category && i.Key == item.Key && i.Container.KnownValue != InventoryContainer.Backpack).ToArray();
                    if (matches.Length != 1) throw new InvalidDataException("inventory.containerAmbiguous");
                    container = matches[0].Container.KnownValue!.Value; item = item with { Container = container };
                }
                var key = (item.Category, container, item.Key);
                if (change.Kind.KnownValue == InventoryDeltaKind.Remove)
                {
                    if (!next.Remove(key, out var previous)) throw new InvalidDataException("inventory.removalUnknown");
                    item = previous with { Quantity = new(0) };
                }
                else
                {
                    if (next.TryGetValue(key, out var previous) && (previous.TypeId != item.TypeId || previous.InstanceId != item.InstanceId))
                        throw new InvalidDataException("inventory.instanceIdentityConflict");
                    if (previous is not null) item = item with { Equipped = previous.Equipped };
                    next[key] = item;
                }
                changes.Add(change with { Item = item });
            }
            if (next.Count > 10000) throw new InvalidDataException("inventory.itemBudget");
            records.Clear(); foreach (var pair in next) records.Add(pair.Key, pair.Value);
            Emit(InventoryFrameKind.Delta, mono, utc, [], changes);
        }
    }
    public void Tick(double mono, DateTimeOffset utc)
    {
        if (pending is null) return;
        try { if (ResolveByMaster(mono, utc)) return; }
        catch (Exception error) when (error is InvalidDataException or OverflowException or ArgumentException) { Reset("inventory.protocolInvalid", mono, utc); return; }
        if (mono - pendingAt <= IdentityWindowMs) return;
        var character = pending.CharacterId.Value;
        if (Valid(localName?.Invoke(character)) is { } name)
        {
            var text = character.ToString(System.Globalization.CultureInfo.InvariantCulture);
            try { Complete(new(CharacterAccountPrefix + text, text, name, name.Normalize(System.Text.NormalizationForm.FormKC)), mono, utc); }
            catch (Exception error) when (error is InvalidDataException or OverflowException or ArgumentException) { Reset("inventory.protocolInvalid", mono, utc); }
            return;
        }
        Reset("inventory.identityTimeout", mono, utc);
    }
    /// <summary>
    /// 等待中的登录数据读出了账户主角色、角色名称已知（170631 给过的名称或本机昵称）时立即完成同步：账户为本进程内 170631 记下的游戏账户编号，
    /// 没有时为 <c>master-</c> 加主角色 ID。名称还不知道时留待 <see cref="Tick"/> 再试，170631 先到则照常关联。
    /// </summary>
    private bool ResolveByMaster(double mono, DateTimeOffset utc)
    {
        if (pending?.Wallet?.MasterCharacterId is not { } key) return false;
        var character = pending.CharacterId.Value;
        if (Valid(accounts?.Name(character) ?? localName?.Invoke(character)) is not { } name) return false;
        var account = accounts?.Account(key) ?? MasterAccountPrefix + key.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var text = character.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Complete(new(account, text, name, name.Normalize(System.Text.NormalizationForm.FormKC)), mono, utc);
        byMaster = true; return true;
    }
    private static string? Valid(string? name) =>
        name is { Length: > 0 and <= 128 } && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl) ? name : null;
    /// <summary>当前同步按账户主角色确定了账户、尚未由 170631 核对。</summary>
    private bool byMaster;
    /// <summary>当前同步的登录数据读出的账户主角色。</summary>
    private ulong? master;
    private void Complete(InventoryIdentity joined, double mono, DateTimeOffset utc)
    {
        master = pending!.Wallet?.MasterCharacterId; byMaster = false;
        identity = joined with { MasterCharacterId = master?.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        records.Clear(); foreach (var item in pending.Items)
            if (!records.TryAdd((item.Category, item.Container.KnownValue!.Value, item.Key), item)) throw new InvalidDataException("inventory.duplicateItem");
        coverage = pending.Coverage;
        Emit(InventoryFrameKind.Full, mono, utc, pending.Items, [], gold: pending.Gold, hash: pending.BodySha256, resources: wallet.Login(pending.Wallet, utc));
        pending = null;
        var updates = queued.ToArray(); queued.Clear();
        foreach (var (code, body, at, atUtc) in updates) ApplyUpdate(code, body, at, atUtc);
    }
    public void Reset(string reason, double mono, DateTimeOffset utc)
    {
        if (Generation != 0 && (identity is not null || pending is not null)) Emit(InventoryFrameKind.Invalidated, mono, utc, [], [], reason);
        records.Clear(); pending = null; identity = null; coverage = []; queued.Clear(); byMaster = false; master = null;
    }
    private void Emit(InventoryFrameKind kind, double mono, DateTimeOffset utc, IReadOnlyList<InventoryItem> items,
        IReadOnlyList<InventoryDelta> changes, string? reason = null, UInt64String? gold = null, string? hash = null,
        IReadOnlyList<InventoryResourceChange>? resources = null) =>
        publish(new(1, source, session, new(Generation), new(++sequence), utc, mono, kind, identity, items, changes, coverage, gold, reason, hash,
            Resources: resources));
}

/// <summary>
/// 同一游戏进程各连接共用的账户主角色对应：登录数据的 <c>AccountProfile.MasterCharacterId</c> → 170631 关联出的游戏账户编号，
/// 以及 170631 给出的角色名称。只由匹配的 170631 写入；170631 与之冲突时停用该主角色，本进程内不再按它确定账户。
/// </summary>
public sealed class InventoryAccountMemory
{
    private readonly object gate = new();
    private readonly Dictionary<ulong, string?> masters = [];
    private readonly Dictionary<ulong, string> names = [];
    /// <summary>主角色对应的游戏账户编号；没有或已停用时为空。</summary>
    public string? Account(ulong master) { lock (gate) return masters.GetValueOrDefault(master); }
    /// <summary>170631 给过的角色名称。</summary>
    public string? Name(ulong character) { lock (gate) return names.GetValueOrDefault(character); }
    public void Learn(ulong master, InventoryIdentity joined)
    {
        if (!ulong.TryParse(joined.CharacterId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var character)) return;
        lock (gate)
        {
            if (masters.Count >= 64 && !masters.ContainsKey(master) || names.Count >= 256 && !names.ContainsKey(character)) return;
            names[character] = joined.RawName;
            // 已停用（null）的主角色不再恢复；另一账户再次声明同一主角色时停用。
            if (!masters.TryGetValue(master, out var known)) masters[master] = joined.AccountId;
            else if (known is not null && known != joined.AccountId) masters[master] = null;
        }
    }
    public void Forget(ulong master) { lock (gate) masters[master] = null; }
}

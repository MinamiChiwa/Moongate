// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.Inventory.Domain;

namespace MabiTool.Modules.Inventory.Adapters;

public sealed record InventoryWorkspaceState(bool Current, OpenEnum<ObservationQuality> Quality, IReadOnlyList<string> Reasons,
    int Items, bool IdentityPresent);

public sealed partial class InventoryHistoryStore : IInventoryReadModel
{
    private readonly SqliteStore database;
    private readonly TimeProvider clock;
    private readonly string epoch;
    private readonly SemaphoreSlim mutation = new(1, 1);
    private byte[] cursorKey = RandomNumberGenerator.GetBytes(32);
    private InventorySavedState state;
    private InventoryCatalogSnapshot? catalog;
    private readonly object catalogGate = new();
    private InventoryCatalogSnapshot? indexedCatalog;
    private IReadOnlyDictionary<string, InventoryCatalogEntry> catalogIndex = new Dictionary<string, InventoryCatalogEntry>();
    public static DatabaseMigration Migration { get; } = new("inventory", 1, """
        CREATE TABLE inventory_state(id INTEGER PRIMARY KEY CHECK(id=1),revision TEXT NOT NULL,document TEXT NOT NULL) STRICT;
        CREATE TABLE inventory_catalog(id INTEGER PRIMARY KEY CHECK(id=1),revision TEXT NOT NULL,document TEXT NOT NULL) STRICT;
        CREATE TABLE inventory_import_receipts(id TEXT PRIMARY KEY,hash TEXT NOT NULL,document TEXT NOT NULL) STRICT;
        CREATE TABLE inventory_edit_receipts(id TEXT PRIMARY KEY,hash TEXT NOT NULL,document TEXT NOT NULL) STRICT;
        """);
    public InventoryHistoryStore(SqliteStore database, string epoch, TimeProvider? timeProvider = null)
    {
        this.database = database; this.epoch = epoch; clock = timeProvider ?? TimeProvider.System;
        state = MigrateResources(database.ReadScalar("SELECT document FROM inventory_state WHERE id=1") is string saved
            ? JsonSerializer.Deserialize<InventorySavedState>(saved, ContractJson.Options)! : InventorySavedState.Empty);
        if (state.Workspace is { } workspace) state = state with { Workspace = workspace with
        { Current = false, Quality = ObservationQuality.Stale, Reasons = ["inventory.hostRestarted"],
          Items = workspace.Items.Select(i => i with { Current = false, Quality = ObservationQuality.Stale }).ToArray() } };
    }
    public void Reload()
    {
        mutation.Wait();
        try
        {
            state=MigrateResources(database.ReadScalar("SELECT document FROM inventory_state WHERE id=1") is string saved?JsonSerializer.Deserialize<InventorySavedState>(saved,ContractJson.Options)!:InventorySavedState.Empty);
            lock(catalogGate){catalog=null;indexedCatalog=null;catalogIndex=new Dictionary<string,InventoryCatalogEntry>();}
            lock(imports)imports.Clear();cursorKey=RandomNumberGenerator.GetBytes(32);
        }
        finally{mutation.Release();}
    }
    public UInt64String Revision => state.Revision;
    public InventoryWorkspace? Workspace() => state.Workspace is { } value ? ProjectWorkspace(value, state) : null;
    /// <summary>当前工作区的同步状态摘要；不投影名称、不读取目录，供诊断等只读路径使用。</summary>
    public InventoryWorkspaceState? WorkspaceState() => state.Workspace is { } value
        ? new(value.Current, value.Quality, value.Reasons, value.Items.Count, value.Identity is not null) : null;
    public async ValueTask<InventoryReadModelSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        await mutation.WaitAsync(cancellationToken);
        try
        {
            var snapshot = state;
            return new(snapshot.Revision, snapshot.Workspace is { } value ? ProjectWorkspace(value, snapshot) : null, Catalog());
        }
        finally { mutation.Release(); }
    }
    public InventoryCatalogSnapshot Catalog()
    {
        lock (catalogGate)
        {
            if (catalog is not null) return catalog;
            catalog = database.ReadScalar("SELECT document FROM inventory_catalog WHERE id=1") is string saved
                ? JsonSerializer.Deserialize<InventoryCatalogSnapshot>(saved, ContractJson.Options)! : new(new(0), null);
            return catalog;
        }
    }
    public InventoryCatalogSummary CatalogSummary()
    {
        var snapshot = Catalog(); var value = snapshot.Data;
        return new(snapshot.Revision, value?.Id, value?.IndexSha256, value?.Entries.Count ?? 0, value?.Excluded.Count ?? 0, value?.Categories ?? []);
    }
    public async Task<InventoryWorkspace?> ApplyFrameAsync(InventoryFrame frame, CancellationToken ct = default)
    {
        try { ValidateFrame(frame); }
        catch(OperationException) { await InvalidateAsync("inventory.protocolInvalid", ct); throw; }
        await mutation.WaitAsync(ct);
        try
        {
            var previous = state; var existing = previous.Workspace;
            if (existing is not null && existing.GameSessionId == frame.GameSessionId &&
                (frame.Generation.Value < existing.Generation.Value || frame.Generation == existing.Generation && frame.Sequence.Value <= previous.Sequence.Value))
                return Workspace();
            if (frame.Kind.KnownValue == InventoryFrameKind.Invalidated)
            { await InvalidateCoreAsync(frame.Reason ?? "inventory.observationGap", ct); return Workspace(); }
            if (frame.Kind.KnownValue == InventoryFrameKind.Delta && (existing is null || !existing.Current ||
                existing.GameSessionId != frame.GameSessionId || existing.Generation != frame.Generation || frame.Sequence.Value != previous.Sequence.Value + 1))
            { await InvalidateCoreAsync("inventory.sequenceGap", ct); return Workspace(); }
            if(frame.Kind.KnownValue==InventoryFrameKind.Delta && frame.Changes.Any(change=>!existing!.Coverage.Any(cover=>cover.Category==change.Item.Category && cover.Container==change.Item.Container && cover.Complete)))
            { await InvalidateCoreAsync("inventory.protocolInvalid",ct);throw Rejected("deltaCoverage","库存增量缺少对应完整覆盖基线"); }
            var accounts = new Dictionary<string, InventorySavedAccount>(previous.Accounts, StringComparer.Ordinal);
            var merged = new HashSet<string>(StringComparer.Ordinal);
            var identity = (frame.Identity is { } sent ? Resolve(accounts, sent, merged) : null) ?? existing?.Identity ?? throw Rejected("identityMissing", "库存身份尚未确认", "inventory.identityUnresolved");
            if (frame.Kind.KnownValue == InventoryFrameKind.Delta && existing?.Identity is { } old &&
                ((merged.Contains(old.AccountId) ? identity.AccountId : old.AccountId) != identity.AccountId || old.CharacterId != identity.CharacterId))
            { await InvalidateCoreAsync("inventory.identityConflict", ct); return Workspace(); }
            // 帧按游戏会话归属：同一会话内绑定修订（窗口几何）不同的完整帧照常成为当前，增量的连续性由代次与序号判定。
            if(accounts.TryGetValue(identity.AccountId,out var knownAccount) && knownAccount.Characters.TryGetValue(identity.CharacterId,out var knownCharacter) && knownCharacter.Identity.RawName != identity.RawName)
            { await InvalidateCoreAsync("inventory.identityConflict",ct); throw Rejected("identityName","角色名称与已保存身份冲突","inventory.identityConflict"); }
            var revision = Next(previous.Revision);
            var items = frame.Kind.KnownValue == InventoryFrameKind.Full ? frame.Items.Where(i => i.Quantity?.Value != 0).ToArray()
                : ApplyDeltas(existing!.Items.Select(i => i.Item).ToArray(), frame.Changes);
            var coverage = frame.Kind.KnownValue == InventoryFrameKind.Full ? frame.Coverage : existing!.Coverage;
            var views = items.Select(i => new InventoryItemView(i, Unknown(i.Type), i.Quantity is not null, frame.ObservedAt,
                i.Quantity is null ? ObservationQuality.Partial : ObservationQuality.Confirmed, frame.Source,
                i.Container.KnownValue == InventoryContainer.AccountStorage ? null : identity.CharacterId)).ToArray();
            var workspace = new InventoryWorkspace(existing?.Id ?? Guid.NewGuid().ToString("N"), frame.GameSessionId, identity,
                frame.Generation, revision, frame.BindingRevision, coverage.All(c => c.Complete) && items.All(i=>i.Quantity is not null) ? ObservationQuality.Confirmed : ObservationQuality.Partial,
                true, frame.ObservedAt, views, coverage, frame.Kind.KnownValue == InventoryFrameKind.Full ? frame.Gold : frame.Gold ?? existing?.Gold, [], frame.Source);
            accounts.TryGetValue(identity.AccountId, out var account);
            account ??= new(identity.AccountId, identity.AccountId, new Dictionary<string, InventorySavedCharacter>(), [], [], null, [], false,
                new Dictionary<string, InventoryNameAssociation>());
            var characters = new Dictionary<string, InventorySavedCharacter>(account.Characters, StringComparer.Ordinal)
            { [identity.CharacterId] = new(identity, frame.ObservedAt, items.Where(i => i.Container.KnownValue != InventoryContainer.AccountStorage).ToArray(),
                coverage.Where(c => c.Container.KnownValue != InventoryContainer.AccountStorage).ToArray(), workspace.Gold, frame.Source,
                account.Characters.GetValueOrDefault(identity.CharacterId)?.Resources) };
            accounts[identity.AccountId] = account with { Characters = characters, SharedItems = items.Where(i => i.Container.KnownValue == InventoryContainer.AccountStorage).ToArray(),
                SharedCoverage = coverage.Where(c => c.Container.KnownValue == InventoryContainer.AccountStorage).ToArray(), SharedObservedAt = frame.ObservedAt, SharedSource=frame.Source };
            ApplyResources(accounts, identity, frame.Resources, frame.ObservedAt, frame.Kind.KnownValue == InventoryFrameKind.Full);
            // 角色在 master- 或游戏账户编号账户里完整同步后，按角色归属的旧账户不再保留：资源已在上一步按较新值并入，道具以本次完整同步为准。
            if (frame.Kind.KnownValue == InventoryFrameKind.Full && !Fallback(identity.AccountId)) accounts.Remove(InventoryResources.CharacterAccountPrefix + identity.CharacterId);
            if (accounts.Count > 128 || accounts.Values.Sum(a => a.Characters.Values.Sum(c => c.Items.Count) + a.SharedItems.Count) > 100000)
                throw new OperationException("resource.capacityExceeded", "库存账户或历史条目达到预算上限");
            var next = new InventorySavedState(revision, workspace, frame.Sequence, accounts);
            await SaveStateAsync(previous, next, new InventorySnapshotChanged(revision, identity.AccountId, identity.CharacterId, true), ct);
            return Workspace();
        }
        finally { mutation.Release(); }
    }
    public async Task<InventoryWorkspace?> InvalidateAsync(string reason, CancellationToken ct = default)
    { await mutation.WaitAsync(ct); try { await InvalidateCoreAsync(reason, ct); return Workspace(); } finally { mutation.Release(); } }
    private async Task InvalidateCoreAsync(string reason, CancellationToken ct)
    {
        if (state.Workspace is not { } workspace || !workspace.Current) return;
        var revision = Next(state.Revision); var next = state with { Revision = revision, Workspace = workspace with
        { Revision = revision, Current = false, Quality = ObservationQuality.Stale, Reasons = [reason],
          Items = workspace.Items.Select(i => i with { Current = false, Quality = ObservationQuality.Stale }).ToArray() } };
        await SaveStateAsync(state, next, new InventorySnapshotChanged(revision, workspace.Identity?.AccountId, workspace.Identity?.CharacterId, false), ct);
    }
    private async Task SaveStateAsync(InventorySavedState previous, InventorySavedState next, InventorySnapshotChanged changed, CancellationToken ct,
        Action<DatabaseTransaction>? extra = null)
    {
        var json = JsonSerializer.Serialize(next, ContractJson.Options);
        if (Encoding.UTF8.GetByteCount(json) > 64 * 1024 * 1024) throw new OperationException("resource.capacityExceeded", "库存历史超过 64 MiB");
        var eventData=JsonSerializer.SerializeToElement(changed,ContractJson.Options);
        await database.WriteAsync(tx =>
        {
            var revision = tx.Scalar("SELECT revision FROM inventory_state WHERE id=1") as string ?? "0";
            if (revision != previous.Revision.ToString()) throw new OperationException("inventory.planStale", "库存修订已经变化");
            tx.Execute("INSERT INTO inventory_state VALUES(1,$r,$d) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,document=excluded.document", ("$r", next.Revision.ToString()), ("$d", json));
            extra?.Invoke(tx);
            SqliteStore.AppendEvent(tx, new(Guid.NewGuid().ToString("N"), "inventory", "inventory", next.Revision, "inventory.snapshot.changed",
                eventData)); return true;
        }, ct);
        state = next;
    }
    private static InventoryItem[] ApplyDeltas(IReadOnlyList<InventoryItem> old, IReadOnlyList<InventoryDelta> changes)
    {
        var result = old.ToDictionary(ItemKey, StringComparer.Ordinal);
        foreach (var change in changes)
            if (change.Kind.KnownValue == InventoryDeltaKind.Remove || change.Item.Quantity?.Value == 0) result.Remove(ItemKey(change.Item));
            else result[ItemKey(change.Item)] = change.Item;
        if (result.Count > 10000) throw new OperationException("resource.capacityExceeded", "库存超过 10000 个实例");
        return result.Values.ToArray();
    }
    private static string ItemKey(InventoryItem item) => item.Category + "\0" + item.Container.RawValue + "\0" + item.Key;
    internal static string TypeKey(InventoryTypeKey item) => item.Category + "\0" + item.TypeId;
    internal static InventoryNameAssociation Unknown(InventoryTypeKey type) => new(type, null, null, InventoryNameState.Unknown, null);
    private static UInt64String Next(UInt64String revision) => new(checked(revision.Value + 1));
    private static void ValidateFrame(InventoryFrame frame)
    {
        if(frame is null || frame.Source is null || frame.Items is null || frame.Changes is null || frame.Coverage is null)
            throw Rejected("frameMissing","库存帧及集合不能为空");
        if (frame.SchemaVersion != 1 || frame.Kind.KnownValue is null || !double.IsFinite(frame.MonotonicMs) || frame.MonotonicMs < 0 || frame.Generation.Value == 0 || frame.Sequence.Value == 0 || frame.BindingRevision?.Value == 0 || frame.ObservedAt == DateTimeOffset.MinValue || string.IsNullOrWhiteSpace(frame.GameSessionId) || frame.GameSessionId.Length > 256 ||
            frame.Items.Count > 10000 || frame.Changes.Count > 10000 || frame.Coverage.Count > 256)
            throw Rejected("frameFormat", "库存帧格式或预算无效");
        if (frame.Kind.KnownValue == InventoryFrameKind.Full && (frame.Identity is null || frame.Coverage.Count == 0))
            throw Rejected("fullIdentity", "完整库存需要完整身份和覆盖声明");
        if(frame.Changes.Any(c=>c is null || c.Item is null) || frame.Coverage.Any(c=>c is null || string.IsNullOrWhiteSpace(c.Category) || c.Category.Length>128 || c.Container.KnownValue is null) ||
            frame.Coverage.Select(c=>c.Category+"\0"+c.Container.RawValue).Distinct(StringComparer.Ordinal).Count()!=frame.Coverage.Count ||
            frame.Kind.KnownValue==InventoryFrameKind.Full && frame.Changes.Count>0 || frame.Kind.KnownValue==InventoryFrameKind.Delta && frame.Items.Count>0)
            throw Rejected("frameShape","库存帧种类、覆盖或增量无效");
        foreach (var item in frame.Items.Concat(frame.Changes.Select(c => c.Item)))
            if (item is null || string.IsNullOrWhiteSpace(item.Category) || item.Category.Length > 128 || string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 1024 || item.TypeId.Value == 0 && !ComposedScore(item) ||
                item.Container.KnownValue is null || item.Attributes.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(item.Attributes.GetRawText()) > 64 * 1024)
                throw Rejected("itemIdentity", "库存实例身份或属性无效");
        if(frame.Kind.KnownValue==InventoryFrameKind.Full && frame.Items.Any(i=>!frame.Coverage.Any(c=>c.Category==i.Category && c.Container==i.Container && c.Complete)))
            throw Rejected("itemCoverage","库存实例缺少对应完整覆盖");
        if (frame.Items.Select(ItemKey).Distinct(StringComparer.Ordinal).Count() != frame.Items.Count ||
            frame.Changes.Select(c => ItemKey(c.Item)).Distinct(StringComparer.Ordinal).Count() != frame.Changes.Count || frame.Changes.Any(c => c.Kind.KnownValue is null))
            throw Rejected("itemDuplicate", "库存包含重复实例或未知增量");
        if (frame.Resources is { } resources && (resources.Count > InventoryResources.MaximumChanges || resources.Any(r => r is null ||
            !Token(r.Key, ':') || !Token(r.MoneyType) || r.RawName is { } name && (name.Length is 0 or > 64 || name.Any(char.IsControl)) ||
            r.Description is { } text && (text.Length is 0 or > 256 || text.Any(char.IsControl)) ||
            r.RefillIntervalSeconds is { } seconds && (!double.IsFinite(seconds) || seconds <= 0 || seconds > 30 * 86400) ||
            r.RefillCap is <= 0 or > 1_000_000)))
            throw Rejected("resourceInvalid", "钱包资源字段无效");
        if (frame.Identity is { } identity && (string.IsNullOrWhiteSpace(identity.AccountId) || string.IsNullOrWhiteSpace(identity.CharacterId) || string.IsNullOrWhiteSpace(identity.RawName) || identity.AccountId.Length>256 || identity.CharacterId.Length>256 || identity.RawName.Length>128 || identity.RawName.Any(char.IsControl)))
            throw Rejected("identityFields", "库存身份字段缺失", "inventory.identityUnresolved");
    }
    /// <summary>库存帧拒绝分支码（Data["branch"]），宿主诊断事件据此记录拒绝环节，不含条目内容。</summary>
    public const string RejectionBranch = "branch";
    private static OperationException Rejected(string branch, string message, string code = "inventory.protocolInvalid")
    { var error = new OperationException(code, message); error.Data[RejectionBranch] = branch; return error; }
    // 玩家自制乐谱表编号为 0，以实例 ID 区分（与 Provider 解码规则一致）；其他类别必须有表编号。
    internal static bool ComposedScore(InventoryItem item) => item.Category == "MusicNoteItem" && item.TypeId.Value == 0 && item.InstanceId is { Value: > 0 };
    private static bool Token(string? value, char extra = '_') => value is { Length: > 0 and <= 64 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == extra);

    /// <summary>
    /// 合并本帧的资源变化：新值覆盖并按前值算出最近增减（增减通知给出时以通知为准）；上次补充时间、补充通知时刻单独更新；
    /// 只带名称、说明与补充间隔的项只更新这些说明字段，不改变“已读取”状态。
    /// <paramref name="login"/> 为真时是完整同步带来的登录数值：只是当时的读数，与前值不同也不记为一次增减（离线期间的变化量未知），最近增减清空；
    /// 补充上限只由完整同步判定（随会员变化），以本次为准，本次未能确定时清空；增量帧保留上次的上限。
    /// </summary>
    private static IReadOnlyDictionary<string, InventorySavedResource>? MergeResources(IReadOnlyDictionary<string, InventorySavedResource>? previous,
        IReadOnlyList<InventoryResourceChange>? changes, DateTimeOffset observedAt, string? characterId = null, bool login = false)
    {
        if (changes is not { Count: > 0 }) return previous;
        var result = previous is null ? new Dictionary<string, InventorySavedResource>(StringComparer.Ordinal)
            : new Dictionary<string, InventorySavedResource>(previous, StringComparer.Ordinal);
        foreach (var change in changes)
        {
            var old = result.GetValueOrDefault(change.Key) ?? new(change.Key, change.MoneyType, null, null, null, null, null, null, null, null, null, characterId);
            var next = old with { MoneyType = change.MoneyType, RawName = change.RawName ?? old.RawName, Description = change.Description ?? old.Description,
                RefillIntervalSeconds = change.RefillIntervalSeconds ?? old.RefillIntervalSeconds, RefillCap = login ? change.RefillCap : old.RefillCap };
            if (login && change.Value is { } reading)
                next = old.Value == reading ? next with { ObservedAt = observedAt, CharacterId = characterId }
                    : next with { Value = reading, ObservedAt = observedAt, CharacterId = characterId, LastIncrease = null, LastDecrease = null };
            else if (change.Value is { } value && old.Value != value)
                next = next with { Value = value, ObservedAt = observedAt, CharacterId = characterId,
                    LastIncrease = old.Value is { } before && value.Value > before.Value ? new UInt64String(value.Value - before.Value) : null,
                    LastDecrease = old.Value is { } prior && value.Value < prior.Value ? new UInt64String(prior.Value - value.Value) : null };
            else if (change.Value is not null) next = next with { ObservedAt = observedAt, CharacterId = characterId };
            if (change.Increase is { } increase) next = next with { LastIncrease = increase, LastDecrease = null, ObservedAt = next.ObservedAt ?? observedAt };
            if (change.Decrease is { } decrease) next = next with { LastDecrease = decrease, LastIncrease = null, ObservedAt = next.ObservedAt ?? observedAt };
            if (change.RefilledAt is { } refilled) next = next with { LastRefilledAt = refilled };
            if (change.RefillNotified) next = next with { RefillNotifiedAt = observedAt };
            result[change.Key] = next;
        }
        return result.Count > 256 ? throw new OperationException("resource.capacityExceeded", "钱包资源超过预算") : result;
    }

    /// <summary>
    /// 资源视图：一个库存账户的账户级资源，加该账户下各角色的角色级资源（每项带 <c>Scope</c> 与所属角色）。
    /// 没有指定账户时取当前库存身份所属账户。归属范围由 <see cref="InventoryResources.ScopeOf"/> 的对照表决定。
    /// 登录数据与变化消息都没有给出数值的为 unread（<c>inventory.walletUnread</c>）；下次补充时刻 = 上次补充时间 + 客户端表补充间隔，
    /// 持有量达到或超过补充上限时不给出（游戏不再补充）；上限未知时照常给出。
    /// 迁移时无法确定所属角色的角色级旧值作为账户下的历史值返回（角色为空、非当前，原因 <c>inventory.resourceOwnerUnknown</c>）。
    /// </summary>
    public InventoryResourcesView Resources(string? accountId = null)
    {
        var snapshot = state; var workspace = snapshot.Workspace; var identity = workspace?.Identity;
        accountId ??= identity is null ? null : ResourceAccount(snapshot.Accounts, identity);
        var account = accountId is null ? null : snapshot.Accounts.GetValueOrDefault(accountId);
        var own = identity is not null && accountId is not null && ResourceAccount(snapshot.Accounts, identity) == accountId;
        var live = workspace?.Current == true && own;
        var accountSaved = account?.Resources ?? new Dictionary<string, InventorySavedResource>();
        InventoryResourceView View(InventoryResourceDefinition definition, InventorySavedResource? value, string? characterId, bool current, string? reason = null)
        {
            var full = value is { Value: { } held, RefillCap: { } cap } && held.Value >= (ulong)cap;
            DateTimeOffset? next = !full && value?.LastRefilledAt is { } refilled && value.RefillIntervalSeconds is { } seconds ? refilled.AddSeconds(seconds) : null;
            var observed = value?.Value is not null;
            return new(definition.Key, value?.MoneyType ?? definition.MoneyType, observed ? "observed" : "unread",
                reason ?? (observed ? null : accountId is null ? "inventory.identityUnresolved" : "inventory.walletUnread"), value?.RawName, value?.Description,
                value?.Value, value?.LastIncrease, value?.LastDecrease, value?.ObservedAt, value?.LastRefilledAt, value?.RefillIntervalSeconds, next,
                value?.RefillNotifiedAt, definition.Verified, current && observed, value?.CharacterId, definition.Scope, characterId, value?.RefillCap);
        }
        IEnumerable<InventoryResourceDefinition> Definitions(InventoryResourceScope scope, IEnumerable<InventorySavedResource> extra) =>
            InventoryResources.Definitions.Where(d => d.Scope == scope).Concat(extra.Where(r => InventoryResources.Definitions.All(d => d.Key != r.Key))
                .Select(r => new InventoryResourceDefinition(r.Key, r.MoneyType, false, InventoryResourceScope.Character)).DistinctBy(d => d.Key));
        var items = new List<InventoryResourceView>();
        foreach (var definition in InventoryResources.Definitions.Where(d => d.Scope == InventoryResourceScope.Account))
            items.Add(View(definition, accountSaved.GetValueOrDefault(definition.Key), null, live));
        var characters = account?.Characters.Values.OrderByDescending(c => own && c.Identity.CharacterId == identity!.CharacterId).ThenBy(c => c.Identity.CharacterId, StringComparer.Ordinal).ToArray() ?? [];
        foreach (var character in characters)
        {
            var saved = character.Resources ?? new Dictionary<string, InventorySavedResource>();
            var current = live && character.Identity.CharacterId == identity!.CharacterId;
            foreach (var definition in Definitions(InventoryResourceScope.Character, saved.Values))
                items.Add(View(definition, saved.GetValueOrDefault(definition.Key), character.Identity.CharacterId, current,
                    InventoryResources.Definitions.Any(d => d.Key == definition.Key) || saved.GetValueOrDefault(definition.Key)?.Value is null ? null : "inventory.resourceScopeUnknown"));
        }
        // 迁移时找不到所属角色的角色级旧值：账户下的历史值，非当前。
        foreach (var orphan in accountSaved.Values.Where(r => InventoryResources.ScopeOf(r.Key) == InventoryResourceScope.Character && r.Value is not null))
            items.Add(View(InventoryResources.Definitions.FirstOrDefault(d => d.Key == orphan.Key) ?? new(orphan.Key, orphan.MoneyType, false, InventoryResourceScope.Character),
                orphan, null, false, "inventory.resourceOwnerUnknown"));
        return new(accountId, own ? identity!.CharacterId : null, own ? identity!.RawName : null, live, snapshot.Revision, items,
            characters.Select(c => new InventoryCharacterSlot(c.Identity.CharacterId, c.Identity.RawName, c.Identity.DisplayName)).ToArray());
    }

    private static bool Fallback(string accountId) => accountId.StartsWith(InventoryResources.CharacterAccountPrefix, StringComparison.Ordinal);

    /// <summary>
    /// 确定本帧记入的账户（主角色 ID 可作为判定账户的依据），对应关系全部从已保存数据推出：
    /// 1. 帧带游戏账户编号 A：键为 A 或别名 <see cref="InventorySavedAccount.GameAccountId"/> 为 A 的账户。
    /// 2. 找不到时按主角色 M：键为 <c>master-M</c> 或有角色记下同一主角色的账户，排除已带另一游戏账户编号的；有多个时取 <c>master-M</c>，否则不定。
    /// 3. 都找不到时新建，键依次取 A、<c>master-M</c>、帧里的账户 ID（<c>character-</c>）。
    /// 选定账户之外、同一主角色且没有其他游戏账户编号的账户并入选定账户（键记入 <paramref name="merged"/>）；选定账户不是 A 而帧带 A 时把 A 记为别名。
    /// </summary>
    private static InventoryIdentity Resolve(Dictionary<string, InventorySavedAccount> accounts, InventoryIdentity identity, HashSet<string> merged)
    {
        var master = identity.MasterCharacterId;
        var game = GameOf(identity.AccountId, null);
        var target = game is null ? null : accounts.ContainsKey(game) ? game : accounts.Values.FirstOrDefault(a => a.GameAccountId == game)?.AccountId;
        var masterKey = master is null ? null : InventoryResources.MasterAccountPrefix + master;
        var owners = master is null ? [] : accounts.Values.Where(a => !Fallback(a.AccountId) &&
            (a.AccountId == masterKey || a.Characters.Values.Any(c => c.Identity.MasterCharacterId == master))).ToList();
        if (target is null)
        {
            var compatible = owners.Where(a => game is null || GameOf(a) is null || GameOf(a) == game).Select(a => a.AccountId).ToList();
            target = compatible.Count == 1 ? compatible[0] : compatible.FirstOrDefault(id => id == masterKey);
        }
        if (target is not null)
        {
            var targetGame = game ?? GameOf(accounts[target]);
            foreach (var other in owners.Where(a => a.AccountId != target && (GameOf(a) is null || GameOf(a) == targetGame)).ToArray())
            { accounts[target] = Merge(accounts[target], accounts[other.AccountId]); accounts.Remove(other.AccountId); merged.Add(other.AccountId); }
        }
        target ??= game ?? masterKey ?? identity.AccountId;
        if (game is not null && target != game && accounts.TryGetValue(target, out var aliased) && aliased.GameAccountId is null)
            accounts[target] = aliased with { GameAccountId = game, Label = aliased.Label == aliased.AccountId ? game : aliased.Label };
        return identity with { AccountId = target };
    }

    /// <summary>账户的游戏账户编号：键本身（不是 <c>character-</c>、<c>master-</c> 时）或记下的别名。</summary>
    private static string? GameOf(InventorySavedAccount account) => GameOf(account.AccountId, account.GameAccountId);
    private static string? GameOf(string accountId, string? alias) => alias ??
        (Fallback(accountId) || accountId.StartsWith(InventoryResources.MasterAccountPrefix, StringComparison.Ordinal) ? null : accountId);

    /// <summary>
    /// 把 <paramref name="from"/> 并入 <paramref name="into"/>：同一角色取观察时间较新的一份（角色级资源逐项取较新值），公共保管箱取读取时间较新的一份，
    /// 账户级资源逐项取较新值，名称关联与角色槽以 <paramref name="into"/> 为准补入缺少的项。
    /// </summary>
    private static InventorySavedAccount Merge(InventorySavedAccount into, InventorySavedAccount from)
    {
        var characters = new Dictionary<string, InventorySavedCharacter>(into.Characters, StringComparer.Ordinal);
        foreach (var (id, character) in from.Characters)
            characters[id] = characters.TryGetValue(id, out var own)
                ? (character.LastObservedAt > own.LastObservedAt ? character : own) with { Resources = Newer(own.Resources, character.Resources) }
                : character;
        var sharedFromOther = from.SharedObservedAt is { } theirs && (into.SharedObservedAt is not { } ours || theirs > ours);
        var associations = new Dictionary<string, InventoryNameAssociation>(into.Associations, StringComparer.Ordinal);
        foreach (var (key, value) in from.Associations) associations.TryAdd(key, value);
        return into with
        {
            Characters = characters, Associations = associations, Resources = Newer(into.Resources, from.Resources),
            SharedItems = sharedFromOther ? from.SharedItems : into.SharedItems, SharedCoverage = sharedFromOther ? from.SharedCoverage : into.SharedCoverage,
            SharedObservedAt = sharedFromOther ? from.SharedObservedAt : into.SharedObservedAt, SharedSource = sharedFromOther ? from.SharedSource : into.SharedSource,
            Slots = [.. into.Slots, .. from.Slots.Where(slot => into.Slots.All(own => own.CharacterId != slot.CharacterId))],
            SlotsComplete = into.SlotsComplete && from.Slots.All(slot => into.Slots.Any(own => own.CharacterId == slot.CharacterId)),
            GameAccountId = into.GameAccountId ?? from.GameAccountId,
        };
    }

    /// <summary>
    /// 资源记入的账户：库存身份的账户；按角色归属的身份（<c>character-</c>）而历史里该角色已属于某个游戏账户时，记入那个账户。
    /// </summary>
    private static string ResourceAccount(IReadOnlyDictionary<string, InventorySavedAccount> accounts, InventoryIdentity identity) =>
        !Fallback(identity.AccountId) ? identity.AccountId
            : accounts.Values.FirstOrDefault(a => !Fallback(a.AccountId) && a.Characters.ContainsKey(identity.CharacterId))?.AccountId ?? identity.AccountId;

    /// <summary>
    /// 本帧的资源变化按归属范围记入：账户级记在资源账户上，角色级记在资源账户下该角色的记录上。游戏账户出现时，
    /// 同一角色保底身份下已有的两类资源逐项取观察时间较新的值并入，再清空那份，避免两份。角色级新值写入时，
    /// 账户下同一项的历史值（迁移时找不到所属角色的）随之移除。
    /// </summary>
    private static void ApplyResources(Dictionary<string, InventorySavedAccount> accounts, InventoryIdentity identity,
        IReadOnlyList<InventoryResourceChange>? changes, DateTimeOffset observedAt, bool login = false)
    {
        var target = ResourceAccount(accounts, identity);
        if (!accounts.TryGetValue(target, out var account) || !account.Characters.TryGetValue(identity.CharacterId, out var character)) return;
        var accountResources = account.Resources; var characterResources = character.Resources;
        var fallbackId = InventoryResources.CharacterAccountPrefix + identity.CharacterId;
        if (!Fallback(target) && accounts.TryGetValue(fallbackId, out var fallback))
        {
            var fallbackCharacter = fallback.Characters.GetValueOrDefault(identity.CharacterId);
            if (fallback.Resources is { Count: > 0 } || fallbackCharacter?.Resources is { Count: > 0 })
            {
                accountResources = Newer(accountResources, fallback.Resources);
                characterResources = Newer(characterResources, fallbackCharacter?.Resources);
                accounts[fallbackId] = fallback with { Resources = null, Characters = fallback.Characters.ToDictionary(p => p.Key,
                    p => p.Key == identity.CharacterId ? p.Value with { Resources = null } : p.Value, StringComparer.Ordinal) };
            }
        }
        var (accountChanges, characterChanges) = Split(changes);
        accountResources = MergeResources(accountResources, accountChanges, observedAt, identity.CharacterId, login);
        characterResources = MergeResources(characterResources, characterChanges, observedAt, identity.CharacterId, login);
        if (accountResources is not null && characterChanges.Any(c => c.Value is not null))
        {
            var replaced = characterChanges.Where(c => c.Value is not null).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            var kept = accountResources.Where(p => !replaced.Contains(p.Key) || InventoryResources.ScopeOf(p.Key) == InventoryResourceScope.Account)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            if (kept.Count != accountResources.Count) accountResources = kept;
        }
        accounts[target] = account with { Resources = accountResources,
            Characters = new Dictionary<string, InventorySavedCharacter>(account.Characters, StringComparer.Ordinal) { [identity.CharacterId] = character with { Resources = characterResources } } };
    }

    private static (IReadOnlyList<InventoryResourceChange> Account, IReadOnlyList<InventoryResourceChange> Character) Split(IReadOnlyList<InventoryResourceChange>? changes) =>
        changes is not { Count: > 0 } ? ([], [])
            : (changes.Where(c => InventoryResources.ScopeOf(c.Key) == InventoryResourceScope.Account).ToArray(),
               changes.Where(c => InventoryResources.ScopeOf(c.Key) != InventoryResourceScope.Account).ToArray());

    /// <summary>逐项合并两份资源：已读取的值取观察时间较新的一份，名称、说明与补充信息缺失时互补。</summary>
    private static IReadOnlyDictionary<string, InventorySavedResource>? Newer(IReadOnlyDictionary<string, InventorySavedResource>? first,
        IReadOnlyDictionary<string, InventorySavedResource>? second)
    {
        if (first is not { Count: > 0 }) return second is { Count: > 0 } ? second : first;
        if (second is not { Count: > 0 }) return first;
        var result = new Dictionary<string, InventorySavedResource>(first, StringComparer.Ordinal);
        foreach (var (key, value) in second)
        {
            if (!result.TryGetValue(key, out var existing)) { result[key] = value; continue; }
            var (winner, other) = (value.ObservedAt ?? DateTimeOffset.MinValue) > (existing.ObservedAt ?? DateTimeOffset.MinValue) ? (value, existing) : (existing, value);
            result[key] = winner with { RawName = winner.RawName ?? other.RawName, Description = winner.Description ?? other.Description,
                RefillIntervalSeconds = winner.RefillIntervalSeconds ?? other.RefillIntervalSeconds, RefillCap = winner.RefillCap ?? other.RefillCap,
                LastRefilledAt = Max(winner.LastRefilledAt, other.LastRefilledAt), RefillNotifiedAt = Max(winner.RefillNotifiedAt, other.RefillNotifiedAt) };
        }
        return result;
        static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;
    }

    /// <summary>
    /// 载入时按归属范围整理资源（只改内存，下次写入时持久化），覆盖此前两个版本的存法：
    /// 1. 记在账户上的角色级货币（上一版全部按账户）迁回该项“最近一次更新的角色”；该角色不在本账户时保留为账户下的历史值。
    /// 2. 记在角色上的账户级货币（更早一版全部按角色）逐项取较新值并入账户。
    /// 3. 保底身份 <c>character-&lt;角色 ID&gt;</c> 下的资源，若该角色已属于某个游戏账户，按同样的范围并入那个账户。
    /// </summary>
    private static InventorySavedState MigrateResources(InventorySavedState saved)
    {
        bool Misplaced(InventorySavedAccount a) =>
            a.Resources?.Keys.Any(k => InventoryResources.ScopeOf(k) != InventoryResourceScope.Account &&
                a.Resources[k].CharacterId is { } who && a.Characters.ContainsKey(who)) == true ||
            a.Characters.Values.Any(c => c.Resources?.Keys.Any(k => InventoryResources.ScopeOf(k) == InventoryResourceScope.Account) == true);
        if (!saved.Accounts.Values.Any(Misplaced) && !saved.Accounts.Keys.Any(id => Fallback(id) && saved.Accounts[id] is { } f &&
                (f.Resources is { Count: > 0 } || f.Characters.Values.Any(c => c.Resources is { Count: > 0 })) &&
                saved.Accounts.Values.Any(a => !Fallback(a.AccountId) && a.Characters.ContainsKey(id[InventoryResources.CharacterAccountPrefix.Length..]))))
            return saved;
        var accounts = new Dictionary<string, InventorySavedAccount>(saved.Accounts, StringComparer.Ordinal);
        foreach (var (id, account) in saved.Accounts)
        {
            var accountResources = new Dictionary<string, InventorySavedResource>(account.Resources ?? new Dictionary<string, InventorySavedResource>(), StringComparer.Ordinal);
            var characters = account.Characters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            foreach (var (key, value) in accountResources.ToArray())
                if (InventoryResources.ScopeOf(key) != InventoryResourceScope.Account && value.CharacterId is { } who && characters.TryGetValue(who, out var owner))
                {
                    characters[who] = owner with { Resources = Newer(owner.Resources, new Dictionary<string, InventorySavedResource> { [key] = value }) };
                    accountResources.Remove(key);
                }
            foreach (var (who, character) in characters.ToArray())
            {
                if (character.Resources is not { Count: > 0 } own) continue;
                var shared = own.Where(p => InventoryResources.ScopeOf(p.Key) == InventoryResourceScope.Account).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                if (shared.Count == 0) continue;
                foreach (var (key, value) in Newer(accountResources, shared)!) accountResources[key] = value;
                characters[who] = character with { Resources = own.Where(p => !shared.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) };
            }
            accounts[id] = account with { Resources = accountResources.Count == 0 ? null : accountResources, Characters = characters };
        }
        foreach (var id in accounts.Keys.Where(Fallback).ToArray())
        {
            var who = id[InventoryResources.CharacterAccountPrefix.Length..];
            var owner = accounts.Values.FirstOrDefault(a => !Fallback(a.AccountId) && a.Characters.ContainsKey(who));
            var fallback = accounts[id];
            var fallbackCharacter = fallback.Characters.GetValueOrDefault(who);
            if (owner is null || fallback.Resources is not { Count: > 0 } && fallbackCharacter?.Resources is not { Count: > 0 }) continue;
            var ownerCharacter = owner.Characters[who];
            accounts[owner.AccountId] = owner with { Resources = Newer(owner.Resources, fallback.Resources),
                Characters = new Dictionary<string, InventorySavedCharacter>(owner.Characters, StringComparer.Ordinal)
                { [who] = ownerCharacter with { Resources = Newer(ownerCharacter.Resources, fallbackCharacter?.Resources) } } };
            accounts[id] = fallback with { Resources = null, Characters = fallback.Characters.ToDictionary(p => p.Key,
                p => p.Key == who ? p.Value with { Resources = null } : p.Value, StringComparer.Ordinal) };
        }
        return saved with { Accounts = accounts };
    }

    private InventoryWorkspace ProjectWorkspace(InventoryWorkspace workspace, InventorySavedState saved)
    {
        var account = workspace.Identity is { } who ? saved.Accounts.GetValueOrDefault(who.AccountId) : null;
        return workspace with { Items = workspace.Items.Select(i => { var score = InventoryScores.Info(i.Item); return i with { Name = Name(i.Item, score, account), Score = score }; }).ToArray() };
    }
    /// <summary>条目的名称：乐谱有实例标题时为该标题（<see cref="InventoryScores.Name"/>），否则按类型关联。</summary>
    private InventoryNameAssociation Name(InventoryItem item, InventoryScoreInfo? score, InventorySavedAccount? account)
    {
        var byType = Name(item.Type, account);
        return InventoryScores.Name(item, score, byType.IsInstrument) ?? byType;
    }
    private InventoryNameAssociation Name(InventoryTypeKey type, InventorySavedAccount? account)
    {
        var snapshot = Catalog();
        InventoryCatalogEntry? entry;
        lock(catalogGate)
        {
            if (!ReferenceEquals(snapshot, indexedCatalog))
            { catalogIndex = (snapshot.Data?.Entries ?? []).ToDictionary(e => TypeKey(e.Type), StringComparer.Ordinal); indexedCatalog = snapshot; }
            catalogIndex.TryGetValue(TypeKey(type), out entry);
        }
        InventoryNameAssociation? manual = null;
        if (account?.Associations.TryGetValue(TypeKey(type), out manual) == true && manual.State.KnownValue is InventoryNameState.UserConfirmed or InventoryNameState.UserCleared or InventoryNameState.VerifiedSource)
            return manual with { IsInstrument = entry?.IsInstrument };
        return entry is not null ? new(type, entry.RawName, entry.DisplayName, InventoryNameState.ClientCatalog, snapshot.Data!.Id,
            entry.Table + ":" + entry.Row, entry.IsInstrument) : manual ?? Unknown(type);
    }
}

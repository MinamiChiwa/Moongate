// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.Inventory.Domain;

namespace MabiTool.Modules.Inventory.Adapters;

public sealed partial class InventoryHistoryStore
{
    public InventoryItemPage Items(InventoryItemQuery? query = null)
    {
        query ??= new(); ValidatePage(query.PageSize, 1000);
        var snapshot = state;
        var currentKeys=snapshot.Workspace?.Items.Where(i=>i.Current).Select(i=>ItemKey(i.Item)).ToHashSet(StringComparer.Ordinal)??[];
        IEnumerable<InventoryItemView> items;
        if (query.CurrentOnly)
            items = Workspace() is { } current && (query.AccountId is null || query.AccountId == current.Identity?.AccountId) && (query.CharacterId is null || query.CharacterId == current.Identity?.CharacterId) ? current.Items.Where(i => i.Current).Select(i => i with { CharacterId = ViewCharacter(i.Item, current.Identity?.CharacterId) }) : [];
        else
            items = snapshot.Accounts.Values.Where(a => query.AccountId is null || a.AccountId == query.AccountId)
                .SelectMany(a => a.Characters.Values.Where(c => query.CharacterId is null || c.Identity.CharacterId == query.CharacterId)
                    .SelectMany(c => c.Items.Select(i => HistoricalView(i, c.LastObservedAt, a, c.Identity.CharacterId, snapshot, currentKeys, c.Source)))
                    .Concat(a.SharedItems.Select(i => HistoricalView(i, a.SharedObservedAt ?? DateTimeOffset.MinValue, a, null, snapshot, currentKeys, a.SharedSource))));
        var filtered = items.Where(i => (query.Container is null || query.Container.Value == i.Item.Container) &&
            (query.Category is null || query.Category == i.Item.Category) && Search(i.Name.DisplayName ?? i.Item.TypeId.ToString(), query.Search))
            .OrderBy(i => i.Item.Category, StringComparer.Ordinal).ThenBy(i => i.Item.Container.RawValue, StringComparer.Ordinal).ThenBy(i => i.Item.Key, StringComparer.Ordinal).ToArray();
        var cursor = query with { Cursor = null }; var offset = Offset(query.Cursor, snapshot.Revision, cursor);
        var page = filtered.Skip(offset).Take(query.PageSize).ToArray();
        return new(page, offset + page.Length < filtered.Length ? Cursor(offset + page.Length, snapshot.Revision, cursor) : null, snapshot.Revision);
    }
    public InventoryAccountPage Accounts(InventoryAccountQuery? query = null)
    {
        query ??= new(); ValidatePage(query.PageSize, 100);
        var snapshot = state; var cursor = query with { Cursor = null }; var offset = Offset(query.Cursor, snapshot.Revision, cursor);
        var accounts = snapshot.Accounts.Values.OrderBy(a => a.AccountId, StringComparer.Ordinal).ToArray();
        var page = accounts.Skip(offset).Take(query.PageSize).Select(a => AccountView(a, snapshot)).ToArray();
        return new(page, offset + page.Length < accounts.Length ? Cursor(offset + page.Length, snapshot.Revision, cursor) : null, snapshot.Revision);
    }
    public InventoryCatalogPage CatalogQuery(InventoryCatalogQuery? query = null)
    {
        query ??= new(); ValidatePage(query.PageSize, 1000); var snapshot = Catalog();
        var filtered = (snapshot.Data?.Entries ?? []).Where(e => (query.Category is null || e.Type.Category == query.Category) && Search(e.DisplayName, query.Search))
            .OrderBy(e => e.Type.Category, StringComparer.Ordinal).ThenBy(e => e.Type.TypeId.Value).ToArray();
        var cursor = query with { Cursor = null }; var offset = Offset(query.Cursor, snapshot.Revision, cursor);
        var page = filtered.Skip(offset).Take(query.PageSize).ToArray();
        return new(page, offset + page.Length < filtered.Length ? Cursor(offset + page.Length, snapshot.Revision, cursor) : null, snapshot.Revision, snapshot.Data?.Categories ?? []);
    }
    public InventoryAssociationSnapshot Associations(string accountId)
    {
        var snapshot = state; var account = Account(accountId, snapshot);
        var observed = account.Characters.Values.SelectMany(c => c.Items).Concat(account.SharedItems).Select(i => i.Type)
            .DistinctBy(TypeKey).OrderBy(t => t.Category, StringComparer.Ordinal).ThenBy(t => t.TypeId.Value).ToArray();
        return new(accountId, snapshot.Revision, observed.Select(t => Name(t, account)).ToArray());
    }
    public IReadOnlyList<InventoryTypeKey> ResolveName(string accountId, string name)
    {
        var normalized = NormalizeName(name);
        return Associations(accountId).Items.Where(a => a.State.KnownValue is InventoryNameState.UserConfirmed or InventoryNameState.VerifiedSource or InventoryNameState.ClientCatalog)
            .Where(a => a.DisplayName is not null && NormalizeName(a.DisplayName) == normalized).Select(a => a.Type).ToArray();
    }
    private InventoryAccountView AccountView(InventorySavedAccount account, InventorySavedState snapshot)
    {
        var workspace = snapshot.Workspace;
        var characters = account.Characters.Values.OrderBy(c => c.Identity.CharacterId, StringComparer.Ordinal)
            .Select(c => new InventoryCharacterView(c.Identity, c.LastObservedAt, c.Coverage,
                workspace is { Current: true } && workspace.Identity?.AccountId == account.AccountId && workspace.Identity.CharacterId == c.Identity.CharacterId, c.Gold)).ToArray();
        var all = account.Characters.Values.SelectMany(c => c.Items).Concat(account.SharedItems);
        var aggregates = all.GroupBy(i => TypeKey(i.Type), StringComparer.Ordinal).Select(group =>
        {
            var type = group.First().Type;
            ulong amount = 0; foreach (var item in group) amount = checked(amount + (item.Quantity?.Value ?? 0));
            var relevantCharacters = account.SlotsComplete ? account.Slots.Select(s => s.CharacterId).ToArray() : account.Characters.Keys.ToArray();
            var complete = account.SlotsComplete && relevantCharacters.Length > 0 && relevantCharacters.All(id => account.Characters.TryGetValue(id, out var c) &&
                Enum.GetValues<InventoryContainer>().Where(container => container != InventoryContainer.AccountStorage).All(container =>
                    c.Coverage.Any(cover => cover.Category == type.Category && cover.Container.KnownValue == container && cover.Complete))) &&
                account.SharedCoverage.Any(cover => cover.Category == type.Category && cover.Container.KnownValue == InventoryContainer.AccountStorage && cover.Complete) &&
                group.All(i => i.Quantity is not null);
            return new InventoryAggregate(type, new(amount), complete ? new UInt64String(amount) : null, complete, Name(type, account));
        }).OrderBy(a => a.Type.Category, StringComparer.Ordinal).ThenBy(a => a.Type.TypeId.Value).ToArray();
        return new(account.AccountId, account.Label, snapshot.Revision, characters, account.Slots, account.SlotsComplete, aggregates,
            account.SharedCoverage, account.SharedObservedAt);
    }
    private InventoryItemView HistoricalView(InventoryItem item, DateTimeOffset observedAt, InventorySavedAccount account, string? character, InventorySavedState snapshot, HashSet<string> currentKeys, ObservationSource? source)
    {
        var current = snapshot.Workspace is { Current: true } workspace && workspace.Identity?.AccountId == account.AccountId &&
            (character is null || workspace.Identity.CharacterId == character) && currentKeys.Contains(ItemKey(item));
        return new(item, Name(item.Type, account), current, observedAt, current ? ObservationQuality.Confirmed : ObservationQuality.Stale, source,
            ViewCharacter(item, character));
    }
    /// <summary>公共保管箱条目不属于任何角色，CharacterId 为空。</summary>
    private static string? ViewCharacter(InventoryItem item, string? character) => item.Container.KnownValue == InventoryContainer.AccountStorage ? null : character;
    private static InventorySavedAccount Account(string accountId, InventorySavedState saved) => saved.Accounts.TryGetValue(accountId, out var account)
        ? account : throw new OperationException("resource.notFound", "库存账户不存在");
    private static bool Search(string value, string? search) => string.IsNullOrWhiteSpace(search) || value.Contains(search, StringComparison.OrdinalIgnoreCase);
    private static void ValidatePage(int size, int maximum) { if (size is < 1 || size > maximum) throw new OperationException("request.invalid", "分页大小超出范围"); }
    private string Cursor<T>(int offset, UInt64String revision, T query)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { epoch, revision = revision.ToString(), offset, query }, ContractJson.Options);
        return Convert.ToBase64String(payload) + "." + Convert.ToHexStringLower(HMACSHA256.HashData(cursorKey, payload));
    }
    private int Offset<T>(string? cursor, UInt64String revision, T query)
    {
        if (cursor is null) return 0;
        try
        {
            if (cursor.Length > 8192) throw new FormatException();
            var pieces = cursor.Split('.'); if (pieces.Length != 2) throw new FormatException();
            var bytes = Convert.FromBase64String(pieces[0]); var mac = Convert.FromHexString(pieces[1]);
            if (!CryptographicOperations.FixedTimeEquals(mac, HMACSHA256.HashData(cursorKey, bytes))) throw new FormatException();
            using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
            if (root.GetProperty("epoch").GetString() != epoch || root.GetProperty("revision").GetString() != revision.ToString() ||
                !JsonElement.DeepEquals(root.GetProperty("query"), JsonSerializer.SerializeToElement(query, ContractJson.Options)))
                throw new FormatException();
            var offset = root.GetProperty("offset").GetInt32(); if (offset < 0) throw new FormatException(); return offset;
        }
        catch (Exception error) when (error is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new OperationException("inventory.planStale", "库存分页游标已过期或不匹配查询"); }
    }
    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Normalize(NormalizationForm.FormKC).Trim();
        if (normalized.Length > 160 || normalized.Any(char.IsControl)) throw new OperationException("request.invalid", "道具名称超出范围");
        return normalized;
    }
}

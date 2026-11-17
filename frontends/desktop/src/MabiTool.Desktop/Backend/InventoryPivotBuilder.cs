using MabiTool.Contracts;
using MabiTool.Desktop.Pages.Inventory;

namespace MabiTool.Desktop.Backend;

/// <summary>一个账号读到的道具：每个角色的背包与角色保管箱道具，以及账号共用的公共保管箱道具。</summary>
public sealed record InventoryAccountItems(InventoryAccountView Account,
    IReadOnlyDictionary<string, IReadOnlyList<InventoryItemView>> ByCharacter, IReadOnlyList<InventoryItemView> Shared);

/// <summary>
/// 背包透视表的读取与汇总：按道具类型键（类别 + 类型编号）一行，带实例元数据的乐谱每张一行；每个角色的背包、角色保管箱各一列。
/// </summary>
public static class InventoryPivotBuilder
{
    public const int PageSize = 1000;

    /// <summary>
    /// 读取一个账号的全部道具：不带角色编号按账号查询一次，按 <c>NextCursor</c> 读完。条目的 <c>CharacterId</c>
    /// 标明所属角色，公共保管箱条目为空；按它分列到各角色与公共保管箱。
    /// </summary>
    public static async Task<InventoryAccountItems> ReadAsync(InventoryAccountView account,
        Func<InventoryItemQuery, CancellationToken, Task<InventoryItemPage>> query, CancellationToken token)
    {
        var byCharacter = new Dictionary<string, List<InventoryItemView>>(StringComparer.Ordinal);
        var shared = new List<InventoryItemView>();
        await foreach (var item in ReadAllAsync(new InventoryItemQuery(account.AccountId, PageSize: PageSize), query, token))
        {
            if (item.Item.Container.KnownValue == InventoryContainer.AccountStorage || item.CharacterId is not { Length: > 0 } character)
                shared.Add(item);
            else if (byCharacter.TryGetValue(character, out var list)) list.Add(item);
            else byCharacter[character] = [item];
        }
        return new(account, byCharacter.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<InventoryItemView>)pair.Value, StringComparer.Ordinal),
            shared);
    }

    private static async IAsyncEnumerable<InventoryItemView> ReadAllAsync(InventoryItemQuery first,
        Func<InventoryItemQuery, CancellationToken, Task<InventoryItemPage>> query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var next = first;
        while (true)
        {
            var page = await query(next, token);
            foreach (var item in page.Items) yield return item;
            if (page.NextCursor is not { Length: > 0 } cursor) yield break;
            next = first with { Cursor = cursor };
        }
    }

    public static string TypeKey(InventoryItem item) => item.Category + ":" + item.TypeId;

    /// <summary>
    /// 行键：带乐谱元数据（<c>Score</c>）且有实例 ID 的乐谱按实例成行（类别:表编号:实例 ID），各张显示自己的谱名；其余按类型键汇总。
    /// </summary>
    public static string RowKey(InventoryItemView view) =>
        view.Score is not null && view.Item.InstanceId is { } instance ? TypeKey(view.Item) + ":" + instance : TypeKey(view.Item);

    /// <summary>
    /// 汇总成透视表。列的角色顺序：已读取的角色按后端顺序，之后是角色名单里尚未读取的角色。
    /// 一个格子：有这件道具时为数量之和（任一条数量未知则为“未知”）；没有时，该角色该处已完整读取（覆盖里对应类别与容器
    /// <c>Complete</c>）为 0，否则为“未读取”。公共保管箱按账号视图的 <c>SharedCoverage</c> 判定（任何账号都适用）：
    /// 对应类别 <c>Complete</c> 时没有的道具为 0；从未读取（空）或该类别不完整时为“未读取”。
    /// 合计是已知数量之和，有不确知的格子时标为部分。
    /// </summary>
    public static (IReadOnlyList<InventoryCharacter> Characters, IReadOnlyList<InventoryPivotRow> Rows) Build(InventoryAccountItems items)
    {
        var account = items.Account;
        var sharedCoverage = account.SharedCoverage;
        var characters = account.Characters.Select(c => (Id: c.Identity.CharacterId, Name: c.Identity.RawName, Coverage: c.Coverage))
            .Concat(account.Slots.Where(slot => account.Characters.All(c => c.Identity.CharacterId != slot.CharacterId))
                .Select(slot => (Id: slot.CharacterId, Name: slot.RawName, Coverage: (IReadOnlyList<InventoryCoverage>)[])))
            .ToList();
        var all = items.ByCharacter.Values.SelectMany(list => list).Concat(items.Shared).ToList();
        var rows = all.GroupBy(RowKey, StringComparer.Ordinal).Select(group =>
        {
            var first = group.First();
            var category = first.Item.Category;
            var name = first.Name.RawName ?? first.Name.DisplayName;
            bool Complete(IReadOnlyList<InventoryCoverage>? coverage, InventoryContainer container) =>
                coverage?.Any(cover => cover.Category == category && cover.Container.KnownValue == container && cover.Complete) == true;
            IEnumerable<InventoryItemView> Of(string character, InventoryContainer container) =>
                items.ByCharacter.TryGetValue(character, out var list)
                    ? list.Where(view => RowKey(view) == group.Key && view.Item.Container.KnownValue == container)
                    : [];
            var backpack = characters.Select(c => Cell(Of(c.Id, InventoryContainer.Backpack), Complete(c.Coverage, InventoryContainer.Backpack))).ToList();
            var storage = characters.Select(c => Cell(Of(c.Id, InventoryContainer.CharacterStorage), Complete(c.Coverage, InventoryContainer.CharacterStorage))).ToList();
            var shared = Cell(items.Shared.Where(view => RowKey(view) == group.Key), Complete(sharedCoverage, InventoryContainer.AccountStorage));
            var cells = backpack.Concat(storage).Append(shared).ToList();
            ulong sum = 0;
            foreach (var cell in cells) sum += cell.Quantity ?? 0;
            var total = new InventoryCount(sum, cells.All(cell => cell.State == InventoryCountState.Value) ? InventoryCountState.Value : InventoryCountState.Partial);
            // 名称来源按已知枚举名给出（线路值为 kebab-case，界面按枚举名对照文字）。
            return new InventoryPivotRow(group.Key, name, first.Item.TypeId.ToString(),
                name is null ? null : first.Name.State.KnownValue?.ToString() ?? first.Name.State.RawValue,
                category, total, shared, backpack, storage, group.Key == TypeKey(first.Item) ? null : first.Score);
        }).OrderBy(row => row.Category, StringComparer.Ordinal).ThenBy(row => row.Name, StringComparer.Ordinal).ToList();
        return (characters.Select(c => new InventoryCharacter(c.Id, c.Name)).ToList(), rows);
    }

    private static InventoryCount Cell(IEnumerable<InventoryItemView> views, bool complete)
    {
        var list = views.ToList();
        if (list.Count == 0) return complete ? new InventoryCount(0, InventoryCountState.Value) : InventoryCount.NotRead;
        if (list.Any(view => view.Item.Quantity is null)) return new InventoryCount(null, InventoryCountState.Unknown);
        ulong sum = 0;
        foreach (var view in list) sum += view.Item.Quantity!.Value.Value;
        return new InventoryCount(sum, InventoryCountState.Value);
    }
}

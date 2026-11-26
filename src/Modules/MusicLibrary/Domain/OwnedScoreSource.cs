using System.Text;
using MabiTool.Contracts;

namespace MabiTool.Modules.MusicLibrary.Domain;

/// <summary>抓包来源一次选择的结果：入库的条目，以及被归属规则或标题校验排除的数量。</summary>
/// <param name="GameProvided">表编号非 0 的乐谱（游戏提供），由游戏列表来源提供，抓包来源不收录。</param>
/// <param name="Unusable">标题为空、含控制字符、过长或没有有效实例 ID 而跳过的实例。</param>
public sealed record OwnedScoreSelection(IReadOnlyList<ScoreSourceItem> Items, int GameProvided, int Unusable);

/// <summary>
/// 乐谱库的抓包来源：背包抓包读到的 <c>MusicNoteItem</c> 实例按实例生成条目。
/// 归属按表编号划分：0 是玩家自制与复制的乐谱（命令行 <c>get_music_scores</c> 不会列出），全部入库；
/// 非 0 是游戏提供的乐谱（命令行列表里的那类），只由游戏列表来源提供，这里不收录，避免同一张乐谱出现两条。
/// 命令行列表只有标题与存放位置，没有表编号或实例 ID，两边的条目无法逐张对上，所以按类别分流而不是按标题去重。
/// </summary>
public static class OwnedScoreSource
{
    public const string Provider = "tw-capture";
    public const string RealmId = "capture";
    public const int TitleLimit = 4096;

    /// <summary>当前库存身份角色的来源范围；角色标识必须是无符号十进制数。</summary>
    public static ScoreScope ScopeFor(string characterId) => new("TW", RealmId, characterId, Provider, ScoreSourceKind.Inventory);

    /// <summary>表编号非 0 的乐谱是游戏提供的乐谱；玩家自制与复制的乐谱表编号为 0。</summary>
    public static bool IsGameProvided(MusicOwnedScore row) => row.Type.TypeId.Value != 0;

    public static OwnedScoreSelection Select(IEnumerable<MusicOwnedScore> rows)
    {
        var items = new List<ScoreSourceItem>();
        var seen = new HashSet<ulong>();
        int gameProvided = 0, unusable = 0;
        foreach (var row in rows.OrderBy(row => row.InstanceId?.Value ?? 0).ThenBy(row => row.Key, StringComparer.Ordinal))
        {
            if (IsGameProvided(row)) { gameProvided++; continue; }
            if (row.InstanceId is not { Value: > 0 } instance || !UsableTitle(row.RawTitle) || !seen.Add(instance.Value)) { unusable++; continue; }
            items.Add(new(row.RawTitle!, instance.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        return new(items, gameProvided, unusable);
    }

    private static bool UsableTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > TitleLimit || title.Any(char.IsControl)) return false;
        try { _ = new UTF8Encoding(false, true).GetByteCount(title); return true; }
        catch (EncoderFallbackException) { return false; }
    }
}

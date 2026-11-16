using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MabiTool.Modules.MusicLibrary.Domain;

public enum ScoreSourceKind { Inventory, ObservationSummary, ImportedMml, ImportedLibrary }
public enum ScoreRefreshStatus { Complete, Partial, Failed }
public enum ScoreAvailability { Present, Missing, Uncertain, Observed, Imported }

public sealed record ScoreScope(string Region, string RealmId, string CharacterId, string Provider, ScoreSourceKind Kind)
{
    // 长度分隔由 JSON 保证，避免服务器或 Provider 名称中的分隔符造成范围碰撞。
    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
        new[] { Region, RealmId, CharacterId, Provider, Kind.ToString() }))));
}

public sealed record ScoreSourceItem(string RawTitle, string? StableSourceId = null);
public sealed record CatalogScore(string Id, string RawTitle, string? StableSourceId, string CandidateGroup,
    bool Ambiguous, ScoreAvailability Availability);
public sealed record ScoreCandidateGroup(string Id, string RawTitle, int ReportedCount, bool CountIsExact,
    IReadOnlyList<string> CandidateIds);
public sealed record ScoreCatalogSnapshot(ScoreScope Scope, ulong Revision, string HostEpoch,
    DateTimeOffset UpdatedAt, DateTimeOffset? LastCompleteAt, ScoreRefreshStatus Status,
    IReadOnlyList<CatalogScore> Entries, IReadOnlyList<ScoreCandidateGroup> Groups)
{
    public bool IsStale(string hostEpoch, DateTimeOffset now, TimeSpan maximumAge) =>
        HostEpoch != hostEpoch || Status != ScoreRefreshStatus.Complete || now < UpdatedAt || now - UpdatedAt > maximumAge;
}

public static class ScoreCatalog
{
    public const int MaximumEntries = 20_000;

    public static ScoreCatalogSnapshot Refresh(ScoreScope scope, ScoreCatalogSnapshot? previous,
        IReadOnlyList<ScoreSourceItem> items, ScoreRefreshStatus status, string hostEpoch, DateTimeOffset now)
    {
        ValidateScope(scope);
        if (!Enum.IsDefined(status)) throw new ArgumentException("刷新状态无效");
        ValidateText(hostEpoch, 128);
        if (previous is not null && previous.Scope != scope) throw new ArgumentException("乐谱来源范围不一致");
        if (items.Count > MaximumEntries) throw new ArgumentException("单来源乐谱数量超过上限");
        // 固定输入快照，序号和标题规范化结果均不充当来源身份。
        var input = items.ToArray();
        long characters = 0;
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in input)
        {
            ValidateText(item.RawTitle, 4096);
            characters += item.RawTitle.Length;
            if (item.StableSourceId is not null)
            {
                ValidateText(item.StableSourceId, 256);
                if (!sourceIds.Add(item.StableSourceId)) throw new ArgumentException("来源返回重复物品 ID");
            }
        }
        if (characters > 1024 * 1024) throw new ArgumentException("乐谱标题总量超过上限");
        if (status == ScoreRefreshStatus.Failed && input.Length != 0) throw new ArgumentException("失败刷新不能携带有效条目");

        var revision = checked((previous?.Revision ?? 0) + 1);
        if (status == ScoreRefreshStatus.Failed)
            return new(scope, revision, hostEpoch, now, previous?.LastCompleteAt, status,
                previous?.Entries ?? [], previous?.Groups ?? []);

        var old = previous?.Entries ?? [];
        var bySource = old.Where(x => x.StableSourceId is not null).ToDictionary(x => x.StableSourceId!, StringComparer.Ordinal);
        var oldByTitle = old.GroupBy(x => x.RawTitle, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var incoming = input.GroupBy(x => x.RawTitle, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var result = old.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var fullInventory = status == ScoreRefreshStatus.Complete && scope.Kind == ScoreSourceKind.Inventory;
        foreach (var (title, rows) in incoming)
        {
            var prior = oldByTitle.GetValueOrDefault(title) ?? [];
            var group = prior.FirstOrDefault()?.CandidateGroup ?? Guid.NewGuid().ToString("N");
            foreach (var row in rows.Where(x => x.StableSourceId is not null))
            {
                var entry = bySource.GetValueOrDefault(row.StableSourceId!);
                var id = entry?.Id ?? Guid.NewGuid().ToString("N");
                result[id] = new(id, title, row.StableSourceId, group, false, Positive(scope.Kind));
                seen.Add(id);
            }
            var anonymousCount = rows.Count(x => x.StableSourceId is null);
            var candidates = prior.Where(x => x.StableSourceId is null).ToList();
            while (candidates.Count < anonymousCount)
            {
                var id = Guid.NewGuid().ToString("N");
                candidates.Add(new(id, title, null, group, true, ScoreAvailability.Uncertain));
            }
            foreach (var entry in candidates)
            {
                // 历史引用保留；不从两个候选中任意挑选一个作为本次存活副本。
                if (anonymousCount == 0) continue;
                result[entry.Id] = entry with { CandidateGroup = group, Ambiguous = true,
                    Availability = scope.Kind == ScoreSourceKind.Inventory ? ScoreAvailability.Uncertain : Positive(scope.Kind) };
                seen.Add(entry.Id);
            }
        }
        if (fullInventory)
            foreach (var entry in result.Values.ToArray())
                if (!seen.Contains(entry.Id)) result[entry.Id] = entry with { Availability = ScoreAvailability.Missing };
        if (result.Count > MaximumEntries) throw new ArgumentException("来源历史条目达到上限，须显式整理后刷新");

        var priorGroups = (previous?.Groups ?? []).ToDictionary(x => x.RawTitle, StringComparer.Ordinal);
        var groups = result.Values.GroupBy(x => x.RawTitle, StringComparer.Ordinal).Select(g =>
        {
            var count = incoming.GetValueOrDefault(g.Key)?.Length;
            var previousGroup = priorGroups.GetValueOrDefault(g.Key);
            return new ScoreCandidateGroup(g.First().CandidateGroup, g.Key, count ?? (fullInventory ? 0 : previousGroup?.ReportedCount ?? 0),
                fullInventory, g.Select(x => x.Id).Order(StringComparer.Ordinal).ToArray());
        }).OrderBy(x => x.RawTitle, StringComparer.Ordinal).ToArray();
        return new(scope, revision, hostEpoch, now, status == ScoreRefreshStatus.Complete ? now : previous?.LastCompleteAt,
            status, result.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray(), groups);
    }

    private static ScoreAvailability Positive(ScoreSourceKind kind) => kind switch
    {
        ScoreSourceKind.Inventory => ScoreAvailability.Present,
        ScoreSourceKind.ObservationSummary => ScoreAvailability.Observed,
        _ => ScoreAvailability.Imported
    };

    public static void ValidateScope(ScoreScope scope)
    {
        if (scope.Region is not ("TW" or "KR") || !Enum.IsDefined(scope.Kind)) throw new ArgumentException("乐谱地区或来源类型无效");
        ValidateText(scope.RealmId, 128);
        ValidateText(scope.Provider, 128);
        MabiTool.Contracts.UInt64String.Parse(scope.CharacterId);
    }

    private static void ValidateText(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)) throw new ArgumentException("乐谱来源字段无效或超过上限");
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException) { throw new ArgumentException("乐谱来源字段包含无效 Unicode"); }
    }
}

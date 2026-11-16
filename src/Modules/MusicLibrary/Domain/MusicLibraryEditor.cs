using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Modules.MusicLibrary.Domain;

public sealed record MusicLibraryState(UInt64String Revision, IReadOnlyList<MusicFolder> Folders,
    IReadOnlyList<MusicPlaylist> Playlists, MusicNameSettings NameSettings)
{
    public static MusicLibraryState Empty { get; } = new(new(0), [], [], new([], new Dictionary<string, string>(), []));
    public MusicLibraryInfo Info => new(Revision, Folders.Count, Playlists.Count, Playlists.Sum(p => p.Items.Count), NameSettings, []);
}

public static class MusicLibraryEditor
{
    public const int MaximumFolders = 500;
    public const int MaximumPlaylists = 500;
    public const int MaximumItems = 20_000;
    public const int MaximumBatchChanges = 128;

    // 标准目录只描述稳定乐器身份与显示别名；持有/装备状态来自来源同步，不能由列表偏好推断。
    public static IReadOnlyList<InstrumentDefinition> InstrumentCatalog { get; } = Array.AsReadOnly(new InstrumentDefinition[] {
        new("piano", new Dictionary<string, string> { ["zh-TW"] = "鋼琴", ["ko-KR"] = "피아노", ["en-US"] = "Piano" }, ["鋼琴", "피아노", "Piano"]),
        new("violin", new Dictionary<string, string> { ["zh-TW"] = "小提琴", ["ko-KR"] = "바이올린", ["en-US"] = "Violin" }, ["小提琴", "바이올린", "Violin"]),
        new("guitar", new Dictionary<string, string> { ["zh-TW"] = "吉他", ["ko-KR"] = "기타", ["en-US"] = "Guitar" }, ["吉他", "기타", "Guitar"]),
        new("flute", new Dictionary<string, string> { ["zh-TW"] = "長笛", ["ko-KR"] = "플루트", ["en-US"] = "Flute" }, ["長笛", "플루트", "Flute"]),
        new("harp", new Dictionary<string, string> { ["zh-TW"] = "豎琴", ["ko-KR"] = "하프", ["en-US"] = "Harp" }, ["豎琴", "하프", "Harp"])
    });

    public static (MusicLibraryState State, MusicEditResult Result) Apply(MusicLibraryState previous, MusicEditBatch request,
        IReadOnlySet<string> knownScores)
        => ApplyCore(previous, request, knownScores, false);

    public static (MusicLibraryState State, MusicEditResult Result) MergeImported(MusicLibraryState previous,
        IReadOnlyList<MusicFolder> folders, IReadOnlyList<MusicPlaylist> playlists, MusicNameSettings settings,
        IReadOnlySet<string> knownScores)
        => ApplyCore(previous, new(folders, [], playlists, [], settings), knownScores, true);

    private static (MusicLibraryState State, MusicEditResult Result) ApplyCore(MusicLibraryState previous, MusicEditBatch request,
        IReadOnlySet<string> knownScores, bool importing)
    {
        if (request.PutFolders.Any(x => x is null) || request.PutPlaylists.Any(x => x is null || x.Items.Any(i => i is null))) Invalid("曲库数组不能包含空元素");
        var count = request.PutFolders.Count + request.DeleteFolders.Count + request.PutPlaylists.Count + request.DeletePlaylists.Count;
        if (!importing && count > MaximumBatchChanges || count == 0 && request.NameSettings is null) Invalid("批量编辑需要 1–128 项变更或名称设置");
        var folderIds = request.PutFolders.Select(x => x.Id).Concat(request.DeleteFolders).ToArray();
        var playlistIds = request.PutPlaylists.Select(x => x.Id).Concat(request.DeletePlaylists).ToArray();
        Unique(folderIds); Unique(playlistIds);
        foreach (var id in folderIds.Concat(playlistIds)) Id(id);
        var folders = previous.Folders.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var playlists = previous.Playlists.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var folder in request.PutFolders) folders[folder.Id] = folder;
        foreach (var playlist in request.PutPlaylists) playlists[playlist.Id] = playlist;
        foreach (var id in request.DeletePlaylists)
            if (!playlists.Remove(id)) Missing("播放列表不存在");
        // 先确定所有被删除文件夹的最近保留祖先，再移动直属项；结果不依赖删除数组次序。
        var deleting = request.DeleteFolders.ToHashSet(StringComparer.Ordinal);
        foreach (var id in deleting) if (!folders.ContainsKey(id)) Missing("文件夹不存在");
        string? SurvivingParent(string? id)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (id is not null && deleting.Contains(id))
            {
                if (!visited.Add(id)) Invalid("文件夹存在循环引用");
                id = folders[id].ParentId;
            }
            return id;
        }
        foreach (var folder in folders.Values.ToArray())
            if (!deleting.Contains(folder.Id)) folders[folder.Id] = folder with { ParentId = SurvivingParent(folder.ParentId) };
        foreach (var playlist in playlists.Values.ToArray())
            playlists[playlist.Id] = playlist with { FolderId = SurvivingParent(playlist.FolderId) };
        foreach (var id in deleting) folders.Remove(id);
        if (folders.Count > MaximumFolders || playlists.Count > MaximumPlaylists || playlists.Values.Sum(x => x.Items.Count) > MaximumItems)
            Capacity("曲库文件夹、列表或条目超过上限");

        foreach (var folder in folders.Values)
        {
            Text(folder.Name, 200); Text(folder.SortKey, 128);
            var visited = new HashSet<string>(StringComparer.Ordinal) { folder.Id };
            var parent = folder.ParentId;
            while (parent is not null)
            {
                if (!visited.Add(parent)) Invalid("文件夹存在循环引用");
                if (!folders.TryGetValue(parent, out var ancestor)) Missing("父文件夹不存在");
                parent = ancestor!.ParentId;
            }
        }
        var allItems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var playlist in playlists.Values.ToArray())
        {
            Text(playlist.Name, 200); Text(playlist.Notes, 4000, allowEmpty: true, multiline: true); Text(playlist.SortKey, 128);
            OptionalInstrument(playlist.DefaultInstrumentId);
            if (playlist.FolderId is not null && !folders.ContainsKey(playlist.FolderId)) Missing("列表所属文件夹不存在");
            foreach (var item in playlist.Items)
            {
                Id(item.Id); Id(item.ScoreId); Text(item.SortKey, 128); OptionalInstrument(item.InstrumentId);
                if (!allItems.Add(item.Id)) Invalid("列表项 ID 必须全库唯一");
                if (!knownScores.Contains(item.ScoreId)) Missing("列表引用的本地乐谱不存在");
            }
            // 相同排序键用条目 ID 确定顺序；重复 ScoreId 保留为不同列表项。
            playlists[playlist.Id] = playlist with { Items = playlist.Items.OrderBy(x => x.SortKey, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray() };
        }
        var settings = request.NameSettings ?? previous.NameSettings;
        if (settings.Artists.Any(x => x is null || x.Aliases.Any(a => a is null)) || settings.Noise.Any(x => x is null)) Invalid("名称设置不能包含空元素");
        foreach (var id in settings.Assignments.Keys) if (!knownScores.Contains(id)) Missing("歌手指定引用的乐谱不存在");
        // 复用名称规则的别名冲突、手工归属、Unicode 和容量校验。
        _ = ScoreNameRules.Classify([], new(ScoreRegion.TW, settings.Artists.Select(x => new ArtistDefinition(x.Id, x.Name, x.Aliases)).ToArray(),
            settings.Assignments, settings.Noise));
        foreach (var artist in settings.Artists) Id(artist.Id);
        var revision = new UInt64String(checked(previous.Revision.Value + 1));
        var next = new MusicLibraryState(revision,
            folders.Values.OrderBy(x => x.SortKey, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray(),
            playlists.Values.OrderBy(x => x.SortKey, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray(), settings);
        // 删除父级造成的隐式移动也必须出现在通知中。
        var changedFolders = folderIds.Concat(next.Folders.Where(f => previous.Folders.Any(p => p.Id == f.Id && p.ParentId != f.ParentId)).Select(f => f.Id)).Distinct().Order(StringComparer.Ordinal).ToArray();
        var changedPlaylists = playlistIds.Concat(next.Playlists.Where(f => previous.Playlists.Any(p => p.Id == f.Id && p.FolderId != f.FolderId)).Select(f => f.Id)).Distinct().Order(StringComparer.Ordinal).ToArray();
        return (next, new(revision, changedFolders, changedPlaylists, request.NameSettings is not null));
    }

    public static void Id(string value)
    {
        if (value is null || !Regex.IsMatch(value, @"\A[a-zA-Z0-9][a-zA-Z0-9._:-]{0,127}\z")) Invalid("曲库资源 ID 无效");
    }
    private static void OptionalInstrument(string? value)
    {
        if (value is null) return;
        Text(value, 128);
        if (!InstrumentCatalog.Any(x => x.Id == value)) Missing("乐器目录中不存在该乐器");
    }
    private static void Unique(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values) if (!seen.Add(value)) Invalid("同一批次不能重复修改或同时删除同一资源");
    }
    public static void Text(string value, int maximum, bool allowEmpty = false, bool multiline = false)
    {
        if (value is null || value.Length > maximum || !allowEmpty && string.IsNullOrWhiteSpace(value) ||
            value.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t'))) Invalid("曲库字段为空、无效或超限");
        try { _ = new UTF8Encoding(false, true).GetByteCount(value!); }
        catch (EncoderFallbackException) { Invalid("曲库字段包含无效 Unicode"); }
    }
    private static void Invalid(string message) => throw new OperationException("request.invalid", message);
    private static void Missing(string message) => throw new OperationException("resource.notFound", message);
    private static void Capacity(string message) => throw new OperationException("resource.capacityExceeded", message);
}

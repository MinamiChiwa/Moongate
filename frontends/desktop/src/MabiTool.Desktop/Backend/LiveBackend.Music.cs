using System.Globalization;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Desktop.Pages.Music;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 乐谱库的来源合并、清单读取、清单编辑与乐谱导入。读取在 LiveBackend.Pages.cs 的例行刷新里调用，编辑与导入由页面意图触发。
public sealed partial class LiveBackend
{
    private const int MusicPageSize = 200;
    private const int MusicScorePages = 50;
    private const int MusicFolderLimit = 600;
    private static readonly TimeSpan OwnedSyncInterval = TimeSpan.FromSeconds(60);

    /// <summary>一个来源在某个修订下的全部乐谱（无搜索词）。来源修订不变时沿用，不重复查询。</summary>
    private sealed record ScoreSourceCache(ulong Revision, IReadOnlyList<ScoreEntry> Entries);

    private readonly Dictionary<string, ScoreSourceCache> scoreCache = [];

    // 乐谱库的抓包来源同步：打开乐谱页、点“重新同步”与每分钟各一次；旧宿主没有该接口（404）时不再尝试。
    private bool ownedSyncUnsupported;
    private long ownedSyncAt;

    /// <summary>
    /// 乐谱表读取的来源：游戏列表、当前角色的抓包来源与全部导入来源（清单引用的就是这些乐谱）；次序固定：游戏列表、抓包、导入。
    /// 当前角色由最近一次抓包同步给出的范围键确定，其他角色的抓包来源不显示；还没有同步成功过（例如宿主刚重启、游戏没有运行）时，
    /// 曲库里只有一个抓包来源就显示它，有多个无法判断是哪个角色就都不显示。
    /// </summary>
    internal static IReadOnlyList<MusicScoreSource> DisplayedMusicSources(MusicLibraryInfo? library, string? ownedScopeKey)
    {
        if (library is null) return [];
        var captures = library.Sources.Where(source => IsKind(source, "Inventory")).ToList();
        var current = ownedScopeKey ?? (captures.Count == 1 ? captures[0].ScopeKey : null);
        return [.. library.Sources
            .Where(source => !IsKind(source, "Inventory") || source.ScopeKey == current)
            .Where(source => !IsKind(source, "ObservationSummary"))
            .OrderBy(source => IsKind(source, "GameList") ? 0 : IsKind(source, "Inventory") ? 1 : 2).ThenBy(source => source.ScopeKey, StringComparer.Ordinal)];
    }

    private static bool IsKind(MusicScoreSource source, string kind) => string.Equals(source.Kind, kind, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 乐谱的来源键：提供方 <c>tw-cli</c>（游戏列表）为游戏乐谱，<c>tw-capture</c>（背包抓包，只含玩家自制与复制乐谱）为自制乐谱，
    /// <c>mobifolio</c> 开头为导入；其他提供方原样返回，没有来源时为空。
    /// </summary>
    internal static string ScoreSourceKey(ScoreEntry score)
    {
        var provider = score.SourceRefs.FirstOrDefault()?.Provider ?? string.Empty;
        return provider switch
        {
            "tw-cli" => ScoreSources.Game,
            "tw-capture" => ScoreSources.Composed,
            _ when provider.StartsWith("mobifolio", StringComparison.Ordinal) => ScoreSources.Imported,
            _ => provider
        };
    }

    /// <summary>名称前来源标签的文字：游戏乐谱、自制乐谱、导入；其他提供方显示原文，没有来源时为“未知”。</summary>
    internal static string ScoreSourceLabel(ScoreEntry score) => ScoreSourceKey(score) switch
    {
        ScoreSources.Game => Pick("游戏乐谱", "遊戲樂譜"),
        ScoreSources.Composed => Pick("自制乐谱", "自製樂譜"),
        ScoreSources.Imported => Pick("导入", "匯入"),
        "" => Pick("未知", "未知"),
        var provider => provider
    };

    // 调用方持有 gate。乐谱来源与修订的指纹：来源集合或修订变化后，已查到的可演奏性需要重查。
    private string MusicSourceStamp() =>
        string.Join(',', DisplayedMusicSources(pages.Library, pages.OwnedScopeKey).Select(source => source.ScopeKey + ":" + source.Revision));

    /// <summary>
    /// 乐谱表的乐谱：逐个来源按游标读完（每页 200 张），来源修订不变时沿用缓存；有搜索词时再按词读取各来源的命中。
    /// 同时给出全部乐谱的标题表（清单条目与队列取标题用，不受搜索影响）与可选乐谱数。任何一次读取失败返回空，调用方沿用上次结果。
    /// </summary>
    private async Task<(IReadOnlyList<ScoreEntry> Scores, IReadOnlyDictionary<string, string> Titles, int Count)?> ReadMusicScoresAsync(
        MabiToolClient api, MusicLibraryInfo library, string? ownedScopeKey, string search, CancellationToken token)
    {
        var sources = DisplayedMusicSources(library, ownedScopeKey);
        var all = new List<ScoreEntry>();
        var shown = new List<ScoreEntry>();
        foreach (var source in sources)
        {
            ScoreSourceCache? cached;
            lock (gate) cached = scoreCache.GetValueOrDefault(source.ScopeKey);
            IReadOnlyList<ScoreEntry> entries;
            if (cached is not null && cached.Revision == source.Revision.Value) entries = cached.Entries;
            else
            {
                var read = await ReadAllScoresAsync(api, source.ScopeKey, string.Empty, token);
                if (read is null) return null;
                entries = read;
                lock (gate) scoreCache[source.ScopeKey] = new(source.Revision.Value, entries);
            }
            all.AddRange(entries);
            if (search.Length == 0) { shown.AddRange(entries); continue; }
            var hits = await ReadAllScoresAsync(api, source.ScopeKey, search, token);
            if (hits is null) return null;
            shown.AddRange(hits);
        }
        lock (gate)
            foreach (var stale in scoreCache.Keys.Where(key => sources.All(source => source.ScopeKey != key)).ToList()) scoreCache.Remove(stale);
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var score in all) titles[score.Id] = score.RawTitle;
        return (shown, titles, all.Count(score => score.Availability != "missing"));
    }

    private static async Task<List<ScoreEntry>?> ReadAllScoresAsync(MabiToolClient api, string scopeKey, string search, CancellationToken token)
    {
        var result = new List<ScoreEntry>();
        string? cursor = null;
        for (var page = 0; page < MusicScorePages; page++)
        {
            var current = cursor;
            var read = await Try(() => api.QueryMusicLibraryAsync(new(MusicCollection.Scores, null, null, scopeKey, search, MusicPageSize, current), token));
            if (read is null) return null;
            result.AddRange(read.Scores);
            cursor = read.NextCursor;
            if (cursor is null) break;
        }
        return result;
    }

    /// <summary>
    /// 全部文件夹层级里的文件夹与清单（清单条目逐页读完）。曲库修订与上次相同时沿用，不重复查询；
    /// 任何一次读取失败返回空（调用方沿用上次结果）。文件夹数与读取次数有上限。
    /// </summary>
    private async Task<(IReadOnlyList<MusicFolder> Folders, IReadOnlyList<MusicPlaylist> Playlists)?> ReadMusicListsAsync(
        MabiToolClient api, UInt64String revision, CancellationToken token)
    {
        var folders = new List<MusicFolder>();
        var playlists = new List<MusicPlaylist>();
        var pending = new Queue<string?>();
        pending.Enqueue(null);
        while (pending.Count > 0)
        {
            var parent = pending.Dequeue();
            string? cursor = null;
            do
            {
                var current = cursor;
                var page = await Try(() => api.QueryMusicLibraryAsync(new(MusicCollection.Folders, parent, null, null, string.Empty, MusicPageSize, current), token));
                if (page is null) return null;
                foreach (var folder in page.Folders) { folders.Add(folder); pending.Enqueue(folder.Id); }
                cursor = page.NextCursor;
            } while (cursor is not null && folders.Count < MusicFolderLimit);
            cursor = null;
            do
            {
                var current = cursor;
                var page = await Try(() => api.QueryMusicLibraryAsync(new(MusicCollection.Playlists, parent, null, null, string.Empty, MusicPageSize, current), token));
                if (page is null) return null;
                playlists.AddRange(page.Playlists);
                cursor = page.NextCursor;
            } while (cursor is not null);
            if (folders.Count >= MusicFolderLimit) break;
        }
        for (var index = 0; index < playlists.Count; index++)
        {
            var items = new List<PlaylistItem>();
            string? cursor = null;
            do
            {
                var current = cursor; var id = playlists[index].Id;
                var page = await Try(() => api.QueryMusicLibraryAsync(new(MusicCollection.PlaylistItems, null, id, null, string.Empty, MusicPageSize, current), token));
                if (page is null) return null;
                items.AddRange(page.Items);
                cursor = page.NextCursor;
            } while (cursor is not null);
            playlists[index] = playlists[index] with { Items = items };
        }
        return (folders, playlists);
    }

    /// <summary>把背包里的玩家自制与复制乐谱同步为抓包来源；旧宿主没有该接口（404）时不再尝试。</summary>
    private async Task<MusicOwnedSync?> SyncOwnedMusicScoresAsync(MabiToolClient api, CancellationToken token)
    {
        MusicOwnedSync? result = null;
        var unsupported = false;
        try { result = await api.SyncOwnedMusicScoresAsync(token); }
        catch (MabiToolApiException error) { unsupported = error.Status == 404; }
        lock (gate)
        {
            if (unsupported) ownedSyncUnsupported = true;
            else ownedSyncAt = Now;
        }
        return result;
    }

    // ---- 清单编辑 ----

    public Task<BackendReply> EditMusicLibraryAsync(MusicListEdit edit, CancellationToken cancellationToken) =>
        ReplyAsync(async (api, token) =>
        {
            string? tag = null;
            var info = await api.GetMusicLibraryAsync(token, metadata => tag = metadata.ETag);
            // 编辑必须基于当前修订的完整清单：页面读到的就是同一修订时沿用，否则先重读。
            IReadOnlyList<MusicFolder>? folders; IReadOnlyList<MusicPlaylist> playlists; UInt64String? readAt;
            lock (gate) (folders, playlists, readAt) = (pages.Folders, pages.Playlists, pages.ListsRevision);
            if (folders is null || readAt != info.Revision)
            {
                var read = await ReadMusicListsAsync(api, info.Revision, token)
                    ?? throw new InvalidOperationException(Pick("没能读取曲库里的清单，请稍后再试。", "沒能讀取曲庫裡的清單，請稍後再試。"));
                (folders, playlists) = read;
            }
            var batch = BuildMusicEdit(edit, folders, playlists);
            if (batch is null) return string.Empty;
            await api.EditMusicLibraryAsync(batch, IdempotencyKey(), tag ?? string.Empty, token);
            return MusicEditMessage(edit.Kind);
        }, cancellationToken);

    private static string MusicEditMessage(MusicListEditKind kind) => kind switch
    {
        MusicListEditKind.CreatePlaylist => Pick("已新建清单", "已新增清單"),
        MusicListEditKind.CreateFolder => Pick("已新建文件夹", "已新增資料夾"),
        MusicListEditKind.DeletePlaylist => Pick("已删除清单", "已刪除清單"),
        MusicListEditKind.DeleteFolder => Pick("已删除文件夹", "已刪除資料夾"),
        MusicListEditKind.AddItem => Pick("已加入清单", "已加入清單"),
        MusicListEditKind.RemoveItem => Pick("已从清单移除", "已從清單移除"),
        _ => Pick("已保存", "已儲存")
    };

    /// <summary>
    /// 一次清单编辑对应的后端批量编辑（读到的清单是当前修订的完整内容）。目标不存在、名称无效、条目不属于清单时抛出带中文说明的
    /// <see cref="InvalidOperationException"/>；排序已在边界（没有变化）时返回空。条目 ID、清单 ID 与文件夹 ID 为新的十六进制标识，
    /// 排序键按同级最大值加一补齐为六位数字（与导入一致），重排时清单条目全部按位置重新编号。
    /// </summary>
    internal static MusicEditBatch? BuildMusicEdit(MusicListEdit edit, IReadOnlyList<MusicFolder> folders, IReadOnlyList<MusicPlaylist> playlists)
    {
        static string NewId() => Guid.NewGuid().ToString("N");
        static string Key(int value) => value.ToString("D6", CultureInfo.InvariantCulture);
        static string NextKey(IEnumerable<string> keys) =>
            Key(keys.Select(key => int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1).DefaultIfEmpty(-1).Max() + 1);
        static string Name(string? text, string what)
        {
            var name = (text ?? string.Empty).Trim();
            if (name.Length is < 1 or > 200) throw new InvalidOperationException(Pick($"{what}名称需要 1 到 200 个字。", $"{what}名稱需要 1 到 200 個字。"));
            if (name.Any(char.IsControl)) throw new InvalidOperationException(Pick($"{what}名称不能含控制字符。", $"{what}名稱不能含控制字元。"));
            return name;
        }
        MusicPlaylist Playlist() => playlists.FirstOrDefault(item => item.Id == edit.PlaylistId)
            ?? throw new InvalidOperationException(Pick("这个清单已经不存在，请重新选择。", "這個清單已經不存在，請重新選擇。"));
        string? Folder(string? id) => string.IsNullOrEmpty(id) ? null : folders.Any(item => item.Id == id) ? id
            : throw new InvalidOperationException(Pick("这个文件夹已经不存在，请重新选择。", "這個資料夾已經不存在，請重新選擇。"));
        static MusicEditBatch Put(MusicPlaylist playlist) => new([], [], [playlist], [], null);

        switch (edit.Kind)
        {
            case MusicListEditKind.CreatePlaylist:
            {
                var folder = Folder(edit.FolderId);
                return Put(new(NewId(), folder, Name(edit.Text, Pick("清单", "清單")), string.Empty, null,
                    NextKey(playlists.Where(item => item.FolderId == folder).Select(item => item.SortKey)), []));
            }
            case MusicListEditKind.RenamePlaylist:
                return Put(Playlist() with { Name = Name(edit.Text, Pick("清单", "清單")) });
            case MusicListEditKind.SetNotes:
            {
                var notes = (edit.Text ?? string.Empty).Replace("\r\n", "\n");
                if (notes.Length > 4000) throw new InvalidOperationException(Pick("备注最多 4000 个字。", "備註最多 4000 個字。"));
                return Put(Playlist() with { Notes = notes });
            }
            case MusicListEditKind.SetDefaultInstrument:
                return Put(Playlist() with { DefaultInstrumentId = string.IsNullOrEmpty(edit.Text) ? null : edit.Text });
            case MusicListEditKind.MovePlaylist:
            {
                var playlist = Playlist(); var folder = Folder(edit.FolderId);
                if (playlist.FolderId == folder) return null;
                return Put(playlist with { FolderId = folder, SortKey = NextKey(playlists.Where(item => item.FolderId == folder).Select(item => item.SortKey)) });
            }
            case MusicListEditKind.DeletePlaylist:
                return new([], [], [], [Playlist().Id], null);
            case MusicListEditKind.AddItem:
            {
                var playlist = Playlist();
                if (string.IsNullOrEmpty(edit.ScoreId)) throw new InvalidOperationException(Pick("没有选择乐谱。", "沒有選擇樂譜。"));
                return Put(playlist with { Items = [.. playlist.Items, new PlaylistItem(NewId(), edit.ScoreId, null, NextKey(playlist.Items.Select(item => item.SortKey)))] });
            }
            case MusicListEditKind.RemoveItem:
            {
                var playlist = Playlist();
                if (playlist.Items.All(item => item.Id != edit.ItemId)) throw new InvalidOperationException(Pick("这个条目已经不在清单里。", "這個條目已經不在清單裡。"));
                return Put(playlist with { Items = [.. playlist.Items.Where(item => item.Id != edit.ItemId)] });
            }
            case MusicListEditKind.MoveItem:
            {
                var playlist = Playlist();
                var ordered = playlist.Items.OrderBy(item => item.SortKey, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).ToList();
                var index = ordered.FindIndex(item => item.Id == edit.ItemId);
                if (index < 0) throw new InvalidOperationException(Pick("这个条目已经不在清单里。", "這個條目已經不在清單裡。"));
                var target = index + Math.Sign(edit.Direction);
                if (edit.Direction == 0 || target < 0 || target >= ordered.Count) return null;
                (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
                return Put(playlist with { Items = [.. ordered.Select((item, position) => item with { SortKey = Key(position) })] });
            }
            case MusicListEditKind.SetItemInstrument:
            {
                var playlist = Playlist();
                if (playlist.Items.All(item => item.Id != edit.ItemId)) throw new InvalidOperationException(Pick("这个条目已经不在清单里。", "這個條目已經不在清單裡。"));
                var instrument = string.IsNullOrEmpty(edit.Text) ? null : edit.Text;
                return Put(playlist with { Items = [.. playlist.Items.Select(item => item.Id == edit.ItemId ? item with { InstrumentId = instrument } : item)] });
            }
            case MusicListEditKind.CreateFolder:
            {
                var parent = Folder(edit.FolderId);
                return new([new(NewId(), parent, Name(edit.Text, Pick("文件夹", "資料夾")),
                    NextKey(folders.Where(item => item.ParentId == parent).Select(item => item.SortKey)))], [], [], [], null);
            }
            case MusicListEditKind.RenameFolder:
            {
                var folder = folders.FirstOrDefault(item => item.Id == edit.FolderId)
                    ?? throw new InvalidOperationException(Pick("这个文件夹已经不存在，请重新选择。", "這個資料夾已經不存在，請重新選擇。"));
                return new([folder with { Name = Name(edit.Text, Pick("文件夹", "資料夾")) }], [], [], [], null);
            }
            case MusicListEditKind.DeleteFolder:
            {
                var folder = folders.FirstOrDefault(item => item.Id == edit.FolderId)
                    ?? throw new InvalidOperationException(Pick("这个文件夹已经不存在，请重新选择。", "這個資料夾已經不存在，請重新選擇。"));
                return new([], [folder.Id], [], [], null);
            }
            default:
                throw new InvalidOperationException(Pick("不支持的清单编辑。", "不支援的清單編輯。"));
        }
    }

    // ---- 乐谱导入 ----

    public async Task<MusicImportPreviewReply> PreviewMusicImportAsync(IReadOnlyList<MusicImportFile> files, string region, CancellationToken cancellationToken)
    {
        MusicImportPreviewView? view = null;
        var reply = await ReplyAsync(async (api, token) =>
        {
            var tag = region == "TW" ? "tw" : "kr";
            var request = new MusicImportRequest("mobifolio-" + tag, Pick("MobiFolio 导入", "MobiFolio 匯入") + " " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                region == "TW" ? "TW" : "KR", "mobifolio", new(0),
                [.. files.Select(file => new ImportSourceFile(file.Name, Convert.ToBase64String(file.Content)))]);
            var preview = await api.PreviewMusicImportAsync(request, IdempotencyKey(), token);
            if (preview.State == "committed") throw new InvalidOperationException(Pick("这批数据已经导入过。", "這批資料已經匯入過。"));
            if (preview.State == "conflict") throw new InvalidOperationException(Pick("导入数据与曲库里的内容冲突，不能导入。", "匯入資料與曲庫裡的內容衝突，不能匯入。"));
            view = MapImportPreview(preview);
            return string.Empty;
        }, cancellationToken, remember: false);
        return new(reply.Ok && view is not null, ImportMessage(reply), reply.Ok ? view : null);
    }

    // 导入文件被后端拒绝时给出文件名；其余走共用的错误文案。
    private static string ImportMessage(BackendReply reply) => reply.Code switch
    {
        "import.invalidInput" => Pick("导入文件无效：", "匯入檔案無效：") + Pick("需要是登记的 MobiFolio 数据文件（UTF-8 JSON）。", "需要是登記的 MobiFolio 資料檔案（UTF-8 JSON）。"),
        "request.bodyTooLarge" => Pick("导入文件太大，合计不能超过 8 MiB。", "匯入檔案太大，合計不能超過 8 MiB。"),
        _ => reply.Message
    };

    internal static MusicImportPreviewView MapImportPreview(MusicImportPreview preview) => new(preview.Id, preview.Counts.Scores, preview.Counts.Folders,
        preview.Counts.Playlists, preview.Counts.PlaylistItems, preview.Counts.Artists, preview.Counts.History, preview.Counts.Durations,
        preview.Counts.UnresolvedReferences,
        [.. preview.Issues.Take(5).Select(issue => issue.File.Length == 0 ? issue.Message : issue.File + "：" + issue.Message)], preview.Issues.Count);

    public async Task<MusicImportCommitReply> CommitMusicImportAsync(string previewId, CancellationToken cancellationToken)
    {
        string message = string.Empty;
        var reply = await ReplyAsync(async (api, token) =>
        {
            var preview = await api.GetMusicImportAsync(previewId, token);
            var etag = "\"music-library-" + preview.ExpectedLibraryRevision.Value.ToString(CultureInfo.InvariantCulture) + "\"";
            var operation = await api.CommitMusicImportAsync(previewId, IdempotencyKey(), etag, token);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
            while (operation.State.KnownValue is OperationState.Queued or OperationState.Running or OperationState.CancelRequested or OperationState.WaitingUser)
            {
                if (DateTimeOffset.UtcNow > deadline) throw new InvalidOperationException(Pick("导入仍在进行，请稍后查看曲库。", "匯入仍在進行，請稍後查看曲庫。"));
                await Task.Delay(300, token);
                operation = await api.GetOperationAsync(operation.Id, token);
            }
            if (operation.State.KnownValue != OperationState.Succeeded)
                throw operation.Problem is { } problem
                    ? new MabiToolApiException(409, problem)
                    : new InvalidOperationException(Pick("导入没有完成。", "匯入沒有完成。"));
            var counts = operation.Result?.Deserialize<MusicImportReceipt>(ContractJson.Options)?.Counts;
            message = counts is null ? Pick("已导入", "已匯入")
                : Pick($"已导入 {counts.Scores} 张乐谱、{counts.Playlists} 个清单", $"已匯入 {counts.Scores} 張樂譜、{counts.Playlists} 個清單");
            return message;
        }, cancellationToken);
        return new(reply.Ok, reply.Ok ? message : ImportMessage(reply) is { Length: > 0 } text ? text : reply.Message);
    }
}

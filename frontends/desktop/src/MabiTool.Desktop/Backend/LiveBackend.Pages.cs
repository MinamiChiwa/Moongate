using System.Globalization;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Pages.Inventory;
using MabiTool.Desktop.Pages.Modules;
using MabiTool.Desktop.Pages.Music;
using MabiTool.Desktop.Pages.Settings;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 乐谱库、背包、模块和设置页的后端读取，染色页的方案与引擎状态也在这里一并读取（页面在 LiveBackend.Color.cs，合奏在 LiveBackend.Rooms.cs）。
// 这些状态变化慢，每五秒读一次，发出意图后立即重读；某个接口不可用时只影响对应页面。
public sealed partial class LiveBackend
{
    private sealed record PageState(MusicLibraryInfo? Library, IReadOnlyList<MusicPlaylist> Playlists,
        IReadOnlyList<ScoreEntry> Scores, IReadOnlyList<InstrumentDefinition> Instruments,
        PerformanceQueueState? Session, ColorLibrary? Colors,
        ColorEngineRuntime? ColorRuntime, InventorySnapshot? Inventory, IReadOnlyList<ModuleSnapshot> Modules,
        IReadOnlyList<GlobalShortcutSnapshot> Shortcuts, UpdateStateSnapshot? Update, SystemInfo? System,
        ShortcutSettingsSnapshot? ShortcutSettings = null,
        IReadOnlyList<CapabilitySnapshot>? Capabilities = null, string? Nickname = null, UpdatePolicySnapshot? Policy = null,
        ObservedGameIdentity? Identity = null, IReadOnlyList<PresentationSourceStatus>? Sources = null,
        IReadOnlyList<InventoryAccountView>? InventoryAccounts = null, InventoryPivotCache? Pivot = null,
        InventoryResourcesView? InventoryResources = null);

    /// <summary>所选账号的透视表；账号修订与名称目录修订都不变时沿用同一份行对象，表格不必重排。名称目录变化（自动提取完成）时重读，名称随之更新。</summary>
    private sealed record InventoryPivotCache(string AccountId, string Revision, string CatalogRevision, IReadOnlyList<InventoryCharacter> Characters,
        IReadOnlyList<InventoryPivotRow> Rows);

    private PageState pages = new(null, [], [], [], null, null, null, null, [], [], null, null);
    private string musicSearch = string.Empty;
    private string? playlistId;
    private string? inventoryAccount;
    private DateTimeOffset pagesReadAt;
    private bool pagesDue = true;

    public Task SendIntentAsync(string intent, string? argument, CancellationToken cancellationToken)
    {
        if (intent.StartsWith("rooms.", StringComparison.Ordinal)) return SendRoomIntentAsync(intent, argument, cancellationToken);
        if (intent.StartsWith("combat.", StringComparison.Ordinal)) return SendCombatPageIntentAsync(intent, argument, cancellationToken);
        switch (intent)
        {
            // 搜索词、所选清单是界面上的显示选择，记在这里并触发重读。
            case "music.search":
                lock (gate) { musicSearch = argument ?? string.Empty; pagesDue = true; }
                Signal();
                return Task.CompletedTask;
            case "music.playlist":
                lock (gate) { playlistId = argument is "all" or null ? null : argument; pagesDue = true; }
                Signal();
                Notify();
                return Task.CompletedTask;
            // 背包所选账号是界面上的显示选择：记下后重读，后端没有该账号时取默认账号。
            case "inventory.account":
                lock (gate) { inventoryAccount = string.IsNullOrEmpty(argument) ? null : argument; pagesDue = true; }
                Signal();
                return Task.CompletedTask;
        }

        return SendAsync(async (api, current, token) =>
        {
            var key = IdempotencyKey();
            switch (intent)
            {
                case "music.resync":
                    lock (gate) pagesDue = true;
                    return null;
                case "update.check":
                    await api.CreateUpdateCheckAsync(new(), key, token);
                    break;
                default:
                    var message = await ActAsync(intent, argument, api, current, token);
                    lock (gate) pagesDue = true;
                    return message;
            }
            lock (gate) pagesDue = true;
            return null;
        }, cancellationToken);
    }

    public MusicPageData GetMusic()
    {
        var (current, page, search, playlist) = ReadPages();
        var unknown = Pick("未知", "未知");
        var artists = page.Library?.NameSettings.Artists.ToDictionary(artist => artist.Id, artist => artist.Name) ?? [];
        var selected = page.Playlists.FirstOrDefault(item => item.Id == playlist);
        var allowed = selected?.Items.Select(item => item.ScoreId).ToHashSet();
        var canQueue = current.Connected && current.Binding?.Binding is not null;
        var playing = page.Session is { Phase: PerformancePhase.Performing or PerformancePhase.Executing } ? page.Session.CurrentScoreId : null;
        // 指定乐器来自所选清单的条目，条目没有写时取清单的默认乐器；“全部乐谱”不属于任何清单，显示未指定。
        var assigned = selected?.Items.GroupBy(item => item.ScoreId)
            .ToDictionary(group => group.Key, group => group.First().InstrumentId ?? selected.DefaultInstrumentId);
        string Length(string scoreId) =>
            durations.TryGet(scoreId, out var ms) && ms is { } known ? $"{(int)(known / 60000)}:{(int)(known / 1000) % 60:00}" : unknown;
        var scores = page.Scores.Where(score => allowed is null || allowed.Contains(score.Id)).Select(score =>
        {
            var instrumentId = assigned?.GetValueOrDefault(score.Id);
            var available = score.Availability == "available";
            var tag = score.Id == playing
                ? Pick("演奏中", "演奏中")
                : !available
                    ? Pick($"游戏无法选取：{score.Availability}", $"遊戲無法選取：{score.Availability}")
                    : score.DuplicateGroup is null ? string.Empty : Pick("有同名乐谱", "有同名樂譜");
            return new ScoreRow(score.Id, score.RawTitle, tag,
                score.ArtistId is { } artist && artists.TryGetValue(artist, out var name) ? name : Pick("未识别", "未辨識"),
                InstrumentName(page, instrumentId), Length(score.Id), score.Id == playing, canQueue && available, instrumentId);
        }).ToList();

        var titles = page.Scores.ToDictionary(score => score.Id, score => score.RawTitle);
        var queue = page.Session is not { } session ? [] : session.Order.Select((sourceIndex, position) =>
        {
            var item = session.Items[sourceIndex].Item;
            return new QueueRow(item.Id, (position + 1).ToString(CultureInfo.InvariantCulture),
                titles.GetValueOrDefault(item.ScoreId, item.ScoreId), InstrumentName(page, item.InstrumentId), Length(item.ScoreId),
                position == session.CurrentPosition);
        }).ToList();

        var status = !current.Connected
            ? Pick("未连接后端", "未連接後端")
            : page.Library is not { } library
                ? Pick("后端没有提供乐谱库", "後端沒有提供樂譜庫")
                : library.Sources.Count == 0
                    ? Pick($"{library.ItemCount} 张乐谱，尚未从游戏同步", $"{library.ItemCount} 張樂譜，尚未從遊戲同步")
                    : Pick($"{library.ItemCount} 张乐谱，来源状态：{library.Sources[0].RefreshStatus}", $"{library.ItemCount} 張樂譜，來源狀態：{library.Sources[0].RefreshStatus}");
        return new(status, search,
            [
                new("all", Pick("全部乐谱", "全部樂譜"), Pick($"{page.Scores.Count} 张", $"{page.Scores.Count} 張"), "#161A26", selected is null),
                .. page.Playlists.Select((item, index) => new PlaylistChip(item.Id, item.Name,
                    Pick($"{item.Items.Count} 首", $"{item.Items.Count} 首"), index % 2 == 0 ? "#E7A23A" : "#7C96FF", item.Id == selected?.Id))
            ],
            scores,
            page.Session is { } active
                ? Pick($"{queue.Count} 首，{active.Phase}", $"{queue.Count} 首，{active.Phase}")
                : Pick("没有进行中的演奏", "沒有進行中的演奏"),
            queue,
            page.Session is not { } state ? [] :
            [
                new(Pick("播放顺序", "播放順序"), $"{(state.Shuffle ? Pick("随机", "隨機") : Pick("依序", "依序"))}，{state.Repeat}"),
                new(Pick("曲间等待", "曲間等待"), Pick($"{state.GapSeconds} 秒", $"{state.GapSeconds} 秒"))
            ],
            new(current.Connected, canQueue, page.Session is { Phase: PerformancePhase.Idle, Items.Count: > 0 },
                page.Session is { } running && ActivePhases.Contains(running.Phase) && running.Items.Count > 1,
                page.Session is { } stoppable && ActivePhases.Contains(stoppable.Phase)));
    }

    public InventoryPageData GetInventory()
    {
        var (current, page, _, _) = ReadPages();
        var workspace = page.Inventory?.Data;
        string Container(OpenEnum<InventoryContainer> container) => container.KnownValue switch
        {
            InventoryContainer.Backpack => Pick("背包", "背包"),
            InventoryContainer.CharacterStorage => Pick("角色保管箱", "角色保管箱"),
            InventoryContainer.AccountStorage => Pick("公共保管箱", "公共保管箱"),
            _ => container.RawValue
        };
        var status = InventorySyncText.Status(current.Connected, GetCaptureStatus(), workspace);
        var pivot = page.Pivot;
        var accounts = page.InventoryAccounts ?? [];
        return new(status,
            // 概况的角色与金币、银币、魔族貢品由页面模型按资源区所选角色取自 Resources；这里只给道具种数，未读取时写明，行数固定。
            [new(Pick("道具", "道具"), pivot is null ? Pick("未读取", "未讀取") : Pick($"{pivot.Rows.Count} 种", $"{pivot.Rows.Count} 種"))],
            accounts.Select(account => new InventoryAccountOption(account.AccountId, InventoryAccountText.Label(account.AccountId, account.Label))).ToList(),
            pivot?.AccountId, pivot?.Characters ?? [], pivot?.Rows ?? [],
            workspace?.Coverage.Select(coverage => new CoverageRow($"{Container(coverage.Container)}：{coverage.Category}",
                coverage.Complete ? Pick("完整", "完整") : Pick($"不完整，{coverage.Reason ?? Pick("原因未知", "原因未知")}", $"不完整，{coverage.Reason ?? Pick("原因未知", "原因未知")}"),
                coverage.Complete)).ToList() ?? [],
            CatalogRows(page),
            InventoryResourceText.Rows(page.InventoryResources,
                pivot?.Characters.DistinctBy(character => character.Id).ToDictionary(character => character.Id, character => character.Name)),
            page.InventoryResources?.CharacterId);
    }

    /// <summary>
    /// “道具名称目录”卡片：已收录时为条数与各类别条数；目录为空时按能力 <c>inventory.catalog</c> 写明提取中、提取失败的原因或等待绑定，不写 0 条。
    /// 目录由后端在常驻抓包绑定游戏后自动从客户端提取。
    /// </summary>
    private static IReadOnlyList<KeyValue> CatalogRows(PageState page)
    {
        if (page.Inventory is not { } snapshot) return [new(Pick("状态", "狀態"), Pick("未读取", "未讀取"))];
        if (snapshot.Catalog.Id is not null)
            return
            [
                new(Pick("已收录", "已收錄"), Pick($"{snapshot.Catalog.Entries:N0} 条", $"{snapshot.Catalog.Entries:N0} 條")),
                .. snapshot.Catalog.Categories.Where(category => category.Supported && category.Entries > 0)
                    .Select(category => new KeyValue(InventoryCategoryNames.Display(category.Category), Pick($"{category.Entries:N0} 条", $"{category.Entries:N0} 條")))
            ];
        var reason = page.Capabilities?.FirstOrDefault(item => item.Id == "inventory.catalog")?.ReasonCode;
        return [new(Pick("状态", "狀態"), reason switch
        {
            "inventory.catalogExtracting" => Pick("正在从游戏客户端提取名称目录", "正在從遊戲用戶端提取名稱目錄"),
            "inventory.catalogNotLoaded" or null => Pick("尚未生成：常驻抓包绑定游戏后自动提取", "尚未生成：常駐抓包綁定遊戲後自動提取"),
            "inventory.sourceUnavailable" => Pick("提取失败：读取游戏客户端失败，下次启动游戏时重试", "提取失敗：讀取遊戲用戶端失敗，下次啟動遊戲時重試"),
            var code => Pick($"提取失败：{code}，下次启动游戏时重试", $"提取失敗：{code}，下次啟動遊戲時重試")
        })];
    }

    public ModulesPageData GetModules()
    {
        var (current, page, _, _) = ReadPages();
        var extensions = current.Connected ? GetExtensions() : null;
        return new(current.Connected, page.Modules.Select(module => new ModuleState(module.Id, module.Version, module.Enabled,
            Code(module.State), string.Join("、", module.Problems.Select(problem => problem.Message)),
            ModuleText.Pick(module.DisplayNames), ModuleText.Pick(module.Descriptions), module.Essential == true)).ToList(), current.Connected,
            extensions is not null, extensions);
    }

    /// <summary>预览渠道的发行包以 <c>InformationalVersion=&lt;版本&gt;-preview</c> 构建前端，设置页的版本后标注“预览版”。</summary>
    private static readonly bool PreviewBuild = typeof(LiveBackend).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .Any(attribute => attribute.InformationalVersion.Split('+')[0].EndsWith("-preview", StringComparison.Ordinal));

    public SettingsPageData GetSettings()
    {
        var (current, page, _, _) = ReadPages();
        string Chord(ShortcutChordSnapshot chord) => string.Join(" + ", new[]
        {
            chord.Control ? "Ctrl" : null, chord.Alt ? "Alt" : null, chord.Shift ? "Shift" : null,
            chord.Windows ? "Win" : null, chord.Key
        }.Where(part => part is not null));
        return new(
            [
                new(Pick("连接", "連接"), current.Connected ? Pick("已连接", "已連接") : current.Problem ?? Pick("未连接", "未連接")),
                new(Pick("版本", "版本"), (page.System?.Version ?? Pick("未知", "未知")) + (PreviewBuild ? Pick("（预览版）", "（預覽版）") : "")),
                new("API", page.System?.ApiVersion ?? Pick("未知", "未知")),
                new(Pick("运行环境", "執行環境"), page.System?.Runtime ?? Pick("未知", "未知")),
                new(Pick("游戏数据", "遊戲資料"), current.Simulation ? Pick("模拟", "模擬") : Pick("真实游戏", "真實遊戲"))
            ],
            ShortcutRows(current, page, Chord),
            page.Update is not { } update ? [new(Pick("状态", "狀態"), Pick("未知", "未知"))] :
            [
                new(Pick("当前版本", "目前版本"), update.InstalledReleaseId ?? Pick("未知", "未知")),
                new(Pick("状态", "狀態"), Code(update.State)),
                new(Pick("更新来源", "更新來源"), update.Configured ? Pick("已配置", "已設定") : Pick("未配置", "未設定")),
                new(Pick("可用更新", "可用更新"), update.Available?.ReleaseId ?? Pick("没有", "沒有"))
            ],
            current.Connected && page.Update is { Configured: true },
            current.Connected && page.Update?.Available is { PlanId: not null } && page.Update.State is not ("downloaded" or "ready"),
            current.Connected && page.Update is { Available.PlanId: not null, State: "downloaded" or "ready" },
            current.Connected && page.Policy?.Policy is { } policy
                ? new UpdatePolicyView(policy.AutomaticChecks, policy.CheckIntervalHours, policy.DownloadConcurrency, policy.MaximumBytesPerSecond)
                : null,
            current.Connected ? page.Update?.Available?.ReleaseId : null, current.Connected);
    }

    // 快捷键行：后端注册清单与可修改的动作合并；紧急停止始终列出，后端没有报告时显示未知。
    private static List<ShortcutRow> ShortcutRows(State current, PageState page, Func<ShortcutChordSnapshot, string> chord)
    {
        var editable = page.ShortcutSettings?.Actions.Select(action => action.ActionId).ToHashSet(StringComparer.Ordinal) ?? [];
        var registrations = page.Shortcuts.Concat(page.ShortcutSettings?.Registrations ?? [])
            .GroupBy(item => item.ActionId, StringComparer.Ordinal).Select(group => group.First()).ToList();
        var rows = registrations.Select(shortcut => new ShortcutRow(ActionName(shortcut.ActionId), chord(shortcut.Registered ?? shortcut.Configured),
            shortcut.FailureCode is { } code ? $"{Code(shortcut.Status)}：{ShortcutFailure(code)}" : Code(shortcut.Status),
            shortcut.Registered is not null && shortcut.FailureCode is null, shortcut.ActionId,
            current.Connected && editable.Contains(shortcut.ActionId))).ToList();
        foreach (var binding in page.ShortcutSettings?.Bindings ?? [])
            if (rows.All(row => row.Id != binding.ActionId))
                rows.Add(new(ActionName(binding.ActionId), chord(binding.Chord), Pick("未注册", "未註冊"), false, binding.ActionId,
                    current.Connected && editable.Contains(binding.ActionId)));
        foreach (var action in page.ShortcutSettings?.Actions ?? [])
            if (rows.All(row => row.Id != action.ActionId))
                rows.Add(new(ActionName(action.ActionId), Pick("未配置", "未設定"), Pick("未注册", "未註冊"), false,
                    action.ActionId, current.Connected));
        if (rows.All(row => row.Id != "emergency-stop"))
            rows.Add(new(ActionName("emergency-stop"), Pick("未知", "未知"), Pick("后端没有报告", "後端沒有回報"), false, "emergency-stop",
                current.Connected && editable.Contains("emergency-stop")));
        return [.. rows.OrderBy(row => row.Id == "emergency-stop" ? 0 : 1)];
    }

    /// <summary>系统热键注册失败的原因（后端 WindowsGlobalShortcutSource 的码）；未收录的原样显示。</summary>
    internal static string ShortcutFailure(string code) => code switch
    {
        "shortcut.inUse" => Pick("组合键已被其他程序或本工具的其他动作占用，请换一个组合", "組合鍵已被其他程式或本工具的其他動作佔用，請換一個組合"),
        "shortcut.registrationFailed" => Pick("系统拒绝注册这个组合键", "系統拒絕註冊這個組合鍵"),
        "shortcut.stopped" => Pick("快捷键服务已停止", "快捷鍵服務已停止"),
        _ => code
    };

    private static string ActionName(string actionId) => actionId switch
    {
        "emergency-stop" => Pick("紧急停止", "緊急停止"),
        "overlay-toggle" => Pick("显示或隐藏浮窗", "顯示或隱藏浮窗"),
        "overlay-click-through-toggle" => Pick("浮窗元件全部上锁或解锁", "浮窗元件全部上鎖或解鎖"),
        "combat-measurement-toggle" => Pick("开始／取消定时测量", "開始／取消定時測量"),
        _ => actionId
    };

    // 常见的后端状态码换成界面文字；没有收录的原样显示，不猜含义。
    private static string Code(string raw) => raw switch
    {
        "running" => Pick("运行中", "執行中"),
        "idle" => Pick("待命，使用时才启动", "待命，使用時才啟動"),
        "unavailable" => Pick("不可用", "不可用"),
        "disabled" => Pick("已停用", "已停用"),
        "unconfigured" => Pick("未配置", "未設定"),
        "exact-hex" => Pick("精确色码", "精確色碼"),
        "similar" => Pick("相似即可", "相似即可"),
        "screenshot" => Pick("截图读取", "截圖讀取"),
        "confirmed" => Pick("观测完整", "觀測完整"),
        "partial" => Pick("部分观测", "部分觀測"),
        "registered" => Pick("已注册", "已註冊"),
        "up-to-date" => Pick("已是最新", "已是最新"),
        "connected" => Pick("已连线", "已連線"),
        "disconnected" => Pick("未连线", "未連線"),
        "unknown" => Pick("未知", "未知"),
        _ => raw
    };

    private static string InstrumentName(PageState page, string? instrumentId)
    {
        if (instrumentId is null) return Pick("未指定", "未指定");
        var definition = page.Instruments.FirstOrDefault(item => item.Id == instrumentId);
        // 乐器名称取游戏地区的原文，目录里没有时显示标识。
        return definition?.DisplayNames.GetValueOrDefault("zh-TW") ?? definition?.DisplayNames.Values.FirstOrDefault() ?? instrumentId;
    }

    private (State State, PageState Pages, string Search, string? Playlist) ReadPages()
    {
        lock (gate) return (state, pages, musicSearch, playlistId);
    }

    private async Task RefreshPagesAsync(MabiToolClient api, CancellationToken token)
    {
        string search;
        lock (gate)
        {
            if (!pagesDue && DateTimeOffset.UtcNow - pagesReadAt < TimeSpan.FromSeconds(5)) return;
            pagesDue = false;
            search = musicSearch;
        }

        var library = await Try(() => api.GetMusicLibraryAsync(token));
        var playlists = await Try(() => api.QueryMusicLibraryAsync(new(MusicCollection.Playlists, null, null, null, string.Empty, 200, null), token));
        var scope = library?.Sources.FirstOrDefault()?.ScopeKey;
        var scores = scope is null ? null
            : await Try(() => api.QueryMusicLibraryAsync(new(MusicCollection.Scores, null, null, scope, search, 200, null), token));
        var next = new PageState(library, playlists?.Playlists ?? [], scores?.Scores ?? [],
            (await Try(() => api.GetMusicInstrumentsAsync(token)))?.Items ?? [],
            (await Try(() => api.ListPerformanceSessionsAsync(token)))?.Items.FirstOrDefault(IsLive),
            await Try(() => api.GetColorLibraryAsync(token)), await Try(() => api.GetColorRuntimeAsync(token)),
            await Try(() => api.GetInventorySnapshotAsync(token)), (await Try(() => api.GetModulesAsync(token)))?.Items ?? [],
            (await Try(() => api.GetShortcutsAsync(token)))?.Items ?? [], await Try(() => api.GetUpdateStateAsync(token)),
            await Try(() => api.GetSystemInfoAsync(token)),
            await Try(() => api.GetShortcutSettingsAsync(token)), (await Try(() => api.GetCapabilitiesAsync(token)))?.Items,
            null, await Try(() => api.GetUpdatePolicyAsync(token)));
        next = await ReadInventoryPivotAsync(api, next, token);
        // 资源视图按账户读取（账户级加该账户下各角色的角色级），跟随账号下拉（与公共保管箱列同一选择）；还没有所选账号时读取当前库存身份所属账户。
        next = next with { InventoryResources = next.Pivot?.AccountId is { } account
            ? await Try(() => api.GetInventoryAccountResourcesAsync(account, token))
            : await Try(() => api.GetInventoryResourcesAsync(token)) };
        // 游戏观测只读取：昵称用于绑定卡与合奏页的昵称预填。
        var identity = (await Try(() => api.GetGameObservationAsync(token)))?.Latest?.Value.Identity;
        // 展示接口只取附近与环境两个主题的状态，浮窗元件据此显示或隐藏。
        var sources = (await Try(() => api.QueryPresentationAsync(new(PresentationTopics.Watched), token)))?.Sources;
        next = next with { Identity = identity, Nickname = identity?.Nickname is { Length: > 0 } nickname ? nickname : null, Sources = sources };
        await RefreshDurationsAsync(api, next.Session, token);
        await RefreshExtensionsAsync(api, token);
        lock (gate)
        {
            pages = next;
            pagesReadAt = DateTimeOffset.UtcNow;
        }
        Notify();
    }

    /// <summary>
    /// 背包账号与所选账号的透视表。账号列表按 <c>NextCursor</c> 读完；所选账号取前端记下的账号，没有时取当前背包快照所属账号，
    /// 再没有时取第一个。所选账号与其修订都没变时沿用上次的透视表；读取被拒绝时保留上次结果。
    /// </summary>
    private async Task<PageState> ReadInventoryPivotAsync(MabiToolClient api, PageState next, CancellationToken token)
    {
        string? requested;
        PageState previous;
        lock (gate) { requested = inventoryAccount; previous = pages; }
        var first = await Try(() => api.GetInventoryAccountsAsync(token));
        if (first is null) return next with { InventoryAccounts = previous.InventoryAccounts, Pivot = previous.Pivot };
        var accounts = first.Items.ToList();
        for (var cursor = first.NextCursor; cursor is { Length: > 0 };)
        {
            var more = await Try(() => api.QueryInventoryAccountsAsync(new(100, cursor), token));
            if (more is null) break;
            accounts.AddRange(more.Items);
            cursor = more.NextCursor;
        }
        var workspace = next.Inventory?.Data;
        var selected = accounts.FirstOrDefault(account => account.AccountId == requested)
            ?? accounts.FirstOrDefault(account => account.AccountId == workspace?.Identity?.AccountId)
            ?? accounts.FirstOrDefault();
        if (selected is null) return next with { InventoryAccounts = accounts, Pivot = null };
        var catalog = next.Inventory?.Catalog.Revision.ToString() ?? string.Empty;
        if (previous.Pivot is { } cached && cached.AccountId == selected.AccountId && cached.Revision == selected.Revision.ToString() && cached.CatalogRevision == catalog)
            return next with { InventoryAccounts = accounts, Pivot = cached };
        var items = await Try(() => InventoryPivotBuilder.ReadAsync(selected, (query, ct) => api.QueryInventoryItemsAsync(query, ct), token));
        if (items is null) return next with { InventoryAccounts = accounts, Pivot = previous.Pivot?.AccountId == selected.AccountId ? previous.Pivot : null };
        var (characters, rows) = InventoryPivotBuilder.Build(items);
        return next with { InventoryAccounts = accounts, Pivot = new(selected.AccountId, selected.Revision.ToString(), catalog, characters, rows) };
    }

    // 单个接口被拒绝（例如模块未启用或权限不足）不影响其他页面；连接类错误交给外层重连。
    private static async Task<T?> Try<T>(Func<Task<T>> read) where T : class
    {
        try { return await read(); }
        catch (MabiToolApiException) { return null; }
    }
}

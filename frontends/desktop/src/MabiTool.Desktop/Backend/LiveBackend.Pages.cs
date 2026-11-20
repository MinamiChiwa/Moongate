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
// 这些状态变化慢：有界面可见时每五秒读一次，发出意图后立即重读，内容没变时不通知界面；背包只在背包页可见时读取。
// 某个接口不可用时只影响对应页面。
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
    private long pagesReadAt;
    private bool pagesDue = true;
    // 背包快照（含全部条目）只在后端发出背包事件、用户操作或重新连接后重读；五秒例行刷新沿用上次结果。
    private bool inventoryDue = true;

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
        return musicMemo.Get([current.Connected, current.Binding?.Binding is null, page, search, playlist, durations.KnownCount, Localization.UiText.Language],
            MapMusic);
    }

    private MusicPageData MapMusic()
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
                    : Pick($"{library.ItemCount} 张乐谱，", $"{library.ItemCount} 張樂譜，") + SourceStatus(library.Sources[0].RefreshStatus);
        return new(status, search,
            [
                new("all", Pick("全部乐谱", "全部樂譜"), Pick($"{page.Scores.Count} 张", $"{page.Scores.Count} 張"), "#161A26", selected is null),
                .. page.Playlists.Select((item, index) => new PlaylistChip(item.Id, item.Name,
                    Pick($"{item.Items.Count} 首", $"{item.Items.Count} 首"), index % 2 == 0 ? "#E7A23A" : "#7C96FF", item.Id == selected?.Id))
            ],
            scores,
            page.Session is { } active
                ? Pick($"{queue.Count} 首，", $"{queue.Count} 首，") + Localization.CodeText.Phase(System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(active.Phase.ToString()))
                : Pick("没有进行中的演奏", "沒有進行中的演奏"),
            queue,
            page.Session is not { } state ? [] :
            [
                new(Pick("播放顺序", "播放順序"), $"{(state.Shuffle ? Pick("随机", "隨機") : Pick("依序", "依序"))}，{RepeatText(state.Repeat)}"),
                new(Pick("曲间等待", "曲間等待"), Pick($"{state.GapSeconds} 秒", $"{state.GapSeconds} 秒"))
            ],
            new(current.Connected, canQueue, page.Session is { Phase: PerformancePhase.Idle, Items.Count: > 0 },
                page.Session is { } running && ActivePhases.Contains(running.Phase) && running.Items.Count > 1,
                page.Session is { } stoppable && ActivePhases.Contains(stoppable.Phase)));
    }

    public InventoryPageData GetInventory()
    {
        var (current, page, _, _) = ReadPages();
        object?[] key;
        lock (gate) key = [current.Connected, page, captureRuntime, captureSnapshot, Localization.UiText.Language];
        return inventoryMemo.Get(key, MapInventory);
    }

    private InventoryPageData MapInventory()
    {
        var (current, page, _, _) = ReadPages();
        var workspace = page.Inventory?.Data;
        string Container(OpenEnum<InventoryContainer> container) => container.KnownValue switch
        {
            InventoryContainer.Backpack => Pick("背包", "背包"),
            InventoryContainer.CharacterStorage => Pick("角色保管箱", "角色保管箱"),
            InventoryContainer.AccountStorage => Pick("公共保管箱", "公共保管箱"),
            _ => Localization.CodeText.Unknown("container", container.RawValue, "未知位置", "未知位置")
        };
        var status = InventorySyncText.Status(current.Connected, GetCaptureStatus(), workspace);
        var pivot = page.Pivot;
        var accounts = page.InventoryAccounts ?? [];
        return new(status,
            // 概况的角色与金币、银币、魔族貢品由页面模型按资源区所选角色取自 Resources；这里只给道具种数，未读取时写明，行数固定。
            [new(Pick("道具", "道具"), pivot is null ? Pick("未读取", "未讀取") : Pick($"{pivot.Rows.Count} 种", $"{pivot.Rows.Count} 種"))],
            accounts.Select(account => new InventoryAccountOption(account.AccountId, InventoryAccountText.Label(account.AccountId, account.Label))).ToList(),
            pivot?.AccountId, pivot?.Characters ?? [], pivot?.Rows ?? [],
            workspace?.Coverage.Select(coverage => new CoverageRow($"{Container(coverage.Container)}：{InventoryCategoryNames.Display(coverage.Category)}",
                coverage.Complete ? Pick("完整", "完整") : Pick("不完整：", "不完整：") + Localization.CodeText.Reason(coverage.Reason),
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
            "inventory.catalogExtracting" => Pick("正在提取名称目录", "正在提取名稱目錄"),
            "inventory.catalogNotLoaded" or null => Pick("未生成，抓包绑定游戏后自动提取", "未生成，抓包綁定遊戲後自動提取"),
            "inventory.sourceUnavailable" => Pick("提取失败，下次启动游戏时重试", "提取失敗，下次啟動遊戲時重試"),
            var code => Pick("提取失败：", "提取失敗：") + Localization.CodeText.Reason(code)
        })];
    }

    public ModulesPageData GetModules()
    {
        var (current, page, _, _) = ReadPages();
        object?[] key;
        lock (gate) key = [current.Connected, page, extensions, Localization.UiText.Language];
        return modulesMemo.Get(key, MapModules);
    }

    private ModulesPageData MapModules()
    {
        var (current, page, _, _) = ReadPages();
        var extensions = current.Connected ? GetExtensions() : null;
        return new(current.Connected, page.Modules.Select(module => new ModuleState(module.Id, module.Version, module.Enabled,
            Code(module.State), string.Join("、", module.Problems.Select(problem => Localization.CodeText.Readable(problem.Message) ?? Localization.CodeText.Reason(problem.Code)).Distinct()),
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
        object?[] key;
        lock (gate) key = [current.Connected, current.Problem, current.Simulation, page, apiAddress, Localization.UiText.Language];
        return settingsMemo.Get(key, MapSettings);
    }

    private SettingsPageData MapSettings()
    {
        var (current, page, _, _) = ReadPages();
        string? address;
        lock (gate) address = apiAddress;
        string Chord(ShortcutChordSnapshot chord) => string.Join(" + ", new[]
        {
            chord.Control ? "Ctrl" : null, chord.Alt ? "Alt" : null, chord.Shift ? "Shift" : null,
            chord.Windows ? "Win" : null, chord.Key
        }.Where(part => part is not null));
        return new(
            [
                new(Pick("连接", "連接"), current.Connected ? Pick("已连接", "已連接") : current.Problem ?? Pick("未连接", "未連接")),
                new(Pick("地址", "位址"), current.Connected && address is not null ? address : Pick("未知", "未知")),
                new(Pick("版本", "版本"), (page.System?.Version ?? Pick("未知", "未知")) + (PreviewBuild ? Pick("（预览版）", "（預覽版）") : "")),
                new("API", page.System?.ApiVersion ?? Pick("未知", "未知")),
                new(Pick("运行环境", "執行環境"), page.System?.Runtime ?? Pick("未知", "未知")),
                new(Pick("游戏数据", "遊戲資料"), current.Simulation ? Pick("模拟", "模擬") : Pick("真实游戏", "真實遊戲"))
            ],
            ShortcutRows(current, page, Chord),
            UpdateRows(page.Update),
            current.Connected && page.Update is { Configured: true } && !UpdateBusy(page.Update),
            current.Connected && page.Update is { State: "available", Available.PlanId: not null },
            current.Connected && page.Update is { State: "ready", Available.PlanId: not null },
            current.Connected && page.Policy?.Policy is { } policy
                ? new UpdatePolicyView(policy.AutomaticChecks, policy.CheckIntervalHours, policy.DownloadConcurrency, policy.MaximumBytesPerSecond)
                : null,
            current.Connected ? page.Update?.Available?.ReleaseId : null, current.Connected);
    }

    // 更新卡片固定六行，状态变化只改文字：当前版本、状态、可用更新、下载进度、上次检查、上次安装。
    internal static List<KeyValue> UpdateRows(UpdateStateSnapshot? update)
    {
        var unknown = Pick("未知", "未知");
        if (update is null)
            return [new(Pick("当前版本", "目前版本"), unknown), new(Pick("状态", "狀態"), unknown), new(Pick("可用更新", "可用更新"), unknown),
                new(Pick("下载进度", "下載進度"), unknown), new(Pick("上次检查", "上次檢查"), unknown), new(Pick("上次安装", "上次安裝"), unknown)];
        var notApplicable = Pick("不适用", "不適用");
        var state = !update.Configured
            ? Pick("不可用：仅发行压缩包版本支持", "不可用：僅發行壓縮檔版本支援")
            : update.State switch
            {
                "checking" => Pick("正在检查", "正在檢查"),
                "available" => Pick("发现新版本", "發現新版本"),
                "preparing" => Pick("准备下载", "準備下載"),
                "downloading" => Pick("正在下载", "正在下載"),
                "verifying" => Pick("正在校验", "正在校驗"),
                "ready" => Pick("已下载，可以安装", "已下載，可以安裝"),
                "waitingForIdle" => Pick("等待当前活动结束", "等待目前活動結束"),
                "applying" => Pick("正在安装", "正在安裝"),
                "handedOff" => Pick("正在重启到新版本", "正在重新啟動到新版本"),
                // 空闲时按后端的最近检查：没有即尚未检查，只有结论为 current 才是已是最新版本。
                _ => update.Operation switch
                {
                    { Kind: not "update.check", State.KnownValue: OperationState.Failed, Problem: { } problem } =>
                        Pick("上次操作失败：", "上次操作失敗：") + Localization.CodeText.Reason(problem.Code),
                    _ => update.LastCheck switch
                    {
                        null => Pick("尚未检查", "尚未檢查"),
                        { Outcome: "failed" } failed => Pick("检查失败：", "檢查失敗：") + Localization.CodeText.Reason(failed.FailureCode),
                        { Outcome: "current" } => Pick("已是最新版本", "已是最新版本"),
                        _ => Pick("待命", "待命")
                    }
                }
            };
        var available = update.Available is { } offer
            ? offer.ReleaseId + (offer switch
            {
                { DownloadKind: "incremental", DownloadBytes: { } bytes } => Pick($"（增量更新 {Megabytes(bytes.Value)}）", $"（增量更新 {Megabytes(bytes.Value)}）"),
                { DownloadKind: "full", DownloadBytes: { } bytes } => Pick($"（完整下载 {Megabytes(bytes.Value)}）", $"（完整下載 {Megabytes(bytes.Value)}）"),
                // 后端还没比对完本机文件时下载方式未知，只写版本、不写大小。
                _ => ""
            })
            : update.Configured ? Pick("没有", "沒有") : notApplicable;
        if (update.Available?.FallbackReason is { } fallback)
            available += Pick(" · 增量失败：", " · 增量失敗：") + Localization.CodeText.Reason(fallback);
        var progress = notApplicable;
        if (update.Operation is { Kind: "update.download", Result: { ValueKind: System.Text.Json.JsonValueKind.Object } result } &&
            result.TryGetProperty("verifiedBytes", out var verified) && ulong.TryParse(verified.GetString(), out var done) &&
            result.TryGetProperty("totalBytes", out var total) && ulong.TryParse(total.GetString(), out var all) && all > 0)
            progress = $"{Megabytes(done)} / {Megabytes(all)}（{done * 100 / all}%）";
        else if (update.State == "available" && update.Available?.ResumableBytes is { Value: > 0 } resumable)
            progress = Pick($"已下载 {Megabytes(resumable.Value)}，可续传", $"已下載 {Megabytes(resumable.Value)}，可續傳");
        var checkedText = update.LastCheck is { } check
            ? check.CheckedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + (check.Outcome == "failed" ? Pick("，失败", "，失敗") : "")
            : update.Configured ? Pick("尚未检查", "尚未檢查") : notApplicable;
        var last = update.LastInstall switch
        {
            null => update.Configured ? Pick("没有", "沒有") : notApplicable,
            { Outcome: "committed" } install => Pick("已更新到 ", "已更新到 ") + install.ReleaseId,
            { Outcome: "rolledBack" } install => Pick($"安装 {install.ReleaseId} 失败，已回到 {install.PreviousReleaseId}", $"安裝 {install.ReleaseId} 失敗，已回到 {install.PreviousReleaseId}")
                + (install.FailureCode is { } reason ? "：" + Localization.CodeText.Reason(reason) : ""),
            { Outcome: "aborted" } install => Pick($"{install.ReleaseId} 未安装：", $"{install.ReleaseId} 未安裝：") + Localization.CodeText.Reason(install.FailureCode),
            var install => Pick($"安装 {install.ReleaseId} 失败，请手动升级", $"安裝 {install.ReleaseId} 失敗，請手動升級")
        };
        return [new(Pick("当前版本", "目前版本"), update.InstalledReleaseId ?? (update.Configured ? unknown : notApplicable)),
            new(Pick("状态", "狀態"), state), new(Pick("可用更新", "可用更新"), available),
            new(Pick("下载进度", "下載進度"), progress), new(Pick("上次检查", "上次檢查"), checkedText), new(Pick("上次安装", "上次安裝"), last)];
    }

    private static bool UpdateBusy(UpdateStateSnapshot update) =>
        update.State is "checking" or "preparing" or "downloading" or "verifying" or "waitingForIdle" or "applying" or "handedOff";

    private static string Megabytes(ulong bytes) => (bytes / 1048576d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";

    // 快捷键行：后端注册清单与可修改的动作合并；紧急停止始终列出，后端没有报告时显示未知。
    private static List<ShortcutRow> ShortcutRows(State current, PageState page, Func<ShortcutChordSnapshot, string> chord)
    {
        var editable = page.ShortcutSettings?.Actions.Select(action => action.ActionId).ToHashSet(StringComparer.Ordinal) ?? [];
        var actions = page.ShortcutSettings?.Actions ?? [];
        string ActionName(string id) => LiveBackend.ActionName(id, actions);
        var registrations = page.Shortcuts.Concat(page.ShortcutSettings?.Registrations ?? [])
            .GroupBy(item => item.ActionId, StringComparer.Ordinal).Select(group => group.First()).ToList();
        var rows = registrations.Select(shortcut => new ShortcutRow(ActionName(shortcut.ActionId), chord(shortcut.Registered ?? shortcut.Configured),
            shortcut.FailureCode is { } code ? ShortcutFailure(code) : Code(shortcut.Status),
            shortcut.Registered is not null && shortcut.FailureCode is null, shortcut.ActionId,
            current.Connected && editable.Contains(shortcut.ActionId))).ToList();
        foreach (var binding in page.ShortcutSettings?.Bindings ?? [])
            if (rows.All(row => row.Id != binding.ActionId))
                rows.Add(new(ActionName(binding.ActionId), chord(binding.Chord), Pick("未注册", "未註冊"), false, binding.ActionId,
                    current.Connected && editable.Contains(binding.ActionId)));
        foreach (var action in page.ShortcutSettings?.Actions ?? [])
            if (rows.All(row => row.Id != action.ActionId))
                rows.Add(new(ActionName(action.ActionId), Pick("未配置", "未設定"),
                    action.UnavailableReason is { } unavailable ? Pick("不可用：", "不可用：") + ShortcutFailure(unavailable) : Pick("未注册", "未註冊"), false,
                    action.ActionId, current.Connected));
        if (rows.All(row => row.Id != "emergency-stop"))
            rows.Add(new(ActionName("emergency-stop"), Pick("未知", "未知"), Pick("未知", "未知"), false, "emergency-stop",
                current.Connected && editable.Contains("emergency-stop")));
        return [.. rows.OrderBy(row => row.Id == "emergency-stop" ? 0 : 1)];
    }

    /// <summary>系统热键注册失败的原因（后端 WindowsGlobalShortcutSource 的码）；未收录的原样显示。</summary>
    internal static string ShortcutFailure(string code) => code switch
    {
        "shortcut.inUse" => Pick("组合键已被占用，请换一个", "組合鍵已被佔用，請換一個"),
        "shortcut.registrationFailed" => Pick("系统拒绝注册这个组合键", "系統拒絕註冊這個組合鍵"),
        "shortcut.stopped" => Pick("快捷键服务已停止", "快捷鍵服務已停止"),
        "shortcut.conflictWithConfiguredAction" => Pick("与其他动作的组合相同", "與其他動作的組合相同"),
        "extension.disabled" => Pick("扩展已停用", "擴充已停用"),
        "extension.notRunning" => Pick("扩展未运行", "擴充未執行"),
        "extension.permissionDenied" => Pick("未授予快捷键权限", "未授予快捷鍵權限"),
        "extension.commandMissing" => Pick("扩展已没有这个命令", "擴充已沒有這個命令"),
        _ => Localization.CodeText.Reason(code)
    };

    // 扩展命令的名称由后端给出（“扩展名：命令名”）；内建动作用本地文字。
    private static string ActionName(string actionId, IReadOnlyList<ShortcutActionSnapshot> actions) =>
        actions.FirstOrDefault(x => x.ActionId == actionId)?.Names is { Count: > 0 } names
            ? Pages.Modules.ModuleText.Pick(names) ?? actionId : BuiltInActionName(actionId);

    private static string BuiltInActionName(string actionId) => actionId switch
    {
        "emergency-stop" => Pick("紧急停止", "緊急停止"),
        "overlay-toggle" => Pick("显示或隐藏浮窗", "顯示或隱藏浮窗"),
        "overlay-click-through-toggle" => Pick("浮窗元件全部上锁或解锁", "浮窗元件全部上鎖或解鎖"),
        "combat-measurement-toggle" => Pick("开始／取消定时测量", "開始／取消定時測量"),
        _ => Localization.CodeText.Unknown("shortcut-action", actionId, "未知动作", "未知動作")
    };

    // 乐谱来源的同步状态（ScoreRefreshStatus）。
    private static string SourceStatus(string status) => status switch
    {
        "complete" => Pick("已同步", "已同步"),
        "partial" => Pick("部分同步", "部分同步"),
        "failed" => Pick("同步失败", "同步失敗"),
        _ => Code(status)
    };

    // 演奏队列的循环方式（PerformanceRepeatMode）。
    private static string RepeatText(PerformanceRepeatMode mode) => mode switch
    {
        PerformanceRepeatMode.All => Pick("全部循环", "全部循環"),
        PerformanceRepeatMode.One => Pick("单曲循环", "單曲循環"),
        _ => Pick("不循环", "不循環")
    };

    // 后端状态码换成界面文字，统一经共用映射；没有收录的写成中文概括并括注原文。
    private static string Code(string raw) => Localization.CodeText.State(raw);

    private static string InstrumentName(PageState page, string? instrumentId)
    {
        if (instrumentId is null) return Pick("未指定", "未指定");
        var definition = page.Instruments.FirstOrDefault(item => item.Id == instrumentId);
        // 乐器名称取游戏地区的原文，目录里没有时显示标识。
        return definition?.DisplayNames.GetValueOrDefault("zh-TW") ?? definition?.DisplayNames.Values.FirstOrDefault()
            ?? Localization.CodeText.Unknown("instrument", instrumentId, "未知乐器", "未知樂器");
    }

    private (State State, PageState Pages, string Search, string? Playlist) ReadPages()
    {
        lock (gate) return (state, pages, musicSearch, playlistId);
    }

    // 背包读取被拒绝（模块未启用等）后，下一次按五秒的例行间隔再试，不立即重读整轮页面状态。
    private bool inventoryFailed;
    // 系统信息（版本、API、运行环境）在一次连接内不变，连接后读一次。
    private SystemInfo? systemInfo;
    private byte[]? pagesContent;

    // 调用方持有 gate。没有任何可见界面时不读页面状态；背包只在背包页或背包表格窗口可见时读取。
    private bool PagesDue(long now) => AnySurface && (pagesDue || inventoryDue && Showing("inventory")
        || now - pagesReadAt >= Span(TimeSpan.FromSeconds(5)));

    private async Task RefreshPagesAsync(MabiToolClient api, CancellationToken token)
    {
        string search;
        bool readInventory, readMusic, readColor, readSettings;
        InventorySnapshot? previousInventory;
        PageState previous;
        lock (gate)
        {
            if (!PagesDue(Now)) return;
            // 各页专用的读取只在对应页面（或用到它的浮窗元件、迷你窗）显示时进行；不显示时沿用上次结果。
            previous = pages;
            readMusic = Showing("music");
            readColor = Showing("color");
            readSettings = Showing("settings");
            readInventory = Showing("inventory") && (pagesDue || inventoryDue || inventoryFailed || pages.Inventory is null);
            pagesDue = false;
            if (readInventory) { inventoryDue = false; inventoryFailed = false; }
            search = musicSearch;
            previousInventory = pages.Inventory;
            pagesReadAt = Now;
        }

        var library = readMusic ? await Try(() => api.GetMusicLibraryAsync(token)) : previous.Library;
        var playlists = readMusic
            ? (await Try(() => api.QueryMusicLibraryAsync(new(MusicCollection.Playlists, null, null, null, string.Empty, 200, null), token)))?.Playlists ?? []
            : previous.Playlists;
        var scope = library?.Sources.FirstOrDefault()?.ScopeKey;
        var scores = !readMusic ? previous.Scores : scope is null ? []
            : (await Try(() => api.QueryMusicLibraryAsync(new(MusicCollection.Scores, null, null, scope, search, 200, null), token)))?.Scores ?? [];
        SystemInfo? system;
        lock (gate) system = systemInfo;
        system ??= await Try(() => api.GetSystemInfoAsync(token));
        lock (gate) systemInfo = system;
        var next = new PageState(library, playlists, scores,
            readMusic ? (await Try(() => api.GetMusicInstrumentsAsync(token)))?.Items ?? [] : previous.Instruments,
            readMusic ? (await Try(() => api.ListPerformanceSessionsAsync(token)))?.Items.FirstOrDefault(IsLive) : previous.Session,
            readColor ? await Try(() => api.GetColorLibraryAsync(token)) : previous.Colors,
            readColor ? await Try(() => api.GetColorRuntimeAsync(token)) : previous.ColorRuntime,
            readInventory ? await Try(() => api.GetInventorySnapshotAsync(token)) : previousInventory, (await Try(() => api.GetModulesAsync(token)))?.Items ?? [],
            readSettings ? (await Try(() => api.GetShortcutsAsync(token)))?.Items ?? [] : previous.Shortcuts, await Try(() => api.GetUpdateStateAsync(token)),
            system,
            readSettings ? await Try(() => api.GetShortcutSettingsAsync(token)) : previous.ShortcutSettings,
            WithCombatCapabilities((await Try(() => api.GetCapabilitiesAsync(token)))?.Items,
                await Try(() => api.GetCombatCapabilitiesAsync(token))),
            null, await Try(() => api.GetUpdatePolicyAsync(token)));
        if (readInventory)
        {
            next = await ReadInventoryPivotAsync(api, next, token);
            // 资源视图按账户读取（账户级加该账户下各角色的角色级），跟随账号下拉（与公共保管箱列同一选择）；还没有所选账号时读取当前库存身份所属账户。
            next = next with { InventoryResources = next.Pivot?.AccountId is { } account
                ? await Try(() => api.GetInventoryAccountResourcesAsync(account, token))
                : await Try(() => api.GetInventoryResourcesAsync(token)) };
        }
        else next = next with { InventoryAccounts = previous.InventoryAccounts, Pivot = previous.Pivot, InventoryResources = previous.InventoryResources };
        // 游戏观测只读取：昵称用于绑定卡与合奏页的昵称预填；与抓包状态读的是同一份观测（RefreshCaptureAsync）。
        GameObservationSnapshot? observation;
        lock (gate) observation = captureSnapshot;
        observation ??= await Try(() => api.GetGameObservationAsync(token));
        var identity = observation?.Latest?.Value.Identity;
        // 展示接口只取附近与环境两个主题的状态，浮窗元件据此显示或隐藏。
        var sources = (await Try(() => api.QueryPresentationAsync(new(PresentationTopics.Watched), token)))?.Sources;
        next = next with { Identity = identity, Nickname = identity?.Nickname is { Length: > 0 } nickname ? nickname : null, Sources = sources };
        await RefreshDurationsAsync(api, next.Session, token);
        var extensionsChanged = await RefreshExtensionsAsync(api, token);
        // 内容与上次相同时保留原对象、不通知界面：页面与外壳取到同一份数据，不重排。
        var content = PageContent(next);
        bool changed;
        lock (gate)
        {
            if (readInventory && next.Inventory is null) inventoryFailed = true;
            changed = !ReferenceEquals(next.Inventory, pages.Inventory) || !ReferenceEquals(next.Pivot, pages.Pivot)
                || pagesContent is null || !content.AsSpan().SequenceEqual(pagesContent);
            if (changed) { pages = next; pagesContent = content; }
        }
        if (changed || extensionsChanged) Notify();
    }

    /// <summary>
    /// 全局能力表没有的战斗能力（怪物血量与当前目标）取自战斗能力表，换成能力表的一项并入，界面经同一个入口读取说明与状态。
    /// 检查时刻取能力自己的最近观测时刻（没有时为固定值），内容不变时页面状态保持不变。
    /// </summary>
    internal static IReadOnlyList<CapabilitySnapshot>? WithCombatCapabilities(IReadOnlyList<CapabilitySnapshot>? items, CombatCapabilities? combat)
    {
        if (combat?.Capabilities.FirstOrDefault(item => item.Id == CapabilityIds.MonsterHealth) is not { } health) return items;
        var snapshot = new CapabilitySnapshot(health.Id, "combat-observation", health.Status, health.Source?.Provider, health.Source?.ProviderVersion,
            health.Source?.GameBuild, null, health.Reason ?? "available", health.LastObservedAt ?? DateTimeOffset.UnixEpoch,
            combat.Simulation ? ["synthetic-observation"] : []);
        return [.. (items ?? []).Where(item => item.Id != health.Id), snapshot];
    }

    // 页面状态（背包快照与透视表按对象比较，不计入）序列化后的内容，用来判断两次读取是否相同。
    private static byte[] PageContent(PageState value)
    {
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(value with { Inventory = null, Pivot = null }, ContractJson.Options);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return Guid.NewGuid().ToByteArray(); }
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

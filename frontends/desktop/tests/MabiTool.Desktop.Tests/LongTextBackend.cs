using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Pages.Combat;
using MabiTool.Desktop.Pages.Diagnostics;
using MabiTool.Desktop.Pages.Dyeing;
using MabiTool.Desktop.Pages.Inventory;
using MabiTool.Desktop.Pages.Modules;
using MabiTool.Desktop.Pages.Music;
using MabiTool.Desktop.Pages.Rooms;
using MabiTool.Desktop.Pages.Settings;

namespace MabiTool.Desktop.Tests;

/// <summary>名称和说明都很长的样本，用来检查截断、换行与容器增高。</summary>
internal sealed class LongTextBackend : IDesktopBackend
{
    private readonly SampleBackend sample = new();

    public event EventHandler? Changed { add { } remove { } }
    public event EventHandler<string>? ShortcutTriggered { add { } remove { } }

    public ActivitySnapshot GetActivity() => new("color", "染色模組持有遊戲輸入，其他需要輸入的模組暫時無法開始",
        "正在為「暗夜星辰華麗禮服（限定版）」的第三個染色區域尋找最接近的顏色組合",
        "已送出 128 次輸入，其中 3 次效果未知；可以隨時按緊急停止，停止後不會繼續輸入，也不會聲稱已還原顏色。",
        EffectStage.Done, EffectStage.Unknown, EffectStage.Pending);

    public BindingView GetGameBinding() => new("名字非常非常長的角色名稱測試用琉特",
        "洛奇 Mobile 台服（測試用的很長地區說明）", "遊戲視窗綁定狀態未知，請回到遊戲後重新偵測", false);

    public Task RequestEmergencyStopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Avalonia.PixelRect? GetGameWindowBounds() => null;

    public string? GetCapabilityNotice(string capabilityId) =>
        "後端尚未實作，暫不可用：名字非常非常長的原因碼 module.notImplemented.veryLongReasonCodeForLayoutTesting";

    public TopicNotice? GetPresentationNotice(string topic) =>
        new("provider.unconfigured", "暫不可用：名字非常非常長的原因碼 provider.unconfigured.veryLongReasonCodeForLayoutTesting");

    public CapabilityLevel GetCapabilityLevel(string capabilityId) => CapabilityLevel.Unavailable;

    public Task SendCombatIntentAsync(CombatIntent intent, string? targetId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task SendIntentAsync(string intent, string? argument, CancellationToken cancellationToken) => Task.CompletedTask;

    public void RequestScoreDetails(IReadOnlyList<string> scoreIds) { }

    public const string LongProfileName = "名字非常非常長的染色方案：暗夜星辰華麗禮服（限定版・第二套配色）";

    public Task<BackendReply> SaveColorDraftAsync(ColorDraft draft, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, "請求未完成：request.invalid，需要三個有效區域、大寫 #RRGGBB、1–3 優先級及 1–35 色差"));

    public Task<BackendReply> SaveColorProfileAsync(string name, ColorDraft draft, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, "請求未完成：resource.conflict，配色方案名稱與另一個名字非常非常長的方案重複，請改名後再儲存"));

    public Task<BackendReply> LoadColorProfileAsync(string profileId, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, "請求未完成：resource.notFound，所選方案已不存在或已被另一個視窗刪除"));

    public Task<BackendReply> DeleteColorProfileAsync(string profileId, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, "請求未完成：resource.notFound，所選方案已不存在或已被另一個視窗刪除"));

    public Task<ColorAtlasReply> GetColorAtlasAsync(string targetHex, string matchMode, double tolerance, bool full, CancellationToken cancellationToken) =>
        Task.FromResult(new ColorAtlasReply(null, "染色引擎不可用：名字非常非常長的原因碼 color.paletteWorkerUnavailable.veryLongReasonCodeForLayoutTesting"));

    public Task<GameWindowActivation> ActivateGameWindowAsync(CancellationToken cancellationToken) => sample.ActivateGameWindowAsync(cancellationToken);

    public Task<CaptureReply> CaptureScreenAsync(CancellationToken cancellationToken) => sample.CaptureScreenAsync(cancellationToken);

    public Task ReleaseCaptureAsync(string captureId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<BackendReply> ChangeShortcutAsync(string actionId, KeyChord chord, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, $"已儲存 {chord}，註冊狀態：不可用，shortcut.sourceUnavailable，系統快捷鍵來源目前無法使用"));

    public const string LongWindowName = "Mabinogi Mobile - 名字非常非常長的遊戲視窗標題（第二個用戶端・測試用）";

    public Task<GameWindowListReply> ListGameWindowsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new GameWindowListReply(true, string.Empty,
        [
            new("long-1", LongWindowName, "MabinogiMobileWithAVeryLongProcessNameForLayoutTesting", 2_147_483_647,
                "TW-名稱很長的區服識別碼", true),
            new("long-2", LongWindowName + "（備用）", "MabinogiMobile", 15312, "TW", false)
        ]));

    public Task<BackendReply> SelectGameWindowAsync(string candidateId, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, "請求未完成：game.selectionExpired，遊戲視窗候選已到期，請重新整理後再選一次"));

    public Task<BackendReply> SaveUpdatePolicyAsync(UpdatePolicyView policy, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, "請求未完成：revision.stale"));

    // 本机设置：合奏的开始是屏幕点击、表情同步是聊天草稿，说明文字最长；保存被拒绝时的说明也很长。
    public async Task<RoomPreferencesReply> LoadRoomPreferencesAsync(CancellationToken cancellationToken)
    {
        var reply = await sample.LoadRoomPreferencesAsync(cancellationToken);
        var value = reply.Value! with
        {
            Bindings = new Dictionary<string, MabiTool.Contracts.ModeInputPreferences>(reply.Value!.Bindings)
            {
                ["ensemble"] = new(new(MabiTool.Contracts.LocalBindingKind.Button, X: 32767, Y: 32767, ClientWidth: 32768,
                    ClientHeight: 32768, Dpi: 960), new(MabiTool.Contracts.LocalBindingKind.Key, 0x7B, true, true, true), false)
            }
        };
        return reply with { Value = value };
    }

    public Task<BackendReply> SaveRoomPreferencesAsync(MabiTool.Contracts.RoomPreferences preferences, string etag,
        CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, "請求未完成：revision.stale，合奏本機設定已在另一個視窗修改，請放棄修改後重新開啟本機設定"));

    public async Task<ColorHistoryReply> QueryColorHistoryAsync(string? beforeOperationId, CancellationToken cancellationToken)
    {
        var reply = await sample.QueryColorHistoryAsync(beforeOperationId, cancellationToken);
        return reply with
        {
            Rows =
            [
                new("long", "2026-11-10 21:14", "operation.state.withAVeryLongUnknownStateForLayoutTesting.andAnotherSegment", false,
                [
                    new("區域 1", "#1F3A6B", "#213D70", "ΔE 123456.78", new("judgement.withAVeryLongUnknownState"), true),
                    new("區域 2", "#F2E9D8", null, string.Empty, new(ColorMark.NotStarted), true),
                    new("區域 3", "#8C2F45", "#A8455A", "ΔE 98765.43", new(ColorMark.Disabled), false)
                ]),
                .. reply.Rows
            ]
        };
    }

    public MusicPageData GetMusic()
    {
        var data = sample.GetMusic();
        return data with
        {
            Status = "31,200 張樂譜，來源狀態：上次同步在 2026/11/10 14:02，之後遊戲內的新增與刪除尚未讀取，請回到遊戲開啟樂譜欄後重新同步",
            Playlists = [.. data.Playlists, new("long", "名字非常非常長的自訂樂譜清單名稱測試用", "1,234 首，自訂封面，含備註", "#7C96FF", false)],
            Scores =
            [
                new("long", "標題非常非常長的樂譜：艾明馬夏月光下的遠征隊進行曲（完整合奏版・第二樂章）", "名稱結尾有空白，遊戲無法選取，請在遊戲內重新命名",
                    "名字非常非常長的編曲者名稱", "名稱很長的指定樂器（限定款）", "1:23:45", false, false),
                .. data.Scores
            ],
            Queue =
            [
                new("0", "1", "標題非常非常長的樂譜：艾明馬夏月光下的遠征隊進行曲（完整合奏版・第二樂章）",
                    "名稱很長的指定樂器（限定款），演奏前自動換裝，換裝失敗時沿用目前樂器", "1:23:45", true),
                .. data.Queue.Skip(1)
            ]
        };
    }

    // 合奏：超长昵称与乐器、长房间协议原因、长反馈与本机服务说明。
    public RoomsPageData GetRooms()
    {
        var data = sample.GetRooms();
        var room = data.Room!;
        return data with
        {
            Room = room with
            {
                Members =
                [
                    room.Members[0] with { Nickname = "名字非常非常長的房主角色名稱測試用琉特", Instrument = "名稱很長的指定樂器（限定款・第二代）" },
                    .. room.Members.Skip(1)
                ]
            },
            GameWindowName = "名字非常非常長的角色名稱測試用琉特（第二個用戶端）",
            Feedback = new("請求未完成：room.unknownReasonCodeWithAVeryLongNameForLayoutTesting.andAnotherSegment，房間服務拒絕了本次輪次命令", RoomTone.Error),
            Server = data.Server with
            {
                StateText = "本機服務啟動失敗：room.serverUnavailable.withAVeryLongReasonCodeForLayoutTesting",
                TunnelText = "公網連線失敗：tunnel.componentMissing.withAVeryLongReasonCodeForLayoutTesting"
            }
        };
    }

    public ColorPageData GetColor()
    {
        var data = sample.GetColor();
        return data with
        {
            Draft = data.Draft! with
            {
                Regions =
                [
                    data.Draft.Regions[0] with { AlternativeHex = ["#E6DCC2", "#D9CDB0", "#CFC2A1", "#C4B691", "#BAAA82", "#B09F74", "#A69466", "#9C8958"] },
                    data.Draft.Regions[1] with { Enabled = false },
                    data.Draft.Regions[2] with { Available = false }
                ]
            },
            Status = "尋色已結束：operation.outcome.withAVeryLongUnknownOutcomeForLayoutTesting，請以遊戲內目前顏色為準",
            Profiles = [new("long", LongProfileName, [new("#1F3A6B", 63), new("#F2E9D8", 0), new("#8C2F45", 12)]), new("none", "沒有啟用區域的方案", []),
                .. data.Profiles]
        };
    }

    public InventoryPageData GetInventory()
    {
        var data = sample.GetInventory();
        static InventoryCount N(ulong value) => new(value, InventoryCountState.Value);
        var characters = data.Characters.Append(new InventoryCharacter("long", "名稱非常非常長的角色：暗夜星辰華麗騎士")).ToList();
        return data with
        {
            Accounts = [new("account-long", "帳號標籤非常非常長的帳號：主帳號（台服・第一伺服器）"), .. data.Accounts],
            AccountId = "account-long",
            Characters = characters,
            Rows =
            [
                new("equipment:1", "名稱非常非常長的道具：暗夜星辰華麗禮服（限定版・附帶染色安瓶套組）", "1", "UserConfirmed",
                    "類別名稱很長的分類", new(1_234_567_890, InventoryCountState.Partial), InventoryCount.NotRead,
                    [N(1_234_567_890), N(0), InventoryCount.NotRead], [N(0), InventoryCount.NotRead, InventoryCount.NotRead]),
                .. data.Rows.Select(row => row with { Backpack = [.. row.Backpack, InventoryCount.NotRead], Storage = [.. row.Storage, InventoryCount.NotRead] })
            ]
        };
    }

    public ModulesPageData GetModules() => sample.GetModules() with
    {
        Modules =
        [
            .. sample.GetModules().Modules,
            new("extension.with-a-very-long-identifier.for-layout-testing", "12.345.6789-preview", true, "不可用",
                "相依模組版本不符：需要 music-library 1.2.0 以上，目前為 0.4.6；啟用前請先升級相依模組")
        ]
    };

    public SettingsPageData GetSettings()
    {
        var data = sample.GetSettings();
        return data with
        {
            Shortcuts =
            [
                new("名稱很長的快捷鍵動作：開始或停止目前佇列的演奏", "Ctrl + Alt + Shift + Win + F12",
                    "註冊失敗：按鍵已被其他程式佔用，已改用備用按鍵", false, "long.action", true),
                .. data.Shortcuts
            ]
        };
    }

    public CombatPageData GetCombat()
    {
        var data = sample.GetCombat();
        return data with
        {
            Status = "本場進行中 12:31，部分觀測：中途斷線 6 秒，斷線期間的傷害與效果不計入統計，只讀取不操作遊戲",
            Kpis = [new("整場 DPS", "1,234,567,890,123", true), .. data.Kpis.Skip(1)],
            Skills =
            [
                new("名稱特別長的技能：終結的審判之光・改 (間接)", "98,765,432,109,876", "100%", "123,456", "100%",
                    "不適用", "不適用", "不適用", "1,234,567～98,765,432,109", "100%", "100%", "不適用", "100%",
                    "出血 100%、精神 100%、火焰 100%、其他 100%"),
                .. data.Skills
            ],
            SelfEffects =
            [
                new("?", "名稱目錄尚未收錄而且編號很長的效果 4021740217", "12 層，施放者：名字非常非常長的隊友角色名稱，用戶端估計",
                    "時長未知", "覆蓋 100%"),
                .. data.SelfEffects
            ],
            TargetChips =
            [
                new("boss", "名字非常非常長的首領怪物・第二階段，目前目標", true, true), .. data.TargetChips.Skip(1)
            ],
            History =
            [
                new("e0", "2026/11/10 21:14", "名字非常非常長的首領怪物・第二階段等 12 個目標", "1:04:02", "DPS 1,234,567,890",
                    "部分觀測，中途斷線 6 秒，斷線期間的資料不計入", false, "第 12 段 · 切換地圖後開始的很長原因說明文字"),
                .. data.History
            ],
            FirstAttack = new("名稱特別長的技能：終結的審判之光・改", "98,765,432,109", "名字非常非常長的首領怪物・第二階段"),
            Scope = new(CombatScopeKind.Measurement, "名稱非常非常長的定時測量紀錄：木樁三分鐘・第二輪與增益全開・2026/11/14 21:30",
                [new("m1", "名稱非常非常長的定時測量紀錄：木樁三分鐘・第二輪與增益全開", true), .. data.Scope!.Measurements],
                "不完整：期間切換了地圖、等待識別本機角色、抓包缺口（combat.streamGap.captureDrops）、超出事件保留範圍", string.Empty),
            RangeTargets = [new("long", "名字非常非常長的首領怪物・第二階段", "98,765,432,109,876", "100%", true), .. data.RangeTargets!.Skip(1)],
            TargetDetail = new("名字非常非常長的首領怪物・第二階段", "98,765,432,109,876", "100%", 1),
            Auto = new(true, "自動觀測未能開始：combat.sourceUnavailable.withAVeryLongReasonCodeFromTheBackend，稍後自動重試", true)
        };
    }

    // ---- 诊断：超长探针、键值、事件码与文字详情 ----

    private const string LongCode = "observation.worker.veryLongReasonCodeThatKeepsGoing.forLayoutTesting.0123456789";

    public DiagnosticAccess GetDiagnosticAccess() => sample.GetDiagnosticAccess();

    public event EventHandler<DiagnosticPushedRecord>? DiagnosticRecordReceived { add { } remove { } }
    public event EventHandler? DiagnosticStreamRestored { add { } remove { } }

    public async Task<DiagnosticRuntimeReply> ReadDiagnosticRuntimeAsync(CancellationToken cancellationToken)
    {
        var reply = await sample.ReadDiagnosticRuntimeAsync(cancellationToken);
        var view = reply.Value!;
        return new(view with
        {
            Probes =
            [
                new(LongCode, "game-access-with-a-long-module-name", "unavailable", LongCode,
                    [new(LongCode + "Ms", "123456789012"), new("queues.depthBytes.withAVeryLongNestedKeyName.forLayoutTesting", "9876543210987")],
                    [new(LongCode + ".value", LongCode + "-" + LongCode)], 12345, 6789),
                .. view.Probes
            ]
        }, string.Empty);
    }

    public Task<DiagnosticRecordsReply> QueryDiagnosticRecordsAsync(DiagnosticRecordFilter filter, CancellationToken cancellationToken) =>
        Task.FromResult(new DiagnosticRecordsReply(true, string.Empty,
        [
            new("0123456789abcdef0123456789abcdef", DateTimeOffset.UnixEpoch.AddDays(20000), "warning", "combat-observation",
                LongCode, LongCode + ".event", LongCode,
                [new(LongCode + ".key", LongCode + "-value"), new("another.long.key.name", "an-even-longer-value-" + LongCode)],
                "123456789012345", string.Join('\n', Enumerable.Repeat("System.InvalidOperationException: " + LongCode + " at <install>/Worker/" + LongCode + ".cs:line 123", 12)))
        ], "next"));

    public Task<DiagnosticCaptureReply> GetDiagnosticCaptureAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new DiagnosticCaptureReply(true, string.Empty, new("capture-long", "0123456789abcdef0123456789abcdef",
            DiagnosticsText.Modules, "trace", true, "interrupted", "restarted", DateTimeOffset.UnixEpoch.AddDays(20000),
            DateTimeOffset.UnixEpoch.AddDays(20000).AddMinutes(10), DateTimeOffset.UnixEpoch.AddDays(20000).AddMinutes(3),
            123456789012, 98765432109, 5555555555, 123456789012, 4444444)));

    public Task<DiagnosticCaptureReply> StartDiagnosticCaptureAsync(DebugCaptureSettings settings, CancellationToken cancellationToken) =>
        Task.FromResult(new DiagnosticCaptureReply(false, LongCode, null));

    public Task<DiagnosticCaptureReply> StopDiagnosticCaptureAsync(string captureId, CancellationToken cancellationToken) =>
        Task.FromResult(new DiagnosticCaptureReply(false, LongCode, null));

    public Task<DiagnosticExportReply> ExportDiagnosticsAsync(DiagnosticExportScope scope, string? captureSessionId,
        IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(new DiagnosticExportReply(false, LongCode, null, false, string.Empty));
}

using System.Globalization;
using System.Windows.Input;
using Avalonia.Input;
using Avalonia.Threading;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Rooms;

/// <summary>分段开关里的一个选项。</summary>
public sealed record RoomChoice(string Value, string Label, bool Selected);

/// <summary>
/// 合奏页模型。房间、本轮与成员来自后端（<see cref="BackendPageModel{T}.Data"/>）；本机动作和房主的本轮设置保存在后端的本机设置里，
/// 改动即以读取到的修订保存完整设置，保存期间显示新值，结束后以后端重读为准。界面上的显示选择（卡片收起、未入房时的模式）只在这里。
/// </summary>
public sealed class RoomsPageModel : BackendPageModel<RoomsPageData>, IPageLifecycle
{
    // 页面保留：数据全部来自后端适配层的常驻刷新，回来时读到的就是最近结果，不需要“更新中”。
    protected override bool Cacheable => true;

    // 切走时停止接收通知与页面自己的计时器；时间微调停在防抖中途时立即保存，不丢改动。
    void IPageLifecycle.Deactivate()
    {
        Deactivate();
        clockTimer.Stop();
        if (offsetTimer.IsEnabled)
        {
            offsetTimer.Stop();
            if (offsetDraft is { } value) _ = SaveAsync(current => current with { TimingOffsetMs = value });
        }
    }

    void IPageLifecycle.Activate()
    {
        Activate();
        liveTiming = Backend.GetRoomTiming();
        clockTimer.Start();
    }

    private readonly SemaphoreSlim saveGate = new(1, 1);
    private readonly DispatcherTimer offsetTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    // 倒计时、阶段剩余秒数与乐谱进度按本机单调时钟每 150 ms 更新；时间基准取后端例行刷新的最新值，不另发请求。
    private readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private RoomTiming? liveTiming;
    private (RoomPreferences Value, string ETag)? known;
    private RoomPreferences? pending;
    private RoomFeedback? localFeedback;
    private RoomFeedback? seenFeedback;
    private string localMode = "ensemble";
    private string? recording;
    private string? nicknameDraft, lanDraft, endpointDraft, countdownDraft, durationDraft, tailDraft;
    private string invitationText = string.Empty;
    private string countdownError = string.Empty, durationError = string.Empty, tailError = string.Empty;
    private int? offsetDraft;
    private string serverMode = "loopback";
    private readonly Dictionary<string, InputBindingPreference> remembered = new(StringComparer.Ordinal);

    public RoomsPageModel(IDesktopBackend backend) : base(backend, source => source.GetRooms())
    {
        AbsorbKnown();
        liveTiming = Data.Timing;
        seenFeedback = Data.Feedback;
        RebuildRows();
        ModeCommand = new RelayCommand(value => SetMode(value as string));
        ProviderCommand = new RelayCommand(value =>
        {
            if (value is string provider && CanEditConnection) _ = SaveAsync(current => current with { PublicProvider = provider }, false);
        });
        CreateCommand = new RelayCommand(_ => Create(Provider, LanAddress));
        JoinCommand = new RelayCommand(_ => Join(Provider, LanAddress));
        AdvancedCreateCommand = new RelayCommand(_ => Create("manual", ManualEndpoint));
        AdvancedJoinCommand = new RelayCommand(_ => Join("manual", ManualEndpoint));
        ServerModeCommand = new RelayCommand(value => { if (value is string mode) { serverMode = mode; Raise(nameof(ServerModes)); } });
        StartServerCommand = new RelayCommand(_ => Send("rooms.start-server", serverMode));
        StartKindCommand = new RelayCommand(value => SetStartKind(value as string));
        StopKindCommand = new RelayCommand(value => SetStopKind(value as string));
        RecordCommand = new RelayCommand(value => { if (value is string slot && CanEditLocal) { recording = slot; RaiseLocal(); } });
        CancelRecordCommand = new RelayCommand(_ => { recording = null; RaiseLocal(); });
        PickCommand = new RelayCommand(value => { if (value is string slot && CanEditLocal) PickRequested?.Invoke(this, slot); });
        StopRuleCommand = new RelayCommand(value =>
        {
            if (value is string rule && Status.CanEditHostSettings)
                _ = SaveAsync(current => current with { MusicStopRule = new OpenEnum<MusicStopRule>(rule) }, false);
        });
        ResetOffsetCommand = new RelayCommand(_ => SetOffset(0));
        ReadyCommand = new RelayCommand(_ => _ = ReadyAsync());
        StartCommand = new RelayCommand(_ => _ = StartAsync());
        ToggleCommand = new RelayCommand(value => Toggle(value as string));
        CloseLayerCommand = new RelayCommand(_ => { LanGuideOpen = false; AdvancedOpen = false; });
        OperateGameCommand = new RelayCommand(_ =>
        {
            if (CanToggleOperateGame) Send("rooms.game-operation", Data.OperateGame ? "off" : "on");
            else Raise(nameof(OperateGame));
        });
        clockTimer.Tick += (_, _) => OnClockTick();
        clockTimer.Start();
        RoomsPreference.Changed += OnSectionsChanged;
        CardStateStore.Changed += OnCardChanged;
        offsetTimer.Tick += (_, _) =>
        {
            offsetTimer.Stop();
            if (offsetDraft is { } value) _ = SaveAsync(current => current with { TimingOffsetMs = value });
        };
    }

    /// <summary>页面请求点选按钮位置（参数为 start 或 stop）；由视图经 <see cref="PickAsync"/> 激活游戏窗口并打开拾取层。</summary>
    public event EventHandler<string>? PickRequested;

    public ICommand ModeCommand { get; }
    public ICommand ProviderCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand JoinCommand { get; }
    public ICommand AdvancedCreateCommand { get; }
    public ICommand AdvancedJoinCommand { get; }
    public ICommand ServerModeCommand { get; }
    public ICommand StartServerCommand { get; }
    public ICommand StartKindCommand { get; }
    public ICommand StopKindCommand { get; }
    public ICommand RecordCommand { get; }
    public ICommand CancelRecordCommand { get; }
    public ICommand PickCommand { get; }
    public ICommand StopRuleCommand { get; }
    public ICommand ResetOffsetCommand { get; }
    public ICommand ReadyCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand ToggleCommand { get; }
    public ICommand CloseLayerCommand { get; }
    public ICommand OperateGameCommand { get; }
    /// <summary>打开设置页的抓包区。</summary>

    // ---- 状态 ----

    /// <summary>按原工具默认值补齐后的本机设置；保存期间是保存中的新值。未连接时为空。</summary>
    public RoomPreferences? Settings => pending ?? (known is { } value ? RoomLocalSettings.Effective(value.Value) : null);

    public string Mode => Data.Room?.Mode ?? localMode;
    public bool IsEmote => Mode == "emote";
    public bool IsEnsemble => !IsEmote;
    public RoomStatus Status => status ??= RoomFlow.Describe(Data with { Timing = liveTiming }, Mode, Settings,
        countdownError.Length + durationError.Length + tailError.Length > 0);
    private RoomStatus? status;
    public bool InRoom => Data.InRoom;
    public bool NotInRoom => !Data.InRoom;
    public bool IsHost => Data.IsHost;

    public IReadOnlyList<RoomChoice> Modes =>
    [
        new("emote", Pick("表情动作", "表情動作"), IsEmote), new("ensemble", Pick("合奏", "合奏"), IsEnsemble)
    ];

    public string FlowHint => IsEmote ? Pick("各成员准备好动作后，由房主统一开始。", "各成員準備好動作後，由房主統一開始。")
        : Pick("各组乐谱使用者使用自己的乐谱同步开始，组员依序在游戏中加入。", "各組樂譜使用者使用自己的樂譜同步開始，組員依序在遊戲中加入。");

    /// <summary>房间服务让本机退出时的提示（原工具的“你已被房主移出房间。”与“房主已关闭房间，你已退出。”），重新建房或加入后消失。</summary>
    public bool RemovedByHost => !Data.InRoom && Data.ExitReason == "kicked";
    public bool ClosedByHost => !Data.InRoom && Data.ExitReason == "roomClosed";
    public bool HasExitNotice => RemovedByHost || ClosedByHost;

    /// <summary>反馈横幅：最近一次合奏操作或本机设置保存的结果。</summary>
    public RoomFeedback? Feedback => localFeedback ?? Data.Feedback;
    public bool HasFeedback => Feedback is { Text.Length: > 0 };
    public string FeedbackText => Feedback?.Text ?? string.Empty;
    public bool FeedbackSuccess => Feedback?.Tone == RoomTone.Success;
    public bool FeedbackError => Feedback?.Tone == RoomTone.Error;
    public bool FeedbackWarning => Feedback?.Tone == RoomTone.Warning;
    public bool FeedbackInfo => Feedback?.Tone == RoomTone.Info;

    /// <summary>开始按钮停用的原因（等待上一次结果、倒计时等参数有误），停用时显示在操作行下方。</summary>
    public string StartHint => Status.ShowStart && !Status.CanStart ? Status.StartHint : string.Empty;
    public bool HasStartHint => StartHint.Length > 0;

    public bool PhaseSuccess => Status.PhaseTone == RoomTone.Success;
    public bool PhaseError => Status.PhaseTone == RoomTone.Error;

    /// <summary>后端能力表说明合奏开始不可用的原因；可用时为空。</summary>
    public string CapabilityNotice => Backend.GetCapabilityNotice(CapabilityIds.RoomsCoordinate) ?? string.Empty;
    public bool HasCapabilityNotice => CapabilityNotice.Length > 0;

    // ---- 卡片收起与二级层 ----
    // 四张页面卡片的折叠由卡片外壳自己存取；底部状态卡自己排版（收起后操作行仍在），它的展开状态由这里读写同一个卡片状态存取处。

    public bool StatusExpanded
    {
        get => CardStateStore.IsExpanded(RoomsPreference.Page, RoomsPreference.Status);
        set => CardStateStore.SetExpanded(RoomsPreference.Page, RoomsPreference.Status, value);
    }

    private void OnCardChanged(object? sender, string key)
    {
        if (key == CardStateStore.Key(RoomsPreference.Page, RoomsPreference.Status)) Raise(nameof(StatusExpanded));
    }
    private bool lanGuideOpen, advancedOpen;
    public bool LanGuideOpen { get => lanGuideOpen; set { lanGuideOpen = value; Raise(); } }
    public bool AdvancedOpen { get => advancedOpen; set { advancedOpen = value; Raise(); } }

    private void Toggle(string? section)
    {
        switch (section)
        {
            case "lan-guide": LanGuideOpen = !LanGuideOpen; return;
            case "advanced": AdvancedOpen = !AdvancedOpen; return;
        }
    }

    private void OnSectionsChanged(object? sender, EventArgs e) => RaiseLocal();

    // ---- 连接房间 ----

    public bool CanEditConnection => Data.Connected && !Data.InRoom && Settings is not null;
    public string Provider => Settings?.PublicProvider is "cloudflare" or "lan" ? Settings.PublicProvider : "community";
    public bool IsLan => Provider == "lan";

    public IReadOnlyList<RoomChoice> Providers =>
    [
        new("community", Pick("服务器直连", "伺服器直連"), Provider == "community"),
        new("cloudflare", Pick("Cloudflare 公网", "Cloudflare 公網"), Provider == "cloudflare"),
        new("lan", Pick("局域网", "區域網路"), Provider == "lan")
    ];

    public string ProviderHint => Data.InRoom && !Data.IsHost ? Pick("已使用房主邀请中的服务地址。", "已使用房主邀請中的服務位址。") : Provider switch
    {
        "lan" => Pick("局域网：连接同一路由器，或先用组网软件互通。房主建房后分享邀请；成员也可填写房主地址与房间码加入。",
            "區域網路：連接同一路由器，或先用組網軟體互通。房主建房後分享邀請；成員也可填寫房主位址與房間碼加入。"),
        "cloudflare" => Pick("Cloudflare：创建临时公网地址，好友粘贴邀请即可加入。", "Cloudflare：建立臨時公網位址，好友貼上邀請即可加入。"),
        _ => Pick($"服务器直连：{CommunityHost}。可创建房间，或输入六位房间码加入。", $"伺服器直連：{CommunityHost}。可建立房間，或輸入六位房間碼加入。")
    };

    // 服务器直连入口取后端本机设置的只读字段 communityEndpoint。
    private string CommunityHost => new Uri(Settings?.CommunityEndpoint ?? RoomPreferences.BuiltInCommunityEndpoint).Authority;

    public string CreateLabel => IsLan ? Pick("创建房间", "建立房間") : Pick("创建公网房间", "建立公網房間");
    public string JoinLabel => Provider == "cloudflare" ? Pick("加入邀请", "加入邀請") : Pick("加入房间", "加入房間");
    public string InvitationHint => Provider == "cloudflare" ? Pick("粘贴房主复制的完整邀请", "貼上房主複製的完整邀請")
        : Pick("输入六位房间码，或粘贴完整邀请", "輸入六位房間碼，或貼上完整邀請");

    // ---- 昵称：下拉选择来源。抓包昵称取全局抓包识别的本机身份（与窗口左上角同一状态源），只读并随识别更新；
    // 自定义昵称可编辑，值保存在后端本机设置的 Nickname，切回抓包昵称时保留。来源选择存于前端偏好。

    private IReadOnlyList<NicknameSourceOption>? nicknameSources;
    public IReadOnlyList<NicknameSourceOption> NicknameSources => nicknameSources ??=
    [
        new(RoomsPreference.CaptureNickname, Pick("抓包昵称", "抓包暱稱")),
        new(RoomsPreference.CustomNickname, Pick("自定义昵称", "自訂暱稱"))
    ];

    public NicknameSourceOption NicknameSource
    {
        get => NicknameSources.First(option => option.Value == RoomsPreference.NicknameSource);
        set
        {
            if (value is null || value.Value == RoomsPreference.NicknameSource) return;
            if (value.Value != RoomsPreference.CustomNickname) nicknameDraft = null;
            RoomsPreference.SetNicknameSource(value.Value);
        }
    }

    public bool NicknameIsCustom => RoomsPreference.NicknameSource == RoomsPreference.CustomNickname;
    public bool NicknameReadOnly => !NicknameIsCustom;
    public bool CanEditNickname => CanEditConnection && NicknameIsCustom;

    /// <summary>输入框内容：抓包昵称时是识别到的本机角色名（游戏原文），未识别时为空并在下方说明；自定义时是保存的值或正在输入的内容。</summary>
    public string Nickname
    {
        get => NicknameIsCustom ? nicknameDraft ?? Settings?.Nickname ?? string.Empty : Capture.Nickname ?? string.Empty;
        set { if (NicknameIsCustom) nicknameDraft = value; }
    }

    public string NicknamePlaceholder => NicknameIsCustom ? Pick("1–20 字", "1–20 字") : Pick("未识别", "未辨識");

    /// <summary>抓包昵称未识别时的原因；已识别或自定义时为空。</summary>
    public string NicknameNote => NicknameIsCustom || Capture.Nickname is { Length: > 0 } ? string.Empty
        : Pick("未识别：", "未辨識：") + (Capture.Status.Running
            ? Pick("抓包中，尚未识别本机角色，请在游戏里切换一次地图。", "抓包中，尚未辨識本機角色，請在遊戲裡切換一次地圖。")
            : CaptureText.Status(Capture.Status));
    public bool HasNicknameNote => NicknameNote.Length > 0;

    public string LanAddress
    {
        get => lanDraft ?? (Settings?.LanEndpoint is { Length: > 0 } saved ? saved : Data.Server.LanEndpoints.FirstOrDefault() ?? string.Empty);
        set => lanDraft = value;
    }

    public IReadOnlyList<string> LanEndpoints => StableLanEndpoints();

    public string ManualEndpoint
    {
        get => endpointDraft ?? Settings?.Endpoint ?? string.Empty;
        set => endpointDraft = value;
    }

    public string InvitationText { get => invitationText; set => invitationText = value; }

    /// <summary>昵称、局域网地址、手动地址离开输入框时保存到本机设置。</summary>
    public void CommitConnection()
    {
        var nickname = NicknameIsCustom ? nicknameDraft?.Trim() : null;
        var lan = lanDraft?.Trim();
        var endpoint = endpointDraft?.Trim();
        if (Settings is not { } current) return;
        if ((nickname is not null && nickname != current.Nickname && nickname.Length <= 20) || (lan is not null && lan != current.LanEndpoint)
            || (endpoint is not null && endpoint != current.Endpoint))
            _ = SaveAsync(value => value with
            {
                Nickname = nickname is { Length: <= 20 } ? nickname : value.Nickname, LanEndpoint = lan ?? value.LanEndpoint,
                Endpoint = endpoint ?? value.Endpoint
            }, false);
    }

    // 抓包昵称未识别时不发送，在反馈横幅写明原因。
    private bool NicknameMissing()
    {
        if (Nickname.Trim().Length > 0) return false;
        ShowLocal(new(NicknameIsCustom ? Pick("请填写 1–20 字的昵称。", "請填寫 1–20 字的暱稱。") : NicknameNote, RoomTone.Error));
        return true;
    }

    private void Create(string provider, string endpoint)
    {
        CommitConnection();
        if (NicknameMissing()) return;
        Send("rooms.create", $"{provider}\n{Nickname.Trim()}\n{endpoint.Trim()}\n{Mode}");
    }

    private void Join(string provider, string endpoint)
    {
        CommitConnection();
        if (NicknameMissing()) return;
        Send("rooms.join", $"{provider}\n{Nickname.Trim()}\n{InvitationText.Trim()}\n{endpoint.Trim()}");
        invitationText = string.Empty;
        Raise(nameof(InvitationText));
    }

    public string RoomTitle => Data.Room is { } room
        ? Pick($"房间码 {room.Code} · {(Data.RoomConnected ? "已连接" : "连接中断，可重连")}",
            $"房間碼 {room.Code} · {(Data.RoomConnected ? "已連線" : "連線中斷，可重連")}")
        : Pick("创建房间，或粘贴邀请加入", "建立房間，或貼上邀請加入");

    public string ConnectionSummary => !Data.Connected ? Pick("未连接后端", "未連接後端")
        : Data.Room is { } room ? (Data.RoomConnected ? Pick($"已连接 · 房间 {room.Code}", $"已連線 · 房間 {room.Code}") : Pick("连接中断", "連線中斷"))
        : Pick("尚未连接", "尚未連線");

    public bool ShowCopyInvite => Data.IsHost && Data.RoomConnected;
    public bool ShowReconnect => Data.InRoom && !Data.RoomConnected;
    public string LeaveLabel => Data.IsHost ? Pick("关闭房间", "關閉房間") : Pick("离开房间", "離開房間");
    public bool InvitationCopied => Data.InvitationCopied;
    public bool HasFallbackInvitation => Data.FallbackInvitation is { Length: > 0 };
    public string FallbackInvitation => Data.FallbackInvitation ?? string.Empty;

    public string LanGuideText
    {
        get
        {
            var addresses = Data.Server.LanEndpoints;
            return Pick("房主\n1. 两台电脑先连接同一个路由器。异地玩家先用 Tailscale 等组网软件连通。\n2. 选择“局域网”，点击“创建房间”；本机服务启动后，在地址框选择本机地址再分享邀请。\n3. Windows 询问防火墙权限时，允许当前使用的网络。\n4. 点击“复制邀请”，发给好友。\n\n成员\n1. 粘贴房主的完整邀请，点击“加入房间”。\n2. 也可填写房主地址和六位房间码加入。\n\n连接不上时\n确认双方在同一网络；关闭路由器的访客隔离。房主检查 Windows 防火墙是否允许本工具的房间服务端口。组网软件用户填写房主的虚拟网卡地址。\n\n本机可用地址\n",
                "房主\n1. 兩台電腦先連接同一個路由器。異地玩家先用 Tailscale 等組網軟體連通。\n2. 選擇「區域網路」，點擊「建立房間」；本機服務啟動後，在位址框選擇本機位址再分享邀請。\n3. Windows 詢問防火牆權限時，允許目前使用的網路。\n4. 點擊「複製邀請」，發給好友。\n\n成員\n1. 貼上房主的完整邀請，點擊「加入房間」。\n2. 也可填寫房主位址和六位房間碼加入。\n\n連線不上時\n確認雙方在同一網路；關閉路由器的訪客隔離。房主檢查 Windows 防火牆是否允許本工具的房間服務連接埠。組網軟體使用者填寫房主的虛擬網卡位址。\n\n本機可用位址\n")
                + (addresses.Count == 0 ? Pick("本机服务启动后列出。", "本機服務啟動後列出。") : string.Join("\n", addresses));
        }
    }

    public IReadOnlyList<RoomChoice> ServerModes =>
    [
        new("loopback", Pick("仅本机", "僅本機"), serverMode == "loopback"), new("lan", Pick("局域网", "區域網路"), serverMode == "lan"),
        new("public", Pick("公网", "公網"), serverMode == "public")
    ];

    // ---- 抓包识别（只读）：本机角色与当前乐谱；抓包状态与设置入口在窗口左上角 ----

    private RoomCapture Capture => Data.Capture ?? RoomCapture.Unknown;
    public bool HasObserved => Capture.Running && Capture.Nickname is { Length: > 0 };
    public string ObservedNickname => Capture.Nickname ?? string.Empty;
    public bool HasObservedScore => HasObserved && Capture.Music is not null;
    public string ObservedScoreTitle => Capture.Music is { } score
        ? score.Title.Trim().Length == 0 ? Pick("未命名乐谱", "未命名樂譜") : score.Title : string.Empty;
    /// <summary>整谱时长；演奏自动循环，一轮结束按开始时间加整谱时长推算。</summary>
    public string ObservedScoreDuration => Capture.Music?.WholeScoreDurationSeconds is { } whole
        ? Pick($"（{RoomJson.Seconds(whole)} 秒）", $"（{RoomJson.Seconds(whole)} 秒）")
        : Capture.Music is null ? string.Empty : Pick("（时长待解析）", "（時長待解析）");

    // ---- 本机动作 ----

    public bool CanEditLocal => Settings is not null && Status.CanEditLocal;
    /// <summary>“操作游戏”开关（房间会话的 operateGame，不保存到设置）；本轮进行中不能切换。</summary>
    public bool OperateGame => Data.OperateGame;
    public bool CanToggleOperateGame => Data.Connected && !Status.Active;
    public string GameWindowText => Data.GameWindowName is { } name ? Pick($"游戏窗口：{name}", $"遊戲視窗：{name}") : Pick("尚未选择游戏窗口", "尚未選擇遊戲視窗");
    public string LocalSummary => Data.Simulation ? Pick("模拟模式", "模擬模式") : Data.GameWindowName ?? Pick("选择游戏窗口并设置操作", "選擇遊戲視窗並設定操作");
    public RoomStartKind StartKind => Settings is { } value ? RoomLocalSettings.StartKind(value, Mode) : RoomStartKind.Key;

    public IReadOnlyList<RoomChoice> StartKinds => IsEmote
        ?
        [
            new("key", Pick("快捷键", "快捷鍵"), StartKind == RoomStartKind.Key), new("button", Pick("游戏按钮", "遊戲按鈕"), StartKind == RoomStartKind.Button),
            new("chat", Pick("预填指令 · 仅按回车", "預填指令 · 僅按 Enter"), StartKind == RoomStartKind.ChatDraft)
        ]
        : [new("key", Pick("快捷键", "快捷鍵"), StartKind == RoomStartKind.Key), new("button", Pick("游戏按钮", "遊戲按鈕"), StartKind == RoomStartKind.Button)];

    private InputBindingPreference? StopBinding => Settings is { } value ? RoomLocalSettings.Stop(value, Mode) : null;
    private bool StopIsButton => StopBinding is { } binding ? binding.Kind.KnownValue == LocalBindingKind.Button : stopKindOverride == "button";
    private string? stopKindOverride;

    public IReadOnlyList<RoomChoice> StopKinds =>
        [new("key", Pick("快捷键", "快捷鍵"), !StopIsButton), new("button", Pick("游戏按钮", "遊戲按鈕"), StopIsButton)];

    public bool StartIsKey => StartKind == RoomStartKind.Key;
    public bool StartIsButton => StartKind == RoomStartKind.Button;
    public bool StartIsChat => StartKind == RoomStartKind.ChatDraft;
    public bool StopIsKey => !StopIsButton;
    public bool StopIsButtonKind => StopIsButton;

    public string StartKeyText => recording == "start" ? Pick("请直接按下快捷键…", "請直接按下快捷鍵…")
        : Settings is { } value && RoomLocalSettings.KeyText(RoomLocalSettings.Start(value, Mode)) is { Length: > 0 } text ? text : Pick("点击录入", "點擊錄入");
    public string StopKeyText => recording == "stop" ? Pick("请直接按下快捷键…", "請直接按下快捷鍵…")
        : RoomLocalSettings.KeyText(StopBinding) is { Length: > 0 } text ? text : Pick("点击录入", "點擊錄入");
    public string StartButtonText => RoomLocalSettings.ButtonText(Settings is { } value ? RoomLocalSettings.Start(value, Mode) : null);
    public string StopButtonText => RoomLocalSettings.ButtonText(StopBinding);
    public bool RecordingStart => recording == "start";
    public bool RecordingStop => recording == "stop";
    public bool IsRecording => recording is not null;

    public string ChatHint => Pick("在游戏聊天框填好动作指令，再回工具点击“就绪”。保持聊天框打开，开始时会自动切回游戏并按一次回车。",
        "在遊戲聊天框填好動作指令，再回工具點擊「就緒」。保持聊天框開啟，開始時會自動切回遊戲並按一次 Enter。");

    public string ShortcutHint => IsRecording
        ? Pick("请直接按下快捷键，可同时按 Ctrl / Alt / Shift。Esc 取消录入。", "請直接按下快捷鍵，可同時按 Ctrl / Alt / Shift。Esc 取消錄入。")
        : string.Empty;

    public string InputState => Data.Simulation
        ? Pick("模拟模式可先检查联机与倒计时。准备实测时开启“操作游戏”。", "模擬模式可先檢查連線與倒數。準備實測時開啟「操作遊戲」。")
        : Data.StopKey is { } key
            ? IsEmote ? Pick($"{key}：取消本机待触发任务。", $"{key}：取消本機待觸發任務。") : Pick($"{key}：本机停止。", $"{key}：本機停止。")
            : IsEmote ? Pick("停止快捷键不可用，使用工具中的取消本机任务按钮。", "停止快捷鍵不可用，使用工具中的取消本機任務按鈕。")
                : Pick("停止快捷键不可用，使用工具中的本机停止按钮。", "停止快捷鍵不可用，使用工具中的本機停止按鈕。");

    private void SetStartKind(string? value)
    {
        if (!CanEditLocal || Settings is not { } current) return;
        var kind = value switch { "button" => RoomStartKind.Button, "chat" when IsEmote => RoomStartKind.ChatDraft, "key" => RoomStartKind.Key, _ => (RoomStartKind?)null };
        if (kind is null || kind == StartKind) return;
        if (RoomLocalSettings.Start(current, Mode) is { } previous) remembered[Mode + ".start." + previous.Kind.RawValue] = previous;
        var keep = remembered.GetValueOrDefault(Mode + ".start." + (kind == RoomStartKind.Button ? "Button" : "Key"));
        _ = SaveAsync(settings => RoomLocalSettings.WithStartKind(settings, Mode, kind.Value, keep));
    }

    private void SetStopKind(string? value)
    {
        if (!CanEditLocal || value is not ("key" or "button") || (value == "button") == StopIsButton) return;
        if (StopBinding is { } previous) remembered[Mode + ".stop." + previous.Kind.RawValue] = previous;
        var restore = remembered.GetValueOrDefault(Mode + ".stop." + (value == "button" ? "Button" : "Key"));
        stopKindOverride = value;
        _ = SaveAsync(settings => RoomLocalSettings.WithStop(settings, Mode, restore));
    }

    /// <summary>录入状态下收到的按键：只按修饰键时继续等待，Esc 取消，其余写入正在录入的那一格并保存。返回是否处理了这次按键。</summary>
    public bool OnKey(Key key, KeyModifiers modifiers)
    {
        if (recording is not { } slot) return false;
        if (key == Key.Escape)
        {
            recording = null;
            RaiseLocal();
            return true;
        }
        if (VirtualKeys.FromKey(key) is not { } code) return true;
        var binding = new InputBindingPreference(LocalBindingKind.Key, code, modifiers.HasFlag(KeyModifiers.Control),
            modifiers.HasFlag(KeyModifiers.Alt), modifiers.HasFlag(KeyModifiers.Shift));
        recording = null;
        if (slot == "stop") stopKindOverride = "key";
        _ = SaveAsync(settings => slot == "start"
            ? RoomLocalSettings.WithStart(RoomLocalSettings.WithStartKind(settings, Mode, RoomStartKind.Key), Mode, binding)
            : RoomLocalSettings.WithStop(settings, Mode, binding));
        return true;
    }

    /// <summary>
    /// 点选按钮位置：Moongate 仍在前台时先激活绑定的游戏窗口（没有绑定时后端不发请求），再由 <paramref name="picker"/> 隐藏自身并显示拾取层。
    /// 没能切到游戏窗口不阻止拾取，反馈里注明原因。
    /// </summary>
    public async Task PickAsync(string slot, Func<RoomGameWindow?, Task<InputBindingPreference?>> picker)
    {
        var activation = await Backend.ActivateGameWindowAsync(CancellationToken.None);
        var note = activation.Missed
            ? Pick("未能切到游戏窗口", "未能切到遊戲視窗") + (activation.Message.Length > 0 ? "（" + activation.Message + "）" : string.Empty)
            : null;
        ApplyPicked(slot, await picker(Data.GameWindow), note);
    }

    /// <summary>点选按钮位置的结果；为空表示取消，原设置保留。<paramref name="note"/> 是没能切到游戏窗口等附加说明。</summary>
    public void ApplyPicked(string slot, InputBindingPreference? binding, string? note = null)
    {
        var suffix = note is { Length: > 0 } ? note + "。" : string.Empty;
        if (binding is null)
        {
            ShowLocal(new(Pick("已取消位置记录，原按钮设置已保留。", "已取消位置記錄，原按鈕設定已保留。") + suffix, RoomTone.Info));
            return;
        }
        if (slot == "stop") stopKindOverride = "button";
        _ = SaveAsync(settings => slot == "start"
            ? RoomLocalSettings.WithStart(RoomLocalSettings.WithStartKind(settings, Mode, RoomStartKind.Button), Mode, binding)
            : RoomLocalSettings.WithStop(settings, Mode, binding),
            success: Pick($"已记录{(slot == "start" ? "开始" : "停止")}按钮位置 ({binding.X}, {binding.Y})，设置已保存。",
                $"已記錄{(slot == "start" ? "開始" : "停止")}按鈕位置 ({binding.X}, {binding.Y})，設定已儲存。") + suffix);
    }

    // ---- 成员表 ----

    public IReadOnlyList<RosterRow> Rows { get; private set; } = [];
    public bool NoRows => Rows.Count == 0;
    public string RosterTitle => IsEnsemble
        ? Pick($"合奏成员 · {Rows.Count} 人 · 本轮 {Rows.Count(row => row.Selected)} 人", $"合奏成員 · {Rows.Count} 人 · 本輪 {Rows.Count(row => row.Selected)} 人")
        : Pick($"本轮成员 · {Rows.Count} 人 · 本轮 {Rows.Count(row => row.Selected)} 人", $"本輪成員 · {Rows.Count} 人 · 本輪 {Rows.Count(row => row.Selected)} 人");
    public string RosterHint => !IsEnsemble ? string.Empty : Data.IsHost
        ? Pick("房主可编辑分组、顺序、身份与乐器，也可拖动行调整分组。每组一位乐谱使用者，指定新成员会把原成员改为参与者。修改后请重新就绪。",
            "房主可編輯分組、順序、身分與樂器，也可拖動行調整分組。每組一位樂譜使用者，指定新成員會把原成員改為參與者。修改後請重新就緒。")
        : Pick("找到自己的组别、顺序与乐器；乐谱使用者开始后，依序在游戏中加入。", "找到自己的組別、順序與樂器；樂譜使用者開始後，依序在遊戲中加入。");
    public bool CanEditRoster => Status.CanEditRoster;
    public bool CanSelect => Data.IsHost && Data.RoomConnected && !Status.Active;
    public IReadOnlyList<RoomCharacter> Characters => Data.Room?.Characters ?? [];
    /// <summary>
    /// 指定总指挥的下拉：房间成员，选中项是当前总指挥；房主在没有进行中的本轮时可改，改变即发送。
    /// 成员与昵称不变时沿用同一列表，下拉不因就绪等变化重置选中项。
    /// </summary>
    public IReadOnlyList<RoomConductorChoice> ConductorChoices { get; private set; } = [];
    public RoomConductorChoice? Conductor
    {
        get => ConductorChoices.FirstOrDefault(choice => choice.Id == Data.Room?.LeaderId);
        set
        {
            if (value is not null && value.Id != Data.Room?.LeaderId && CanSelectConductor) Send("rooms.leader", value.Id);
        }
    }
    public bool CanSelectConductor => Data.IsHost && Data.RoomConnected && !Status.Active;
    public string LeaderPurpose => RoomFlow.LeaderPurpose(Settings?.MusicStopRule.RawValue ?? "first-cycle");

    /// <summary>单元格编辑：一次提交一个字段；组号与顺序先在这里检查 1–24。</summary>
    public void EditCell(RosterRow row, string field, string value)
    {
        if (field != "selected" ? !CanEditRoster : !CanSelect) return;
        if (field is "group" or "order")
        {
            if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 24)
            {
                ShowLocal(new(field == "group" ? Pick("合奏组请输入 1–24。", "合奏組請輸入 1–24。")
                    : Pick("顺序请输入 1–24；组内会自动连续排列。", "順序請輸入 1–24；組內會自動連續排列。"), RoomTone.Error));
                row.ResetInputs();
                return;
            }
            if (number == (field == "group" ? row.Member.Group : row.Member.Order))
            {
                // “02”“ 2”等与当前值相同的写法不发送，格里改回规范写法。
                row.ResetInputs();
                return;
            }
            value = number.ToString(CultureInfo.InvariantCulture);
        }
        Send("rooms.roster", $"{row.Id}\n{field}\n{value}");
    }

    /// <summary>拖动落位或 Alt+↑/↓：按落点提交分组与组内顺序。</summary>
    public bool MoveRow(int from, int target)
    {
        if (!CanEditRoster || RosterDrop.Resolve(Rows.Select(row => row.Member.Group).ToList(), from, target) is not { } place) return false;
        Send("rooms.move", $"{Rows[from].Id}\n{place.Group}\n{place.Order}");
        return true;
    }

    /// <summary>行菜单里的组内上移、下移与移到分组，换成对应的分组与顺序。</summary>
    public void MoveInGroup(RosterRow row, int delta) =>
        Send("rooms.move", $"{row.Id}\n{row.Member.Group}\n{Math.Clamp(row.Member.Order + delta, 1, 24)}");

    public void MoveToGroup(RosterRow row, int group) => Send("rooms.roster", $"{row.Id}\ngroup\n{group}");
    public void BindCharacter(RosterRow row, RoomCharacter? character) =>
        Send("rooms.character", $"{row.Id}\n{character?.CharacterId}\n{character?.RealmId}");

    /// <summary>行菜单的设为总指挥与移出房间（原工具 select-leader、kick-member）。</summary>
    public void SetConductor(RosterRow row)
    {
        if (CanSelectConductor && row.Id != Data.Room?.LeaderId) Send("rooms.leader", row.Id);
    }

    public void Kick(RosterRow row)
    {
        if (Data.IsHost && Data.RoomConnected && !row.IsSelf) Send("rooms.kick", row.Id);
    }

    // ---- 状态卡：房主的本轮设置与时间微调 ----

    public string StopRule => Settings?.MusicStopRule.RawValue ?? "first-cycle";
    public IReadOnlyList<RoomChoice> StopRules =>
    [
        new("manual", Pick("房主手动统一停止", "房主手動統一停止"), StopRule == "manual"),
        new("duration", Pick("手动指定演奏时长", "手動指定演奏時長"), StopRule == "duration"),
        new("first-cycle", Pick("总指挥一轮后全员停止", "總指揮一輪後全員停止"), StopRule == "first-cycle")
    ];

    public bool ShowStopControls => IsEnsemble && (!Data.InRoom || Data.IsHost);
    public bool ShowDuration => ShowStopControls && StopRule == "duration";
    public bool ShowTail => IsEnsemble && (Status.Active ? Data.Room?.Round?.StopRule == "first-cycle" : StopRule == "first-cycle");
    public bool EditTail => ShowTail && Status.CanEditHostSettings;
    public bool ShowTailRound => ShowTail && !Status.CanEditHostSettings;
    public bool ShowCountdown => Data.IsHost;

    public string CountdownText { get => countdownDraft ?? RoomLocalSettings.Seconds(Settings?.CountdownMs ?? 5000); set => countdownDraft = value; }
    public string DurationText { get => durationDraft ?? RoomLocalSettings.Seconds(Settings?.MusicDurationMs ?? 30000); set => durationDraft = value; }
    public string TailText { get => tailDraft ?? RoomLocalSettings.Seconds(Settings?.MusicTailHoldMs ?? 0); set => tailDraft = value; }
    public string CountdownError => countdownError;
    public string DurationError => durationError;
    public string TailError => tailError;
    public bool HasCountdownError => countdownError.Length > 0;
    public bool HasDurationError => durationError.Length > 0;
    public bool HasTailError => tailError.Length > 0;
    public string CountdownNote => Status.Active ? Status.CountdownNote : string.Empty;
    public bool HasCountdownNote => CountdownNote.Length > 0;

    /// <summary>倒计时、指定时长或尾音保留在按回车或离开输入框时提交；不合法时不保存，输入保留并说明范围。</summary>
    public void CommitNumber(string field)
    {
        string? error;
        switch (field)
        {
            case "countdown" when countdownDraft is { } text:
                var countdown = RoomLocalSettings.ParseCountdown(text, out error);
                countdownError = error ?? string.Empty;
                if (countdown is { } ms) { countdownDraft = null; _ = SaveAsync(value => value with { CountdownMs = ms }, false); }
                break;
            case "duration" when durationDraft is { } text:
                var duration = RoomLocalSettings.ParseDuration(text, out error);
                durationError = error ?? string.Empty;
                if (duration is { } d) { durationDraft = null; _ = SaveAsync(value => value with { MusicDurationMs = d }, false); }
                break;
            case "tail" when tailDraft is { } text:
                var tail = RoomLocalSettings.ParseTail(text, out error);
                tailError = error ?? string.Empty;
                if (tail is { } t) { tailDraft = null; _ = SaveAsync(value => value with { MusicTailHoldMs = t }, false); }
                break;
            default:
                return;
        }
        RaiseLocal();
    }

    public int Offset => offsetDraft ?? Settings?.TimingOffsetMs ?? 0;

    /// <summary>滑块的值；拖动停下 400 ms 后保存，并撤销本机就绪。</summary>
    public double OffsetValue
    {
        get => Offset;
        set => SetOffset((int)Math.Round(value), debounce: true);
    }

    public string OffsetText
    {
        get => Offset.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (RoomLocalSettings.ParseOffset(value, out _) is { } offset) SetOffset(offset);
        }
    }

    public string OffsetSummary => Pick("本机时间微调 · ", "本機時間微調 · ") + RoomLocalSettings.OffsetText(Offset);

    private void SetOffset(int value, bool debounce = false)
    {
        if (!Status.CanEditLocal || Settings is null || value == Offset && offsetDraft is null) return;
        offsetDraft = Math.Clamp(value, -2000, 2000);
        Raise(nameof(Offset));
        Raise(nameof(OffsetValue));
        Raise(nameof(OffsetText));
        Raise(nameof(OffsetSummary));
        offsetTimer.Stop();
        if (debounce) offsetTimer.Start();
        else _ = SaveAsync(current => current with { TimingOffsetMs = offsetDraft.Value });
    }

    // ---- 执行记录与诊断 ----

    public string ClockText => RoomClock.ClockText(liveTiming);

    // ---- 倒计时与乐谱进度（状态卡、总指挥乐谱卡） ----

    public bool HasCountdown => Status.Countdown is not null;
    public string CountdownLabel => Status.Countdown?.Label ?? string.Empty;
    public string CountdownValue => Status.Countdown?.Value ?? string.Empty;
    public double CountdownFraction => Status.Countdown?.Fraction ?? 0;
    public bool CountdownStopping => Status.Countdown?.Stopping == true;
    public bool CountdownStarting => Status.Countdown is { Stopping: false };
    public bool HasScoreProgress => Status.ScoreProgress is not null;
    public bool HasScoreFraction => Status.ScoreProgress?.Fraction is not null;
    public double ScoreFraction => Status.ScoreProgress?.Fraction ?? 0;
    public string ScoreProgressText => Status.ScoreProgress?.Text ?? string.Empty;

    private static readonly string[] ClockProperties =
    [
        nameof(Status), nameof(PhaseSuccess), nameof(PhaseError), nameof(HasCountdown), nameof(CountdownLabel), nameof(CountdownValue),
        nameof(CountdownFraction), nameof(CountdownStopping), nameof(CountdownStarting), nameof(HasScoreProgress), nameof(HasScoreFraction),
        nameof(ScoreFraction), nameof(ScoreProgressText)
    ];

    // 取后端最新的时间基准；本轮进行中重算阶段、倒计时与乐谱进度，校时说明变化时更新。
    private void OnClockTick()
    {
        if (!Data.InRoom) return;
        var clock = ClockText;
        liveTiming = Backend.GetRoomTiming();
        if (ClockText != clock) Raise(nameof(ClockText));
        if (Data.Room?.Round is not { Active: true }) return;
        status = null;
        foreach (var name in ClockProperties) Raise(name);
    }

    public IReadOnlyList<string> Receipts => Data.Room?.Round?.Receipts.TakeLast(8).Select(receipt =>
    {
        var name = Data.Room.Members.FirstOrDefault(member => member.Id == receipt.MemberId)?.Nickname ?? Pick("已离开的成员", "已離開的成員");
        var status = receipt.Status switch
        {
            "executed" => Pick("已执行", "已執行"), "failed" => Pick("执行失败", "執行失敗"), "missed" => Pick("未能按时执行", "未能按時執行"),
            "cancelled" => Pick("已取消", "已取消"), _ => Localization.CodeText.State(receipt.Status)
        };
        return $"{(receipt.Simulated ? Pick("模拟操作", "模擬操作") : Pick("游戏操作", "遊戲操作"))}：{status} · {name}"
               + (MabiTool.Desktop.Backend.RoomReadinessText.Reason(receipt.Detail) is { Length: > 0 } detail ? " · " + detail : string.Empty);
    }).ToList() ?? [];

    public bool NoReceipts => Receipts.Count == 0;

    // ---- 动作 ----

    private void SetMode(string? mode)
    {
        if (mode is not ("emote" or "ensemble") || mode == Mode || !Status.CanChangeMode) return;
        if (Data.InRoom) Send("rooms.mode", mode);
        else
        {
            localMode = mode;
            recording = null;
            RaiseLocal();
        }
    }

    // 修订为 0（后端从未保存）时先把补齐默认值的设置保存一次，就绪与开轮用的就是界面上显示的设置。
    private async Task EnsureSavedAsync()
    {
        if (known is { Value.Revision.Value: 0 }) await SaveAsync(value => value, false);
    }

    private async Task ReadyAsync()
    {
        if (!Status.CanReady) return;
        if (!Status.ReadyOn) await EnsureSavedAsync();
        Send("rooms.ready", Status.ReadyOn ? "off" : "on");
    }

    private async Task StartAsync()
    {
        if (!Status.CanStart) return;
        // 原工具 PrepareClick：参与者、就绪与本机设置在点击时核对，不满足时说明原因，不发送。
        if (Status.StartIssue.Length > 0)
        {
            ShowLocal(new(Status.StartIssue, RoomTone.Error));
            return;
        }
        await EnsureSavedAsync();
        Send("rooms.start", null);
    }

    /// <summary>
    /// 以读取到的修订保存一次完整设置。保存期间界面显示新值；成功后重读，失败时回到后端当前值并在反馈横幅说明。
    /// <paramref name="revokeReady"/> 为真时（本机动作与时间微调的改动）撤销本机已有的就绪。
    /// </summary>
    private async Task SaveAsync(Func<RoomPreferences, RoomPreferences> change, bool revokeReady = true, string? success = null)
    {
        await saveGate.WaitAsync();
        try
        {
            if (known is not { } basis) return;
            var next = change(pending ?? RoomLocalSettings.Effective(basis.Value));
            pending = next;
            RaiseLocal();
            var reply = await Backend.SaveRoomPreferencesAsync(next with { Revision = basis.Value.Revision }, basis.ETag, CancellationToken.None);
            var reread = await Backend.LoadRoomPreferencesAsync(CancellationToken.None);
            if (reread.Value is { } value && (known is not { } old || value.Revision.Value >= old.Value.Revision.Value)) known = (value, reread.ETag);
            pending = null;
            offsetDraft = null;
            if (!reply.Ok) ShowLocal(new(reply.Message, RoomTone.Error));
            else
            {
                if (success is not null) ShowLocal(new(success, RoomTone.Success));
                if (revokeReady && Data.Self is { Ready: true } && Data.RoomConnected && !Status.Active) Send("rooms.ready", "off");
            }
        }
        finally
        {
            saveGate.Release();
            RaiseLocal();
        }
    }

    private void ShowLocal(RoomFeedback feedback)
    {
        localFeedback = feedback;
        RaiseLocal();
    }

    // 后端给出的设置比已知的新时采用它（保存后后端重读、其他窗口修改）。
    private void AbsorbKnown()
    {
        if (Data.Preferences is { } value && (known is not { } old || value.Revision.Value >= old.Value.Revision.Value))
            known = (value, Data.PreferencesETag);
        if (Data.Preferences is null && !Data.Connected) known = null;
    }

    // 合奏页用到乐器目录（与乐谱页同一组读取）。
    protected override string? Surface => "music";

    protected override void OnDataChanged()
    {
        status = null;
        liveTiming = Data.Timing;
        AbsorbKnown();
        var rejected = false;
        if (!ReferenceEquals(Data.Feedback, seenFeedback) && Data.Feedback != seenFeedback)
        {
            seenFeedback = Data.Feedback;
            localFeedback = null;
            rejected = Data.Feedback?.Tone == RoomTone.Error;
        }
        if (!Status.CanEditLocal) recording = null;
        RebuildRows();
        // 后端拒绝时成员数据不变，已提交的组号与顺序文字还原为当前值。
        if (rejected) foreach (var row in Rows) row.ResetInputs();
    }

    private void RebuildRows()
    {
        var choices = Data.Room?.Members.Select(member => new RoomConductorChoice(member.Id, member.Nickname)).ToList() ?? [];
        if (!choices.SequenceEqual(ConductorChoices))
        {
            ConductorChoices = choices;
            // 换了列表后下拉会先清空选中项，排版后再给出当前总指挥。
            Dispatcher.UIThread.Post(() => Raise(nameof(Conductor)));
        }
        if (Data.Room is not { } room) { if (Rows.Count > 0) Rows = []; return; }
        var ensemble = room.Mode == "ensemble";
        var members = ensemble ? room.Members.OrderBy(member => member.Group).ThenBy(member => member.Order).ToList() : room.Members.ToList();
        IReadOnlyList<string> instruments =
        [
            Pick("未指定", "未指定"),
            .. (Data.Instruments ?? RoomInstruments.Defaults).Concat(room.Members.Select(member => member.Instrument))
                .Where(name => name.Length > 0).Distinct(StringComparer.Ordinal)
        ];
        var canEdit = Status.CanEditRoster;
        // 页面每次后端通知都整页重读并通知全部绑定（含每秒的例行刷新）。成员行按成员标识沿用原来的行对象并就地更新，
        // 成员与顺序都没变时保留原列表：表格不替换行，打开中的行菜单与行内下拉不会因刷新被关闭（替换行会让菜单出现后立即消失）。
        var known = Rows.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var next = members.Select((member, index) =>
        {
            var self = member.Id == Data.SelfId;
            var conductor = member.Id == room.LeaderId;
            if (!known.TryGetValue(member.Id, out var row))
                return new RosterRow(member, index, self, conductor, canEdit, ensemble, instruments, EditCell);
            row.Update(member, index, self, conductor, canEdit, ensemble, instruments);
            return row;
        }).ToList();
        if (!next.SequenceEqual(Rows)) Rows = next;
    }

    private IReadOnlyList<string> lanEndpoints = [];
    // 内容相同时沿用同一列表，可编辑下拉不因刷新换数据源而收起。
    private IReadOnlyList<string> StableLanEndpoints()
    {
        if (!Data.Server.LanEndpoints.SequenceEqual(lanEndpoints)) lanEndpoints = Data.Server.LanEndpoints;
        return lanEndpoints;
    }

    private void RaiseLocal()
    {
        status = null;
        Raise(string.Empty);
    }

    public override void Dispose()
    {
        RoomsPreference.Changed -= OnSectionsChanged;
        CardStateStore.Changed -= OnCardChanged;
        offsetTimer.Stop();
        clockTimer.Stop();
        base.Dispose();
    }

}

/// <summary>指定总指挥下拉里的一位成员。</summary>
public sealed record RoomConductorChoice(string Id, string Nickname);

/// <summary>昵称来源下拉里的一项：抓包昵称或自定义昵称。</summary>
public sealed record NicknameSourceOption(string Value, string Label);

/// <summary>抓包识别网卡下拉里的一项：网卡说明与它的本机地址。</summary>

/// <summary>后端没有乐器目录时使用的乐器列表（原工具 InstrumentCatalog.Defaults）；名称是游戏里的乐器名。</summary>
public static class RoomInstruments
{
    public static IReadOnlyList<string> Defaults { get; } =
    [
        "鲁特琴", "2和弦鲁特琴", "3和弦鲁特琴", "长笛", "小提琴", "夏卢莫管", "曼陀林",
        "2和弦曼陀林", "3和弦曼陀林", "木琴", "钢琴", "2和弦钢琴", "3和弦钢琴", "大鼓"
    ];
}

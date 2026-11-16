using System.Diagnostics;
using System.Globalization;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Desktop.Pages.Rooms;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 合奏：房间会话、本机房间服务与本机设置的读取，以及 rooms.* 意图。
// 房间状态在每秒的例行刷新里读取（倒计时与就绪需要及时反映），在房间里时同时重读本轮的时间基准；本机设置每五秒或保存后读取。
// 每个意图只发送一次；结果同时显示在合奏页的反馈横幅与活动胶囊。
public sealed partial class LiveBackend
{
    private sealed record RoomExtras(RoomLifecycleSnapshot? Session, RoomServerSnapshot? Server, RoomPreferences? Preferences,
        string PreferencesETag, RoomTiming? Timing = null);

    // 合奏意图的结果：文字与反馈横幅的语气；普通成功的文字直接写成字符串。
    private readonly record struct RoomReply(string? Text, RoomTone Tone = RoomTone.Success)
    {
        public static implicit operator RoomReply(string? text) => new(text);
    }


    private RoomExtras roomExtras = new(null, null, null, string.Empty);
    private DateTimeOffset roomPreferencesReadAt;
    private bool roomPreferencesDue = true;
    private string? roomSignature;
    private RoomFeedback? roomFeedback;
    private string? fallbackInvitation;
    private bool invitationCopied;
    // 合奏在房间内时本前端持有的音乐观察需求（见 SyncMusicDemandAsync）。
    private string? captureDemandId;
    // 开始或就绪请求等待后端执行结果期间为真（原工具 _roundActionBusy），页面停用对应按钮。
    private bool roomRoundBusy;

    /// <summary>把文字放进系统剪贴板；由界面提供，用于复制房间邀请。失败时抛出异常，页面改为显示完整邀请。</summary>
    public Func<string, Task>? CopyText { get; set; }

    private void MarkRoomPreferencesDue()
    {
        lock (gate) roomPreferencesDue = true;
        Signal();
    }

    private async Task RefreshRoomAsync(MabiToolClient api, CancellationToken token)
    {
        bool readPreferences;
        RoomExtras previous;
        lock (gate)
        {
            readPreferences = roomPreferencesDue || DateTimeOffset.UtcNow - roomPreferencesReadAt >= TimeSpan.FromSeconds(5);
            roomPreferencesDue = false;
            previous = roomExtras;
        }
        var session = await Try(() => api.GetRoomSessionAsync(token));
        var server = await Try(() => api.GetRoomServerAsync(token));
        // 时间基准不改变轮次 ETag，随每秒刷新重读；收到响应的本机单调时刻是之后递减的零点。
        RoomTiming? timing = null;
        if (session is { State: "connected" } && await Try(() => api.GetCurrentRoomRoundAsync(token)) is { } round)
            timing = RoomTiming.From(round.Timing, Stopwatch.GetTimestamp());
        var preferences = previous.Preferences;
        var etag = previous.PreferencesETag;
        if (readPreferences)
        {
            string? tag = null;
            if (await Try(() => api.GetRoomPreferencesAsync(token, metadata => tag = metadata.ETag)) is { } read)
                (preferences, etag) = (read, tag ?? string.Empty);
        }
        // 抓包状态由 RefreshCaptureAsync 读取；在房间内时登记音乐识别需求。
        await SyncMusicDemandAsync(api, session, token);
        await RefreshBoundWindowAsync(api, token);
        GameObservationSnapshot? capture;
        string? window;
        lock (gate) { capture = captureSnapshot; window = BoundWindowText(); }
        // 时间基准只在有无变化时通知界面；数值由界面定时经 GetRoomTiming 取用。
        var next = string.Join('|', session?.State, session?.ExitReason, session?.Room?.GetRawText(), session?.LocalPreparation?.Revision, session?.OperateGame,
            server?.Revision, server?.State.RawValue, server?.Tunnel.State.RawValue, preferences?.Revision, etag,
            timing is null, timing?.StartRemainingMs is null, timing?.StopRemainingMs is null,
            capture?.BackendEpoch, capture?.Revision, capture?.State, capture?.Reason,
            capture?.Latest?.Value.Identity?.Nickname,
            capture?.Latest?.Value.Music?.Title, capture?.Latest?.Value.Music?.WholeScoreDurationSeconds, window);
        bool changed;
        lock (gate)
        {
            // 后端重启或捕获已停止时，本前端记下的需求随之失效。
            if (capture is not null && (capture.State is "stopped" or "faulted" || capture.SubscriberCount == 0)) captureDemandId = null;
            roomExtras = new(session, server, preferences, etag, timing);
            if (readPreferences) roomPreferencesReadAt = DateTimeOffset.UtcNow;
            changed = next != roomSignature;
            roomSignature = next;
        }
        if (changed) Notify();
    }

    public RoomsPageData GetRooms()
    {
        var (current, page, _, _) = ReadPages();
        RoomExtras extras;
        RoomFeedback? feedback;
        string? fallback;
        bool copied, busy;
        RoomCapture capture;
        string? windowName;
        lock (gate)
        {
            extras = roomExtras;
            feedback = roomFeedback;
            fallback = fallbackInvitation;
            copied = invitationCopied;
            busy = roomRoundBusy;
            capture = CaptureView();
            windowName = BoundWindowText();
        }
        if (!current.Connected) return RoomsPageData.Offline with { Feedback = feedback };
        var session = extras.Session;
        var inRoom = session is { State: not "disconnected" };
        var room = inRoom ? RoomJson.Parse(session!.Room, session.Code) : null;
        var binding = current.Binding?.Binding;
        var window = binding is null ? null
            : new RoomGameWindow(binding.Bounds.X, binding.Bounds.Y, binding.Bounds.Width, binding.Bounds.Height, (uint)Math.Round(binding.DpiX));
        var instruments = page.Instruments.Select(item => item.DisplayNames.GetValueOrDefault("zh-TW") ?? item.DisplayNames.Values.FirstOrDefault())
            .OfType<string>().Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return new(true, session?.State ?? "disconnected", inRoom ? session!.MemberId : null, room, ServerView(extras.Server),
            extras.Preferences, extras.PreferencesETag, Simulation(session, current), window,
            binding is null ? null : windowName ?? BoundWindowFallback(binding.Session), StopKey(page), feedback,
            session?.LocalPreparation is { State: not "released" } local ? local.Id : null, capture.Nickname,
            instruments.Count > 0 ? instruments : null, inRoom ? fallback : null, inRoom && copied, inRoom ? extras.Timing : null, session?.OperateGame ?? true,
            inRoom ? null : session?.ExitReason, capture, busy);
    }

    // 调用方持有 gate。昵称取全局抓包状态的本机角色（CaptureStatusView.Character），与窗口左上角是同一个状态源。
    private RoomCapture CaptureView()
    {
        var snapshot = captureSnapshot;
        var value = snapshot?.Latest is { Quality.KnownValue: not ObservationQuality.Stale } latest ? latest.Value : null;
        var status = CaptureStatusView.From(true, captureRuntime, snapshot);
        return new(status, status.Character, value?.Music);
    }

    // 绑定窗口的名称：候选列表里与当前绑定是同一窗口（进程号、进程启动时间、窗口标识相同）的一项；绑定变化时重读一次，失败 10 秒后再试。
    private string? boundWindowKey;
    private string? boundWindowName;
    private DateTimeOffset boundWindowRetryAt;

    private static string WindowKey(GameSession session) => $"{session.ProcessId}|{session.ProcessStartTime:O}|{session.WindowIdentity}";

    private async Task RefreshBoundWindowAsync(MabiToolClient api, CancellationToken token)
    {
        GameSession? session;
        string? known;
        DateTimeOffset retry;
        lock (gate) { session = state.Binding?.Binding?.Session; known = boundWindowKey; retry = boundWindowRetryAt; }
        if (session is null)
        {
            lock (gate) { boundWindowKey = null; boundWindowName = null; }
            return;
        }
        var key = WindowKey(session);
        if (key == known || DateTimeOffset.UtcNow < retry) return;
        try
        {
            var list = await api.GetGameWindowsAsync(token);
            var match = list.Items.FirstOrDefault(item => item.ProcessId == session.ProcessId
                && item.ProcessStartTime == session.ProcessStartTime && item.WindowIdentity == session.WindowIdentity);
            lock (gate)
            {
                boundWindowKey = key;
                boundWindowName = match is null ? null : WindowLabel(match.Name, match.ProcessName, match.ProcessId);
            }
        }
        catch (MabiToolApiException)
        {
            lock (gate) boundWindowRetryAt = DateTimeOffset.UtcNow.AddSeconds(10);
        }
    }

    /// <summary>合奏页“游戏窗口”一行：窗口标题原文与进程名、进程号；标题为空时只写进程。</summary>
    internal static string WindowLabel(string title, string process, int processId)
    {
        var name = process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process : process + ".exe";
        return title.Trim().Length == 0 ? $"{name}（PID {processId}）" : $"{title.Trim()}（{name} · PID {processId}）";
    }

    // 调用方持有 gate；绑定变化后名称尚未读到时为空。
    private string? BoundWindowText() =>
        state.Binding?.Binding?.Session is { } session && boundWindowKey == WindowKey(session) ? boundWindowName : null;

    // 候选列表里没有当前窗口或尚未读到时，只写进程号，不显示玩家昵称。
    private static string BoundWindowFallback(GameSession session) =>
        Pick($"进程 {session.ProcessId}（窗口名未读取）", $"處理程序 {session.ProcessId}（視窗名未讀取）");

    public RoomTiming? GetRoomTiming()
    {
        lock (gate) return roomExtras.Session is { State: not "disconnected" } ? roomExtras.Timing : null;
    }

    // 模拟与后端判定一致：宿主本身是模拟输入，或关闭了“操作游戏”（房间会话的 operateGame 为假）。
    private static bool Simulation(RoomLifecycleSnapshot? session, State current) => current.Simulation || session is { OperateGame: false };

    // 实际注册的紧急停止键；没有注册时为空。
    private static string? StopKey(PageState page)
    {
        var registration = page.Shortcuts.Concat(page.ShortcutSettings?.Registrations ?? [])
            .FirstOrDefault(item => item.ActionId == "emergency-stop" && item.Registered is not null);
        return registration?.Registered is { } chord
            ? string.Join(" + ", new[] { chord.Control ? "Ctrl" : null, chord.Alt ? "Alt" : null, chord.Shift ? "Shift" : null,
                chord.Windows ? "Win" : null, chord.Key }.Where(part => part is not null))
            : null;
    }

    private static RoomServerView ServerView(RoomServerSnapshot? server)
    {
        if (server is null) return RoomServerView.Unknown;
        var mode = server.Mode?.KnownValue switch
        {
            RoomServerMode.Loopback => Pick("仅本机", "僅本機"),
            RoomServerMode.Lan => Pick("局域网", "區域網路"),
            RoomServerMode.Public => Pick("公网", "公網"),
            _ => server.Mode?.RawValue
        };
        var state = server.State.KnownValue switch
        {
            RoomServerState.Stopped => Pick("本机服务未启动", "本機服務未啟動"),
            RoomServerState.Starting => Pick("本机服务正在启动", "本機服務正在啟動"),
            RoomServerState.Running => Pick($"本机服务运行中（{mode}）", $"本機服務執行中（{mode}）"),
            RoomServerState.Stopping => Pick("本机服务正在停止", "本機服務正在停止"),
            RoomServerState.Failed => Pick($"本机服务启动失败：{server.Problem?.Code}", $"本機服務啟動失敗：{server.Problem?.Code}"),
            _ => server.State.RawValue
        };
        var tunnel = server.Tunnel.State.KnownValue switch
        {
            RoomTunnelState.Starting => Pick("正在建立公网连接", "正在建立公網連線"),
            RoomTunnelState.Available => Pick($"公网地址：{server.Tunnel.Endpoint}", $"公網位址：{server.Tunnel.Endpoint}"),
            RoomTunnelState.Failed => Pick($"公网连接失败：{server.Tunnel.Problem?.Code}", $"公網連線失敗：{server.Tunnel.Problem?.Code}"),
            _ => Pick("未建立公网连接", "未建立公網連線")
        };
        return new(state, server.State.KnownValue == RoomServerState.Running, server.Mode?.RawValue, server.LocalEndpoint,
            server.LanEndpoints, tunnel, server.Tunnel.State.KnownValue == RoomTunnelState.Available ? server.Tunnel.Endpoint : null);
    }

    private void SetRoomFeedback(RoomFeedback? feedback)
    {
        lock (gate) roomFeedback = feedback;
        Notify();
    }

    // 合奏意图：结果写进反馈横幅（成功、错误），也照常写进活动胶囊。
    private Task SendRoomIntentAsync(string intent, string? argument, CancellationToken cancellationToken)
    {
        if (intent == "rooms.feedback-clear")
        {
            SetRoomFeedback(null);
            return Task.CompletedTask;
        }
        // 开始与就绪等到后端执行结果才结束；期间再次点击不发送（原工具 _roundActionBusy）。
        var round = intent is "rooms.start" or "rooms.ready";
        lock (gate)
        {
            if (round && roomRoundBusy) return Task.CompletedTask;
            if (round) roomRoundBusy = true;
        }
        SetRoomFeedback(new(Pick("正在处理…", "正在處理…"), RoomTone.Info));
        // 就绪、开始与各种停止会让宿主准备输入并把游戏窗口切到前台。在合奏页按下时 Moongate 是前台进程，
        // Windows 只允许前台进程让出前台：先允许宿主前置窗口（与激活游戏窗口相同），否则宿主得到 input.focusChanged（“无法切换到游戏窗口”）。
        if (intent is "rooms.start" or "rooms.ready" or "rooms.stop-all" or "rooms.local-stop") AllowHostForeground();
        return SendAsync(async (api, current, token) =>
        {
            try
            {
                var reply = await RoomActAsync(intent, argument, api, current, token);
                lock (gate) roomPreferencesDue = true;
                SetRoomFeedback(new(reply.Text ?? Pick("请求已发送，房间状态将自动更新。", "請求已送出，房間狀態將自動更新。"),
                    reply.Text is null ? RoomTone.Info : reply.Tone));
                return reply.Text;
            }
            catch (MabiToolApiException error)
            {
                SetRoomFeedback(new(RoomReadinessText.Failure(error.Problem, error.Status, intent), RoomTone.Error));
                throw;
            }
            catch (Exception error) when (error is InvalidOperationException or HttpRequestException or IOException or TimeoutException)
            {
                SetRoomFeedback(new(error.Message, RoomTone.Error));
                throw;
            }
        }, cancellationToken).ContinueWith(_ =>
        {
            lock (gate)
            {
                if (round) roomRoundBusy = false;
            }
            Notify();
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// 轮次命令由后端排队执行，受理（202）不等于成功：等待 Operation 结束，执行阶段被拒时返回原因（错误码与房间协议码）。
    /// 最多等待 30 秒，仍未结束时说明结果待确认。
    /// </summary>
    private static async Task<RoomReply?> RoundOutcomeAsync(MabiToolClient api, OperationSnapshot operation, string intent, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (operation.State.KnownValue is OperationState.Queued or OperationState.Running or OperationState.CancelRequested
               && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(150, token);
            operation = await api.GetOperationAsync(operation.Id, token);
        }
        return RoomReadinessText.RoundOutcome(operation, intent) is { } outcome ? new RoomReply(outcome.Text, outcome.Tone) : null;
    }

    private async Task<RoomReply> RoomActAsync(string intent, string? argument, MabiToolClient api, State current, CancellationToken token)
    {
        var key = IdempotencyKey();
        RoomExtras extras;
        lock (gate) extras = roomExtras;
        var session = extras.Session;
        RoomState Room() => RoomJson.Parse(session?.Room, session?.Code)
            ?? throw new InvalidOperationException(Pick("请先创建或加入房间。", "請先建立或加入房間。"));
        var parts = (argument ?? string.Empty).Split('\n');
        string Part(int index) => index < parts.Length ? parts[index].Trim() : string.Empty;

        switch (intent)
        {
            case "rooms.start-server":
            {
                var mode = Part(0) switch { "lan" => RoomServerMode.Lan, "public" => RoomServerMode.Public, _ => RoomServerMode.Loopback };
                await EnsureServerAsync(api, mode, key, token);
                return Pick("本机房间服务已启动", "本機房間服務已啟動");
            }
            case "rooms.create":
            {
                var nickname = Nickname(Part(1));
                var endpoint = Part(0) switch
                {
                    "community" => await CommunityEndpointAsync(api, extras, token),
                    "lan" => (await EnsureServerAsync(api, RoomServerMode.Lan, key, token)).LocalEndpoint,
                    "cloudflare" => (await EnsureServerAsync(api, RoomServerMode.Public, key, token)).LocalEndpoint,
                    _ => Part(2)
                };
                if (string.IsNullOrEmpty(endpoint)) throw new InvalidOperationException(Pick("请在高级连接里填写服务地址。", "請在進階連線裡填寫服務位址。"));
                await api.CreateRoomAsync(new(endpoint, nickname), IdempotencyKey(), token);
                // 新建的房间按界面上选择的模式设置（原工具 CreateConfiguredRoom）。
                if (Part(3) is "emote" or "ensemble") await api.SetRoomModeAsync(new(Part(3)), token);
                ResetInvitation();
                return Pick("房间已创建，复制邀请给好友。", "房間已建立，複製邀請給好友。");
            }
            case "rooms.join":
            {
                var nickname = Nickname(Part(1));
                var invitation = RoomInvitationText.Parse(Part(2), out var error) ?? throw new InvalidOperationException(error);
                var endpoint = invitation.Endpoint ?? Part(0) switch
                {
                    "community" => await CommunityEndpointAsync(api, extras, token),
                    "lan" or "manual" when Part(3).Length > 0 => Part(3).Contains("://", StringComparison.Ordinal) ? Part(3) : "ws://" + Part(3),
                    "lan" => throw new InvalidOperationException(Pick("请填写房主的局域网地址，或粘贴完整邀请。", "請填寫房主的區域網路位址，或貼上完整邀請。")),
                    "cloudflare" => throw new InvalidOperationException(Pick("Cloudflare 公网需要房主分享的完整邀请。", "Cloudflare 公網需要房主分享的完整邀請。")),
                    _ => throw new InvalidOperationException(Pick("请在高级连接里填写服务地址。", "請在進階連線裡填寫服務位址。"))
                };
                await api.JoinRoomAsync(new(endpoint, invitation.Code, nickname), key, token);
                ResetInvitation();
                return Pick("已加入房间，请确认按键或按钮设置后点击底部“就绪”。", "已加入房間，請確認按鍵或按鈕設定後點擊底部「就緒」。");
            }
            case "rooms.reconnect":
                await api.ReconnectRoomAsync(token);
                return Pick("房间已重新连接，成员列表已更新。", "房間已重新連線，成員列表已更新。");
            case "rooms.leave":
            {
                var owner = session?.IsOwner == true;
                await api.LeaveRoomAsync(new(owner), token);
                ResetInvitation();
                return owner ? Pick("房间已关闭，旧邀请已失效。", "房間已關閉，舊邀請已失效。") : Pick("已退出房间。", "已退出房間。");
            }
            case "rooms.copy-invite":
            {
                RoomInvitation invitation;
                try { invitation = await api.GetRoomInvitationAsync(token); }
                catch (MabiToolApiException) { invitation = await api.CreateRoomInvitationAsync(key, token); }
                string? preferredLan;
                lock (gate) preferredLan = roomExtras.Preferences?.LanEndpoint;
                var text = RoomInvitationText.Format(ShareEndpoint(invitation.Endpoint, extras.Server, preferredLan), invitation.Code);
                try
                {
                    if (CopyText is not { } copy) throw new InvalidOperationException();
                    await copy(text);
                    lock (gate) { invitationCopied = true; fallbackInvitation = null; }
                    return Pick("邀请已复制", "邀請已複製");
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    lock (gate) { invitationCopied = false; fallbackInvitation = text; }
                    return Pick("剪贴板不可用，请选中下方完整邀请后复制。", "剪貼簿不可用，請選取下方完整邀請後複製。");
                }
            }
            case "rooms.mode" when Part(0) is "emote" or "ensemble":
                await api.SetRoomModeAsync(new(Part(0)), token);
                return Pick("本轮模式已同步给所有成员。", "本輪模式已同步給所有成員。");
            case "rooms.ready" when Part(0) is "on" or "off":
            {
                var room = Room();
                var self = room.Members.FirstOrDefault(member => member.Id == session!.MemberId)
                    ?? throw new InvalidOperationException(Pick("房间名单里没有本机成员", "房間名單裡沒有本機成員"));
                var preferences = await api.GetRoomPreferencesAsync(token);
                var role = room.Mode == "ensemble" && !self.Initiator ? "joiner" : "initiator";
                // 关闭“操作游戏”时就绪带 simulation: true，开启时带 false（与房间会话的 operateGame 一致）。
                await api.SetRoomReadinessAsync(RoomRequests.Readiness(Part(0) == "on", role,
                    Simulation(session, current), current.Binding?.Binding?.Session.BindingRevision, preferences), token);
                return Part(0) == "on" ? Pick("已就绪", "已就緒") : Pick("已取消就绪", "已取消就緒");
            }
            case "rooms.start":
            {
                var room = Room();
                var preferences = RoomLocalSettings.Effective(await api.GetRoomPreferencesAsync(token));
                string? tag = null;
                await api.GetCurrentRoomRoundAsync(token, metadata => tag = metadata.ETag);
                var accepted = await api.PrepareRoomRoundAsync(RoomRequests.Round(room, preferences), key, tag ?? string.Empty, token);
                if (await RoundOutcomeAsync(api, accepted, intent, token) is { } refused) return refused;
                return room.Mode != "emote" && preferences.MusicStopRule.RawValue == "first-cycle"
                    ? Pick("等待识别总指挥本轮的乐谱，识别后会安排全员停止。", "等待辨識總指揮本輪的樂譜，辨識後會安排全員停止。")
                    : Pick("已开始本轮倒计时。", "已開始本輪倒數。");
            }
            case "rooms.cancel" or "rooms.stop-all" or "rooms.continue" or "rooms.interrupt":
            {
                string? tag = null;
                var round = await api.GetCurrentRoomRoundAsync(token, metadata => tag = metadata.ETag);
                if (round.Id is null) throw new InvalidOperationException(Pick("当前没有正在进行的本轮。", "目前沒有正在進行的本輪。"));
                var (action, message) = intent switch
                {
                    "rooms.cancel" => (RoomRoundControl.Cancel, Pick("已取消本轮。", "已取消本輪。")),
                    "rooms.stop-all" => (RoomRoundControl.StopAll, Pick("已通知全员停止。", "已通知全員停止。")),
                    "rooms.continue" => (RoomRoundControl.Continue, Pick("已继续演奏。", "已繼續演奏。")),
                    _ => (RoomRoundControl.Interrupt, Pick("已通知房主选择全员停止或继续。", "已通知房主選擇全員停止或繼續。"))
                };
                var accepted = await api.ControlRoomRoundAsync(round.Id, new(action, intent == "rooms.interrupt" ? "player-report" : "user"), key, tag ?? string.Empty, token);
                if (await RoundOutcomeAsync(api, accepted, intent, token) is { } refused) return refused;
                return message;
            }
            case "rooms.local-stop":
            {
                // 合奏的本机停止与表情的取消本机任务：后端按本轮决定排入停止键或取消待执行动作。
                var result = await api.StopRoomLocallyAsync(token);
                lock (gate) roomExtras = roomExtras with { Session = result.Session };
                var (text, tone) = RoomReadinessText.LocalStop(result.LocalAction, result.ServerConfirmed);
                return new RoomReply(text, tone);
            }
            case "rooms.game-operation" when Part(0) is "on" or "off":
                await api.SetRoomGameOperationAsync(new(Part(0) == "on"), token);
                return Part(0) == "on" ? Pick("已开启操作游戏，开始时会按设置操作游戏。", "已開啟操作遊戲，開始時會按設定操作遊戲。")
                    : Pick("已关闭操作游戏，本机只模拟倒计时和执行过程。", "已關閉操作遊戲，本機只模擬倒數和執行過程。");
            case "rooms.leader":
            {
                var member = Room().Members.FirstOrDefault(item => item.Id == Part(0))
                    ?? throw new InvalidOperationException(Pick("该成员已离开房间，请刷新后重试。", "該成員已離開房間，請重新整理後重試。"));
                await api.SelectRoomLeaderAsync(new(member.Id), token);
                return Pick($"已将 {member.Nickname} 设为总指挥。", $"已將 {member.Nickname} 設為總指揮。");
            }
            case "rooms.kick":
            {
                var member = Room().Members.FirstOrDefault(item => item.Id == Part(0))
                    ?? throw new InvalidOperationException(Pick("该成员已离开房间，请刷新后重试。", "該成員已離開房間，請重新整理後重試。"));
                await api.KickRoomMemberAsync(new(member.Id), token);
                return Pick($"已将 {member.Nickname} 移出房间。", $"已將 {member.Nickname} 移出房間。");
            }
            case "rooms.roster":
            {
                var member = Room().Members.FirstOrDefault(item => item.Id == Part(0))
                    ?? throw new InvalidOperationException(Pick("该成员已离开房间，请刷新后重试。", "該成員已離開房間，請重新整理後重試。"));
                var value = Part(2);
                int Number(string name)
                {
                    if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 24) return number;
                    throw new InvalidOperationException(name == "group"
                        ? Pick("合奏组请输入 1–24。", "合奏組請輸入 1–24。")
                        : Pick("顺序请输入 1–24；组内会自动连续排列。", "順序請輸入 1–24；組內會自動連續排列。"));
                }
                var (request, message) = Part(1) switch
                {
                    "group" => (new RoomRosterEditRequest(member.Id, Group: Number("group")), Pick($"已更新 {member.Nickname} 的分组，列表已同步。", $"已更新 {member.Nickname} 的分組，列表已同步。")),
                    "order" => (new RoomRosterEditRequest(member.Id, Order: Number("order")), Pick($"已更新 {member.Nickname} 的顺序，列表已同步。", $"已更新 {member.Nickname} 的順序，列表已同步。")),
                    "role" => (new RoomRosterEditRequest(member.Id, MusicRole: value == "initiator" ? "initiator" : "joiner"),
                        value == "initiator" ? Pick($"已将 {member.Nickname} 设为乐谱使用者，列表已同步。", $"已將 {member.Nickname} 設為樂譜使用者，列表已同步。")
                            : Pick($"已将 {member.Nickname} 设为参与者，列表已同步。", $"已將 {member.Nickname} 設為參與者，列表已同步。")),
                    "instrument" => (new RoomRosterEditRequest(member.Id, Instrument: value),
                        Pick($"已为 {member.Nickname} 设置乐器：{RoomFlow.InstrumentText(value)}。", $"已為 {member.Nickname} 設定樂器：{RoomFlow.InstrumentText(value)}。")),
                    "selected" => (new RoomRosterEditRequest(member.Id, Selected: value == "true"), Pick($"已更新 {member.Nickname} 的本轮参与状态。", $"已更新 {member.Nickname} 的本輪參與狀態。")),
                    _ => throw new InvalidOperationException(Pick("成员配置无效", "成員設定無效"))
                };
                await api.EditRoomRosterAsync(request, token);
                return message;
            }
            case "rooms.move":
            {
                // 拖动落位：先改分组（每次只提交一个字段），再改组内顺序；结果以后端重读为准。
                var member = Room().Members.FirstOrDefault(item => item.Id == Part(0))
                    ?? throw new InvalidOperationException(Pick("该成员已离开房间，请刷新后重试。", "該成員已離開房間，請重新整理後重試。"));
                if (!int.TryParse(Part(1), out var group) || group is < 1 or > 24 || !int.TryParse(Part(2), out var order) || order is < 1 or > 24)
                    throw new InvalidOperationException(Pick("合奏组与顺序应为 1–24。", "合奏組與順序應為 1–24。"));
                if (group != member.Group) await api.EditRoomRosterAsync(new(member.Id, Group: group), token);
                await api.EditRoomRosterAsync(new(member.Id, Order: order), token);
                return Pick($"已把 {member.Nickname} 排到第 {group} 组第 {order} 位，列表已同步。", $"已把 {member.Nickname} 排到第 {group} 組第 {order} 位，列表已同步。");
            }
            case "rooms.character":
            {
                await api.BindRoomCharacterAsync(RoomRequests.Character(Part(0), Part(1), Part(2)), token);
                return Part(1).Length == 0 ? Pick("已取消角色匹配。", "已取消角色匹配。") : Pick("已匹配游戏角色。", "已匹配遊戲角色。");
            }
        }
        throw new InvalidOperationException(Pick($"后端没有对应这个操作的接口：{intent}", $"後端沒有對應這個操作的介面：{intent}"));
    }

    // 服务器直连的入口取本机设置给出的 communityEndpoint（后端只读字段）。
    private static async Task<string> CommunityEndpointAsync(MabiToolClient api, RoomExtras extras, CancellationToken token) =>
        (extras.Preferences ?? await api.GetRoomPreferencesAsync(token)).CommunityEndpoint;

    private void ResetInvitation()
    {
        lock (gate) { fallbackInvitation = null; invitationCopied = false; }
    }

    private static string Nickname(string text) => text.Length is >= 1 and <= 20 && !text.Any(char.IsControl)
        ? text
        : throw new InvalidOperationException(Pick("请填写 1–20 字的昵称。", "請填寫 1–20 字的暱稱。"));

    /// <summary>
    /// 邀请里的地址：房间建在本机服务上时（地址是本机回环），改用公网隧道地址，没有隧道时用局域网地址；其余原样。
    /// </summary>
    internal static string ShareEndpoint(string endpoint, RoomServerSnapshot? server, string? preferredLan)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback) return endpoint;
        if (server?.Tunnel is { State.KnownValue: RoomTunnelState.Available, Endpoint: { Length: > 0 } tunnel }) return tunnel;
        var lan = server?.LanEndpoints ?? [];
        return lan.FirstOrDefault(item => item == preferredLan) ?? lan.FirstOrDefault() ?? endpoint;
    }

    // 启动（或沿用）本机房间服务，等到运行；公网方式另等隧道可用。
    private async Task<RoomServerSnapshot> EnsureServerAsync(MabiToolClient api, RoomServerMode mode, string key, CancellationToken token)
    {
        string? tag = null;
        var server = await api.GetRoomServerAsync(token, metadata => tag = metadata.ETag);
        bool Ready(RoomServerSnapshot value) => value.State.KnownValue == RoomServerState.Running && value.Mode?.KnownValue == mode
            && (mode != RoomServerMode.Public || value.Tunnel.State.KnownValue == RoomTunnelState.Available);
        if (Ready(server)) return server;
        if (server.State.KnownValue is not (RoomServerState.Running or RoomServerState.Starting) || server.Mode?.KnownValue != mode)
        {
            if (server.State.KnownValue is RoomServerState.Running or RoomServerState.Starting)
                throw new InvalidOperationException(Pick("本机房间服务正以其他方式运行，请在高级连接里查看。", "本機房間服務正以其他方式執行，請在進階連線裡查看。"));
            await api.StartRoomServerAsync(new(mode), key, tag ?? string.Empty, token);
        }
        var deadline = DateTimeOffset.UtcNow + (mode == RoomServerMode.Public ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(15));
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(300, token);
            server = await api.GetRoomServerAsync(token);
            lock (gate) roomExtras = roomExtras with { Server = server };
            Notify();
            if (Ready(server)) return server;
            if (server.State.KnownValue == RoomServerState.Failed)
                throw new InvalidOperationException(Pick($"本机房间服务启动失败：{server.Problem?.Code}", $"本機房間服務啟動失敗：{server.Problem?.Code}"));
            if (server.Tunnel.State.KnownValue == RoomTunnelState.Failed)
                throw new InvalidOperationException(Pick($"公网连接失败：{server.Tunnel.Problem?.Code}", $"公網連線失敗：{server.Tunnel.Problem?.Code}"));
        }
        throw new InvalidOperationException(Pick("本机房间服务启动超时，请稍后重试。", "本機房間服務啟動逾時，請稍後重試。"));
    }
}

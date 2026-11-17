using MabiTool.Desktop.Pages.Diagnostics;
using MabiTool.Desktop.Pages.Dyeing;
using MabiTool.Desktop.Pages.Combat;
using MabiTool.Desktop.Pages.Inventory;
using MabiTool.Desktop.Pages.Modules;
using MabiTool.Desktop.Pages.Music;
using MabiTool.Desktop.Pages.Rooms;
using MabiTool.Desktop.Pages.Settings;

namespace MabiTool.Desktop.Backend;

public enum EffectStage { Pending, Done, Unknown }

/// <summary>
/// 活动列显示的事实：谁持有游戏输入，以及受理、送出、观测三段各自的状态。
/// 未连接后端时 <see cref="CanEmergencyStop"/> 为假，紧急停止按钮不可用。
/// </summary>
public sealed record ActivitySnapshot(string? OwnerModuleId, string OwnerLine, string Title, string Detail,
    EffectStage Accepted, EffectStage Dispatched, EffectStage Observed, bool CanEmergencyStop = true)
{
    /// <summary>持有输入的操作号；主界面不显示，只放在说明的悬停提示里，供反馈问题时对照诊断记录。</summary>
    public string? Reference { get; init; }

    /// <summary>说明的悬停提示：说明全文，有操作号时另起一行写明。</summary>
    public string DetailTip => Reference is { Length: > 0 } reference
        ? Detail + Environment.NewLine + Localization.UiText.Pick($"操作号：{reference}", $"操作編號：{reference}")
        : Detail;
}

/// <summary>
/// 侧栏顶部的游戏绑定：名称用游戏原文，状态文字和是否已证实来自后端。
/// <see cref="Connected"/> 为假时不能选择游戏窗口。
/// </summary>
/// <see cref="CharacterName"/> 是观测到的角色昵称（迷你窗等处使用）；侧栏绑定卡的标题用 <see cref="Title"/>：
/// 描述绑定的窗口本身（“游戏窗口”“模拟游戏窗口”“未绑定”“未连接后端”），角色名只在侧栏的抓包状态里显示。
public sealed record BindingView(string CharacterName, string Region, string Status, bool Confirmed, bool Connected = true,
    string? WindowTitle = null)
{
    public string Title => WindowTitle ?? Localization.UiText.Pick("游戏窗口", "遊戲視窗");
}

/// <summary>战斗观测页能向后端表达的意图；是否可用由后端状态决定。</summary>
public enum CombatIntent
{
    StartObservation, StopObservation, EndEncounter, SplitEncounter, ResetEncounter, ToggleFavorite, CleanHistory, SelectTarget
}

/// <summary>
/// 页面读取数据和发送意图的唯一入口。业务判定全部在后端；这里只转发意图并返回后端状态。
/// 状态变化时触发 <see cref="Changed"/>（在界面线程上），页面重新读取。
/// </summary>
public partial interface IDesktopBackend
{
    event EventHandler? Changed;
    /// <summary>本次在线连接的有效快捷键动作，在 UI 线程通知。</summary>
    event EventHandler<string>? ShortcutTriggered;
    ActivitySnapshot GetActivity();
    BindingView GetGameBinding();
    CombatPageData GetCombat();

    /// <summary>
    /// DPS检测页显示或切走。切走后后端适配层不再读取只供页面使用的数据（首击时间线）；显示时立即安排一轮刷新。
    /// 返回真表示刷新后一定会触发 <see cref="Changed"/>，页面可先标注“更新中”。
    /// </summary>
    bool SetCombatPageVisible(bool visible) => false;

    /// <summary>DPS 明细窗口打开或关闭；打开期间后端适配层按窗口自己的选择读取范围汇总与时间线分桶。</summary>
    void SetCombatDetailOpen(bool open) { }

    /// <summary>
    /// DPS 明细窗口的数据：窗口自己的范围与目标（意图 combat.detail.scope、combat.detail.scope.live、combat.detail.target），
    /// 与主页面的选择互不影响。不区分的后端返回与主页面相同的数据。
    /// </summary>
    CombatPageData GetCombatDetail() => GetCombat();

    /// <summary>只有明细窗口需要重读时触发（窗口内切换范围或目标的结果到达），在界面线程上；主页面与浮窗不订阅。</summary>
    event EventHandler? CombatDetailChanged { add { } remove { } }

    /// <summary>DPS 随时间曲线的数据；后端或适配层不提供时返回停用说明。</summary>
    MabiTool.Desktop.Pages.Combat.CombatTimelineView? GetCombatTimeline() =>
        new([], 0, 0, Localization.UiText.Pick("后端没有提供时间线", "後端沒有提供時間線"));
    MusicPageData GetMusic();
    RoomsPageData GetRooms();

    /// <summary>
    /// 合奏的最新时间基准（例行刷新时重读，不触发 <see cref="Changed"/>）；倒计时在界面线程按本机单调时钟递减，
    /// 定时取这里校正，不增加后端请求。不在房间或没有校时样本时为空。
    /// </summary>
    RoomTiming? GetRoomTiming() => GetRooms().Timing;
    ColorPageData GetColor();
    InventoryPageData GetInventory();
    ModulesPageData GetModules();
    SettingsPageData GetSettings();

    /// <summary>游戏窗口在屏幕上的位置（物理像素），浮窗据此对齐；未绑定或未知时为空。</summary>
    Avalonia.PixelRect? GetGameWindowBounds();

    /// <summary>绑定的游戏窗口当前是否最小化；没有绑定或不知道时为假。浮窗的“游戏最小化时隐藏”据此判断。</summary>
    bool IsGameMinimized() => false;

    /// <summary>把只在前端发生的操作结果（浮窗显隐、全部上锁）写进活动胶囊；不连接后端时不显示。</summary>
    void ReportNotice(string text) { }

    /// <summary>
    /// 后端能力表里该能力不可用时的本地化说明，页面显示在对应主操作旁；
    /// 能力可用、状态未知或能力表没有该编号时为空。
    /// </summary>
    string? GetCapabilityNotice(string capabilityId);

    /// <summary>能力表里该能力的状态；未连接或能力表没有该编号时为未知。</summary>
    CapabilityLevel GetCapabilityLevel(string capabilityId);

    /// <summary>
    /// 展示接口里该主题（nearby、environment）缺失时的说明，浮窗元件据此显示或隐藏；
    /// 主题可用、降级、状态未知、没有该主题或未连接时为空。
    /// </summary>
    TopicNotice? GetPresentationNotice(string topic);
    Task RequestEmergencyStopAsync(CancellationToken cancellationToken);

    Task SendCombatIntentAsync(CombatIntent intent, string? targetId, CancellationToken cancellationToken);

    /// <summary>
    /// 其余页面的意图，以名称区分（例如 music.enqueue、inventory.account），参数是所选对象的标识。
    /// 页面数据里的可用标记说明此刻哪些意图能发。
    /// </summary>
    Task SendIntentAsync(string intent, string? argument, CancellationToken cancellationToken);

    /// <summary>
    /// 登记页面当前显示的乐谱（例如乐谱表的这一页）。后端按需分批查询它们的时长并缓存，
    /// 查到后触发 <see cref="Changed"/>；查不到的保持未知。
    /// </summary>
    void RequestScoreDetails(IReadOnlyList<string> scoreIds);

    // 下面的意图带结构化内容，单独成方法；结果原样交回页面显示，是否接受由后端判定。

    /// <summary>把一个全局快捷键改成新的组合键；回执说明后端的注册结果。</summary>
    Task<BackendReply> ChangeShortcutAsync(string actionId, KeyChord chord, CancellationToken cancellationToken);

    /// <summary>向后端重新读取候选游戏窗口；候选会过期，每次都重新读取，不使用旧列表。</summary>
    Task<GameWindowListReply> ListGameWindowsAsync(CancellationToken cancellationToken);

    /// <summary>把游戏绑定换到一个候选窗口，带当前修订发送一次；失败原因显示在活动胶囊。</summary>
    Task<BackendReply> SelectGameWindowAsync(string candidateId, CancellationToken cancellationToken);

    /// <summary>保存完整的更新策略，带当前修订发送一次；失败原因显示在活动胶囊。</summary>
    Task<BackendReply> SaveUpdatePolicyAsync(UpdatePolicyView policy, CancellationToken cancellationToken);

    /// <summary>读取合奏本机设置（开始与停止按键、倒计时、时间偏移、结束方式等）。</summary>
    Task<RoomPreferencesReply> LoadRoomPreferencesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 保存完整的合奏本机设置，以读取时的 <paramref name="etag"/> 为 If-Match 发送一次；失败原因显示在活动胶囊。
    /// </summary>
    Task<BackendReply> SaveRoomPreferencesAsync(MabiTool.Contracts.RoomPreferences preferences, string etag,
        CancellationToken cancellationToken);

    // ---- 诊断页：读取在页面内显示，不进入活动胶囊 ----

    /// <summary>当前主体的诊断权限（握手时的授权信息）。</summary>
    DiagnosticAccess GetDiagnosticAccess();

    /// <summary>实时推送的调试采集记录（diagnostics.record），在界面线程上通知；只有拥有 diagnostics.debug 时才会收到。</summary>
    event EventHandler<DiagnosticPushedRecord>? DiagnosticRecordReceived;

    /// <summary>事件流（重新）连上，在界面线程上通知；断线期间的记录用查询补齐。</summary>
    event EventHandler? DiagnosticStreamRestored;

    Task<DiagnosticRuntimeReply> ReadDiagnosticRuntimeAsync(CancellationToken cancellationToken);
    Task<DiagnosticRecordsReply> QueryDiagnosticRecordsAsync(DiagnosticRecordFilter filter, CancellationToken cancellationToken);
    Task<DiagnosticCaptureReply> GetDiagnosticCaptureAsync(CancellationToken cancellationToken);
    Task<DiagnosticCaptureReply> StartDiagnosticCaptureAsync(DebugCaptureSettings settings, CancellationToken cancellationToken);
    Task<DiagnosticCaptureReply> StopDiagnosticCaptureAsync(string captureId, CancellationToken cancellationToken);

    /// <summary>
    /// 导出排查包：提交导出、轮询 Operation、读取导出文档的全部分段，把进度（0–1）交给 <paramref name="progress"/>。
    /// <paramref name="captureSessionId"/> 在范围为最近一次采集时是该采集的会话编号。
    /// </summary>
    Task<DiagnosticExportReply> ExportDiagnosticsAsync(DiagnosticExportScope scope, string? captureSessionId,
        IProgress<double>? progress, CancellationToken cancellationToken);
}

using MabiTool.Desktop.Pages.Settings;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 样本后端的编辑类意图：快捷键、候选窗口与更新策略。只模拟后端的回执形状，供界面开发与测试使用。
public sealed partial class SampleBackend
{
    private readonly List<(string Id, string Name, KeyChord Chord, bool Editable)> shortcuts =
    [
        ("emergency-stop", "紧急停止|緊急停止", new("F9", false, false, false, false), true),
        ("overlay-toggle", "显示或隐藏浮窗|顯示或隱藏浮窗", new("F8", false, false, false, false), true),
        ("overlay-click-through-toggle", "浮窗元件全部上锁或解锁|浮窗元件全部上鎖或解鎖", new("F7", false, false, false, false), true),
        // 与后端相同：定时测量的开始／取消默认不分配按键。
        ("combat-measurement-toggle", "开始／取消定时测量|開始／取消定時測量", new("", false, false, false, false), true),
        ("music.toggle", "开始或停止演奏|開始或停止演奏", new("P", true, true, false, false), false),
        ("color.pick", "吸管取色|吸管取色", new("C", true, true, false, false), false)
    ];

    private readonly Dictionary<string, string> shortcutFailures = new(StringComparer.Ordinal) { ["color.pick"] = "shortcut.conflict" };

    public Task<BackendReply> ChangeShortcutAsync(string actionId, KeyChord chord, CancellationToken cancellationToken)
    {
        UiIntents.Add(("shortcut.change", $"{actionId}:{chord}"));
        var index = shortcuts.FindIndex(item => item.Id == actionId);
        if (index < 0 || !shortcuts[index].Editable)
            return Task.FromResult(new BackendReply(false, Pick("请求未完成：request.invalid", "請求未完成：request.invalid")));
        shortcuts[index] = shortcuts[index] with { Chord = chord };
        // 样本模拟注册失败：与其他快捷键相同的组合登记成功，但注册不上。
        var conflict = shortcuts.Where((item, position) => position != index).Any(item => item.Chord == chord);
        if (conflict) shortcutFailures[actionId] = "shortcut.conflict";
        else shortcutFailures.Remove(actionId);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(conflict
            ? new BackendReply(false, Pick($"已登记 {chord}，注册失败：shortcut.conflict", $"已登記 {chord}，註冊失敗：shortcut.conflict"))
            : new BackendReply(true, Pick($"已注册 {chord}", $"已註冊 {chord}")));
    }

    // 样本的候选游戏窗口：窗口名称与进程名是系统原文；绑定后侧栏名称用该窗口观测到的昵称。
    private readonly List<(string Id, string Name, string ProcessName, int ProcessId, string? Nickname)> sampleWindows =
    [
        ("sample-window-1", "Mabinogi Mobile", "MabinogiMobile", 12840, "琉特"),
        ("sample-window-2", "Mabinogi Mobile", "MabinogiMobile", 15312, "芙蕾雅"),
        ("sample-window-3", "Mabinogi Mobile", "MabinogiMobile", 20476, null)
    ];

    private string boundWindow = "sample-window-1";

    // 合奏页“游戏窗口”一行：绑定窗口的标题与进程（与真实后端同一写法）。
    private string? BoundWindowLabel() => sampleWindows.FirstOrDefault(window => window.Id == boundWindow) is { Id: not null } window
        ? LiveBackend.WindowLabel(window.Name, window.ProcessName, window.ProcessId) : null;
    private string? notice;
    private UpdatePolicyView samplePolicy = new(true, 6, 2, 4_194_304);

    /// <summary>测试用：模拟未连接后端。绑定卡停用，候选读取失败，更新策略为空。</summary>
    internal bool Offline { get; set; }

    /// <summary>测试用：后端返回空的候选列表。</summary>
    internal bool NoGameWindows { get; set; }

    /// <summary>测试用：绑定与保存策略被后端拒绝时的错误码。</summary>
    internal string? RejectCode { get; set; }

    /// <summary>测试用：设置后候选读取等它完成才返回，用来观察读取中的界面。</summary>
    internal TaskCompletionSource? WindowListGate { get; set; }

    /// <summary>保存策略的请求内容，按发送顺序记录。</summary>
    public List<UpdatePolicyView> SavedPolicies { get; } = [];

    public async Task<GameWindowListReply> ListGameWindowsAsync(CancellationToken cancellationToken)
    {
        UiIntents.Add(("game.windows", null));
        if (WindowListGate is { } gate) await gate.Task;
        if (Offline) return new(false, Pick("请求未完成：未连接后端", "請求未完成：未連接後端"), []);
        if (NoGameWindows) return new(true, string.Empty, []);
        return new(true, string.Empty, sampleWindows.Select(window =>
            new GameWindowChoice(window.Id, window.Name, window.ProcessName, window.ProcessId, "TW", window.Id == boundWindow)).ToList());
    }

    public Task<BackendReply> SelectGameWindowAsync(string candidateId, CancellationToken cancellationToken)
    {
        UiIntents.Add(("game.select", candidateId));
        if (RejectCode is { } code || sampleWindows.All(window => window.Id != candidateId))
            return Task.FromResult(Reject(RejectCode ?? "resource.notFound"));
        boundWindow = candidateId;
        notice = Pick("已绑定游戏窗口", "已綁定遊戲視窗");
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new BackendReply(true, notice));
    }

    /// <summary>测试用：返回的更新说明；<see cref="PendingUpdateNotes"/> 非空时等它完成再返回（模拟读取中）。</summary>
    internal UpdateNotesView? UpdateNotes { get; set; }
    internal TaskCompletionSource<UpdateNotesView?>? PendingUpdateNotes { get; set; }
    internal int UpdateNotesLoads { get; private set; }

    public Task<UpdateNotesView?> LoadUpdateNotesAsync(CancellationToken cancellationToken)
    {
        UpdateNotesLoads++;
        if (Offline) return Task.FromResult<UpdateNotesView?>(null);
        return PendingUpdateNotes?.Task ?? Task.FromResult(UpdateNotes);
    }

    public Task<BackendReply> SaveUpdatePolicyAsync(UpdatePolicyView policy, CancellationToken cancellationToken)
    {
        SavedPolicies.Add(policy);
        if (Offline || RejectCode is not null) return Task.FromResult(Reject(RejectCode ?? "connection.unavailable"));
        samplePolicy = policy;
        notice = Pick("已保存更新策略", "已儲存更新策略");
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new BackendReply(true, notice));
    }

    // 被拒绝的写操作：错误码显示在活动胶囊。
    private BackendReply Reject(string code)
    {
        notice = Pick("请求未完成：", "請求未完成：") + Localization.CodeText.Reason(code);
        Changed?.Invoke(this, EventArgs.Empty);
        return new(false, notice);
    }

    private IReadOnlyList<ShortcutRow> SampleShortcuts() => shortcuts.Select(item =>
    {
        var names = item.Name.Split('|');
        var failure = shortcutFailures.GetValueOrDefault(item.Id);
        if (item.Chord.Key.Length == 0)
            return new ShortcutRow(Pick(names[0], names[1]), Pick("未配置", "未設定"), Pick("未注册", "未註冊"), false, item.Id, item.Editable);
        return new ShortcutRow(Pick(names[0], names[1]), item.Chord.ToString(),
            failure is null ? Pick("已注册", "已註冊") : Pick($"注册失败：{failure}", $"註冊失敗：{failure}"), failure is null,
            item.Id, item.Editable);
    }).ToList();
}

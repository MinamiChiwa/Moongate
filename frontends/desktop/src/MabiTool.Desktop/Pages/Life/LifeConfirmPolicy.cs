namespace MabiTool.Desktop.Pages.Life;

/// <summary>
/// “本次运行期间不再询问”：用户在确认框里选择后，同一次后端运行内的执行不再弹确认框；宿主换了一次运行（后端代次变化）或前端重启后
/// 恢复每次询问。只存在内存里，不写偏好文件。后端仍核对每次请求带的花费文字，花费变化时拒绝并要求重新确认。
/// </summary>
public static class LifeConfirmPolicy
{
    /// <summary>采集、加工、领取、制作共用的确认类别。</summary>
    public const string Actions = "actions";

    /// <summary>专注钓鱼的确认类别：长时间后台发送按键，与命令行动作分开记住。</summary>
    public const string Fishing = "fishing";

    private static readonly Dictionary<string, string?> remembered = new(StringComparer.Ordinal);

    /// <summary>当前后端代次下是否需要弹出确认框。</summary>
    public static bool ShouldAsk(string? backendEpoch, string kind = Actions) =>
        backendEpoch is null || !remembered.TryGetValue(kind, out var epoch) || epoch != backendEpoch;

    /// <summary>记住：到宿主或前端重启前不再询问。</summary>
    public static void Remember(string? backendEpoch, string kind = Actions) => remembered[kind] = backendEpoch;

    /// <summary>恢复每次询问（花费变化、被后端拒绝后调用，也供测试使用）。</summary>
    public static void Reset() => remembered.Clear();
}

/// <summary>自动领取的开关：只在本次运行内有效，重启后恢复默认关闭（领取会自动移动角色，不应在用户不知情时继续开着）。</summary>
public static class LifePreference
{
    public static bool AutoCollect { get; private set; }

    public static event EventHandler? Changed;

    public static void SetAutoCollect(bool value)
    {
        if (AutoCollect == value) return;
        AutoCollect = value;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}

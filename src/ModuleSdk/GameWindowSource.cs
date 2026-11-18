namespace MabiTool.ModuleSdk;

public sealed record SelectableGameWindow(string Name, string ProcessName, GameWindowObservation Observation);

/// <summary>窗口激活后的实际回读；Attempted 为假表示没有向系统发出请求。</summary>
public sealed record GameWindowActivation(bool Attempted, bool Restored, bool Foreground);

/// <summary>准备时读取的客户端：主程序路径与各文件 SHA-256（键为相对客户端目录的路径）；文件戳变化后不再提供。</summary>
public sealed record GameClientFiles(string ImagePath, IReadOnlyDictionary<string, string> Sha256);

public interface IGameWindowSource
{
    IReadOnlyList<SelectableGameWindow> Enumerate();
    GameWindowObservation? Refresh(GameWindowObservation selected);
    ValueTask<GameWindowObservation?> PrepareAsync(GameWindowObservation selected, CancellationToken ct) => ValueTask.FromResult(Refresh(selected));
    bool TryActivate(GameWindowObservation selected);
    /// <summary>显式准备时核对过的客户端文件，供宿主核对构建与记录指纹摘要；未准备或不适用时为空。</summary>
    GameClientFiles? PreparedClient(GameWindowObservation selected) => null;
    /// <summary>
    /// 窗口访问问题的原因码；没有问题时为空。<c>game.elevated</c>（游戏以管理员身份运行，输入会被系统丢弃）拒绝绑定；
    /// 其他码（如 <c>game.integrityUnreadable</c>）只记诊断，不拦截绑定。
    /// </summary>
    string? AccessProblem(GameWindowObservation selected) => null;
    /// <summary>恢复并前置窗口后回读实际状态；不发送任何游戏输入。默认实现只能按 <see cref="TryActivate"/> 的结果推断。</summary>
    GameWindowActivation Activate(GameWindowObservation selected)
    {
        var activated = TryActivate(selected);
        return new(true, activated || !selected.IsMinimized, activated);
    }
}

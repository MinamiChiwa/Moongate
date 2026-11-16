namespace MabiTool.ModuleSdk;

public sealed record SelectableGameWindow(string Name, string ProcessName, GameWindowObservation Observation);

/// <summary>窗口激活后的实际回读；Attempted 为假表示没有向系统发出请求。</summary>
public sealed record GameWindowActivation(bool Attempted, bool Restored, bool Foreground);

public interface IGameWindowSource
{
    IReadOnlyList<SelectableGameWindow> Enumerate();
    GameWindowObservation? Refresh(GameWindowObservation selected);
    ValueTask<GameWindowObservation?> PrepareAsync(GameWindowObservation selected, CancellationToken ct) => ValueTask.FromResult(Refresh(selected));
    bool TryActivate(GameWindowObservation selected);
    /// <summary>恢复并前置窗口后回读实际状态；不发送任何游戏输入。默认实现只能按 <see cref="TryActivate"/> 的结果推断。</summary>
    GameWindowActivation Activate(GameWindowObservation selected)
    {
        var activated = TryActivate(selected);
        return new(true, activated || !selected.IsMinimized, activated);
    }
}

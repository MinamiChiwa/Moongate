using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 样本后端的游戏窗口激活：样本数据没有真实的游戏窗口，回执与模拟宿主相同（已绑定、没有发出系统请求）。
public sealed partial class SampleBackend
{
    /// <summary>测试用：替换激活回执；为空时按模拟宿主回答。</summary>
    internal GameWindowActivation? Activation { get; set; }

    public Task<GameWindowActivation> ActivateGameWindowAsync(CancellationToken cancellationToken)
    {
        UiIntents.Add(("game.activate", null));
        return Task.FromResult(Activation ?? (Offline
            ? GameWindowActivation.Unbound
            : new GameWindowActivation(true, false, false, false, null, Pick("模拟宿主没有真实的游戏窗口", "模擬宿主沒有真實的遊戲視窗"))));
    }
}

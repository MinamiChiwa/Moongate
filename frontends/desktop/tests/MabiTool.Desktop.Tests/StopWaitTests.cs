using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Pages.Combat;
using Xunit;
using static MabiTool.Desktop.Tests.TestUi;

namespace MabiTool.Desktop.Tests;

/// <summary>停止观测：后端停止 Worker 最多再等 2 秒，按下后到状态变化前显示“正在停止”并停用按钮。</summary>
public sealed class StopWaitTests
{
    [AvaloniaFact]
    public void StoppingObservationShowsStoppingAndDisablesTheButtonUntilTheStateChanges()
    {
        var backend = new SampleBackend();
        // 自动观测开启时由后端保持观测、不提供停止按钮；关闭自动观测后才有手动停止。
        _ = backend.SendIntentAsync("combat.auto", "off", CancellationToken.None);
        var window = LayoutProbe.Open(backend, 1280, 860, "combat-observation");
        var button = Named<Button>(window, "StopButton");
        Assert.Equal("停止观测", button.Content);

        Click(window, button);
        Assert.Single(backend.Intents, item => item.Intent == CombatIntent.StopObservation);
        Assert.Equal("正在停止", button.Content);
        Assert.False(button.IsEffectivelyEnabled);

        // 等待期间再点不会重复发送。
        Click(window, button);
        Assert.Single(backend.Intents, item => item.Intent == CombatIntent.StopObservation);
        window.Close();
    }
}

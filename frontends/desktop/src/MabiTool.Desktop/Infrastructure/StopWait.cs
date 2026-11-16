using Avalonia.Threading;

namespace MabiTool.Desktop.Infrastructure;

/// <summary>
/// 停止类操作的等待提示：后端停止观察 Worker 最多再等 2 秒，按下后到状态变化前页面显示“正在停止”并停用按钮。
/// 状态一直没有变化时（例如请求被拒绝）超过 <see cref="Limit"/> 后自动恢复按钮，失败原因显示在活动胶囊。
/// </summary>
public static class StopWait
{
    public static TimeSpan Limit { get; set; } = TimeSpan.FromSeconds(10);

    public static void Expire(Action onExpire) =>
        _ = Task.Delay(Limit).ContinueWith(_ => Dispatcher.UIThread.Post(onExpire), TaskScheduler.Default);
}

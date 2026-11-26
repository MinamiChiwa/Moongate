using MabiTool.Contracts;

namespace MabiTool.Desktop.Backend;

// “生活”页：后端保存读取结果与执行进度，页面显示时按需读取、发出确认后的执行与停止。
// 成员都带默认实现，只提供部分接口的后端（测试替身、样本后端）不必逐个实现。
// 其中钓鱼部分是生活模块的原地专注钓鱼：状态与命令接口，页面是“生活”页的钓鱼区。
public partial interface IDesktopBackend
{
    /// <summary>最近一次取得的“生活”快照；没连上后端、还没读到或后端没有这个接口时为空。</summary>
    LifeSnapshot? GetLife() => null;

    /// <summary>
    /// 读取一块或全部（<paramref name="section"/>：gather、alter、works、craft、all）；<paramref name="query"/> 是纯文本筛选。
    /// 执行进行中后端拒绝（回执 Code 为 life.actionRunning）。结果经 <see cref="Changed"/> 通知后由 <see cref="GetLife"/> 取得。
    /// </summary>
    Task<BackendReply> RefreshLifeAsync(string section, string? query, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, string.Empty));

    /// <summary>发出用户已确认的执行请求；后端异步执行，进度经 <see cref="GetLife"/> 的 Action 读取。</summary>
    Task<BackendReply> StartLifeActionAsync(LifeActionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, string.Empty));

    /// <summary>停止进行中的执行：中断命令行进程，游戏里的动作状态按未知处理。</summary>
    Task<BackendReply> StopLifeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, string.Empty));

    /// <summary>总队列：加入一项任务（后端按顺序执行，开始或继续由 <see cref="CommandLifeQueueAsync"/> 发出）。</summary>
    Task<BackendReply> AddLifeTaskAsync(LifeTaskAddRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, string.Empty));

    /// <summary>总队列：修改一项任务的数量或位置（带页面所见的队列修订）。</summary>
    Task<BackendReply> UpdateLifeTaskAsync(string taskId, LifeTaskUpdateRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, string.Empty));

    /// <summary>总队列：删除一项任务或一条已结束的记录。</summary>
    Task<BackendReply> RemoveLifeTaskAsync(string taskId, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, string.Empty));

    /// <summary>总队列命令：start（带确认的整队花费上限）、pause、stop-current、clear-finished。</summary>
    Task<BackendReply> CommandLifeQueueAsync(LifeQueueCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, string.Empty));

    /// <summary>钓鱼能力与当前运行；桌面没连上后端、没读到或后端没有这个接口时由 <see cref="FishingView.Read"/> 标明。</summary>
    FishingView GetFishing() => FishingView.Offline;

    /// <summary>
    /// 钓鱼命令：<c>start</c>（<paramref name="durationSeconds"/> 为空表示直到停止）、<c>stop</c>（离开钓鱼姿态后结束）、
    /// <c>cancel</c>（立即停止并释放输入，不再发送游戏输入）。回执的 <see cref="BackendReply.Code"/> 是后端拒绝时的错误码。
    /// </summary>
    Task<BackendReply> CommandFishingAsync(string action, int? durationSeconds, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, string.Empty));
}

namespace MabiTool.Contracts;

/// <summary>
/// “生活”模块接口：采集、加工、制作的快照与读取，宿主总队列的编辑与命令，单项立即执行与停止（经游戏连接器，执行在后端异步进行），
/// 以及原地专注钓鱼的快照、开始、读取与结束。
/// 写操作的权限是模块自己的控制权限 <see cref="ApiPermissions.LifeControl"/>。
/// </summary>
public static class LifeApi
{
    public static EndpointDefinition State { get; } = new("/api/v1/life", "getLife", typeof(LifeSnapshot), ETag: true, MaximumResponseBytes: 8 * 1024 * 1024);
    public static EndpointDefinition Refresh { get; } = new("/api/v1/life/refresh", "refreshLife", typeof(LifeSnapshot), Method: "POST",
        RequestType: typeof(LifeRefreshRequest), Permission: ApiPermissions.LifeControl, MaximumResponseBytes: 8 * 1024 * 1024);
    public static EndpointDefinition Execute { get; } = new("/api/v1/life/actions", "startLifeAction", typeof(LifeSnapshot), Method: "POST",
        RequestType: typeof(LifeActionRequest), Permission: ApiPermissions.LifeControl, Idempotent: true, MaximumResponseBytes: 8 * 1024 * 1024);
    public static EndpointDefinition Stop { get; } = new("/api/v1/life/stop", "stopLifeAction", typeof(LifeSnapshot), Method: "POST",
        Permission: ApiPermissions.LifeControl, MaximumResponseBytes: 8 * 1024 * 1024);

    /// <summary>总队列：加入任务（幂等）、修改数量或位置（带队列修订）、删除任务、队列命令（开始或继续、暂停、停止当前、清空已完成）。</summary>
    public static EndpointDefinition AddTask { get; } = new("/api/v1/life/queue/tasks", "addLifeTask", typeof(LifeSnapshot), Method: "POST",
        RequestType: typeof(LifeTaskAddRequest), Permission: ApiPermissions.LifeControl, Idempotent: true, MaximumResponseBytes: 8 * 1024 * 1024);
    public static EndpointDefinition UpdateTask { get; } = new("/api/v1/life/queue/tasks/{id}", "updateLifeTask", typeof(LifeSnapshot), Method: "PUT",
        RequestType: typeof(LifeTaskUpdateRequest), Permission: ApiPermissions.LifeControl, MaximumResponseBytes: 8 * 1024 * 1024);
    public static EndpointDefinition RemoveTask { get; } = new("/api/v1/life/queue/tasks/{id}", "removeLifeTask", typeof(LifeSnapshot), Method: "DELETE",
        Permission: ApiPermissions.LifeControl, MaximumResponseBytes: 8 * 1024 * 1024);
    public static EndpointDefinition QueueCommand { get; } = new("/api/v1/life/queue/commands", "commandLifeQueue", typeof(LifeSnapshot), Method: "POST",
        RequestType: typeof(LifeQueueCommand), Permission: ApiPermissions.LifeControl, MaximumResponseBytes: 8 * 1024 * 1024);

    public static EndpointDefinition Fishing { get; } = new("/api/v1/life/fishing", "getFishing", typeof(FishingSnapshot), ETag: true);
    public static EndpointDefinition StartFishing { get; } = new("/api/v1/life/fishing/runs", "startFishing", typeof(OperationSnapshot),
        Method: "POST", RequestType: typeof(FishingRunRequest), Permission: ApiPermissions.LifeControl, SuccessStatus: 202, Idempotent: true);
    public static EndpointDefinition FishingRun { get; } = new("/api/v1/life/fishing/runs/{id}", "getFishingRun", typeof(FishingRun), ETag: true);
    /// <summary>结束钓鱼：离开钓鱼姿态后正常结束；立即取消用 <c>POST /api/v1/operations/{id}/cancel</c>，只释放输入、不再发送游戏输入。</summary>
    public static EndpointDefinition StopFishing { get; } = new("/api/v1/life/fishing/runs/{id}/stop", "stopFishing", typeof(FishingRun),
        Method: "POST", Permission: ApiPermissions.LifeControl);

    public static IReadOnlyList<EndpointDefinition> Endpoints { get; } = Array.AsReadOnly<EndpointDefinition>([
        State, Refresh, Execute, Stop, AddTask, UpdateTask, RemoveTask, QueueCommand, Fishing, StartFishing, FishingRun, StopFishing
    ]);
}

/// <summary>“生活”模块（采集、加工、制作）的错误码及 HTTP 状态。</summary>
public static class LifeErrors
{
    public static IReadOnlyList<ErrorDefinition> Definitions { get; } = Array.AsReadOnly<ErrorDefinition>([
        new("life.actionRunning", 409, false), new("life.costChanged", 409, false), new("life.connectorUnavailable", 409, false),
        new("life.fishingNotSupported", 409, false), new("life.nothingToCollect", 409, false), new("life.commandUnavailable", 409, false),
        new("life.queueFull", 409, false), new("life.taskRunning", 409, false), new("life.queueRunning", 409, false), new("life.nothingToRun", 409, false)
    ]);
}

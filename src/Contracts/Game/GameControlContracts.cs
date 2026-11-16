namespace MabiTool.Contracts;

public sealed record GameInputOwnership(string OwnerId, string OperationId, string GameSessionId,
    UInt64String BindingRevision, IReadOnlyList<string> Resources, DateTimeOffset ExpiresAt);
public sealed record GameInputOwnershipSnapshot(string BackendEpoch, bool Simulation,
    DateTimeOffset ObservedAt, IReadOnlyList<GameInputOwnership> Owners);
/// <summary>窗口激活回读：<c>Attempted</c> 表示已向系统发出恢复/前置请求，<c>Restored</c>、<c>Foreground</c> 是请求后读到的实际窗口状态。</summary>
public sealed record GameWindowActivationReceipt(string BackendEpoch, bool Simulation, string GameSessionId,
    UInt64String BindingRevision, bool Attempted, bool Restored, bool Foreground, DateTimeOffset ObservedAt);
public sealed record GameEmergencyStopRequest(string BackendEpoch, DateTimeOffset ExpiresAt);
public sealed record GameEmergencyStopFailure(string Component, string Code);
public sealed record GameEmergencyStopReceipt(string BackendEpoch, bool Simulation, DateTimeOffset StoppedAt,
    int RevokedLeases, int? ReleasedControls, string InputReleaseState, bool CancellationRequested,
    OpenEnum<EffectStatus> EffectStatus, IReadOnlyList<GameEmergencyStopFailure> Failures);

using System.Text.Json;

namespace MabiTool.Contracts;

public enum OperationState { Queued, Running, WaitingUser, CancelRequested, Succeeded, Failed, Cancelled, Interrupted }
public enum EffectStatus { None, Unknown, InputDispatched, ObservedApplied }
public enum CancellationState { None, Requested, Deferred, Completed }
public sealed record ResourceReference(string Type, string Id);
public sealed record OperationTimestamps(DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);
public sealed record CancellationSnapshot(OpenEnum<CancellationState> State, string? Reason);
public sealed record OperationSnapshot(string Id, string Kind, string Owner, IReadOnlyList<ResourceReference> ResourceRefs,
    OpenEnum<OperationState> State, string Phase, UInt64String Revision, double? Progress, OperationTimestamps Timestamps,
    JsonElement? Result, ApiProblem? Problem, CancellationSnapshot Cancellation, OpenEnum<EffectStatus> EffectStatus);

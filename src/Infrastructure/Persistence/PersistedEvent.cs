using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed record StateEvent(string EventId, string ResourceType, string ResourceId, UInt64String Revision,
    string EventType, JsonElement Payload);
public sealed record PersistedEvent(long Sequence, StateEvent Event, DateTimeOffset CreatedAt);

// 只投递已提交状态；执行器不会从该通道接收游戏命令。
public interface IStateEventSink
{
    Task PublishAsync(PersistedEvent state, CancellationToken cancellationToken);
}

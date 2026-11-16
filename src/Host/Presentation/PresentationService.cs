using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Presentation;

public sealed class PresentationService(StateEventHub hub, PresentationStateFeed feed, SystemInfo system, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private static readonly IReadOnlyDictionary<string, string[]> TopicEvents = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["performance"] = ["performance.state.changed"],
        ["nearby"] = ["game.nearby.changed"],
        ["room"] = ["room.session.changed"],
        ["color"] = ["color.run.changed", "color.library.changed"],
        ["environment"] = ["game.environment.changed"]
    };

    public async Task<PresentationSnapshot> ReadAsync(AuthSession session, PresentationQuery query, CancellationToken ct)
    {
        if (query is null || query.Topics is { Count: > 5 } ||
            query.Topics?.Any(topic => topic is null || !TopicEvents.ContainsKey(topic)) == true)
            throw new OperationException("request.invalid", "展示主题须为 performance、nearby、room、color 或 environment");
        var topics = query.Topics is not { Count: > 0 } ? TopicEvents.Keys.ToArray()
            : query.Topics.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var filter = EventFilter.Create(topics.SelectMany(topic => TopicEvents[topic]), query.ResourceIds);
        if (topics.Contains("room", StringComparer.Ordinal)) await feed.EnsureRoomAsync(session.ClientId, ct);
        var snapshot = await hub.SnapshotAsync(session, filter, ct);
        var sources = topics.Select(topic => new PresentationSourceStatus(topic,
            topic is "room" or "color" || snapshot.Resources.Any(resource => TopicEvents[topic].Contains(resource.Type, StringComparer.Ordinal))
                ? CapabilityStatus.Available : CapabilityStatus.Unavailable,
            topic is "room" or "color" || snapshot.Resources.Any(resource => TopicEvents[topic].Contains(resource.Type, StringComparer.Ordinal))
                ? null : topic == "environment" ? "provider.unavailable" : topic == "nearby" ? "provider.unavailable" : "provider.unconfigured")).ToArray();
        var clockSample = new PresentationClockSample(system.InstanceId, time.GetUtcNow(), time.GetTimestamp() * (1000d / time.TimestampFrequency));
        var result = new PresentationSnapshot(1, snapshot.BackendEpoch, snapshot.EventCursor,
            new(filter.Topics, filter.ResourceIds, snapshot.EventCursor), clockSample, sources, snapshot.Resources);
        if (JsonSerializer.SerializeToUtf8Bytes(result, ContractJson.Options).Length > StateEventHub.MaximumSnapshotBytes)
            throw new OperationException("resource.capacityExceeded", "展示快照超过 4 MiB，请缩小主题或资源范围");
        return result;
    }
}

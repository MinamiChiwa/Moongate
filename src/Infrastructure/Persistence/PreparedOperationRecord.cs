using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

internal sealed record PreparedStateEvent(StateEvent State, string Payload, string Content);
internal sealed record PreparedOperationRecord(OperationSnapshot Snapshot, string Json, IReadOnlyList<PreparedStateEvent> Events);

public sealed partial class SqliteStore
{
    internal static PreparedStateEvent PrepareEvent(StateEvent state)
    {
        if (string.IsNullOrWhiteSpace(state.EventId) || state.EventId.Length > 128 ||
            new[] { state.ResourceType, state.ResourceId, state.EventType }.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128) ||
            state.Payload.ValueKind is JsonValueKind.Undefined)
            throw new StorageException("storage.eventInvalid", "事件身份、资源或内容无效");
        var payload = state.Payload.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(payload) > MaximumEventBytes)
            throw new StorageException("storage.eventTooLarge", "事件内容超过 64 KiB");
        return new(state, payload, JsonSerializer.Serialize(state, ContractJson.Options));
    }

    // 冻结 JSON 和通知在进入短事务前完成；写入时仍核对原操作修订及 outbox 容量。
    internal static PreparedOperationRecord PrepareOperation(OperationSnapshot snapshot, IReadOnlyList<StateEvent>? additionalEvents = null)
    {
        var json = JsonSerializer.Serialize(snapshot, ContractJson.Options);
        var events = new List<StateEvent>
        {
            new("operation-" + snapshot.Id + "-" + snapshot.Revision, "operation", snapshot.Id, snapshot.Revision,
                "operation.changed", JsonSerializer.SerializeToElement(snapshot, ContractJson.Options))
        };
        if (snapshot.Kind == "color.search")
        {
            var run = OperationStore.ReadColor(snapshot);
            events.Add(new("color-" + run.Id + "-" + snapshot.Revision, "color-run", run.Id,
                snapshot.Revision, "color.run.changed", snapshot.Result!.Value));
        }
        if (snapshot.Kind == OperationStore.FishingKind)
        {
            var fishing = OperationStore.ReadFishing(snapshot);
            events.Add(new("fishing-" + fishing.Id + "-" + snapshot.Revision, "fishing-run", fishing.Id,
                snapshot.Revision, "life.fishing.changed", snapshot.Result!.Value));
        }
        if (additionalEvents is not null) events.AddRange(additionalEvents);
        return new(snapshot, json, events.Select(PrepareEvent).ToArray());
    }
}

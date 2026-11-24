using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed record EventFilter(IReadOnlyList<string> Topics, IReadOnlyList<string> ResourceIds)
{
    public static EventFilter Create(IEnumerable<string>? topics = null, IEnumerable<string>? resourceIds = null)
    {
        var types = (topics ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var ids = (resourceIds ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (types.Length > 16 || ids.Length > 32 || types.Any(t => !ContractEvents.Definitions.Any(d => d.Type == t)) ||
            ids.Any(id => !Regex.IsMatch(id, @"\A[a-zA-Z0-9][a-zA-Z0-9._:-]{0,127}\z")))
            throw new OperationException("request.invalid", "事件主题或资源过滤无效");
        return new(Array.AsReadOnly(types), Array.AsReadOnly(ids));
    }
    internal bool Matches(string type, string id) => (Topics.Count == 0 || Topics.Contains(type, StringComparer.Ordinal)) &&
        (ResourceIds.Count == 0 || ResourceIds.Contains(id, StringComparer.Ordinal));
}

public sealed record EventDelivery(ulong Sequence, string Type, string ResourceId, JsonElement Data, string Json, int Bytes, long PublishedAt);

/// <summary>投影提交、快照水位和订阅登记按同一顺序执行；不等待连接写入。</summary>
public sealed class StateEventHub(SqliteStore? store, string epoch, TimeProvider? timeProvider = null) : IBatchStateEventSink
{
    public const int MaximumReplayEvents = 10000;
    public const int MaximumReplayBytes = 16 * 1024 * 1024;
    public const int MaximumQueueEvents = 128;
    public const int MaximumQueueBytes = 1024 * 1024;
    public const int MaximumConnections = 32;
    public const int MaximumConnectionsPerClient = 4;
    public const int MaximumSnapshotResources = 512;
    public const int MaximumSnapshotBytes = 4 * 1024 * 1024;
    public const int MaximumVolatileResources = 128;
    public const int MaximumVolatileBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan ReplayLifetime = TimeSpan.FromMinutes(5);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly EventReplayWindow replay = new(timeProvider ?? TimeProvider.System);
    private readonly HashSet<EventSubscription> subscriptions = [];
    private readonly Dictionary<(string Type, string Id), (ProjectedResource Resource, int Bytes)> volatileResources = [];
    private int volatileBytes;
    private ulong sequence;
    private int subscriptionCount;
    private long resyncedSubscriptions;
    /// <summary>当前订阅数与因队列溢出转为重新同步的订阅累计数，供运行状态总览读取。</summary>
    public int SubscriptionCount => Volatile.Read(ref subscriptionCount);
    public ulong ResyncedSubscriptions => (ulong)Interlocked.Read(ref resyncedSubscriptions);
    internal void CountResync() => Interlocked.Increment(ref resyncedSubscriptions);

    public async Task PublishShortcutAsync(ShortcutTriggered value, CancellationToken ct = default)
    {
        if (!ShortcutSettingsStore.IsNotifiedAction(value.ActionId) || value.Chord is null ||
            !MabiTool.ModuleSdk.ShortcutKeys.TryParse(value.Chord.Key, out _) ||
            value.TriggeredAt.Offset != TimeSpan.Zero || value.ExpiresAt.Offset != TimeSpan.Zero ||
            value.ExpiresAt - value.TriggeredAt != TimeSpan.FromSeconds(2))
            throw new OperationException("request.invalid", "快捷键触发通知无效");
        await gate.WaitAsync(ct);
        try
        {
            if (value.ExpiresAt <= clock.GetUtcNow() || value.TriggeredAt > clock.GetUtcNow()) return;
            var next = checked(sequence + 1);
            var envelope = new EventEnvelope<ShortcutTriggered>(epoch + ":" + next.ToString(CultureInfo.InvariantCulture),
                "shortcut.triggered", 1, "presentation", value.ActionId, new(next), value.TriggeredAt, null, value);
            var json = JsonSerializer.Serialize(envelope, ContractJson.Options);
            var delivery = new EventDelivery(next, envelope.Type, value.ActionId,
                JsonSerializer.SerializeToElement(value, ContractJson.Options), json, Encoding.UTF8.GetByteCount(json), clock.GetTimestamp());
            sequence = next;
            // 界面动作只发给在线连接，不保存为可恢复状态或重连回放。
            foreach (var subscription in subscriptions)
                if (subscription.Filter.Matches(delivery.Type, delivery.ResourceId) && Visible(subscription.Session, delivery.Type, delivery.Data))
                    subscription.Enqueue(delivery);
        }
        finally { gate.Release(); }
    }

    /// <summary>调试采集记录只发给在线且具备 diagnostics.debug 的连接，不进入快照、数据库或重连回放。</summary>
    public async Task PublishDiagnosticRecordAsync(string captureId, DiagnosticRecord record, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (sequence == ulong.MaxValue) return;
            var next = sequence + 1;
            var envelope = new EventEnvelope<DiagnosticRecord>(epoch + ":" + next.ToString(CultureInfo.InvariantCulture),
                "diagnostics.record", 1, "core", captureId, record.Sequence ?? new(next), record.OccurredAt, null, record);
            var json = JsonSerializer.Serialize(envelope, ContractJson.Options);
            var delivery = new EventDelivery(next, envelope.Type, captureId, JsonSerializer.SerializeToElement(record, ContractJson.Options),
                json, Encoding.UTF8.GetByteCount(json), clock.GetTimestamp());
            sequence = next;
            foreach (var subscription in subscriptions)
                if (subscription.Filter.Matches(delivery.Type, delivery.ResourceId) && Visible(subscription.Session, delivery.Type, delivery.Data))
                    subscription.Enqueue(delivery);
        }
        finally { gate.Release(); }
    }

    internal bool Expired(EventDelivery value) => value.Type == "shortcut.triggered" &&
        value.Data.GetProperty("expiresAt").GetDateTimeOffset() <= clock.GetUtcNow();

    public async Task PublishAsync(PersistedEvent state, CancellationToken cancellationToken)
    {
        if (store is null) throw new InvalidOperationException("持久事件需要配置数据库");
        await gate.WaitAsync(cancellationToken);
        try
        {
            var definition = ContractEvents.Definitions.SingleOrDefault(d => d.Type == state.Event.EventType);
            if (definition is null) { await store.ProjectForStreamAsync(state, cancellationToken); return; }
            var next = checked(sequence + 1);
            var envelope = new EventEnvelope<JsonElement>(epoch + ":" + next.ToString(CultureInfo.InvariantCulture), definition.Type, 1, definition.Module,
                state.Event.ResourceId, state.Event.Revision, state.CreatedAt,
                state.Event.ResourceType == "operation" ? state.Event.ResourceId : null, state.Event.Payload);
            var json = JsonSerializer.Serialize(envelope, ContractJson.Options);
            var delivery = new EventDelivery(next, definition.Type, state.Event.ResourceId, state.Event.Payload, json,
                Encoding.UTF8.GetByteCount(json), clock.GetTimestamp());
            if (!await store.ProjectForStreamAsync(state, cancellationToken)) return;
            sequence = next;
            replay.Add(delivery);
            foreach (var subscription in subscriptions)
                if (subscription.Filter.Matches(delivery.Type, delivery.ResourceId) && Visible(subscription.Session, delivery.Type, delivery.Data))
                    subscription.Enqueue(delivery);
        }
        finally { gate.Release(); }
    }

    public async Task PublishBatchAsync(IReadOnlyList<PersistedEvent> batch, CancellationToken cancellationToken)
    {
        if (store is null) throw new InvalidOperationException("持久事件需要配置数据库");
        await gate.WaitAsync(cancellationToken);
        try
        {
            var changed = await store.ProjectBatchForStreamAsync(batch, cancellationToken);
            for (var i = 0; i < batch.Count; i++)
            {
                var state = batch[i];
                if (!changed[i] || ContractEvents.Definitions.SingleOrDefault(d => d.Type == state.Event.EventType) is not { } definition) continue;
                var next = checked(sequence + 1);
                var envelope = new EventEnvelope<JsonElement>(epoch + ":" + next.ToString(CultureInfo.InvariantCulture), definition.Type, 1, definition.Module,
                    state.Event.ResourceId, state.Event.Revision, state.CreatedAt,
                    state.Event.ResourceType == "operation" ? state.Event.ResourceId : null, state.Event.Payload);
                var json = JsonSerializer.Serialize(envelope, ContractJson.Options);
                var delivery = new EventDelivery(next, definition.Type, state.Event.ResourceId, state.Event.Payload, json,
                    Encoding.UTF8.GetByteCount(json), clock.GetTimestamp());
                sequence = next;
                replay.Add(delivery);
                foreach (var subscription in subscriptions)
                    if (subscription.Filter.Matches(delivery.Type, delivery.ResourceId) && Visible(subscription.Session, delivery.Type, delivery.Data))
                        subscription.Enqueue(delivery);
            }
        }
        finally { gate.Release(); }
    }

    public async Task PublishVolatileAsync(StateEvent state, DateTimeOffset occurredAt, CancellationToken ct = default)
    {
        if (state.EventType is not ("room.session.changed" or "room.score-draft.changed" or "room.server.changed" or "desktop.surface.changed" or "game.nearby.changed" or "game.environment.changed" or "game.observation.changed" or "combat.measurement.changed" or "combat.live.changed") ||
            state.Payload.ValueKind != JsonValueKind.Object || state.EventId.Length is < 1 or > 128 ||
            state.ResourceId.Length is < 1 or > 128 || state.ResourceType.Length is < 1 or > 128 ||
            JsonSerializer.SerializeToUtf8Bytes(state.Payload, ContractJson.Options).Length > SqliteStore.MaximumEventBytes)
            throw new OperationException("request.invalid", "临时状态类型、身份或内容无效");
        await gate.WaitAsync(ct);
        try
        {
            var key = (state.EventType, state.ResourceId);
            volatileResources.TryGetValue(key, out var previous);
            if (previous.Resource is { } saved && saved.ResourceRevision.Value >= state.Revision.Value)
            {
                if (saved.ResourceRevision == state.Revision && JsonElement.DeepEquals(saved.Data, state.Payload)) return;
                throw new OperationException("resource.conflict", "临时状态修订不递增或内容冲突");
            }
            var definition = ContractEvents.Definitions.Single(x => x.Type == state.EventType);
            var resource = new ProjectedResource(definition.Type, definition.Module, state.ResourceId, state.Revision, state.Payload.Clone());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(resource, ContractJson.Options).Length;
            if (previous.Resource is null && volatileResources.Count >= MaximumVolatileResources ||
                volatileBytes - previous.Bytes + bytes > MaximumVolatileBytes)
            {
                foreach (var retired in volatileResources.Where(x => x.Key != key && x.Key.Type == "desktop.surface.changed" &&
                    x.Value.Resource.Data.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.False).Select(x => x.Key).ToArray())
                {
                    volatileBytes -= volatileResources[retired].Bytes;
                    volatileResources.Remove(retired);
                }
            }
            if (previous.Resource is null && volatileResources.Count >= MaximumVolatileResources ||
                volatileBytes - previous.Bytes + bytes > MaximumVolatileBytes || sequence == ulong.MaxValue)
                throw new OperationException("resource.capacityExceeded", "临时状态数量或空间预算已耗尽");
            var next = sequence + 1;
            var envelope = new EventEnvelope<JsonElement>(epoch + ":" + next.ToString(CultureInfo.InvariantCulture), definition.Type, 1,
                definition.Module, state.ResourceId, state.Revision, occurredAt, null, resource.Data);
            var json = JsonSerializer.Serialize(envelope, ContractJson.Options);
            var delivery = new EventDelivery(next, definition.Type, state.ResourceId, resource.Data, json,
                Encoding.UTF8.GetByteCount(json), clock.GetTimestamp());
            volatileResources[key] = (resource, bytes);
            volatileBytes = volatileBytes - previous.Bytes + bytes;
            sequence = next;
            // 最新捕获的即时通知只发给在线订阅并保留最新一份供快照，不进重连回放：战斗中每秒可达数十条，旧通知对重连方没有用处，
            // 也不应挤占可靠事件的回放窗口。
            if (state.EventType != "combat.live.changed") replay.Add(delivery);
            foreach (var subscription in subscriptions)
                if (subscription.Filter.Matches(delivery.Type, delivery.ResourceId) && Visible(subscription.Session, delivery.Type, delivery.Data))
                    subscription.Enqueue(delivery);
        }
        finally { gate.Release(); }
    }

    public async Task<ProjectionSnapshot> SnapshotAsync(AuthSession session, EventFilter filter, CancellationToken ct = default)
    {
        filter = EventFilter.Create(filter.Topics, filter.ResourceIds);
        await gate.WaitAsync(ct);
        try { replay.Prune(); return Capture(session, filter); }
        finally { gate.Release(); }
    }

    public async Task<T> CaptureAsync<T>(AuthSession session, EventFilter filter, Func<string, string, T> capture, CancellationToken ct = default)
    {
        filter = EventFilter.Create(filter.Topics, filter.ResourceIds);
        await gate.WaitAsync(ct);
        try { replay.Prune(); return capture(epoch, Cursor(session, filter, sequence)); }
        finally { gate.Release(); }
    }

    public async Task<EventSubscription> SubscribeAsync(AuthSession session, EventFilter filter, string? cursor = null, CancellationToken ct = default)
    {
        filter = EventFilter.Create(filter.Topics, filter.ResourceIds);
        await gate.WaitAsync(ct);
        try
        {
            replay.Prune();
            if (subscriptions.Count >= MaximumConnections || subscriptions.Count(s => s.Session.ClientId == session.ClientId) >= MaximumConnectionsPerClient)
                throw new OperationException("resource.capacityExceeded", "事件订阅数量达到上限");
            var after = cursor is null ? sequence : ParseCursor(cursor, session, filter);
            var subscription = new EventSubscription(this, session, filter,
                replay.After(after, e => filter.Matches(e.Type, e.ResourceId) && Visible(session, e.Type, e.Data)));
            subscriptions.Add(subscription);
            try
            {
                if (cursor is null) subscription.InitialSnapshot = Capture(session, filter);
                return subscription;
            }
            catch { subscriptions.Remove(subscription); throw; }
        }
        finally { Volatile.Write(ref subscriptionCount, subscriptions.Count); gate.Release(); }
    }

    internal async ValueTask RemoveAsync(EventSubscription subscription)
    {
        await gate.WaitAsync();
        try { subscriptions.Remove(subscription); }
        finally { Volatile.Write(ref subscriptionCount, subscriptions.Count); gate.Release(); }
    }

    private ProjectionSnapshot Capture(AuthSession session, EventFilter filter)
    {
        var resources = store?.Read<IReadOnlyList<ProjectedResource>>(connection =>
        {
            using var command = connection.CreateCommand();
            // 仅加载匹配资源；未知事件未进入公开契约，不作为公开投影返回。
            var allowed = ContractEvents.Definitions.Where(d => filter.Topics.Count == 0 || filter.Topics.Contains(d.Type, StringComparer.Ordinal)).Select(d => d.Type).ToArray();
            command.CommandText = "SELECT event_type,resource_id,revision,payload FROM core_state_projection WHERE event_type IN (" +
                string.Join(',', allowed.Select((_, i) => "$topic" + i)) + ")";
            for (var i = 0; i < allowed.Length; i++) command.Parameters.AddWithValue("$topic" + i, allowed[i]);
            if (filter.ResourceIds.Count > 0)
            {
                command.CommandText += " AND resource_id IN (" + string.Join(',', filter.ResourceIds.Select((_, i) => "$resource" + i)) + ")";
                for (var i = 0; i < filter.ResourceIds.Count; i++) command.Parameters.AddWithValue("$resource" + i, filter.ResourceIds[i]);
            }
            command.CommandText += " AND (event_type!='operation.changed' OR $owner=1 OR json_extract(payload,'$.owner')=$principal OR json_extract(payload,'$.kind') IN ('update.check','update.download','update.apply','room.server.start','room.server.stop')) ORDER BY resource_type,resource_id";
            command.Parameters.AddWithValue("$owner", session.Owner ? 1 : 0);
            command.Parameters.AddWithValue("$principal", session.ClientId);
            using var rows = command.ExecuteReader();
            var items = new List<ProjectedResource>();
            var bytes = 0;
            while (rows.Read())
            {
                var type = rows.GetString(0);
                var payload = JsonSerializer.Deserialize<JsonElement>(rows.GetString(3));
                if (!Visible(session, type, payload)) continue;
                var resource = new ProjectedResource(type, ContractEvents.Definitions.Single(d => d.Type == type).Module,
                    rows.GetString(1), UInt64String.Parse(rows.GetString(2)), payload);
                bytes += JsonSerializer.SerializeToUtf8Bytes(resource, ContractJson.Options).Length;
                if (items.Count >= MaximumSnapshotResources || bytes > MaximumSnapshotBytes - 1024)
                    throw new OperationException("resource.capacityExceeded", "投影快照超过预算，请缩小主题或资源过滤范围");
                items.Add(resource);
            }
            return items;
        }) ?? [];
        var combined = resources.Concat(volatileResources.Values.Select(x => x.Resource)
                .Where(x => filter.Matches(x.Type, x.ResourceId) && Visible(session, x.Type, x.Data)))
            .OrderBy(x => x.Module, StringComparer.Ordinal).ThenBy(x => x.ResourceId, StringComparer.Ordinal)
            .ThenBy(x => x.Type, StringComparer.Ordinal).ToArray();
        if (combined.Length > MaximumSnapshotResources || JsonSerializer.SerializeToUtf8Bytes(combined, ContractJson.Options).Length > MaximumSnapshotBytes - 1024)
            throw new OperationException("resource.capacityExceeded", "投影快照超过预算，请缩小主题或资源过滤范围");
        return new(epoch, Cursor(session, filter, sequence), combined);
    }

    private static bool Visible(AuthSession session, string type, JsonElement payload) =>
        session.Permissions.Contains(ApiPermissions.SystemRead, StringComparer.Ordinal) &&
        (type != "shortcut.triggered" || session.Permissions.Contains(ApiPermissions.DesktopControl, StringComparer.Ordinal)) &&
        (type != "diagnostics.record" || session.Permissions.Contains(ApiPermissions.DiagnosticsDebug, StringComparer.Ordinal)) &&
        (type != "game.observation.changed" || session.Permissions.Contains(ApiPermissions.GameObserve, StringComparer.Ordinal)) &&
        (type is not ("storage.changed" or "storage.backup.changed" or "storage.maintenance.changed" or "storage.module-data.changed") || session.Permissions.Contains(ApiPermissions.StorageManage, StringComparer.Ordinal) &&
            payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("owner", out var storageOwner) &&
            storageOwner.ValueKind == JsonValueKind.String && storageOwner.GetString() == session.ClientId) &&
        (type is not ("room.session.changed" or "room.score-draft.changed" or "desktop.surface.changed") ||
            payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("owner", out var subject) && subject.ValueKind == JsonValueKind.String && subject.GetString() == session.ClientId) &&
        (type is not ("operation.changed" or "performance.state.changed" or "music.playback-history.changed" or "music.import.committed" or "color.run.changed" or "room.import.committed" or "color.import.committed" or "room.preferences.changed" or "color.preferences.changed" or "combat.summary.changed" or "combat.effects.changed" or "combat.encounter.closed" or "combat.observation.degraded" or "combat.live.changed") || session.Owner ||
            payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("owner", out var owner) &&
            owner.ValueKind == JsonValueKind.String && (owner.GetString() == session.ClientId ||
                type == "operation.changed" && payload.TryGetProperty("kind", out var kind) &&
                kind.ValueKind == JsonValueKind.String && OperationStore.IsSharedOperation(kind.GetString()!)));

    public string Cursor(AuthSession session, EventFilter filter, ulong value) => epoch + ":" + value.ToString(CultureInfo.InvariantCulture) + ":" + Binding(session, filter);
    private ulong ParseCursor(string cursor, AuthSession session, EventFilter filter)
    {
        var parts = cursor.Split(':');
        if (cursor.Length > 256 || parts.Length != 3 || parts[0] != epoch || parts[2] != Binding(session, filter) ||
            !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value.ToString(CultureInfo.InvariantCulture) != parts[1] || value < replay.Floor || value > sequence)
            throw new OperationException("events.resyncRequired", "事件游标已失效，请重新读取快照");
        return value;
    }
    private static string Binding(AuthSession session, EventFilter filter) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { session.ClientId, session.Owner, filter.Topics, filter.ResourceIds }, ContractJson.Options))))[..32];
}

public sealed class EventSubscription : IAsyncDisposable
{
    private readonly StateEventHub hub;
    private readonly object sync = new();
    private readonly Channel<EventDelivery> queue = Channel.CreateBounded<EventDelivery>(new BoundedChannelOptions(StateEventHub.MaximumQueueEvents)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private int queuedBytes;
    private bool disposed;
    private bool resync;
    public AuthSession Session { get; }
    public EventFilter Filter { get; }
    public ProjectionSnapshot? InitialSnapshot { get; internal set; }
    public IReadOnlyList<EventDelivery> Replay { get; private set; }
    public bool NeedsResync { get { lock (sync) return resync; } }
    public string Cursor(EventDelivery delivery) => hub.Cursor(Session, Filter, delivery.Sequence);

    internal EventSubscription(StateEventHub hub, AuthSession session, EventFilter filter, IReadOnlyList<EventDelivery> replay)
    { this.hub = hub; Session = session; Filter = filter; Replay = replay; }

    internal void Enqueue(EventDelivery delivery)
    {
        lock (sync)
        {
            if (disposed || resync) return;
            if (queuedBytes + delivery.Bytes > StateEventHub.MaximumQueueBytes || !queue.Writer.TryWrite(delivery))
            { resync = true; queue.Writer.TryComplete(); hub.CountResync(); return; }
            queuedBytes += delivery.Bytes;
        }
    }
    public void ReleaseReplay() => Replay = [];
    public bool TryRead(out EventDelivery? delivery)
    {
        lock (sync)
        {
            while (!resync && queue.Reader.TryRead(out delivery))
            {
                queuedBytes -= delivery.Bytes;
                if (!hub.Expired(delivery)) return true;
            }
            delivery = null; return false;
        }
    }
    public async Task<bool> WaitAsync(CancellationToken ct) => await queue.Reader.WaitToReadAsync(ct);
    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true; queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out _)) { }
            queuedBytes = 0; Replay = []; InitialSnapshot = null;
        }
        await hub.RemoveAsync(this);
    }
}

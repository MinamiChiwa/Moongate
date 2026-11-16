using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Host.Rooms;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Presentation;

/// <summary>协议变更合并到最新状态；读取展示快照不新增网络采样。</summary>
public sealed class PresentationStateFeed(RoomSessionManager rooms, DesktopBridgeRegistry desktop, StateEventHub hub,
    SystemInfo system, TimeProvider? clock = null) : BackgroundService
{
    private sealed record SavedRoom(string Fingerprint, UInt64String Revision);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, SavedRoom> savedRooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UInt64String> savedDrafts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> pendingRooms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DesktopSurfaceSnapshot> pendingDesktop = new(StringComparer.Ordinal);
    private readonly Channel<byte> signal = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private const int MaximumRoomSources = 96;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        rooms.StateChanged += QueueRoom; desktop.Changed += QueueDesktop;
        return base.StartAsync(cancellationToken);
    }
    private void QueueRoom(string owner)
    {
        if (pendingRooms.Count < MaximumRoomSources || pendingRooms.ContainsKey(owner))
        { pendingRooms[owner] = 0; signal.Writer.TryWrite(0); }
    }
    private void QueueDesktop(DesktopSurfaceSnapshot snapshot)
    {
        pendingDesktop.AddOrUpdate(snapshot.Id, snapshot, (_, previous) => snapshot.Revision.Value > previous.Revision.Value ? snapshot : previous);
        signal.Writer.TryWrite(0);
    }

    public Task EnsureRoomAsync(string owner, CancellationToken ct) => PublishRoomAsync(owner, false, ct);
    private async Task PublishRoomAsync(string owner, bool clockChanged, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var session = rooms.Get(owner); var sample = rooms.GetClockSample(owner); var draft = rooms.GetScoreDraft(owner, false);
            var fingerprint = JsonSerializer.Serialize(new { session, draft, clock = sample is null ? null : sample with { SampleAgeMs = 0 } }, ContractJson.Options);
            savedRooms.TryGetValue(owner, out var previous);
            if (!clockChanged && previous?.Fingerprint == fingerprint) return;
            if (previous is null && savedRooms.Count >= MaximumRoomSources || previous?.Revision.Value == ulong.MaxValue)
                throw new OperationException("resource.capacityExceeded", "房间展示来源数量或修订已耗尽");
            var revision = new UInt64String((previous?.Revision.Value ?? 0) + 1);
            var observedAt = time.GetUtcNow();
            var payload = new RoomPresentation(owner, system.InstanceId, observedAt, session, sample);
            await hub.PublishVolatileAsync(new("room-view-" + owner + "-" + revision, "room-presentation", "room:" + owner, revision,
                "room.session.changed", JsonSerializer.SerializeToElement(payload, ContractJson.Options)), observedAt, ct);
            savedRooms[owner] = new(fingerprint, revision);
            if (!savedDrafts.TryGetValue(owner, out var previousDraft) || previousDraft != draft.Revision)
            {
                await hub.PublishVolatileAsync(new("room-score-" + owner + "-" + draft.Revision, "room-score-draft", "room-score:" + owner,
                    draft.Revision, "room.score-draft.changed", JsonSerializer.SerializeToElement(new RoomScoreDraftChanged(owner, draft), ContractJson.Options)), observedAt, ct);
                savedDrafts[owner] = draft.Revision;
            }
        }
        finally { gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var pulse in signal.Reader.ReadAllAsync(stoppingToken))
        {
            foreach (var owner in pendingRooms.Keys)
                if (pendingRooms.TryRemove(owner, out _))
                    try { await PublishRoomAsync(owner, true, stoppingToken); }
                    catch (OperationException) { }
            foreach (var id in pendingDesktop.Keys)
                if (pendingDesktop.TryRemove(id, out var snapshot))
                    try
                    {
                        await hub.PublishVolatileAsync(new("desktop-view-" + id + "-" + snapshot.Revision,
                            "desktop-presentation", id, snapshot.Revision, "desktop.surface.changed",
                            JsonSerializer.SerializeToElement(snapshot, ContractJson.Options)), time.GetUtcNow(), stoppingToken);
                    }
                    catch (OperationException) { }
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        rooms.StateChanged -= QueueRoom; desktop.Changed -= QueueDesktop;
        signal.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }
}


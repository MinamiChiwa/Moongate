using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Modules.Rooms.Domain;

public sealed record RoomInputBinding(string GameSessionId, UInt64String BindingRevision, UInt64String WindowIdentity);

public interface IRoomInputBindingSource
{
    RoomInputBinding? Current { get; }
}

public interface IRoomInputActionMapper
{
    bool TryMap(ScheduledAction action, out InputControl control);
}

public sealed class GameSessionRoomInputBindingSource(GameSessionBindingTracker tracker) : IRoomInputBindingSource
{
    public RoomInputBinding? Current => tracker.Current is { CanAcceptInput: true } binding
        ? new(binding.Session.Id, binding.Session.BindingRevision, binding.Session.WindowIdentity)
        : null;
}

public interface IRoomInputTimingQuality
{
    string? ValidateForPreparation(string actionSessionId, RoomClock roomClock, int timingOffsetMs);
}

public sealed class RoomClockInputTimingQuality(double maximumRoundTripMs = 250,
    double maximumUncertaintyMs = 100, double maximumAgeMs = 3_000) : IRoomInputTimingQuality
{
    public string? ValidateForPreparation(string actionSessionId, RoomClock roomClock, int timingOffsetMs)
    {
        _ = actionSessionId;
        var estimate = roomClock.Estimate;
        if (estimate is null) return "room.clockUnavailable";
        if (estimate.RoundTripMs > maximumRoundTripMs || estimate.UncertaintyMs > maximumUncertaintyMs || estimate.AgeMs > maximumAgeMs)
            return "room.clockQualityInsufficient";
        return null;
    }
}

public sealed class LeasedRoomInputExecutor : IInputExecutor
{
    private sealed record Prepared(ResourceLeaseSnapshot Lease, RoomInputBinding Binding, InputControl Control);

    private readonly object gate = new();
    private readonly string ownerId;
    private readonly ResourceLeaseCoordinator leases;
    private readonly IRoomInputBindingSource bindings;
    private readonly IRoomInputActionMapper actionMapper;
    private readonly IRoomInputTimingQuality timingQuality;
    private readonly GameInputBroker broker;
    private readonly IMonotonicClock clock;
    private readonly RoomClock roomClock;
    private readonly Func<string, int> timingOffsetProvider;
    private readonly TimeSpan leaseDuration;
    private readonly Dictionary<string, Prepared> prepared = new(StringComparer.Ordinal);

    public LeasedRoomInputExecutor(string ownerId, ResourceLeaseCoordinator leases, IRoomInputBindingSource bindings,
        IRoomInputActionMapper actionMapper, IRoomInputTimingQuality timingQuality, GameInputBroker broker,
        IMonotonicClock clock, RoomClock roomClock, Func<string, int> timingOffsetProvider,
        TimeSpan? leaseDuration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        this.ownerId = ownerId;
        this.leases = leases;
        this.bindings = bindings;
        this.actionMapper = actionMapper;
        this.timingQuality = timingQuality;
        this.broker = broker;
        this.clock = clock;
        this.roomClock = roomClock;
        this.timingOffsetProvider = timingOffsetProvider;
        this.leaseDuration = leaseDuration ?? TimeSpan.FromSeconds(30);
        if (this.leaseDuration <= TimeSpan.Zero || this.leaseDuration > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
    }

    public bool IsSimulation(ScheduledAction action) => false;

    public InputPreparation Prepare(ScheduledAction action)
    {
        lock (gate)
        {
            if (prepared.ContainsKey(action.Id)) return new(false, "input.actionAlreadyPrepared");
            var offset = timingOffsetProvider(action.SessionId);
            if (offset is < -2000 or > 2000) return new(false, "room.timingOffsetInvalid");
            var timingError = timingQuality.ValidateForPreparation(action.SessionId, roomClock, offset);
            if (timingError is not null) return new(false, timingError);
            var binding = bindings.Current;
            if (binding is null)
                return new(false, "input.bindingUnavailable");
            if (!actionMapper.TryMap(action, out var control) || !InputControls.IsValid(control))
                return new(false, "input.actionMappingUnavailable");

            ResourceLeaseSnapshot lease;
            try
            {
                lease = leases.Acquire(new(ownerId, action.Id, binding.GameSessionId, binding.BindingRevision,
                    ["activity-control:" + binding.GameSessionId, "foreground-input"], leaseDuration));
            }
            catch (ResourceLeaseException error)
            {
                return new(false, error.ConflictingOwner is null ? error.Code : $"{error.Code}:{error.ConflictingOwner}");
            }

            if (bindings.Current != binding)
            {
                leases.Release(lease.Id);
                return new(false, "input.bindingChanged");
            }
            prepared.Add(action.Id, new(lease, binding, control));
            return new(true, null);
        }
    }

    public InputOutcome Execute(ScheduledAction action, CancellationToken cancellationToken)
    {
        Prepared state;
        lock (gate)
        {
            if (!prepared.TryGetValue(action.Id, out state!))
                return new("rejected", null, false, "input.actionNotPrepared");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (bindings.Current != state.Binding)
        {
            Forget(action.Id);
            return new("rejected", null, false, "input.bindingChanged");
        }

        var gameAction = new GameInputAction(ownerId, state.Lease.Id, state.Lease.FencingToken,
            state.Binding.GameSessionId, state.Binding.BindingRevision, state.Binding.WindowIdentity,
            InputActionKind.Press, state.Control);
        var pressed = false;
        try
        {
            broker.Execute(gameAction);
            pressed = true;
            cancellationToken.ThrowIfCancellationRequested();
            broker.Execute(gameAction with { Kind = InputActionKind.Release });
            pressed = false;
            return new("sent", clock.NowMs, false, null);
        }
        catch (GameInputException error)
        {
            return new("rejected", null, false, error.Code);
        }
        finally
        {
            if (pressed)
            {
                try { broker.Execute(gameAction with { Kind = InputActionKind.Release }); }
                catch (GameInputException) { }
            }
            Forget(action.Id);
        }
    }

    public void Forget(string actionId)
    {
        Prepared? item;
        lock (gate)
        {
            if (!prepared.Remove(actionId, out item)) return;
        }
        leases.Release(item.Lease.Id);
    }

    public void ReleaseAll()
    {
        Prepared[] items;
        lock (gate)
        {
            items = prepared.Values.ToArray();
            prepared.Clear();
        }
        foreach (var item in items)
        {
            try
            {
                broker.Execute(new(ownerId, item.Lease.Id, item.Lease.FencingToken,
                    item.Binding.GameSessionId, item.Binding.BindingRevision, item.Binding.WindowIdentity,
                    InputActionKind.Release, item.Control));
            }
            catch (GameInputException) { }
            finally { leases.Release(item.Lease.Id); }
        }
    }
}

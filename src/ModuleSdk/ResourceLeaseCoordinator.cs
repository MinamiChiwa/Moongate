using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.ModuleSdk;

public sealed record ResourceLeaseRequest(string OwnerId, string OperationId, string GameSessionId,
    UInt64String BindingRevision, IReadOnlyList<string> Resources, TimeSpan Duration);

public sealed record ResourceLeaseSnapshot(string Id, string OwnerId, string OperationId, string GameSessionId,
    UInt64String BindingRevision, IReadOnlyList<string> Resources, UInt64String FencingToken,
    DateTimeOffset ExpiresAt);

public sealed class ResourceLeaseException(string code, string? conflictingOwner = null) : Exception(code)
{
    public string Code { get; } = code;
    public string? ConflictingOwner { get; } = conflictingOwner;
}

public sealed class ResourceLeaseCoordinator(TimeProvider? timeProvider = null, ActivityBarrier? activityBarrier = null)
{
    private static readonly Regex ResourcePattern = new(@"\A[a-z][a-z0-9.-]{0,63}(?::[a-zA-Z0-9._-]{1,128})?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly object gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ActivityBarrier activities = activityBarrier ?? new();
    private readonly Dictionary<string, Lease> leases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Lease> ownersByResource = new(StringComparer.Ordinal);
    private ulong nextFencingToken;

    public ResourceLeaseSnapshot Acquire(ResourceLeaseRequest request)
    {
        Validate(request);
        lock (gate)
        {
            ExpireLeases();
            var conflict = request.Resources.Select(resource => ownersByResource.GetValueOrDefault(resource))
                .FirstOrDefault(lease => lease is not null);
            if (conflict is not null) throw new ResourceLeaseException("resource.busy", conflict.Request.OwnerId);

            var token = new UInt64String(checked(++nextFencingToken));
            var lease = new Lease(Guid.NewGuid().ToString("N"), request with
            {
                Resources = Array.AsReadOnly(request.Resources.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())
            }, token, clock.GetTimestamp(), clock.GetUtcNow());
            lease.Activity = activities.EnterActivity(request.OperationId, "game.resource");
            leases.Add(lease.Id, lease);
            foreach (var resource in lease.Request.Resources) ownersByResource.Add(resource, lease);
            return Snapshot(lease);
        }
    }

    public ResourceLeaseSnapshot Renew(string leaseId, UInt64String fencingToken, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
            throw new ResourceLeaseException("resource.durationInvalid");
        lock (gate)
        {
            ExpireLeases();
            if (!leases.TryGetValue(leaseId, out var lease) || lease.FencingToken != fencingToken)
                throw new ResourceLeaseException("resource.leaseExpired");
            lease.Duration = duration;
            lease.AcquiredAt = clock.GetTimestamp();
            lease.ExpiresAt = clock.GetUtcNow().Add(duration);
            return Snapshot(lease);
        }
    }

    public ResourceLeaseSnapshot RebindPrepared(string leaseId, UInt64String token, UInt64String revision)
    {
        lock (gate)
        {
            ExpireLeases();
            if (!leases.TryGetValue(leaseId, out var lease) || lease.FencingToken != token)
                throw new ResourceLeaseException("resource.leaseExpired");
            if (revision.Value < lease.Request.BindingRevision.Value) throw new ResourceLeaseException("input.bindingChanged");
            if (revision == lease.Request.BindingRevision) return Snapshot(lease);
            lease.Request = lease.Request with { BindingRevision = revision };
            lease.FencingToken = new(checked(++nextFencingToken));
            return Snapshot(lease);
        }
    }

    public bool Validate(string leaseId, string resource, UInt64String fencingToken,
        string gameSessionId, UInt64String bindingRevision, string? ownerId = null)
    {
        lock (gate)
        {
            ExpireLeases();
            return leases.TryGetValue(leaseId, out var lease) && lease.FencingToken == fencingToken &&
                (ownerId is null || string.Equals(lease.Request.OwnerId, ownerId, StringComparison.Ordinal)) &&
                lease.Request.Resources.Contains(resource, StringComparer.Ordinal) &&
                string.Equals(lease.Request.GameSessionId, gameSessionId, StringComparison.Ordinal) &&
                lease.Request.BindingRevision == bindingRevision;
        }
    }

    public bool Release(string leaseId)
    {
        lock (gate) return Remove(leaseId);
    }

    public ResourceLeaseSnapshot? FindConflict(IReadOnlyList<string> resources)
    {
        lock (gate)
        {
            ExpireLeases();
            var occupied = resources.Select(resource => ownersByResource.GetValueOrDefault(resource)).FirstOrDefault(value => value is not null);
            return occupied is null ? null : Snapshot(occupied);
        }
    }

    public IReadOnlyList<ResourceLeaseSnapshot> Snapshot()
    {
        lock (gate)
        {
            ExpireLeases();
            return Array.AsReadOnly(leases.Values.OrderBy(lease => lease.Id, StringComparer.Ordinal)
                .Select(Snapshot).ToArray());
        }
    }

    public int RevokeOwner(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        lock (gate) return leases.Values.Where(x => x.Request.OwnerId == ownerId).Select(x => x.Id).ToArray().Count(Remove);
    }

    public int RevokeSession(string gameSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameSessionId);
        lock (gate) return leases.Values.Where(x => x.Request.GameSessionId == gameSessionId).Select(x => x.Id).ToArray().Count(Remove);
    }

    public int RevokeResource(string resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        lock (gate) return leases.Values.Where(x => x.Request.Resources.Contains(resource, StringComparer.Ordinal))
            .Select(x => x.Id).ToArray().Count(Remove);
    }

    public int RevokeAll()
    {
        lock (gate) return leases.Keys.ToArray().Count(Remove);
    }

    public int SweepExpired()
    {
        lock (gate)
        {
            var before = leases.Count;
            ExpireLeases();
            return before - leases.Count;
        }
    }

    private void ExpireLeases()
    {
        var now = clock.GetTimestamp();
        foreach (var lease in leases.Values.Where(x => clock.GetElapsedTime(x.AcquiredAt, now) >= x.Duration)
                     .Select(x => x.Id).ToArray()) Remove(lease);
    }

    private bool Remove(string leaseId)
    {
        if (!leases.Remove(leaseId, out var lease)) return false;
        foreach (var resource in lease.Request.Resources)
            if (ownersByResource.TryGetValue(resource, out var owner) && owner.Id == leaseId)
                ownersByResource.Remove(resource);
        lease.Activity?.Dispose();
        return true;
    }

    private ResourceLeaseSnapshot Snapshot(Lease lease) => new(lease.Id, lease.Request.OwnerId,
        lease.Request.OperationId, lease.Request.GameSessionId, lease.Request.BindingRevision,
        lease.Request.Resources, lease.FencingToken, lease.ExpiresAt);

    private static void Validate(ResourceLeaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.OwnerId) || string.IsNullOrWhiteSpace(request.OperationId) ||
            string.IsNullOrWhiteSpace(request.GameSessionId) || request.Resources is null || request.Resources.Count == 0 ||
            request.Resources.Any(resource => string.IsNullOrWhiteSpace(resource) || !ResourcePattern.IsMatch(resource)) ||
            request.Duration <= TimeSpan.Zero || request.Duration > TimeSpan.FromMinutes(5))
            throw new ResourceLeaseException("resource.requestInvalid");
    }

    private sealed class Lease(string id, ResourceLeaseRequest request, UInt64String fencingToken,
        long acquiredAt, DateTimeOffset acquiredUtc)
    {
        public string Id { get; } = id;
        public IDisposable? Activity { get; set; }
        public ResourceLeaseRequest Request { get; set; } = request;
        public UInt64String FencingToken { get; set; } = fencingToken;
        public long AcquiredAt { get; set; } = acquiredAt;
        public TimeSpan Duration { get; set; } = request.Duration;
        public DateTimeOffset ExpiresAt { get; set; } = acquiredUtc.Add(request.Duration);
    }
}

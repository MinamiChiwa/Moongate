using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Presentation;

/// <summary>注册入口供嵌入式桌面宿主使用；HTTP 只访问已关联本主体的表面。</summary>
public sealed class DesktopBridgeRegistry(TimeProvider? clock = null) : IAsyncDisposable
{
    private sealed record KeyedResult(string Hash, DesktopSurfaceSnapshot Snapshot, long Timestamp);
    private sealed class Entry(string id, string owner, IDesktopSurface surface)
    {
        public string Id { get; } = id;
        public string Owner { get; } = owner;
        public IDesktopSurface Surface { get; } = surface;
        public DesktopSurfaceCapabilities Capabilities { get; } = surface.Capabilities;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationTokenSource Closed { get; } = new();
        public Dictionary<string, KeyedResult> Keys { get; } = new(StringComparer.Ordinal);
        public DesktopSurfaceState? LastState { get; set; }
        public ulong Revision { get; set; }
    }
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private bool stopped;
    public event Action<DesktopSurfaceSnapshot>? Changed;
    public const int MaximumSurfaces = 32;
    public const int MaximumSurfacesPerOwner = 4;
    public const int MaximumKeysPerSurface = 128;
    public static readonly TimeSpan KeyRetention = TimeSpan.FromMinutes(5);
    public static string ETag(UInt64String revision) => "\"desktop-surface-" + revision + "\"";

    public IDisposable Register(string owner, IDesktopSurface surface, out string id)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128) throw new ArgumentException("桌面表面主体无效", nameof(owner));
        ArgumentNullException.ThrowIfNull(surface);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (entries.Count >= MaximumSurfaces || entries.Values.Count(x => x.Owner == owner) >= MaximumSurfacesPerOwner)
                throw new OperationException("resource.capacityExceeded", "桌面表面数量达到上限");
            id = Guid.NewGuid().ToString("N");
            entries.TryAdd(id, new(id, owner, surface));
            return new Registration(this, id);
        }
    }

    public async Task<DesktopBridgeStatus> StatusAsync(string owner, CancellationToken ct)
    {
        var surfaces = new List<DesktopSurfaceSnapshot>();
        foreach (var entry in entries.Values.Where(x => x.Owner == owner).OrderBy(x => x.Id))
        {
            try { surfaces.Add(await ReadAsync(owner, entry.Id, ct)); }
            catch (OperationException error) when (error.Code == "resource.notFound") { }
        }
        return new(surfaces.Count > 0 ? "available" : "unavailable",
            surfaces.Count > 0 ? null : "desktop.bridgeMissing", surfaces);
    }

    public async Task<DesktopSurfaceSnapshot> ReadAsync(string owner, string id, CancellationToken ct)
    {
        var entry = Find(owner, id);
        await entry.Gate.WaitAsync(ct);
        try { return await RefreshAsync(entry, ct); }
        finally { entry.Gate.Release(); }
    }

    public async Task<DesktopSurfaceSnapshot> ApplyAsync(string owner, string id, DesktopSurfaceChange change,
        string? key, string? ifMatch, CancellationToken ct)
    {
        if (key is null) throw new OperationException("idempotency.required", "桌面控制需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z")) throw new OperationException("request.invalid", "桌面控制幂等键无效");
        if (ifMatch is null) throw new OperationException("revision.required", "桌面控制需要 If-Match");
        if (!Regex.IsMatch(ifMatch, "\\A\"desktop-surface-(0|[1-9][0-9]{0,19})\"\\z"))
            throw new OperationException("request.invalid", "桌面表面 ETag 无效");
        if (change is null || change.Topmost is null && change.ClickThrough is null && change.ExcludeFromCapture is null)
            throw new OperationException("request.invalid", "桌面控制须指定至少一个状态");
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { change, ifMatch }, ContractJson.Options)));
        var entry = Find(owner, id);
        await entry.Gate.WaitAsync(ct);
        try
        {
            var before = await RefreshAsync(entry, ct);
            foreach (var expired in entry.Keys.Where(x => time.GetElapsedTime(x.Value.Timestamp) >= KeyRetention).Select(x => x.Key).ToArray())
                entry.Keys.Remove(expired);
            if (entry.Keys.TryGetValue(key, out var previous))
            {
                if (previous.Hash != hash) throw new OperationException("idempotency.conflict", "幂等键已用于其他桌面状态或修订");
                if (entry.Closed.IsCancellationRequested) throw Missing();
                return previous.Snapshot;
            }
            if (ifMatch != ETag(before.Revision)) throw new OperationException("revision.stale", "桌面表面已变化，请重新读取");
            var caps = entry.Capabilities;
            if (change.Topmost is not null && !caps.Topmost || change.ClickThrough is not null && !caps.ClickThrough ||
                change.ExcludeFromCapture is not null && !caps.ExcludeFromCapture)
                throw new OperationException("module.unavailable", "桌面适配器未提供所请求的能力");
            if (entry.Keys.Count >= MaximumKeysPerSurface || entry.Revision == ulong.MaxValue)
                throw new OperationException("resource.capacityExceeded", "桌面表面幂等或修订预算已耗尽");
            // 受理后使用独立期限；HTTP 断开不取消已经提交的布尔状态调整。
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(entry.Closed.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            DesktopSurfaceState state;
            try { state = await entry.Surface.ApplyAsync(change, deadline.Token).AsTask().WaitAsync(deadline.Token); }
            catch (DesktopSurfaceException error) { throw Translate(error); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested || entry.Closed.IsCancellationRequested)
            { throw new OperationException("resource.busy", "桌面表面控制超时或已关闭，请读取实际状态"); }
            if (entry.Closed.IsCancellationRequested) throw Missing();
            if (change.Topmost is { } topmost && state.Topmost != topmost ||
                change.ClickThrough is { } clickThrough && state.ClickThrough != clickThrough ||
                change.ExcludeFromCapture is { } hidden && state.ExcludeFromCapture != hidden)
                throw new OperationException("resource.conflict", "桌面表面尚未呈现请求的状态");
            entry.LastState = state;
            entry.Revision++;
            var result = Snapshot(entry);
            entry.Keys.Add(key, new(hash, result, time.GetTimestamp()));
            Notify(result);
            return result;
        }
        finally { entry.Gate.Release(); }
    }

    private async Task<DesktopSurfaceSnapshot> RefreshAsync(Entry entry, CancellationToken ct)
    {
        if (entry.Closed.IsCancellationRequested) throw Missing();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Closed.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        DesktopSurfaceState state;
        try { state = await entry.Surface.ReadAsync(deadline.Token).AsTask().WaitAsync(deadline.Token); }
        catch (DesktopSurfaceException error)
        {
            if (error.Code == "desktop.surfaceUnavailable") Remove(entry.Id);
            throw Translate(error);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new OperationException("resource.busy", "桌面表面读取超时或已关闭"); }
        if (entry.Closed.IsCancellationRequested) throw Missing();
        if (entry.LastState != state)
        {
            if (entry.Revision == ulong.MaxValue) throw new OperationException("resource.capacityExceeded", "桌面表面修订已耗尽");
            entry.LastState = state; entry.Revision++;
            Notify(Snapshot(entry));
        }
        return Snapshot(entry);
    }

    private Entry Find(string owner, string id) => entries.TryGetValue(id, out var entry) && entry.Owner == owner && !entry.Closed.IsCancellationRequested
        ? entry : throw Missing();
    private static OperationException Missing() => new("resource.notFound", "桌面表面不存在");
    private static OperationException Translate(DesktopSurfaceException error) => error.Code == "desktop.surfaceUnavailable" ? Missing()
        : new("resource.conflict", "桌面表面调整失败，请读取实际状态");
    private static DesktopSurfaceSnapshot Snapshot(Entry entry) => new(entry.Id, entry.Owner, new(entry.Revision), true, null, entry.Capabilities, entry.LastState);
    private void Notify(DesktopSurfaceSnapshot snapshot)
    {
        lock (gate)
        {
            if (snapshot.Available && (!entries.TryGetValue(snapshot.Id, out var entry) || entry.Closed.IsCancellationRequested)) return;
            foreach (var handler in Changed?.GetInvocationList() ?? [])
                try { ((Action<DesktopSurfaceSnapshot>)handler)(snapshot); } catch { }
        }
    }
    private void Remove(string id)
    {
        lock (gate)
        {
            if (!entries.TryRemove(id, out var entry)) return;
            entry.Closed.Cancel();
            Notify(new(entry.Id, entry.Owner, new(entry.Revision == ulong.MaxValue ? ulong.MaxValue : entry.Revision + 1),
                false, "desktop.surfaceUnavailable", entry.Capabilities, null));
        }
    }
    public async ValueTask DisposeAsync()
    {
        Entry[] active;
        lock (gate) { if (stopped) return; stopped = true; active = entries.Values.ToArray(); }
        foreach (var entry in active) Remove(entry.Id);
        foreach (var entry in active) { await entry.Gate.WaitAsync(); entry.Gate.Release(); entry.Closed.Dispose(); entry.Gate.Dispose(); }
    }
    private sealed class Registration(DesktopBridgeRegistry registry, string id) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) registry.Remove(id); }
    }
}

using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Host.Rooms;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Diagnostics;

/// <summary>把游戏绑定变化与房间连接变化记为 info 生命周期事件，只记录固定状态码与数值。</summary>
internal sealed class DiagnosticLifecycleObserver(DiagnosticTrace trace, GameControlContext controls, RoomSessionManager rooms,
    TimeProvider? clock = null) : IHostedService
{
    /// <summary>同一游戏会话内只有窗口位置、尺寸或 DPI 变化的修订，至多每 10 秒记一条，期间的修订并入下一条的 merged。</summary>
    internal static readonly TimeSpan GeometryInterval = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, string> roomStates = new(StringComparer.Ordinal);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object bindingGate = new();
    private GameWindowBinding? lastBinding;
    private long? lastGeometryAt;
    private int mergedGeometry;
    private readonly SortedSet<string> pendingAspects = new(StringComparer.Ordinal);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        controls.Bindings.Changed += OnBinding;
        rooms.StateChanged += OnRoom;
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken)
    {
        controls.Bindings.Changed -= OnBinding;
        rooms.StateChanged -= OnRoom;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 绑定修订变化记 game.binding.changed：changed 写明相对上一次的变化（window、position、size、dpi、minimized，
    /// 以及窗口读不到或恢复时能否接收输入的 input，多项以 + 连接），
    /// 会话、绑定状态或最小化变化立即记录；只有几何变化时按 <see cref="GeometryInterval"/> 合并。
    /// </summary>
    private void OnBinding(GameWindowBinding? binding, bool sessionChanged)
    {
        string changed; int merged;
        lock (bindingGate)
        {
            var previous = lastBinding; lastBinding = binding;
            var aspects = Aspects(previous, binding);
            var geometryOnly = !sessionChanged && binding is not null && previous is not null && aspects.Count > 0 &&
                aspects.All(aspect => aspect is "position" or "size" or "dpi");
            var now = time.GetTimestamp();
            if (geometryOnly && lastGeometryAt is { } last && time.GetElapsedTime(last, now) < GeometryInterval)
            {
                mergedGeometry++; pendingAspects.UnionWith(aspects); return;
            }
            if (geometryOnly) lastGeometryAt = now;
            pendingAspects.UnionWith(aspects);
            changed = sessionChanged || binding is null ? "session" : pendingAspects.Count == 0 ? "none" : string.Join('+', pendingAspects);
            merged = mergedGeometry; mergedGeometry = 0; pendingAspects.Clear();
        }
        trace.Emit(DiagnosticEvents.BindingChanged, new DiagnosticFields().Value("state", binding is null ? "unbound" : "bound")
            .Count("revision", binding?.Session.BindingRevision.Value ?? 0).Flag("sessionChanged", sessionChanged)
            .Flag("minimized", binding?.IsMinimized ?? false).Value("changed", changed).Count("merged", merged));
    }

    private static List<string> Aspects(GameWindowBinding? previous, GameWindowBinding? current)
    {
        var result = new List<string>();
        if (previous is null || current is null) return result;
        if (previous.Session.WindowIdentity != current.Session.WindowIdentity) result.Add("window");
        if (previous.Bounds.X != current.Bounds.X || previous.Bounds.Y != current.Bounds.Y) result.Add("position");
        if (previous.Bounds.Width != current.Bounds.Width || previous.Bounds.Height != current.Bounds.Height) result.Add("size");
        if (previous.DpiX != current.DpiX || previous.DpiY != current.DpiY) result.Add("dpi");
        if (previous.IsMinimized != current.IsMinimized) result.Add("minimized");
        else if (previous.CanAcceptInput != current.CanAcceptInput) result.Add("input");
        return result;
    }

    private void OnRoom(string owner)
    {
        var snapshot = rooms.Get(owner);
        lock (roomStates)
        {
            if (roomStates.TryGetValue(owner, out var previous) && previous == snapshot.State) return;
            if (!roomStates.ContainsKey(owner) && snapshot.State == "disconnected") return;
            if (roomStates.Count >= 64 && !roomStates.ContainsKey(owner)) roomStates.Remove(roomStates.Keys.First());
            roomStates[owner] = snapshot.State;
        }
        trace.Emit(DiagnosticEvents.RoomConnection, new DiagnosticFields().Value("state", snapshot.State).Flag("roomOwner", snapshot.IsOwner));
    }
}

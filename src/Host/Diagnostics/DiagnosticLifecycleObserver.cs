using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Host.Rooms;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Diagnostics;

/// <summary>把游戏绑定变化与房间连接变化记为 info 生命周期事件，只记录固定状态码与数值。</summary>
internal sealed class DiagnosticLifecycleObserver(DiagnosticTrace trace, GameControlContext controls, RoomSessionManager rooms) : IHostedService
{
    private readonly Dictionary<string, string> roomStates = new(StringComparer.Ordinal);

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

    private void OnBinding(GameWindowBinding? binding, bool sessionChanged) =>
        trace.Emit(DiagnosticEvents.BindingChanged, new DiagnosticFields().Value("state", binding is null ? "unbound" : "bound")
            .Count("revision", binding?.Session.BindingRevision.Value ?? 0).Flag("sessionChanged", sessionChanged)
            .Flag("minimized", binding?.IsMinimized ?? false));

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

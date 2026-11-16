using MabiTool.Host.Game;
using MabiTool.ModuleSdk;
using MabiTool.Modules.Inventory.Adapters;

namespace MabiTool.Host.Inventory;

/// <summary>库存完整同步状态、登录身份信号及共享捕获帧计数；不含身份值和条目内容。</summary>
internal sealed class InventoryDiagnosticProbe(InventoryOperations operations, InventoryHistoryStore history,
    GameObservationService observation, GameControlContext controls) : IDiagnosticProbe
{
    public string Id => "inventory";
    public string Module => "inventory";
    public DiagnosticProbeReading Capture()
    {
        var (observing, other, previews, accepting) = operations.DiagnosticState();
        var workspace = history.WorkspaceState();
        var reading = new DiagnosticProbeReading(!accepting ? "stopped" : observing == 0 ? "idle" : workspace?.Current == true ? "synced" : "awaiting-full")
            .Flag("simulation", controls.Simulation).Flag("installed", observation.Runtime().Installed)
            .Count("operations.observing", observing).Count("operations.other", other).Count("importPreviews", previews)
            .Count("revision", history.Revision.Value)
            .Value("fullSync", workspace is null ? "none" : workspace.Current ? "current" : "stale");
        if (workspace is not null)
            reading.Value("workspace.quality", workspace.Quality.RawValue).Flag("workspace.identityPresent", workspace.IdentityPresent)
                .Count("workspace.items", workspace.Items).Count("workspace.reasons", workspace.Reasons.Count)
                .Value("workspace.reason", workspace.Reasons.FirstOrDefault());
        observation.AppendInventoryCapture(reading);
        return reading;
    }
}

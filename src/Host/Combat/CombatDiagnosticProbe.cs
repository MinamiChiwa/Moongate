using MabiTool.Host.Game;
using MabiTool.ModuleSdk;
using MabiTool.Modules.CombatObservation.Application;

namespace MabiTool.Host.Combat;

/// <summary>战斗观察会话、能力门槛及共享捕获上的帧拒绝与缺口计数。</summary>
internal sealed class CombatDiagnosticProbe(CombatOperations operations, ICombatObservationSource source,
    GameObservationService observation, GameControlContext controls) : IDiagnosticProbe
{
    public string Id => "combat";
    public string Module => "combat-observation";
    public DiagnosticProbeReading Capture()
    {
        var (active, sessions, queued, accepting) = operations.DiagnosticState();
        var usage = operations.InputUsage();
        var reading = new DiagnosticProbeReading(!accepting ? "stopped" : sessions > 0 ? "observing" : active > 0 ? "starting" : "idle")
            .Count("operations.active", active).Count("operations.queued", queued).Count("sessions", sessions)
            .Count("buffer.events", usage.QueuedEvents).Count("buffer.bytes", usage.QueueBytes);
        var capabilities = source is SharedCaptureCombatSource shared
            ? shared.Capabilities(controls.Bindings.LastKnown?.Session, null) : source.Capabilities(null);
        if (source is SharedCaptureCombatSource profiled) reading.Value("profile", profiled.ProfileId);
        reading.Flag("simulation", capabilities.Simulation);
        foreach (var capability in capabilities.Capabilities)
            reading.Value("capabilities." + capability.Id + ".status", capability.Status.RawValue)
                .Value("capabilities." + capability.Id + ".reason", capability.Reason);
        observation.AppendCombatCapture(reading);
        return reading;
    }
}

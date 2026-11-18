using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwObservation.Combat;

namespace MabiTool.Host.Game;

/// <summary>游戏绑定、构建识别和 Provider 能力门槛；使用最近一次已刷新的绑定，不查询窗口或客户端文件。</summary>
internal sealed class GameBindingDiagnosticProbe(GameControlContext controls, ProviderCapabilityRegistry capabilities,
    GameBuildAdaptation? builds = null) : IDiagnosticProbe
{
    public string Id => "game.binding";
    public string Module => "game-access";
    public DiagnosticProbeReading Capture()
    {
        var binding = controls.Bindings.LastKnown;
        var session = binding?.Session;
        var registered = session?.GameBuild == TwClientFingerprint.GameBuild;
        var adaptive = TwClientFingerprint.IsAdaptive(session?.GameBuild);
        var reading = new DiagnosticProbeReading(binding is null ? "unbound" : binding.IsMinimized ? "minimized" : "bound")
            .Flag("simulation", controls.Simulation).Count("bindingRevision", session?.BindingRevision.Value ?? 0);
        if (session is not null)
        {
            reading.Value("region", session.Region).Flag("buildRecognized", registered || adaptive)
                .Value("build", registered ? "tw-20261110" : adaptive ? "adaptive" : controls.Simulation ? "simulation" : session.GameBuild is null ? "none" : "unrecognized")
                .Value("combatProfile", registered || adaptive ? Tw20261110CombatProtocol.CaptureProfile : null);
            // 未登记构建的运行时核对：verifying 表示正在读取客户端，其余为各项启用或停用原因码。
            if (adaptive && builds is not null)
            {
                var verdict = builds.Verdict(session);
                static string State(TwCapabilityCheck check) => check.Enabled ? "enabled" : check.Reason ?? "disabled";
                reading.Value("buildCheck", verdict is null ? builds.Verifying(session) ? "verifying" : "unavailable" : "completed");
                if (verdict is not null)
                    reading.Value("buildCheck.identity", State(verdict.Identity)).Value("buildCheck.music", State(verdict.Music))
                        .Value("buildCheck.combat", State(verdict.Combat));
            }
        }
        foreach (var capability in capabilities.Snapshot())
            reading.Value("capabilities." + capability.Id + ".status", capability.Status.RawValue)
                .Value("capabilities." + capability.Id + ".reason", capability.ReasonCode);
        return reading;
    }
}

/// <summary>输入租约与紧急停止；持有者只报告类别，不报告客户端或操作标识。</summary>
internal sealed class GameInputDiagnosticProbe(GameControlContext controls) : IDiagnosticProbe
{
    public string Id => "game.input";
    public string Module => "game-access";
    public DiagnosticProbeReading Capture()
    {
        var leases = controls.Leases.Snapshot();
        var input = leases.Where(lease => lease.Resources.Contains("foreground-input")).ToArray();
        var activity = leases.Count(lease => lease.Resources.Any(resource => resource == "activity-control" ||
            resource.StartsWith("activity-control:", StringComparison.Ordinal)));
        static string Holder(ResourceLeaseSnapshot lease) => lease.OwnerId == "color" ? "color" :
            lease.OperationId.StartsWith("room-", StringComparison.Ordinal) ? "rooms" : "other";
        return new DiagnosticProbeReading(input.Length == 0 ? "idle" : "held")
            .Value("holder", input.Length == 0 ? "none" : Holder(input[0]))
            .Count("leases", leases.Count).Count("leases.foregroundInput", input.Length).Count("leases.activityControl", activity)
            .Count("heldControls", controls.Input.HeldControls).Count("emergencyStops", controls.Input.EmergencyStops);
    }
}

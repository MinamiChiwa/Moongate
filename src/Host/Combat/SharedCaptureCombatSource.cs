using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.Modules.CombatObservation.Application;

namespace MabiTool.Host.Combat;

public sealed class SharedCaptureCombatSource(GameObservationService observations, GameControlContext controls,
    ObservationCombatProfile profile, OperationStore? operations = null) : ICombatObservationSource
{
    public string ProfileId => profile.Id;
    public CombatCapabilities Capabilities(string? gameSessionId) => Capabilities(controls.Bindings.Recent?.Session, gameSessionId);
    /// <summary>按给定绑定判定能力门槛；诊断传入最近一次已刷新的绑定，不重新查询窗口。</summary>
    public CombatCapabilities Capabilities(GameSession? binding, string? gameSessionId)
    {
        var compatible = binding is not null && (gameSessionId is null || gameSessionId == binding.Id) &&
            (profile.Simulation && controls.Simulation || profile.Source.GameBuild == binding.GameBuild);
        var installed = observations.Runtime().Installed;
        return new(profile.Simulation, new[] { ("combat.damage.local", profile.Damage), ("combat.effects.self", profile.SelfEffects),
            ("combat.effects.monster", profile.MonsterEffects), ("combat.healing.local", profile.Damage && profile.Healing) }.Select(value => new CombatCapability(value.Item1,
                value.Item2 && compatible && installed ? CapabilityStatus.Available : CapabilityStatus.Unavailable, profile.Source,
                !value.Item2 || !compatible ? "combat.buildUnsupported" : !installed ? "observation.workerUnavailable" : null, null, null)).ToArray());
    }
    public ValueTask<ICombatObservationSubscription> OpenAsync(string observationId, CombatObservationRequest request, CancellationToken ct) =>
        ValueTask.FromException<ICombatObservationSubscription>(new OperationException("permission.denied", "共享捕获订阅需要授权主体"));
    public ValueTask<ICombatObservationSubscription> OpenAsync(string principal, string observationId, CombatObservationRequest request, CancellationToken ct) =>
        OpenAsync(principal, observationId, request, ct, resident: false);
    internal ValueTask<ICombatObservationSubscription> OpenAsync(string principal, string observationId, CombatObservationRequest request,
        CancellationToken ct, bool resident) =>
        observations.OpenCombatAsync(principal, observationId, request, profile, ct,
            onIdentityPending: operations is null ? null : () => operations.CombatIdentityPendingAsync(observationId), residentDemand: resident);
}

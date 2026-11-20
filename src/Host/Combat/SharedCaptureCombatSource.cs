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
    /// <summary>
    /// 按给定绑定判定能力门槛；诊断传入最近一次已刷新的绑定，不重新查询窗口。未登记构建经运行时核对启用战斗时照常可用，
    /// 原因写 gameBuild.runtimeVerified；核对中为 gameBuild.verifying，核对不过写具体停用原因。
    /// </summary>
    public CombatCapabilities Capabilities(GameSession? binding, string? gameSessionId)
    {
        var scoped = binding is not null && (gameSessionId is null || gameSessionId == binding.Id);
        var compatible = scoped && (profile.Simulation && controls.Simulation || Accepts(binding, profile.Source));
        var verdict = profile.Simulation || !scoped ? null : observations.Builds.Verdict(binding);
        var buildReason = compatible
            ? verdict is { Registered: false } ? "gameBuild.runtimeVerified" : null
            : verdict is { Registered: false, Combat.Enabled: false } ? verdict.Combat.Reason ?? "combat.buildUnsupported"
            : scoped && verdict is null && observations.Builds.Verifying(binding) ? "gameBuild.verifying" : "combat.buildUnsupported";
        var installed = observations.Runtime().Installed;
        var capabilities = new[] { ("combat.damage.local", profile.Damage), ("combat.effects.self", profile.SelfEffects),
            ("combat.effects.monster", profile.MonsterEffects), ("combat.healing.local", profile.Damage && profile.Healing) }.Select(value => new CombatCapability(value.Item1,
                value.Item2 && compatible && installed ? CapabilityStatus.Available : CapabilityStatus.Unavailable, profile.Source,
                !value.Item2 ? "combat.buildUnsupported" : !compatible ? buildReason : !installed ? "observation.workerUnavailable" : buildReason, null, null)).ToList();
        // 怪物血量与当前目标（v4 配置）：未登记构建的血量核对不过时单独停用，其他能力不受影响。
        if (profile.Id == MabiTool.Providers.TwObservation.Combat.Tw20261110CombatProtocol.Profile || profile.Simulation)
        {
            var health = verdict is { Registered: false, Health.Enabled: false } ? verdict.Health.Reason ?? "combat.buildUnsupported" : null;
            capabilities.Add(new("combat.health.monster", profile.Damage && compatible && installed && health is null ? CapabilityStatus.Available : CapabilityStatus.Unavailable,
                profile.Source, !profile.Damage ? "combat.buildUnsupported" : !compatible ? buildReason : !installed ? "observation.workerUnavailable" : health ?? buildReason, null, null));
        }
        return new(profile.Simulation, capabilities);
    }
    /// <summary>战斗来源可用于该绑定（见 <see cref="GameObservationService.CombatAccepts"/>）。</summary>
    internal bool Accepts(GameSession? binding, ObservationSource source) => observations.CombatAccepts(binding, source);
    public ValueTask<ICombatObservationSubscription> OpenAsync(string observationId, CombatObservationRequest request, CancellationToken ct) =>
        ValueTask.FromException<ICombatObservationSubscription>(new OperationException("permission.denied", "共享捕获订阅需要授权主体"));
    public ValueTask<ICombatObservationSubscription> OpenAsync(string principal, string observationId, CombatObservationRequest request, CancellationToken ct) =>
        OpenAsync(principal, observationId, request, ct, resident: false);
    internal ValueTask<ICombatObservationSubscription> OpenAsync(string principal, string observationId, CombatObservationRequest request,
        CancellationToken ct, bool resident) =>
        observations.OpenCombatAsync(principal, observationId, request, profile, ct,
            onIdentityPending: operations is null ? null : () => operations.CombatIdentityPendingAsync(observationId), residentDemand: resident);
}

using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.CombatObservation.Application;

namespace MabiTool.Host.Combat;

internal sealed class UnavailableCombatSource : ICombatObservationSource
{
    public CombatCapabilities Capabilities(string? gameSessionId) => new(false,
        new[] { "combat.damage.local", "combat.effects.self", "combat.effects.monster", "combat.healing.local" }.Select(id =>
            new CombatCapability(id, CapabilityStatus.Unavailable, null, "combat.sourceUnavailable", null, null)).ToArray());
    public ValueTask<ICombatObservationSubscription> OpenAsync(string observationId, CombatObservationRequest request, CancellationToken ct) =>
        ValueTask.FromException<ICombatObservationSubscription>(new OperationException("combat.sourceUnavailable", "当前台服捕获与字段适配尚未通过验收"));
}

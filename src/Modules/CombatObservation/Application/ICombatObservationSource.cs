using MabiTool.Contracts;

namespace MabiTool.Modules.CombatObservation.Application;

public interface ICombatObservationSource
{
    CombatCapabilities Capabilities(string? gameSessionId);
    ValueTask<ICombatObservationSubscription> OpenAsync(string observationId, CombatObservationRequest request, CancellationToken ct);
    ValueTask<ICombatObservationSubscription> OpenAsync(string principal, string observationId, CombatObservationRequest request, CancellationToken ct) =>
        OpenAsync(observationId, request, ct);
}
public interface ICombatObservationSubscription : IAsyncDisposable
{
    ObservationSource Source { get; }
    CombatEntityObservation LocalEntity { get; }
    double ClockRate { get; }
    /// <summary>来源是否解析本机治疗；不支持时分段的治疗块为空，不显示为零。</summary>
    bool HealingSupported => false;
    IAsyncEnumerable<CombatFrame> ReadAsync(CancellationToken ct);
    async ValueTask<IReadOnlyList<CombatGapInput>> CloseAsync()
    { await DisposeAsync(); return []; }
}

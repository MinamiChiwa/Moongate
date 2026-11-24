using System.Globalization;
using System.Numerics;
using MabiTool.Contracts;
using MabiTool.Modules.CombatObservation.Application;
using MabiTool.Modules.CombatObservation.Domain;

namespace MabiTool.ObservationDebugTool;

/// <summary>共享观察快照的脱敏投影：只保留状态、计数与身份布尔值，不保存身份正文。</summary>
public sealed class SharedObservationDiagnostics
{
    private readonly object gate = new();
    private bool frozen;
    private ulong? maximumPackets;
    public long SamplesObserved { get; private set; }
    public SharedObservationSample? BeforeCleanup { get; private set; }
    public SharedObservationSample? LastWithLatest { get; private set; }
    public string? MaximumPacketsObserved => maximumPackets?.ToString(CultureInfo.InvariantCulture);
    public bool MusicIdentityEverObserved { get; private set; }
    public bool MusicEverObserved { get; private set; }
    public bool ConcertEverObserved { get; private set; }
    public void Capture(GameObservationSnapshot snapshot, double elapsedMs, bool freeze = false)
    {
        var value = snapshot.Latest?.Value;
        var projection = new SharedObservationSample(elapsedMs,
            snapshot.State is "stopped" or "starting" or "running" or "stopping" or "faulted" ? snapshot.State : "unknown",
            SafeReason(snapshot.Reason), snapshot.SubscriberCount, value is not null,
            value?.Packets.ToString(), snapshot.Latest?.Quality.KnownValue?.ToString(),
            value?.Identity is not null, value?.Music is not null, value?.Concert is not null);
        lock (gate)
        {
            if (frozen) return;
            SamplesObserved++;
            // 故障会清空共享快照；保留最后一次包计数和身份布尔值，不保存身份正文。
            if (value is not null)
            {
                LastWithLatest = projection;
                maximumPackets = Math.Max(maximumPackets ?? 0, value.Packets.Value);
                MusicIdentityEverObserved |= value.Identity is not null;
                MusicEverObserved |= value.Music is not null;
                ConcertEverObserved |= value.Concert is not null;
            }
            if (freeze) { BeforeCleanup = projection; frozen = true; }
        }
    }
    public static string? SafeReason(string? code) => code is null ? null :
        ApiErrors.Definitions.Any(value => value.Code == code) || code is
            "worker.startFailed" or "worker.peerInvalid" or "worker.handshakeInvalid" or "worker.protocolIncompatible" or
            "worker.disconnected" or "worker.writeFailed" or "worker.cancelTimeout" or "worker.cancelInterrupted" or
            "worker.commandTimeout" or "worker.commandInvalid" or "worker.outputRateExceeded" or "worker.outputBackpressure" or
            "worker.sequenceInvalid" or "worker.frameInvalid" or "worker.frameTooLarge" or "worker.heartbeatTimeout" or
            "worker.requestIdentityInvalid" or "worker.phaseInvalid" or "worker.portInvalid" or "worker.requestInactive" or "worker.stopped"
        ? code : "probe.unrecognizedObservationReason";
}

public sealed record SharedObservationSample(double TotalElapsedMs, string State, string? Reason, int SubscriberCount,
    bool HasLatest, string? Packets, string? Quality, bool MusicIdentityObserved, bool MusicObserved, bool ConcertObserved);

/// <summary>战斗验收计数；字段与 2026-11-11 前的 CombatNativeProbe 报告一致。</summary>
public sealed class CombatAggregate
{
    public long Frames, DamageEvents, LocalDamageEvents, SelfEffects, MonsterEffects;
    public bool LocalIdentityObserved;
    public BigInteger LocalDamage;
    public BusinessSummary? BusinessSummary;
    private readonly Dictionary<string, CombatEncounter> segments = new(StringComparer.Ordinal);
    /// <summary>检查点包含新关闭及当前分段；按 ID 保留最后快照，至多 256 段。</summary>
    public void Segments(CombatCheckpoint checkpoint)
    {
        foreach (var value in checkpoint.Encounters)
            if (segments.ContainsKey(value.Id) || segments.Count < 256) segments[value.Id] = value;
    }
    public IReadOnlyList<CombatEncounter> SegmentSummaries() =>
        segments.Values.OrderBy(value => value.Segment?.Ordinal ?? int.MaxValue).ThenBy(value => value.StartedMonotonicMs).ToArray();
    public long MonsterGaugeUpdates, MonsterGaugesWithCurrent, MonsterGaugesWithMaximum, GaugeProjectionMismatches, GaugeSamplesTruncated;
    public List<BreakGaugeSample> MonsterGauges { get; } = [];
    private readonly Dictionary<EntityKey, CombatBreakGauge> lastGauges = [];
    public Dictionary<string, long> Gaps { get; } = new(StringComparer.Ordinal);
    public string DroppedEvents => dropped.ToString(CultureInfo.InvariantCulture);
    private BigInteger dropped;
    public void AddGap(CombatGapInput gap)
    { Gaps[gap.Reason] = Gaps.GetValueOrDefault(gap.Reason) + 1; dropped += gap.DroppedEvents.Value; }
    public void Accept(CombatFrame frame, CombatWorkspace workspace)
    {
        Frames++; DamageEvents += frame.Damage.Count;
        var local = frame.Entities.FirstOrDefault(entity => entity.Role.KnownValue == CombatEntityRole.LocalPlayer)?.Key;
        LocalIdentityObserved |= local is not null;
        var monsters = frame.Entities.Where(entity => entity.Role.KnownValue == CombatEntityRole.Monster).Select(entity => entity.Key).ToHashSet();
        foreach (var damage in frame.Damage.Where(value => value.SourceEntity == local))
        { LocalDamageEvents++; LocalDamage += damage.Amount.Value; }
        SelfEffects += frame.Effects.Count(effect => effect.EntityKey == local);
        MonsterEffects += frame.Effects.Count(effect => monsters.Contains(effect.EntityKey));
        foreach (var entity in frame.Entities.Where(entity => entity.Role.KnownValue == CombatEntityRole.Monster && entity.BreakGauge is not null))
        {
            var gauge = entity.BreakGauge!;
            if (lastGauges.TryGetValue(entity.Key, out var previous) && previous == gauge) continue;
            if (!lastGauges.ContainsKey(entity.Key) && lastGauges.Count >= 512) { GaugeSamplesTruncated++; continue; }
            lastGauges[entity.Key] = gauge;
            MonsterGaugeUpdates++;
            if (!gauge.Stale && gauge.Current is not null) MonsterGaugesWithCurrent++;
            if (!gauge.Stale && gauge.Maximum is not null) MonsterGaugesWithMaximum++;
            var projected = workspace.Entities.FirstOrDefault(value => value.Key == entity.Key);
            var matches = projected?.BreakGauge == gauge;
            if (!matches) GaugeProjectionMismatches++;
            if (MonsterGauges.Count >= 128) { GaugeSamplesTruncated++; continue; }
            MonsterGauges.Add(new(projected?.Id, gauge.Current, gauge.Maximum?.ToString(), gauge.ObservedAt,
                gauge.Quality.KnownValue?.ToString() ?? "unknown", gauge.Stale, gauge.Reason, matches));
        }
        foreach (var gap in frame.Gaps) AddGap(gap);
    }

    /// <summary>验收口径：本机身份、本机伤害、自身与怪物效果齐备，且至少一段给出 DPS。</summary>
    public IReadOnlyList<string> UnmetCapabilities()
    {
        List<string> unmet = [];
        if (!LocalIdentityObserved) unmet.Add("localIdentity");
        if (LocalDamageEvents <= 0) unmet.Add("localDamage");
        if (SelfEffects <= 0) unmet.Add("selfEffects");
        if (MonsterEffects <= 0) unmet.Add("monsterEffects");
        if (!SegmentSummaries().Any(value => value.Summary.EncounterDps is not null)) unmet.Add("segmentDps");
        return unmet;
    }
}

public sealed record BreakGaugeSample(string? EntityId, double? Current, string? Maximum, DateTimeOffset ObservedAt,
    string Quality, bool Stale, string? Reason, bool ProjectionMatches);

public sealed record BusinessSummary(string AttributedDamage, double DurationMs, double? EncounterDps, double? RollingDps,
    string CalculationVersion, string Quality, IReadOnlyList<string> Reasons);

/// <summary>身份等待超时而共享捕获仍运行时，在原总预算内重新等待并记录尝试次数。</summary>
public sealed class CombatIdentityPreparation
{
    public int Attempts { get; private set; }
    public int TimedOutAttempts { get; private set; }

    public async ValueTask<ICombatObservationSubscription> OpenAsync(
        Func<Action, ValueTask<ICombatObservationSubscription>> open,
        Action onReady, Func<bool> captureRunning, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Attempts++;
            try { return await open(onReady); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && captureRunning())
            {
                TimedOutAttempts++;
                await Task.Delay(50, token);
            }
        }
    }
}

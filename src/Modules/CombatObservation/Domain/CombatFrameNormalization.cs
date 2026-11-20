using MabiTool.Contracts;

namespace MabiTool.Modules.CombatObservation.Domain;

internal static class CombatFrameNormalization
{
    public static bool TryNormalize(CombatFrame? input, out CombatFrame frame)
    {
        frame = input!;
        if (!IsWellFormed(input)) return false;
        frame = Normalize(input!);
        return true;
    }
    /// <summary>结构检查：缺少必需列表、记录为空或时点非有限数时为假。</summary>
    public static bool IsWellFormed(CombatFrame? input) =>
        !(input is null || input.Source is null || input.GameSessionId is null || !double.IsFinite(input.MonotonicMs) ||
            input.Entities is null || input.Damage is null || input.Effects is null || input.Reconciliations is null || input.Gaps is null ||
            input.Entities.Any(x => x is null || x.Key is null || x.RawName is null || x.DisplayName is null || x.RawName.Length > 256 || x.DisplayName.Length > 256) ||
            input.Damage.Any(x => x is null || x.Source is null || x.SourceEntity is null || x.TargetEntity is null || x.EventId is null || x.EventId.Length > 128 || !double.IsFinite(x.MonotonicMs)) ||
            input.Effects.Any(x => !ValidEffect(x)) ||
            input.Reconciliations.Any(x => x is null || x.EntityKey is null || x.Effects is null || x.Effects.Any(value => !ValidEffect(value))) ||
            input.Gaps.Any(x => x is null || x.Reason is null || !double.IsFinite(x.FromMs) || x.ToMs is { } end && !double.IsFinite(end)) ||
            input.Healing is { } healing && (healing.Count > 256 || healing.Any(x => x is null || x.EventId is null || x.EventId.Length > 128 ||
                !double.IsFinite(x.MonotonicMs) || x.EffectiveReason?.Length > 128)) ||
            input.CombatStates is { } states && (states.Count > 256 || states.Any(x => x is null || x.State.RawValue is null ||
                !double.IsFinite(x.MonotonicMs))) ||
            input.MainTarget is { } main && !double.IsFinite(main.MonotonicMs));
    /// <summary>
    /// 规范化并复制全部列表（记录本身不可变，未变化的记录原样沿用）；结果不与输入共享可变集合，对已规范化的帧再次执行结果相同。
    /// </summary>
    public static CombatFrame Normalize(CombatFrame input) =>
        input with
        {
            Entities = input.Entities.Select(value => Gauge(value.BreakGauge, input.ObservedAt) is var gauge && ReferenceEquals(gauge, value.BreakGauge)
                ? value : value with { BreakGauge = gauge }).ToArray(),
            Damage = input.Damage.Select(Damage).ToArray(),
            Effects = input.Effects.Select(Duration).ToArray(),
            Reconciliations = input.Reconciliations.Select(x => x with { Effects = x.Effects.Select(Duration).ToArray() }).ToArray(),
            Gaps = input.Gaps.ToArray(),
            Healing = input.Healing?.ToArray(),
            CombatStates = input.CombatStates?.ToArray()
        };
    private static CombatDamageEvent Damage(CombatDamageEvent value)
    {
        if (value.Classification is not null && value.Elements is null) return value;
        // 元素列表超过已登记的八种或含空项时按未知处理，不截断猜测。
        var elements = value.Elements is { } list && (list.Count > 8 || list.Any(element => element.RawValue is null)) ? null : value.Elements?.ToArray();
        return value with { Classification = value.Classification ?? (OpenEnum<CombatDamageClassification>)CombatDamageClassification.Unknown, Elements = elements };
    }
    private static bool ValidReason(string? value) => value is null || value.Length <= 128 && !value.Any(char.IsControl);
    private static CombatBreakGauge? Gauge(CombatBreakGauge? value, DateTimeOffset observedAt)
    {
        if (value is null) return null;
        if (value.Current is { } current && (!double.IsFinite(current) || current < 0) ||
            value.ObservedAt > observedAt || !ValidReason(value.Reason))
            return value with { Current = null, Maximum = null, ObservedAt = observedAt,
                Quality = ObservationQuality.Unknown, Stale = true, Reason = "break-gauge-invalid" };
        return value;
    }
    private static bool ValidEffect(CombatEffectObservation? value) => value is not null && value.EntityKey is not null &&
        double.IsFinite(value.MonotonicMs) && value.Category?.Length is not > 128;
    private static CombatEffectObservation Duration(CombatEffectObservation value)
    {
        if (value.DurationKind.KnownValue != EffectDurationKind.Finite) return value.DurationMs is null ? value : value with { DurationMs = null };
        if (value.DurationMs is not { } ms || !double.IsFinite(ms) || ms <= 0 || ms > 315576000000 ||
            ms > (DateTimeOffset.MaxValue - value.ObservedAt).TotalMilliseconds)
            return value with { DurationKind = EffectDurationKind.Unknown, DurationMs = null };
        return value;
    }
}

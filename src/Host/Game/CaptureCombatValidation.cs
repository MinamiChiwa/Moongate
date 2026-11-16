using System.Text.Json;
using System.Text.Json.Serialization;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Workers;

namespace MabiTool.Host.Game;

internal static class CaptureCombatValidation
{
    private const string BranchKey = "mabitool.frameInvalidBranch";
    internal static readonly JsonSerializerOptions Json = new(ContractJson.Options)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 24 };
    private static bool Text(string? value, int maximum, bool empty = false) => value is not null &&
        value.Length <= maximum && (empty || !string.IsNullOrWhiteSpace(value)) && !value.Any(char.IsControl);
    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    internal static void Validate(CombatCaptureReport report, ObservationSource source, string game)
    {
        var frame = report.Frame;
        bool Key(EntityKey? value) => value is not null && value.GameSessionId == game &&
            value.ObservationGeneration == frame.Generation && value.RawInstanceId.Value != 0;
        bool Entity(CombatEntityObservation? value) => value is not null && Key(value.Key) &&
            Text(value.RawName, 256, true) && Text(value.DisplayName, 256, true) && Text(value.Role.RawValue, 128) &&
            Text(value.Quality.RawValue, 128) && (value.OwnerKey is null || Key(value.OwnerKey)) && (!value.OwnerConfirmed || value.OwnerKey is not null);
        bool Time(double value, DateTimeOffset at) => double.IsFinite(value) && value >= 0 && value <= frame.MonotonicMs &&
            value >= frame.MonotonicMs - 250 && Utc(at) && at <= frame.ObservedAt;
        bool EffectFields(CombatEffectObservation? value) => value is not null && Key(value.EntityKey) &&
            (value.CasterKey is null || Key(value.CasterKey)) && value.InstanceId.Value != 0 &&
            Text(value.Kind.RawValue, 128) && Text(value.DurationKind.RawValue, 128) && Text(value.Quality.RawValue, 128) &&
            value.Stacks is null or >= 0 && (value.Category is null || Text(value.Category, 128, true));
        bool Effect(CombatEffectObservation? value) => EffectFields(value) && Time(value!.MonotonicMs, value.ObservedAt);
        if (report.SchemaVersion != 1 || !WorkerProtocol.IsId(report.InterestId) || frame is null || frame.SchemaVersion != 1 ||
            frame.Source != source || source.Region != "TW" || !Text(source.Provider, 128) || !Text(source.ProviderVersion, 64) ||
            source.GameBuild is not null && !Text(source.GameBuild, 256) || frame.GameSessionId != game ||
            frame.Generation.Value == 0 || frame.Sequence.Value == 0 || !Utc(frame.ObservedAt) ||
            !double.IsFinite(frame.MonotonicMs) || frame.MonotonicMs is < 0 or > 1e12 ||
            frame.Entities is null || frame.Damage is null || frame.Effects is null || frame.Reconciliations is null || frame.Gaps is null)
            throw Invalid("frame.header");
        var healing = frame.Healing ?? [];
        var states = frame.CombatStates ?? [];
        if (frame.Entities.Count > 128 || frame.Damage.Count > 256 || frame.Effects.Count > 256 ||
            frame.Reconciliations.Count > 256 || frame.Gaps.Count > 256 || healing.Count > 256 || states.Count > 256) throw Invalid("frame.limits");
        if (frame.Entities.Any(value => !Entity(value)) ||
            frame.Entities.Select(value => value.Key).Distinct().Count() != frame.Entities.Count ||
            frame.Entities.Count(value => value.Role.KnownValue == CombatEntityRole.LocalPlayer) > 1) throw Invalid("entity");
        if (frame.Damage.Any(value => value is null || value.Source != source || !Key(value.SourceEntity) || !Key(value.TargetEntity) ||
                !Text(value.EventId, 128, true) || value.EventId.Length == 0 && value.Sequence is null || value.SubEventIndex is < 0 or > 10000 ||
                !Text(value.AmountKind.RawValue, 128) || !Text(value.Quality.RawValue, 128) ||
                value.SkillCasterKey is not null && !Key(value.SkillCasterKey) ||
                value.Chain == true && (value.SkillCasterKey is null || value.SkillCasterKey == value.SourceEntity) ||
                value.Chain == false && value.SkillCasterKey is not null && value.SkillCasterKey != value.SourceEntity ||
                value.SkillResolved == true && value.SkillCasterKey is null ||
                value.SkillResolved == false && (value.SkillId.Value != 0 || value.SkillCasterKey is not null || value.Chain is not null)))
            throw Invalid("damage.fields");
        if (frame.Damage.Any(value => !Time(value.MonotonicMs, value.ObservedAt))) throw Invalid("damage.time");
        if (healing.Any(value => value is null || !Text(value.EventId, 128) || !Text(value.Direction.RawValue, 128) ||
                !Text(value.Quality.RawValue, 128) || value.Amount.Value == 0 ||
                value.Direction.KnownValue == CombatHealDirection.Cast != (value.TargetEntity is not null) ||
                value.TargetEntity is not null && !Key(value.TargetEntity) ||
                value.ReceiptMerged && value.Direction.KnownValue != CombatHealDirection.Cast ||
                (value.EffectiveAmount is null) != (value.Overheal is null) ||
                value.EffectiveAmount is { } effective && (value.Overheal is not { } overheal || effective.Value == 0 ||
                    effective.Value > value.Amount.Value || overheal.Value != value.Amount.Value - effective.Value) ||
                value.EffectiveReason is not null && (!Text(value.EffectiveReason, 128) || value.EffectiveAmount is not null)))
            throw Invalid("heal.fields");
        if (healing.Any(value => !Time(value.MonotonicMs, value.ObservedAt))) throw Invalid("heal.time");
        if (states.Any(value => value is null || !Text(value.State.RawValue, 128) || !Time(value.MonotonicMs, value.ObservedAt)))
            throw Invalid("combatState");
        if (frame.Effects.Any(value => !EffectFields(value))) throw Invalid("effect.fields");
        if (frame.Effects.Any(value => !Time(value.MonotonicMs, value.ObservedAt))) throw Invalid("effect.time");
        if (frame.Reconciliations.Any(value => value is null || !Key(value.EntityKey) || value.Effects is null ||
                value.Effects.Count > 256 || value.Effects.Any(effect => !Effect(effect) || effect.EntityKey != value.EntityKey)))
            throw Invalid("reconciliation");
        if (frame.Gaps.Any(value => value is null || !Text(value.Reason, 128) || !double.IsFinite(value.FromMs) ||
                value.FromMs < 0 || value.FromMs > frame.MonotonicMs || value.ToMs is { } end &&
                (!double.IsFinite(end) || end < value.FromMs || end > frame.MonotonicMs)))
            throw Invalid("gap");
        var records = (long)frame.Entities.Count + frame.Damage.Count + frame.Effects.Count + frame.Gaps.Count + healing.Count + states.Count +
            frame.Reconciliations.Sum(value => (long)Math.Max(1, value.Effects.Count));
        if (records > 256) throw Invalid("frame.records");
        if (report.LocalEntity is { } local &&
            (!Entity(local) || local.Role.KnownValue != CombatEntityRole.LocalPlayer || !local.InScope ||
             local.Quality.KnownValue != ObservationQuality.Confirmed || !frame.Entities.Contains(local)) ||
            report.LocalEntity is null && frame.Entities.Any(value => value.Role.KnownValue == CombatEntityRole.LocalPlayer))
            throw Invalid("localEntity");
        try { _ = frame.Gaps.Aggregate((ulong)Math.Max(1, records), (sum, value) => checked(sum + value.DroppedEvents.Value)); }
        catch (OverflowException) { throw Invalid("gap.dropped"); }
    }
    /// <summary>错误码保持 worker.frameInvalid；具体校验分支只进入进程内诊断。</summary>
    internal static WorkerException Invalid(string branch)
    {
        var error = new WorkerException("worker.frameInvalid");
        error.Data[BranchKey] = branch;
        return error;
    }
    internal static string? Branch(WorkerException error) => error.Data[BranchKey] as string;
}

using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.CombatObservation.Domain;

namespace MabiTool.Modules.CombatObservation.Adapters;

public sealed partial class CombatHistoryStore
{
    private IReadOnlyList<CombatEntityEffects> EffectsFor(string id) => database.Read(connection =>
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT document FROM combat_entities WHERE observation_id=$id";
        command.Parameters.AddWithValue("$id", id); using var rows = command.ExecuteReader(); var result = new List<CombatEntityEffects>(); var bytes = 0;
        while (rows.Read())
        {
            var json = rows.GetString(0); bytes += Encoding.UTF8.GetByteCount(json);
            if (result.Count >= 2048 || bytes > 17 * 1024 * 1024) throw new OperationException("resource.capacityExceeded", "实体状态读取超过预算");
            result.Add(JsonSerializer.Deserialize<CombatEntityEffects>(json, ContractJson.Options)!);
        }
        return (IReadOnlyList<CombatEntityEffects>)result;
    });
    private CombatCheckpoint ClosedCheckpoint(CombatWorkspace value, string reason, bool partial)
    {
        var revision = new UInt64String(checked(value.Revision.Value + 1)); var at = clock.GetUtcNow();
        var boundaryRevision = new UInt64String(checked((value.BoundaryRevision ?? value.Revision).Value + 1));
        var observation = value.Observation with { State = partial ? "interrupted" : "stopped", Revision = revision,
            Quality = partial ? ObservationQuality.Partial : value.Observation.Quality,
            Reasons = partial ? value.Observation.Reasons.Append(reason).Distinct().ToArray() : value.Observation.Reasons };
        var current = value.Encounter;
        if (current is { Summary.Final: false })
        {
            var duration = Math.Max(0, value.Observation.MonotonicMs - current.StartedMonotonicMs);
            var gaps = partial ? CombatGapAccumulator.Compact(current.Gaps.Append(new(reason, duration, null, new(0)))) : current.Gaps;
            current = current with { Revision = revision, BoundaryRevision = boundaryRevision, EndedAt = at, EndedMonotonicMs = value.Observation.MonotonicMs, Gaps = gaps,
                Summary = current.Summary with { Final = true, DurationMs = duration, BoundaryReason = reason, Revision = revision,
                    // 中断或重启结束的分段仍按已提交的伤害与 DPS 时长给出速率，并标注不完整。
                    EncounterDps = CombatRate.Calculate(current.Summary.AttributedDamage.Value, current.Summary.DpsDurationMs ?? duration),
                    Quality = partial ? ObservationQuality.Partial : current.Summary.Quality,
                    Reasons = partial ? current.Summary.Reasons.Append(reason).Distinct().ToArray() : current.Summary.Reasons } };
        }
        CombatEntityEffects Stale(CombatEntityEffects effects) => effects with { Revision = revision,
            Views = effects.Views.Select(x => x with { Stale = true, Instance = x.Instance with { Quality = ObservationQuality.Stale } }).ToArray(),
            Snapshot = effects.Snapshot with { Complete = false, Quality = ObservationQuality.Stale, Revision = revision,
                Effects = effects.Snapshot.Effects.Select(x => x with { Quality = ObservationQuality.Stale }).ToArray() } };
        var effects = EffectsFor(value.Observation.Id).Select(Stale).ToArray();
        var self = value.SelfEffects is null ? null : effects.FirstOrDefault(x => x.Id == value.SelfEffects.Id) ?? Stale(value.SelfEffects);
        var entities = value.Entities.Select(entity => entity.BreakGauge is { } gauge
            ? entity with { BreakGauge = gauge with { Stale = true, Quality = ObservationQuality.Stale, Reason = reason } }
            : entity).ToArray();
        var workspace = value with { Observation = observation, Encounter = current, SelfEffects = self, Entities = entities,
            Revision = revision, BoundaryRevision = boundaryRevision };
        // 观察异常结束或宿主重启时，未结束的定时测量不保存。
        var measurements = database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM combat_encounters WHERE observation_id=$o AND closed=0 AND json_extract(document,'$.record.kind')='timed-measurement'";
            command.Parameters.AddWithValue("$o", value.Observation.Id);
            using var rows = command.ExecuteReader(); var ids = new List<string>();
            while (rows.Read()) ids.Add(rows.GetString(0));
            return ids;
        });
        return new(workspace, value.Encounter is { Summary.Final: false } && current is not null ? [current] : [], effects, [],
            partial ? [new(reason, value.Observation.MonotonicMs, null, new(0))] : [], new(0)) { DeletedEncounters = measurements };
    }
    public Task<bool> StopCommittedAsync(string id, string reason, bool partial, OperationState terminal, CancellationToken ct = default,
        IReadOnlyList<CombatGapInput>? completionGaps = null, IReadOnlyList<CombatEncounter>? extraEncounters = null,
        IReadOnlyList<CombatTimelineRecord>? extraTimeline = null)
    {
        var workspace = Workspace("owner", true, id);
        if (workspace is null) return Task.FromResult(false);
        var collected = new CombatGapAccumulator();
        if (completionGaps is not null) collected.AddRange(completionGaps);
        var gaps = collected.Snapshot();
        if (gaps.Any(value => string.IsNullOrWhiteSpace(value.Reason) || value.Reason.Length > 128 || !double.IsFinite(value.FromMs) ||
            value.ToMs is { } end && !double.IsFinite(end))) throw new OperationException("combat.observationGap", "观察收尾缺口无效");
        partial |= gaps.Count > 0;
        var checkpoint = ClosedCheckpoint(workspace, reason, partial);
        if (gaps.Count > 0)
        {
            var dropped = gaps.Aggregate(0UL, (sum, value) => checked(sum + value.DroppedEvents.Value));
            var observation = checkpoint.Workspace.Observation with
            {
                DroppedEvents = new(checked(workspace.Observation.DroppedEvents.Value + dropped)),
                Reasons = checkpoint.Workspace.Observation.Reasons.Concat(gaps.Select(value => value.Reason)).Distinct().Take(128).ToArray()
            };
            var encounter = checkpoint.Workspace.Encounter;
            if (encounter is not null && workspace.Encounter is { Summary.Final: false })
            {
                var final = gaps.Select(value => new CombatGap(value.Reason, Math.Max(0, value.FromMs - encounter.StartedMonotonicMs),
                    value.ToMs is { } to ? Math.Max(0, to - encounter.StartedMonotonicMs) : null, value.DroppedEvents));
                encounter = encounter with
                {
                    Gaps = CombatGapAccumulator.Compact(encounter.Gaps.Concat(final)),
                    Resources = encounter.Resources with { DroppedEvents = new(checked(encounter.Resources.DroppedEvents.Value + dropped)) },
                    Summary = encounter.Summary with { Reasons = encounter.Summary.Reasons.Concat(gaps.Select(value => value.Reason)).Distinct().Take(128).ToArray() }
                };
            }
            checkpoint = checkpoint with
            {
                Workspace = checkpoint.Workspace with { Observation = observation, Encounter = encounter },
                Encounters = workspace.Encounter is { Summary.Final: false } && encounter is not null ? [encounter] : [],
                Gaps = CombatGapAccumulator.Compact(checkpoint.Gaps.Concat(gaps.Select(value => new CombatGap(value.Reason, value.FromMs, value.ToMs, value.DroppedEvents))))
            };
        }
        // 观察停止时结束的定时测量随收尾检查点保存，不在删除之列。
        if (extraEncounters is { Count: > 0 } extra)
            checkpoint = checkpoint with { Encounters = [.. checkpoint.Encounters, .. extra], Timeline = extraTimeline ?? [],
                DeletedEncounters = checkpoint.DeletedEncounters.Where(x => extra.All(value => value.Id != x)).ToArray() };
        return SaveAsync(checkpoint, terminal, partial ? ApiErrors.Create("combat.observationGap", "观察中断，保留已提交统计", id) : null, ct);
    }
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        var workspaces = database.Read(connection =>
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT workspace FROM combat_observations WHERE terminal=0";
            using var rows = command.ExecuteReader(); var result = new List<CombatWorkspace>();
            while (rows.Read()) result.Add(JsonSerializer.Deserialize<CombatWorkspace>(rows.GetString(0), ContractJson.Options)!);
            return result;
        });
        foreach (var workspace in workspaces)
        {
            var checkpoint = ClosedCheckpoint(workspace, "backend-restarted", true);
            var json = JsonSerializer.Serialize(checkpoint.Workspace, ContractJson.Options);
            var encounters = checkpoint.Encounters.Select(value => (Value: value, Json: JsonSerializer.Serialize(value, ContractJson.Options))).ToArray();
            var effects = checkpoint.Effects.Select(value => (Value: value, Json: JsonSerializer.Serialize(value, ContractJson.Options))).ToArray();
            await database.WriteAsync(tx =>
            {
                tx.Execute("UPDATE combat_observations SET revision=$r,workspace=$json,terminal=1,updated_at=$at,encounter_id=$encounter WHERE id=$id AND terminal=0",
                    ("$r", checkpoint.Workspace.Revision.ToString()), ("$json", json), ("$at", clock.GetUtcNow().ToUnixTimeMilliseconds()),
                    ("$encounter", checkpoint.Workspace.Encounter?.Id), ("$id", workspace.Observation.Id));
                foreach (var deleted in checkpoint.DeletedEncounters)
                    tx.Execute("DELETE FROM combat_encounters WHERE id=$id AND closed=0", ("$id", deleted));
                foreach (var (value, document) in encounters)
                {
                    tx.Execute("UPDATE combat_encounters SET revision=$r,closed=1,quality='partial',document=$json,bytes=$bytes WHERE id=$id AND closed=0",
                        ("$r", value.Revision.ToString()), ("$json", document), ("$bytes", Encoding.UTF8.GetByteCount(document)), ("$id", value.Id));
                    Append(tx, "combat-summary", workspace.Observation.Id, value.Revision, "combat.summary.changed", value.Summary);
                    Append(tx, "combat-closed", value.Id, value.Revision, "combat.encounter.closed", new CombatEncounterClosed(value.Owner, value.ObservationId, value.Id,
                        value.Revision, value.Summary, value.Summary.BoundaryReason));
                }
                foreach (var (value, document) in effects)
                    tx.Execute("UPDATE combat_entities SET document=$json WHERE id=$id AND observation_id=$observation",
                        ("$json", document), ("$id", value.Id), ("$observation", value.ObservationId));
                Append(tx, "combat-quality", workspace.Observation.Id, checkpoint.Workspace.Revision, "combat.observation.degraded",
                    new CombatObservationDegraded(workspace.Observation.Owner, workspace.Observation.Id, workspace.Observation.GameSessionId,
                        workspace.Observation.LocalEntity.ObservationGeneration, checkpoint.Workspace.Revision, checkpoint.Gaps.Take(64).ToArray(), workspace.Observation.DroppedEvents, true));
                if (checkpoint.Workspace.SelfEffects is { } self)
                    Append(tx, "combat-effects", workspace.Observation.Id, checkpoint.Workspace.Revision, "combat.effects.changed",
                        self.Snapshot with { Complete = false, Effects = [], SnapshotRequired = true });
                AdvanceHistory(tx); Clean(tx); return true;
            }, ct);
        }
    }
}

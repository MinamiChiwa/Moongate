using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Modules.CombatObservation.Adapters;

public static class CombatStorageData
{
    public static StorageDataDefinition Definition { get; } = new("combat-observation", ["combat_observations", "combat_encounters", "combat_timeline_pages", "combat_entities", "combat_history_state", "combat_preferences", "combat_auto_observation"]);

    public static IStorageModuleDataOwner Owner(SqliteStore database) => new StorageModuleDataOwner(Definition, () =>
    {
        var previous = UInt64String.Parse((string)database.ReadScalar("SELECT revision FROM combat_history_state WHERE id=1")!);
        var next = new UInt64String(checked(previous.Value + 1));
        return new(tx =>
        {
            if ((string?)tx.Scalar("SELECT revision FROM combat_history_state WHERE id=1") != previous.ToString())
                throw new OperationException("storage.planStale", "战斗历史在删除前改变");
            tx.Execute("DELETE FROM combat_encounters"); tx.Execute("DELETE FROM combat_observations"); tx.Execute("DELETE FROM combat_receipts"); tx.Execute("DELETE FROM combat_preferences");
            tx.Execute("UPDATE combat_history_state SET revision=$revision WHERE id=1", ("$revision", next.ToString()));
        });
    }, ["combat.observe"], ["combat.summary.changed", "combat.effects.changed", "combat.encounter.closed", "combat.observation.degraded"]);
}

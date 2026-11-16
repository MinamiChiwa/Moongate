using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Modules.Rooms.Persistence;

public static class RoomStorageData
{
    public static StorageDataDefinition Definition { get; } = new("rooms", ["rooms_preferences"]);

    public static IStorageModuleDataOwner Owner(SqliteStore database) => new StorageModuleDataOwner(Definition, () =>
    {
        var store = new RoomPreferenceStore(database);
        var rowsJson = database.ReadScalar("SELECT json_group_array(json_object('owner',owner,'revision',revision)) FROM rooms_preferences") as string ?? "[]";
        using var rows = JsonDocument.Parse(rowsJson);
        var imports = rows.RootElement.EnumerateArray().Select(row => store.PrepareImport(row.GetProperty("owner").GetString()!,
            UInt64String.Parse(row.GetProperty("revision").GetString()!), RoomPreferences.Default)).ToArray();
        return new(tx =>
        {
            tx.Execute("DELETE FROM rooms_preference_receipts");
            foreach (var import in imports) store.CommitImport(tx, import);
        });
    }, ["room.settings.import", "room.round.prepare", "room.round.control", "room.scoreDraft.publish", "room.scoreDraft.map", "room.scoreDraft.clear"],
        ["room.preferences.changed", "room.import.committed", "room.session.changed", "room.score-draft.changed"]);
}

using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.Color.Domain;

namespace MabiTool.Modules.Color.Persistence;

public static class ColorStorageData
{
    public static StorageDataDefinition Definition { get; } = new("color", ["color_library_workspace", "color_import_preferences", "color_import_history"],
        [new("color-run-history", "SELECT id,json_extract(snapshot,'$.owner'),json_extract(snapshot,'$.timestamps'),json_extract(snapshot,'$.result') FROM core_operations WHERE terminal=1 AND json_extract(snapshot,'$.kind')='color.search' AND json_extract(snapshot,'$.result') IS NOT NULL")]);

    public static IStorageModuleDataOwner Owner(SqliteStore database) => new StorageModuleDataOwner(Definition, () =>
    {
        var store = new ColorLibraryStore(database); var previous = store.Read();
        var revision = new UInt64String(checked(previous.Revision.Value + 1));
        var next = new ColorLibrary(revision, ColorProfiles.Default with { Revision = revision }, []);
        var library = new ColorLibraryImport(previous.Revision, next, JsonSerializer.Serialize(next, ContractJson.Options), new(revision, 0, true));
        var preferences = new ColorPreferenceStore(database);
        var rowsJson = database.ReadScalar("SELECT json_group_array(json_object('owner',owner,'revision',revision)) FROM color_import_preferences") as string ?? "[]";
        using var rows = JsonDocument.Parse(rowsJson);
        var imports = rows.RootElement.EnumerateArray().Select(row => preferences.PrepareImport(row.GetProperty("owner").GetString()!,
            UInt64String.Parse(row.GetProperty("revision").GetString()!), ColorStudioPreferences.Default, [])).ToArray();
        return new(tx =>
        {
            tx.Execute("DELETE FROM color_library_receipts"); tx.Execute("DELETE FROM color_import_history");
            store.CommitImport(tx, library);
            foreach (var import in imports) preferences.CommitImport(tx, import);
        });
    }, ["color.search", "color.settings.import"], ["color.run.changed", "color.library.changed", "color.preferences.changed", "color.import.committed"]);
}

using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.MusicLibrary.Domain;

namespace MabiTool.Modules.MusicLibrary.Persistence;

public static class MusicStorageData
{
    public static StorageDataDefinition Definition { get; } = new("music-library",
        ["music_library_workspace", "music_library_catalogs", "music_playback_history", "music_import_preferences", "music_score_timeline_evidence", "performance_sessions"]);

    public static DatabaseMigration DeletionMigration { get; } = new("music-library", 6, """
        CREATE TABLE music_playback_history_state(owner TEXT PRIMARY KEY,revision INTEGER NOT NULL CHECK(revision>=0)) STRICT;
        """);

    public static IStorageModuleDataOwner Owner(SqliteStore database) => new StorageModuleDataOwner(Definition, () =>
    {
        var previous = new MusicLibraryStore(database, "storage").Read();
        var next = MusicLibraryState.Empty with { Revision = new(checked(previous.Revision.Value + 1)) };
        var json = JsonSerializer.Serialize(next, ContractJson.Options);
        var catalogsJson = database.ReadScalar("SELECT json_group_array(json(snapshot)) FROM music_library_catalogs") as string ?? "[]";
        var catalogs = JsonSerializer.Deserialize<ScoreCatalogSnapshot[]>(catalogsJson, ContractJson.Options)!;
        var cleared = catalogs.Select(value => (Previous: value, Next: value with { Revision = checked(value.Revision + 1),
            Status = ScoreRefreshStatus.Failed, LastCompleteAt = null, Entries = [], Groups = [] })).ToArray();
        var snapshots = cleared.Select(value => JsonSerializer.Serialize(value.Next, ContractJson.Options)).ToArray();
        return new(tx =>
        {
            if (UInt64String.Parse(tx.Scalar("SELECT revision FROM music_library_workspace WHERE id=1") as string ?? "0") != previous.Revision)
                throw new OperationException("storage.planStale", "曲库在删除前改变");
            tx.Execute("INSERT INTO music_library_workspace VALUES(1,$revision,$snapshot) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,snapshot=excluded.snapshot",
                ("$revision", next.Revision.ToString()), ("$snapshot", json));
            for (var i = 0; i < cleared.Length; i++)
            {
                var value = cleared[i];
                if ((string?)tx.Scalar("SELECT revision FROM music_library_catalogs WHERE scope_key=$key", ("$key", value.Previous.Scope.Key)) != value.Previous.Revision.ToString())
                    throw new OperationException("storage.planStale", "乐谱来源在删除前改变");
                tx.Execute("UPDATE music_library_catalogs SET revision=$revision,snapshot=$snapshot WHERE scope_key=$key",
                    ("$revision", value.Next.Revision.ToString()), ("$snapshot", snapshots[i]), ("$key", value.Next.Scope.Key));
            }
            tx.Execute("INSERT INTO music_playback_history_state SELECT owner,max(revision) FROM music_playback_history GROUP BY owner ON CONFLICT(owner) DO UPDATE SET revision=max(revision,excluded.revision)");
            foreach (var table in new[] { "music_library_receipts", "music_import_keys", "music_imports", "music_import_preferences", "music_score_timeline_evidence", "music_playback_history", "performance_sessions" })
                tx.Execute("DELETE FROM " + table);
            SqliteStore.AppendEvent(tx, new("music-library-" + next.Revision, "music-library", "music-library", next.Revision,
                "music.library.changed", JsonSerializer.SerializeToElement(new MusicEditResult(next.Revision, [], [], true), ContractJson.Options)));
        });
    }, ["music.import"], ["music.catalog.changed", "music.library.changed", "music.playback-history.changed", "music.import.committed", "performance.state.changed"]);
}

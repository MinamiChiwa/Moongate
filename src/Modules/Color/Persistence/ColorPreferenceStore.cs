using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Modules.Color.Persistence;

public sealed record ColorPreferenceImport(string Owner, UInt64String ExpectedRevision, ColorStudioPreferences Value,
    string Json, string HistoryJson, int HistoryCount, int HistoryBytes);

public sealed class ColorPreferenceStore(SqliteStore database)
{
    public static DatabaseMigration Migration { get; } = new("color-imports", 1, """
        CREATE TABLE color_import_preferences(owner TEXT PRIMARY KEY, revision TEXT NOT NULL, snapshot TEXT NOT NULL) STRICT;
        CREATE TABLE color_import_history(
            sequence INTEGER PRIMARY KEY AUTOINCREMENT, owner TEXT NOT NULL, id TEXT NOT NULL UNIQUE, snapshot TEXT NOT NULL
        ) STRICT;
        CREATE INDEX color_import_history_owner ON color_import_history(owner,sequence);
        """);
    public const int MaximumHistoryPerOwner = 500;
    public const int MaximumHistory = 2000;
    public const int MaximumHistoryBytes = 16 * 1024 * 1024;
    public ColorStudioPreferences Read(string owner) => database.ReadScalar("SELECT snapshot FROM color_import_preferences WHERE owner=$owner", ("$owner", owner)) is string json
        ? JsonSerializer.Deserialize<ColorStudioPreferences>(json, ContractJson.Options)! : ColorStudioPreferences.Default;
    public static UInt64String ImportRevision(DatabaseTransaction tx, string owner) => UInt64String.Parse(
        tx.Scalar("SELECT revision FROM color_import_preferences WHERE owner=$owner", ("$owner", owner)) as string ?? "0");

    public ColorPreferenceImport PrepareImport(string owner, UInt64String revision, ColorStudioPreferences preferences,
        IReadOnlyList<ImportedColorHistoryItem> history)
    {
        if (revision.Value == ulong.MaxValue) throw new OperationException("resource.capacityExceeded", "染色偏好修订已耗尽");
        var value = preferences with { Revision = new(revision.Value + 1) };
        var json = JsonSerializer.Serialize(value, ContractJson.Options);
        if (Encoding.UTF8.GetByteCount(json) > 2 * 1024 * 1024) throw new OperationException("resource.capacityExceeded", "染色偏好超过 2 MiB");
        var snapshots = history.Select(item => new { item.Id, Snapshot = JsonSerializer.Serialize(item, ContractJson.Options) }).ToArray();
        if (snapshots.Any(item => Encoding.UTF8.GetByteCount(item.Snapshot) > 32768)) throw new OperationException("resource.capacityExceeded", "单条旧历史超过 32 KiB");
        return new(owner, revision, value, json, JsonSerializer.Serialize(snapshots, ContractJson.Options),
            history.Count, snapshots.Sum(item => Encoding.UTF8.GetByteCount(item.Snapshot)));
    }

    public void CommitImport(DatabaseTransaction tx, ColorPreferenceImport import)
    {
        if (ImportRevision(tx, import.Owner) != import.ExpectedRevision) throw new OperationException("revision.stale", "染色偏好或旧历史已变化，请重新预览");
        if ((long)tx.Scalar("SELECT count(*) FROM color_import_history WHERE owner=$owner", ("$owner", import.Owner))! + import.HistoryCount > MaximumHistoryPerOwner ||
            (long)tx.Scalar("SELECT count(*) FROM color_import_history")! + import.HistoryCount > MaximumHistory ||
            (long)tx.Scalar("SELECT coalesce(sum(length(CAST(snapshot AS BLOB))),0) FROM color_import_history")! + import.HistoryBytes > MaximumHistoryBytes)
            throw new OperationException("resource.capacityExceeded", "导入旧历史超过数量或 16 MiB 存储预算");
        tx.Execute("INSERT INTO color_import_preferences VALUES($owner,$revision,$snapshot) ON CONFLICT(owner) DO UPDATE SET revision=excluded.revision,snapshot=excluded.snapshot",
            ("$owner", import.Owner), ("$revision", import.Value.Revision.ToString()), ("$snapshot", import.Json));
        tx.Execute("""
            INSERT INTO color_import_history(owner,id,snapshot)
            SELECT $owner,json_extract(value,'$.id'),json_extract(value,'$.snapshot') FROM json_each($rows)
            """, ("$owner", import.Owner), ("$rows", import.HistoryJson));
        SqliteStore.AppendEvent(tx, new("color-preferences-" + import.Owner + "-" + import.Value.Revision, "color-preferences", import.Owner, import.Value.Revision,
            "color.preferences.changed", JsonSerializer.SerializeToElement(new PreferenceRevisionChanged(import.Owner, import.Value.Revision), ContractJson.Options)));
    }

    public ApiList<ImportedColorHistoryItem> History(string owner, ImportedColorHistoryQuery query)
    {
        if (query.PageSize is < 1 or > 50 || query.BeforeId is not null && !ColorProfilesId(query.BeforeId))
            throw new OperationException("request.invalid", "旧历史每页一至 50 条，游标须为有效历史 ID");
        long? before = null;
        if (query.BeforeId is not null)
        {
            before = database.ReadScalar("SELECT sequence FROM color_import_history WHERE owner=$owner AND id=$id", ("$owner", owner), ("$id", query.BeforeId)) as long?;
            if (before is null) throw new OperationException("resource.notFound", "旧历史游标不存在");
        }
        var json = database.ReadScalar("""
            SELECT json_group_array(json(snapshot)) FROM (
                SELECT snapshot FROM color_import_history WHERE owner=$owner AND ($before IS NULL OR sequence<$before)
                ORDER BY sequence DESC LIMIT $limit
            )
            """, ("$owner", owner), ("$before", before), ("$limit", query.PageSize + 1)) as string ?? "[]";
        var items = JsonSerializer.Deserialize<ImportedColorHistoryItem[]>(json, ContractJson.Options)!;
        var page = items.Take(query.PageSize).ToArray();
        return new(page, items.Length > query.PageSize ? page[^1].Id : null, "color-import-history-" + Read(owner).Revision);
    }
    private static bool ColorProfilesId(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Modules.Rooms.Persistence;

public sealed record RoomPreferenceImport(string Owner, UInt64String ExpectedRevision, RoomPreferences Value, string Json);

public sealed partial class RoomPreferenceStore(SqliteStore database, TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    public static DatabaseMigration Migration { get; } = new("room-preferences", 1, """
        CREATE TABLE rooms_preferences(owner TEXT PRIMARY KEY, revision TEXT NOT NULL, snapshot TEXT NOT NULL) STRICT;
        """);
    /// <summary>
    /// 尾音保留的缺省值由 1 秒改为 0 秒：已保存的值恰为旧缺省 1000 ms 的按缺省处理改为 0，其他保存值不动；只执行一次。
    /// 修订号不变，前端按修订相同而内容更新照常读取。
    /// </summary>
    public static DatabaseMigration TailDefaultMigration { get; } = new("room-preferences", 3, """
        UPDATE rooms_preferences SET snapshot=json_set(snapshot,'$.musicTailHoldMs',0) WHERE json_extract(snapshot,'$.musicTailHoldMs')=1000;
        """);
    public RoomPreferences Read(string owner) => database.ReadScalar("SELECT snapshot FROM rooms_preferences WHERE owner=$owner", ("$owner", owner)) is string json
        ? JsonSerializer.Deserialize<RoomPreferences>(json, ContractJson.Options)! : RoomPreferences.Default;
    public static UInt64String ImportRevision(DatabaseTransaction tx, string owner) => UInt64String.Parse(
        tx.Scalar("SELECT revision FROM rooms_preferences WHERE owner=$owner", ("$owner", owner)) as string ?? "0");
    public RoomPreferenceImport PrepareImport(string owner, UInt64String expectedRevision, RoomPreferences value)
    {
        if (expectedRevision.Value == ulong.MaxValue) throw new OperationException("resource.capacityExceeded", "房间偏好修订已耗尽");
        var next = value with { Revision = new(expectedRevision.Value + 1), RequiresGameBinding = true, LocalStopActionId = "emergency-stop" };
        var json = JsonSerializer.Serialize(next, ContractJson.Options);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 65536) throw new OperationException("resource.capacityExceeded", "房间偏好超过 64 KiB");
        return new(owner, expectedRevision, next, json);
    }
    public void CommitImport(DatabaseTransaction tx, RoomPreferenceImport import)
    {
        if (ImportRevision(tx, import.Owner) != import.ExpectedRevision) throw new OperationException("revision.stale", "房间偏好已变化，请重新预览");
        tx.Execute("INSERT INTO rooms_preferences VALUES($owner,$revision,$snapshot) ON CONFLICT(owner) DO UPDATE SET revision=excluded.revision,snapshot=excluded.snapshot",
            ("$owner", import.Owner), ("$revision", import.Value.Revision.ToString()), ("$snapshot", import.Json));
        SqliteStore.AppendEvent(tx, new("room-preferences-" + import.Owner + "-" + import.Value.Revision, "rooms", import.Owner, import.Value.Revision,
            "room.preferences.changed", JsonSerializer.SerializeToElement(new PreferenceRevisionChanged(import.Owner, import.Value.Revision), ContractJson.Options)));
    }
}

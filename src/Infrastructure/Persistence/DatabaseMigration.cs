using System.Security.Cryptography;
using System.Text;

namespace MabiTool.Infrastructure;

/// <summary>模块迁移。校验和按规范化换行（统一为 LF）计算，源文件的 LF/CRLF 检出方式不改变迁移身份。</summary>
public sealed record DatabaseMigration(string ModuleId, int Version, string Sql)
{
    public string Checksum => Hash(NormalizedSql);

    /// <summary>判定已记录的校验和是否对应本迁移：规范化（即全 LF）或旧版按全 CRLF 源文本写入的记录都视为同一内容。</summary>
    public bool Matches(string? stored) =>
        stored is not null && (string.Equals(stored, Checksum, StringComparison.Ordinal) ||
            string.Equals(stored, Hash(NormalizedSql.Replace("\n", "\r\n", StringComparison.Ordinal)), StringComparison.Ordinal));

    private string NormalizedSql => Sql.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>平台基础表的增量迁移；core 迁移链由更新器协议固定，新增索引在此以独立模块编号交付。</summary>
public static class PlatformMigrations
{
    // outbox 回收按 event_id 级联删除收据；主键以 consumer_id 开头，不能服务该查找。
    public static DatabaseMigration OutboxReceiptIndex { get; } = new("platform-outbox", 1, """
        CREATE INDEX IF NOT EXISTS core_event_receipts_event ON core_event_receipts(event_id);
        """);

    // 每次追加事件都按创建时间回收已发布事件；无此索引时需逐行越过 payload 溢出页读取 created_at，耗时随保留事件线性增长。
    public static DatabaseMigration OutboxRetentionIndex { get; } = new("platform-outbox", 2, """
        CREATE INDEX IF NOT EXISTS core_outbox_published_created ON core_outbox(published, created_at);
        """);

    public static IReadOnlyList<DatabaseMigration> All { get; } = Array.AsReadOnly([OutboxReceiptIndex, OutboxRetentionIndex]);
}

internal static class CoreMigrations
{
    public static DatabaseMigration Initial { get; } = new("core", 1, """
        CREATE TABLE core_outbox (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            event_id TEXT NOT NULL UNIQUE,
            resource_type TEXT NOT NULL,
            resource_id TEXT NOT NULL,
            revision TEXT NOT NULL,
            event_type TEXT NOT NULL,
            payload TEXT NOT NULL,
            created_at TEXT NOT NULL,
            published INTEGER NOT NULL DEFAULT 0 CHECK (published IN (0,1))
        ) STRICT;
        CREATE INDEX core_outbox_pending ON core_outbox(published, sequence);
        CREATE TABLE core_event_receipts (
            consumer_id TEXT NOT NULL,
            event_id TEXT NOT NULL REFERENCES core_outbox(event_id) ON DELETE CASCADE,
            PRIMARY KEY(consumer_id, event_id)
        ) STRICT;
        CREATE TABLE core_state_projection (
            resource_type TEXT NOT NULL,
            resource_id TEXT NOT NULL,
            revision TEXT NOT NULL,
            event_id TEXT NOT NULL,
            payload TEXT NOT NULL,
            PRIMARY KEY(resource_type, resource_id)
        ) STRICT;
        CREATE TABLE core_operations (
            id TEXT PRIMARY KEY,
            snapshot TEXT NOT NULL,
            terminal INTEGER NOT NULL CHECK (terminal IN (0,1))
        ) STRICT;
        """);

    public static DatabaseMigration Operations { get; } = new("core", 2, """
        CREATE TABLE core_idempotency (
            principal TEXT NOT NULL, route TEXT NOT NULL, resource TEXT NOT NULL, key TEXT NOT NULL,
            request_hash TEXT NOT NULL, operation_id TEXT NOT NULL REFERENCES core_operations(id),
            expires_at INTEGER NOT NULL,
            PRIMARY KEY(principal,route,resource,key)
        ) STRICT;
        CREATE INDEX core_idempotency_expiry ON core_idempotency(expires_at);
        CREATE TABLE core_module_configuration (
            id TEXT PRIMARY KEY, enabled INTEGER NOT NULL CHECK(enabled IN (0,1)), revision TEXT NOT NULL,
            pending_operation TEXT REFERENCES core_operations(id)
        ) STRICT;
        """);

    public static DatabaseMigration BackendMode { get; } = new("core", 4, """
        CREATE TABLE IF NOT EXISTS core_backend_mode (singleton INTEGER PRIMARY KEY CHECK(singleton=1), mode TEXT NOT NULL CHECK(mode IN ('live','mock'))) STRICT;
        INSERT OR IGNORE INTO core_backend_mode VALUES(1,'live');
        """);

    public static DatabaseMigration ProjectionMetadata { get; } = new("core", 3, """
        ALTER TABLE core_state_projection ADD COLUMN event_type TEXT NOT NULL DEFAULT '';
        UPDATE core_state_projection SET event_type=coalesce(
            (SELECT event_type FROM core_outbox WHERE core_outbox.event_id=core_state_projection.event_id),
            CASE resource_type WHEN 'operation' THEN 'operation.changed'
                WHEN 'module-configuration' THEN 'module.configuration-changed' ELSE '' END);
        CREATE INDEX core_operations_active ON core_operations(terminal,id);
        """);
}

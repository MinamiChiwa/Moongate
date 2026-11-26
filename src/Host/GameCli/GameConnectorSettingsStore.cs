using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.GameCli;

/// <param name="RiskAcknowledgedAt">用户确认首次开启风险的时间；为空表示尚未确认。</param>
/// <param name="RememberedDirectory">运行中的绑定进程曾经核对到的台服目录。</param>
/// <param name="ManualDirectory">用户手动指定的台服目录。</param>
public sealed record GameConnectorSettings(DateTimeOffset? RiskAcknowledgedAt = null, string? RememberedDirectory = null, string? ManualDirectory = null);

/// <summary>连接器的持久设置（风险确认与记住的游戏目录）：单行快照。没有数据根时（无界面测试、模拟宿主）保存在内存。</summary>
public sealed class GameConnectorSettingsStore(SqliteStore? database)
{
    private readonly object gate = new();
    private GameConnectorSettings memory = new();

    public static DatabaseMigration Migration { get; } = new("game-connector-settings", 1,
        "CREATE TABLE game_connector_settings(id INTEGER PRIMARY KEY CHECK(id=1), snapshot TEXT NOT NULL) STRICT;");

    public GameConnectorSettings Read()
    {
        if (database is null) { lock (gate) return memory; }
        return database.ReadScalar("SELECT snapshot FROM game_connector_settings WHERE id=1") is string json
            ? JsonSerializer.Deserialize<GameConnectorSettings>(json, ContractJson.Options) ?? new() : new();
    }

    public Task<GameConnectorSettings> UpdateAsync(Func<GameConnectorSettings, GameConnectorSettings> change, CancellationToken ct = default)
    {
        if (database is null)
        {
            lock (gate) return Task.FromResult(memory = change(memory));
        }
        return database.WriteAsync(tx =>
        {
            var current = tx.Scalar("SELECT snapshot FROM game_connector_settings WHERE id=1") is string json
                ? JsonSerializer.Deserialize<GameConnectorSettings>(json, ContractJson.Options) ?? new() : new();
            var next = change(current);
            tx.Execute("INSERT INTO game_connector_settings VALUES(1,$snapshot) ON CONFLICT(id) DO UPDATE SET snapshot=excluded.snapshot",
                ("$snapshot", JsonSerializer.Serialize(next, ContractJson.Options)));
            return next;
        }, ct);
    }
}

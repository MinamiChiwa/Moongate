using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed partial class OperationStore
{
    /// <summary>最近受理的更新 Operation；给出 <paramref name="states"/> 时只取处于这些状态（如 <c>succeeded</c>、<c>failed</c>）的。</summary>
    public OperationSnapshot? LatestUpdate(string? kind = null, params string[] states) => store.Read(connection =>
    {
        using var query = connection.CreateCommand();
        var filter = states.Length == 0 ? "" :
            " AND json_extract(o.snapshot,'$.state') IN (" + string.Join(",", states.Select((_, index) => "$s" + index)) + ")";
        query.CommandText = "SELECT o.snapshot FROM platform_update_requests r JOIN core_operations o ON o.id=r.operation_id WHERE ($kind IS NULL OR json_extract(o.snapshot,'$.kind')=$kind)" +
            filter + " ORDER BY r.rowid DESC LIMIT 1";
        query.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        for (var index = 0; index < states.Length; index++) query.Parameters.AddWithValue("$s" + index, states[index]);
        return query.ExecuteScalar() is string json ? JsonSerializer.Deserialize<OperationSnapshot>(json, ContractJson.Options) : null;
    });
}

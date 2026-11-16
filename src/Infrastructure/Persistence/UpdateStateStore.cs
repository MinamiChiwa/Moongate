using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed partial class OperationStore
{
    public OperationSnapshot? LatestUpdate(string? kind = null) => store.Read(connection =>
    {
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT o.snapshot FROM platform_update_requests r JOIN core_operations o ON o.id=r.operation_id WHERE ($kind IS NULL OR json_extract(o.snapshot,'$.kind')=$kind) ORDER BY r.rowid DESC LIMIT 1";
        query.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        return query.ExecuteScalar() is string json ? JsonSerializer.Deserialize<OperationSnapshot>(json, ContractJson.Options) : null;
    });
}

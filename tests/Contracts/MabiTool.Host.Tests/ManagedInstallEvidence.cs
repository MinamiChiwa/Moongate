using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using Microsoft.Data.Sqlite;

namespace MabiTool.Host.Tests;

internal sealed record InstallFileEvidence(string Name, string Status, long? Length, string? Sha256, JsonNode? Fields);
internal sealed record InstallOperationEvidence(string Id, string Kind, string State, string Phase, string Revision,
    OperationTimestamps Timestamps, string CancellationState, string? CancellationReason, string? ProblemCode, JsonNode? Result);
internal sealed record InstallDatabaseEvidence(string Status, IReadOnlyList<InstallOperationEvidence> Operations);

internal static class ManagedInstallEvidence
{
    internal const int MaximumFileBytes = 4 * 1024 * 1024;
    internal const int MaximumCompletedFiles = 32;
    // Only these scalar fields and structural containers cross the evidence boundary.
    private static readonly HashSet<string> Scalars = new(StringComparer.Ordinal)
    {
        "schemaVersion", "releaseId", "releaseSequence", "manifestSha256", "treeSha256", "transactionId", "planId",
        "operationId", "revision", "phase", "outcome", "failureCode", "code", "healthChecks", "launchHost", "intentSha256", "generation",
        "processId", "startTimeUtcTicks", "executablePath", "version", "requiredUpdaterVersion", "updaterVersion",
        "stage", "elapsedMs", "peerCheck", "peerOpenError", "exceptionType", "hResult", "transactionCallback",
        "transactionElapsedMs", "handoffStage", "handoffMessage", "handoffElapsedMs"
    };
    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
    { "transaction", "previous", "candidate", "previousPointer", "plan", "parent", "predecessor", "launcher", "progress", "timings", "handoffTimings" };

    internal static async Task<IReadOnlyList<InstallFileEvidence>> ReadAsync(string installation)
    {
        var state = Path.Combine(installation, "update-state");
        var names = new List<string> { "launcher/active-version.json", "update-state/pending-install.json",
            "update-state/updater-bootstrap.json", "update-state/install-transaction.json",
            "update-state/install-handoffs.json", "update-state/install-failures.json" };
        var result = new List<InstallFileEvidence>();
        var completed = Path.Combine(state, "completed-installs");
        try
        {
            if (!Directory.Exists(completed)) result.Add(new("update-state/completed-installs", "missing", null, null, null));
            else
            {
                var files = Directory.EnumerateFiles(completed, "*.json").Take(MaximumCompletedFiles + 1).ToArray();
                result.Add(new("update-state/completed-installs", files.Length > MaximumCompletedFiles ? "tooManyFiles" : "present", files.Length, null, null));
                names.AddRange(files.Take(MaximumCompletedFiles).Select(path => Path.GetRelativePath(installation, path).Replace('\\', '/')));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { result.Add(new("update-state/completed-installs", "unreadable", null, null, null)); }
        foreach (var name in names) result.Add(await ReadFileAsync(installation, name));
        return result;
    }

    private static async Task<InstallFileEvidence> ReadFileAsync(string installation, string name)
    {
        long? length = null;
        string? hash = null;
        try
        {
            var path = Path.Combine(installation, name);
            if (!File.Exists(path)) return new(name, "missing", null, null, null);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            length = stream.Length;
            if (length > MaximumFileBytes) return new(name, "oversized", length, null, null);
            var bytes = new byte[(int)length];
            await stream.ReadExactlyAsync(bytes);
            if (stream.ReadByte() != -1) return new(name, "changed", length, null, null);
            hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                return new(name, "invalidJson", length, hash, null);
            return new(name, "present", length, hash, Project(document.RootElement, 0));
        }
        catch (JsonException) { return new(name, "invalidJson", length, hash, null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(name, "unreadable", length, hash, null); }
    }

    private static JsonNode? Project(JsonElement value, int depth)
    {
        if (depth > 12) return null;
        if (value.ValueKind == JsonValueKind.Array)
        {
            var array = new JsonArray();
            foreach (var item in value.EnumerateArray().Take(32)) array.Add(Project(item, depth + 1));
            return array;
        }
        if (value.ValueKind != JsonValueKind.Object) return null;
        var result = new JsonObject();
        foreach (var property in value.EnumerateObject().Take(256))
        {
            if (Scalars.Contains(property.Name) && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
            {
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString()!.Length > 1024) continue;
                result[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
            else if (Containers.Contains(property.Name)) result[property.Name] = Project(property.Value, depth + 1);
        }
        return result;
    }

    internal static async Task<bool> TrySaveAsync(string installation, string evidenceDirectory, string prefix,
        IReadOnlyList<ManagedInstallProcessHistory>? processes = null, IReadOnlyList<InstallOutputEvidence>? output = null,
        string? dataRoot = null)
    {
        try
        {
            if (Path.GetFileName(prefix) != prefix) return false;
            var files = await ReadAsync(installation);
            var database = dataRoot is null ? null : await ReadOperationsAsync(dataRoot);
            var diagnostics = dataRoot is null ? null : await ReadDiagnosticsAsync(dataRoot);
            Directory.CreateDirectory(evidenceDirectory);
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, prefix + ".install-state.json"),
                JsonSerializer.Serialize(new { files, database, diagnostics, processes, output }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        { return false; }
    }

    internal static async Task<InstallDatabaseEvidence> ReadOperationsAsync(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "store", "main.sqlite");
        if (!File.Exists(path)) return new("missing", []);
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT snapshot FROM core_operations WHERE json_extract(snapshot,'$.kind') LIKE 'update.%' ORDER BY rowid DESC LIMIT 8";
            await using var rows = await command.ExecuteReaderAsync();
            var operations = new List<InstallOperationEvidence>();
            while (await rows.ReadAsync())
            {
                var json = rows.GetString(0);
                if (json.Length > MaximumFileBytes) return new("oversized", operations);
                var snapshot = JsonSerializer.Deserialize<OperationSnapshot>(json, ContractJson.Options)!;
                var reason = snapshot.Cancellation.Reason;
                if (reason is not (null or "backend-stopping" or "update-cancelled" or "safe-update-completed")) reason = "custom";
                operations.Add(new(snapshot.Id, snapshot.Kind, snapshot.State.ToString(), snapshot.Phase, snapshot.Revision.ToString(),
                    snapshot.Timestamps, snapshot.Cancellation.State.ToString(), reason, snapshot.Problem?.Code,
                    snapshot.Result is { } result ? Project(result, 0) : null));
            }
            return new("present", operations);
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or JsonException)
        { return new("unreadable", []); }
    }

    private static async Task<IReadOnlyList<JsonNode>> ReadDiagnosticsAsync(string dataRoot)
    {
        var directory = Path.Combine(dataRoot, "logs", "backend");
        var records = new Queue<JsonNode>();
        if (!Directory.Exists(directory)) return [];
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "log-*.jsonl").Take(8))
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaximumFileBytes) continue;
                using var reader = new StreamReader(stream);
                for (var count = 0; count < 512 && await reader.ReadLineAsync() is { } line; count++)
                {
                    if (line.Length > 16_384) continue;
                    using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 24 });
                    if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                    var record = new JsonObject();
                    foreach (var name in new[] { "occurredAt", "level", "module", "code", "exceptionType" })
                        if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 256)
                            record[name] = value.GetString();
                    records.Enqueue(record);
                    if (records.Count > 32) records.Dequeue();
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return records.ToArray();
    }

    internal static string SafeOutput(string value)
    {
        var lines = value.Split('\n').Where(line => !System.Text.RegularExpressions.Regex.IsMatch(line,
            "token|secret|password|credential|pairing|authorization|private.?key|healthTarget|healthEvidence",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant));
        return string.Join('\n', lines).Substring(0, Math.Min(16_384, string.Join('\n', lines).Length));
    }
}

internal sealed record InstallOutputEvidence(int ProcessId, bool Complete, string? Stdout, string? Stderr);

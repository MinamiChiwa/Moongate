using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using Microsoft.Data.Sqlite;

namespace MabiTool.Infrastructure;

/// <summary>各数据所有者登记参与备份及替换预览的表；运行回执、队列和诊断不属于业务指纹。</summary>
public sealed record StorageDataProjection(string Name, string Query);
public sealed record StorageDataDefinition(string ModuleId, IReadOnlyList<string> Tables, IReadOnlyList<StorageDataProjection>? Projections = null);
public sealed record StorageDataEvidence(string Digest, IReadOnlyList<StorageDataCount> Counts);

public sealed class StorageDataCatalog(SqliteStore database, PathService paths, IReadOnlyList<StorageDataDefinition> definitions)
{
    public async Task<StorageDataEvidence> CaptureAsync(CancellationToken ct = default)
    {
        var state = database.Read(connection => CaptureDatabase(connection, definitions, ct));
        var settings = await SettingsAsync(paths.Settings, ct);
        return Combine(state, settings.Files, settings.Directories);
    }

    public async Task<StorageDataEvidence> CaptureBackupAsync(DataSnapshotDescriptor snapshot, CancellationToken ct = default)
    {
        await database.VerifyDataSnapshotAsync(snapshot.SnapshotId, snapshot, ct);
        var state = await database.ReadBackupDataAsync(snapshot.Database, connection => CaptureDatabase(connection, definitions, ct), ct);
        return Combine(state, snapshot.Files.Where(x => x.RelativePath.StartsWith("settings/", StringComparison.Ordinal)).ToArray(),
            snapshot.Directories.Where(x => x.StartsWith("settings/", StringComparison.Ordinal)).ToArray());
    }

    private static StorageDataEvidence Combine(StorageDataEvidence state, IReadOnlyList<DataSnapshotFile> files, IReadOnlyList<string> directories)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { state.Digest, Files = files.OrderBy(x => x.RelativePath, StringComparer.Ordinal), Directories = directories.Order(StringComparer.Ordinal) }, ContractJson.Options);
        return state with { Digest = Convert.ToHexStringLower(SHA256.HashData(bytes)), Counts = [.. state.Counts, new("settings", new((ulong)(files.Count + directories.Count)))] };
    }

    internal static StorageDataEvidence CaptureDatabase(SqliteConnection connection, IReadOnlyList<StorageDataDefinition> definitions, CancellationToken ct)
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var counts = new List<StorageDataCount>(); long totalBytes = 0, totalRows = 0;
        void Add(byte[] bytes)
        {
            totalBytes = checked(totalBytes + bytes.Length);
            if (totalBytes > 256L * 1024 * 1024) throw new StorageException("storage.quotaExceeded", "业务预览超过 256 MiB 读取预算");
            digest.AppendData(BitConverter.GetBytes(bytes.Length)); digest.AppendData(bytes);
        }
        foreach (var definition in definitions.OrderBy(x => x.ModuleId, StringComparer.Ordinal))
        {
            long count = 0; Add(Encoding.UTF8.GetBytes(definition.ModuleId));
            foreach (var table in definition.Tables.Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (!Regex.IsMatch(table, "^[a-z][a-z0-9_]{0,127}$")) throw new ArgumentException("业务表登记无效");
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name";
                command.Parameters.AddWithValue("$name", table);
                if ((long)command.ExecuteScalar()! == 0) continue;
                ReadRows(table, "SELECT * FROM \"" + table + "\"");
            }
            foreach (var projection in definition.Projections ?? []) ReadRows(projection.Name, projection.Query);
            counts.Add(new(definition.ModuleId, new((ulong)count)));

            void ReadRows(string name, string query)
            {
                ct.ThrowIfCancellationRequested(); Add(Encoding.UTF8.GetBytes(name));
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT * FROM (" + query + ") LIMIT 0";
                int columns; using (var schema = command.ExecuteReader()) columns = schema.FieldCount;
                command.CommandText = "SELECT * FROM (" + query + ") ORDER BY " + string.Join(',', Enumerable.Range(1, columns));
                using var rows = command.ExecuteReader();
                while (rows.Read())
                {
                    ct.ThrowIfCancellationRequested(); count++;
                    if (++totalRows > 2_000_000) throw new StorageException("storage.quotaExceeded", "业务预览超过记录预算");
                    for (var i = 0; i < columns; i++)
                        Add(JsonSerializer.SerializeToUtf8Bytes(rows.IsDBNull(i) ? null : rows.GetValue(i), ContractJson.Options));
                }
            }
        }
        transaction.Commit();
        return new(Convert.ToHexStringLower(digest.GetHashAndReset()), counts);
    }

    private static async Task<(IReadOnlyList<DataSnapshotFile> Files, IReadOnlyList<string> Directories)> SettingsAsync(string root, CancellationToken ct)
    {
        var files = new List<DataSnapshotFile>(); var directories = new List<string>(); var pending = new Stack<string>(); pending.Push(root); long bytes = 0;
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested(); StoragePaths.ValidateLocal(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested(); StoragePaths.ValidateLocal(path);
                if (files.Count + pending.Count >= SqliteStore.MaximumSnapshotEntries) throw new StorageException("storage.quotaExceeded", "配置预览条目超限");
                if (Directory.Exists(path)) { directories.Add("settings/" + Path.GetRelativePath(root, path).Replace('\\', '/')); pending.Push(path); continue; }
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                bytes = checked(bytes + input.Length);
                if (bytes > 256L * 1024 * 1024) throw new StorageException("storage.quotaExceeded", "配置预览内容超限");
                files.Add(new("settings/" + Path.GetRelativePath(root, path).Replace('\\', '/'), input.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct))));
            }
        }
        return (files.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray(), directories.Order(StringComparer.Ordinal).ToArray());
    }
}

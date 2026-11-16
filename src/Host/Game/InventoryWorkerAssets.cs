using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Game;

[SupportedOSPlatform("windows")]
internal static class InventoryWorkerAssets
{
    internal const int MaximumBytes = 64 * 1024 * 1024;
    internal static async Task<T> ReadAsync<T>(string root, string? name, UInt64String size, string? sha256, CancellationToken ct)
    {
        if (name is null || name.Length != 37 || !name.EndsWith(".json", StringComparison.Ordinal) || !WorkerProtocol.IsId(name[..32]) ||
            size.Value is 0 or > MaximumBytes || sha256 is null || sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
            throw new OperationException("inventory.protocolInvalid", "库存资产身份或预算无效");
        var path = Path.Combine(Path.GetFullPath(root), name);
        try
        {
            LocalDiscoverySecurity.RejectLinks(path);
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if ((ulong)file.Length != size.Value) throw new OperationException("inventory.protocolInvalid", "库存资产长度不匹配");
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
            if (!string.Equals(hash, sha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("inventory.protocolInvalid", "库存资产哈希不匹配");
            file.Position = 0;
            return await JsonSerializer.DeserializeAsync<T>(file, ContractJson.Options, ct)
                ?? throw new OperationException("inventory.protocolInvalid", "库存资产内容为空");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { throw new OperationException("inventory.protocolInvalid", "库存资产不可读或内容无效"); }
        finally
        {
            try { LocalDiscoverySecurity.RejectLinks(path); File.Delete(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
    internal static void CleanDirectory(string root, bool removeDirectory = false)
    {
        try
        {
            LocalDiscoverySecurity.RejectLinks(root);
            foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly).Take(8))
                if (Path.GetFileName(path) is { Length: 37 } name && WorkerProtocol.IsId(name[..32]))
                { LocalDiscoverySecurity.RejectLinks(path); File.Delete(path); }
            if (removeDirectory && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root, false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

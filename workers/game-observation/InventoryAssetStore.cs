using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Workers;
using MabiTool.ModuleSdk;

namespace MabiTool.Observation.Worker;

public sealed class InventoryAssetStore : IDisposable
{
    public const int InlineReportBytes = 512 * 1024;
    public const int MaximumBytes = 64 * 1024 * 1024;
    public const int MaximumFiles = 8;
    private readonly string root;
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly HashSet<string> owned = new(StringComparer.Ordinal);

    public InventoryAssetStore(string assetsDirectory)
    {
        if (!Path.IsPathFullyQualified(assetsDirectory) || !Directory.Exists(assetsDirectory))
            throw new WorkerException("worker.assetInvalid");
        LocalDiscoverySecurity.RejectLinks(assetsDirectory);
        root = Path.GetFullPath(assetsDirectory);
    }

    /// <summary>
    /// 编码库存报告（已有入队时的编码 <paramref name="encoded"/> 时直接使用）；超过内联上限的报告写入受管资产，返回待发送的事件信封。
    /// </summary>
    public async Task<object> PrepareInventoryAsync(InventoryCaptureReport report, byte[]? encoded, CancellationToken ct)
    {
        var bytes = encoded ?? JsonSerializer.SerializeToUtf8Bytes(report, ContractJson.Options);
        if (bytes.Length <= InlineReportBytes) return EncodedEvent.Inventory(bytes);
        var asset = await WriteAsync(bytes, ct).ConfigureAwait(false);
        return new { inventoryAsset = new InventoryCaptureArtifact(asset.FileName, asset.Bytes, asset.Sha256) };
    }

    public Task<InventoryCatalogArtifact> WriteCatalogAsync(InventoryCatalogData catalog, CancellationToken ct) =>
        WriteAsync(JsonSerializer.SerializeToUtf8Bytes(catalog, ContractJson.Options), ct);

    public async Task<InventoryCatalogArtifact> WriteAsync(byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length is 0 or > MaximumBytes) throw new WorkerException("worker.assetInvalid");
        ct.ThrowIfCancellationRequested();
        await writes.WaitAsync(ct).ConfigureAwait(false);
        string? path = null;
        try
        {
            LocalDiscoverySecurity.RejectLinks(root);
            long total = 0;
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (++count >= MaximumFiles) throw new WorkerException("worker.assetInvalid");
                LocalDiscoverySecurity.RejectLinks(file);
                total = checked(total + new FileInfo(file).Length);
                if (total + bytes.Length > MaximumBytes) throw new WorkerException("worker.assetInvalid");
            }
            var name = Guid.NewGuid().ToString("N") + ".json";
            path = Path.Combine(root, name);
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            lock (owned) { owned.RemoveWhere(value => !File.Exists(value)); owned.Add(path); }
            return new(name, new((ulong)bytes.Length), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch
        {
            if (path is not null) TryDelete(path);
            throw;
        }
        finally { writes.Release(); }
    }

    public void Dispose()
    {
        lock (owned)
            foreach (var path in owned) TryDelete(path);
        writes.Dispose();
    }
    private static void TryDelete(string path)
    {
        try { LocalDiscoverySecurity.RejectLinks(path); File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}

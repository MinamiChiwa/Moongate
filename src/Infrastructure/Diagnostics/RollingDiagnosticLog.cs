using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

/// <summary>只管理平台生成的段文件；受保护段占满预算时丢弃新日志并报告计数。</summary>
public sealed partial class RollingDiagnosticLog(PathService paths, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? current;
    private ulong dropped;
    // 段文件总字节的内存值；-1 表示尚未测量或写入失败后待重新测量。
    private long knownBytes = -1;
    public const long MaximumSegmentBytes = 5 * 1024 * 1024;
    public const int MaximumSegments = 128;
    public string? LastError { get; private set; }
    public ulong DroppedRecords => dropped;
    private string Root => paths.Resolve("logs", "backend");
    private string[] Segments()
    {
        Directory.CreateDirectory(Root);
        var files = Directory.EnumerateFiles(Root, "*.jsonl", SearchOption.TopDirectoryOnly).Take(MaximumSegments + 1).ToArray();
        if (files.Length > MaximumSegments) throw new StorageException("storage.assetInvalid", "日志段登记超出数量预算");
        return files.Where(x => Regex.IsMatch(Path.GetFileName(x), @"\Alog-[0-9a-f]{32}\.jsonl\z")).ToArray();
    }
    private long DiskBytes() => Segments().Sum(x => { StoragePaths.ValidateLocal(x); return new FileInfo(x).Length; });
    /// <summary>内存维护的段文件总字节；启动恢复、写入和回收后更新，尚未测量时为空，读取不枚举磁盘。</summary>
    public long? LastKnownBytes => Interlocked.Read(ref knownBytes) is var value and >= 0 ? value : null;
    /// <summary>段文件总字节；已测量时直接返回内存值，否则测量一次并保存。</summary>
    public long Bytes
    {
        get
        {
            if (LastKnownBytes is { } known) return known;
            var measured = DiskBytes();
            Interlocked.CompareExchange(ref knownBytes, measured, -1);
            return measured;
        }
    }

    /// <summary>启动恢复时按磁盘重新测量段文件总字节。</summary>
    public async Task MeasureAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { Remeasure(); }
        finally { gate.Release(); }
    }
    private void Remeasure()
    {
        try { Interlocked.Exchange(ref knownBytes, DiskBytes()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or StorageException)
        { Interlocked.Exchange(ref knownBytes, -1); }
    }

    public async Task AppendAsync(IReadOnlyList<DiagnosticRecord> records, DiagnosticRetentionPolicy policy, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var limit = checked((long)(policy.LogBudgetBytes?.Value ?? 50 * 1024 * 1024UL));
            foreach (var record in records)
            {
                if (!DiagnosticSanitizer.IsValid(record)) { dropped++; continue; }
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, ContractJson.Options) + "\n");
                var files = Segments();
                if (current is not null && File.Exists(current) && new FileInfo(current).Length + bytes.Length > Math.Min(limit, MaximumSegmentBytes))
                    current = null;
                var total = files.Sum(x => { StoragePaths.ValidateLocal(x); return new FileInfo(x).Length; });
                foreach (var old in files.Where(x => x != current).OrderBy(x => File.GetLastWriteTimeUtc(x)))
                {
                    if (total + bytes.Length <= limit) break;
                    StoragePaths.ValidateLocal(old);
                    if (time.GetUtcNow().UtcDateTime - File.GetLastWriteTimeUtc(old) < TimeSpan.FromSeconds(policy.RecentProtectionSeconds)) continue;
                    var size = new FileInfo(old).Length;
                    File.Delete(old); total -= size;
                }
                Interlocked.Exchange(ref knownBytes, total);
                if (total + bytes.Length > limit) { dropped++; LastError = "resource.capacityExceeded"; continue; }
                if (current is null || !File.Exists(current) || new FileInfo(current).Length + bytes.Length > Math.Min(limit, MaximumSegmentBytes))
                {
                    if (Segments().Length >= MaximumSegments) { dropped++; LastError = "resource.capacityExceeded"; continue; }
                    current = Path.Combine(Root, "log-" + Guid.NewGuid().ToString("N") + ".jsonl");
                }
                StoragePaths.ValidateLocal(current);
                await using var stream = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read,
                    8192, FileOptions.Asynchronous);
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                File.SetLastWriteTimeUtc(current, time.GetUtcNow().UtcDateTime);
                Interlocked.Exchange(ref knownBytes, total + bytes.Length);
                LastError = null;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or StorageException)
        {
            dropped += (ulong)records.Count; LastError = error is StorageException e ? e.Code : "storage.unavailable";
            // 失败可能发生在回收或写入之间，下一次读取重新测量。
            Interlocked.Exchange(ref knownBytes, -1);
        }
        catch (OperationCanceledException) { Interlocked.Exchange(ref knownBytes, -1); throw; }
        finally { gate.Release(); }
    }
}

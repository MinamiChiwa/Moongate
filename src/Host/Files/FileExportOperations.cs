using System.IO.Compression;
using System.Text.Json;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Files;

public sealed class FileExportOperations(OperationStore operations, FileTransferStore files,
    FileTransferService access, AssetStore assets, PathService paths, TimeProvider clock,
    VolumeSpaceReservations? spaceReservations = null, SqliteStore? database = null) : BackgroundService
{
    private sealed record Job(string Id, CancellationTokenSource Stop);
    private readonly Channel<Job> queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(4) { SingleReader = true });
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly object gate = new();
    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);
    private volatile bool accepting;
    public override Task StartAsync(CancellationToken ct) { accepting = true; return base.StartAsync(ct); }
    public async Task<OperationSnapshot> SubmitAsync(AuthSession session, FileExportRequest request, string? key, CancellationToken ct)
    {
        request = JsonSerializer.Deserialize<FileExportRequest>(JsonSerializer.Serialize(request, ContractJson.Options), ContractJson.Options)!;
        FileNames.Validate(request.Name); FileTransferStore.Key(key);
        if (request.Sources is not { Count: >= 1 and <= 16 } || request.Sources.Any(x => x is null) ||
            request.Sources.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != request.Sources.Count)
            throw new OperationException("request.invalid", "文件导出需要 1–16 个不同源资产版本");
        await admission.WaitAsync(ct);
        try
        {
            if (operations.FileExportRetry(session.ClientId, request, key) is { } retry) return retry;
            if (!accepting) throw new OperationException("provider.unavailable", "文件导出执行器正在关闭");
            var sources = new List<FileAssetAccess>(); ulong total = 0;
            foreach (var version in request.Sources)
            {
                var source = access.Resolve(version.Id, session);
                if (source.Asset.Revision != version.Revision) throw new OperationException("revision.stale", "导出源资产修订已变化");
                if (source.Category == "selected-files" && !request.IncludeSelectedFiles)
                    throw new OperationException("storage.scopeDenied", "原文件须明确选择私有文件传输");
                total += source.Asset.SizeBytes.Value;
                if (total > FilePackageReader.MaximumExpandedBytes) throw new OperationException("storage.quotaExceeded", "导出源展开总量超限");
                sources.Add(source);
            }
            bool available; lock (gate) available = jobs.Count < 4;
            var plan = new FileExportPlan(Guid.NewGuid().ToString("N"), request.Name, sources,
                sources.Select(x => x.Category).Distinct(StringComparer.Ordinal).ToArray(), clock.GetUtcNow());
            var accepted = await operations.AdmitFileExportAsync(session.ClientId, request, plan, key!, available, ct);
            if (accepted.Created)
            {
                var job = new Job(accepted.Snapshot.Id, new());
                lock (gate) jobs.Add(job.Id, job);
                if (!queue.Writer.TryWrite(job)) throw new InvalidOperationException("已受理文件导出未能进入队列");
            }
            return accepted.Snapshot;
        }
        finally { admission.Release(); }
    }
    public async Task<OperationSnapshot> CancelAsync(string id, AuthSession session, string? reason, CancellationToken ct)
    {
        var operation = operations.Get(id, session.ClientId, session.Owner);
        if (operation.Kind != "files.export") throw new OperationException("resource.notFound", "文件导出不存在");
        var cancelled = await operations.CancelAsync(id, session.ClientId, session.Owner, reason, ct);
        lock (gate) if (jobs.TryGetValue(id, out var job)) job.Stop.Cancel();
        return cancelled;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync())
        {
            using var stopHost = stoppingToken.Register(() => job.Stop.Cancel());
            FileExportArtifact? artifact = null;
            try
            {
                var operation = await operations.BeginFileExportAsync(job.Id);
                if (operation is null) continue;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(job.Stop.Token);
                deadline.CancelAfter(TimeSpan.FromMinutes(5));
                var plan = operation.Result!.Value.Deserialize<FileExportPlan>(ContractJson.Options)!;
                artifact = await PackAsync(operation.Owner, plan, deadline.Token);
                if (!await operations.FinishFileExportAsync(job.Id, artifact)) await files.DiscardExportAsync(artifact);
            }
            catch (OperationCanceledException)
            {
                if (artifact is not null) await files.DiscardExportAsync(artifact);
                await operations.FinishFileExportAsync(job.Id, null, interrupted: stoppingToken.IsCancellationRequested,
                    cancelled: !stoppingToken.IsCancellationRequested);
            }
            catch (Exception error)
            {
                var code = error switch { OperationException e => e.Code, StorageException e when ApiErrors.Definitions.Any(x => x.Code == e.Code) => e.Code, _ => "storage.unavailable" };
                await operations.FinishFileExportAsync(job.Id, null, ApiErrors.Create(code, "文件导出未完成", job.Id));
            }
            finally
            {
                try { await files.ReleaseSourcesAsync(job.Id); }
                catch { accepting = false; queue.Writer.TryComplete(); throw; }
                finally { lock (gate) jobs.Remove(job.Id); job.Stop.Dispose(); }
            }
        }
    }
    private async Task<FileExportArtifact> PackAsync(string owner, FileExportPlan plan, CancellationToken ct)
    {
        using var activity = database?.Activities.EnterActivity("files", "file.export");
        var directory = paths.Resolve("temp", "file-exports"); Directory.CreateDirectory(directory);
        var path = paths.Resolve("temp", "file-exports", plan.Id + ".zip");
        await using var temporary = database is null ? null : await database.TemporaryFiles.TrackAsync("files", path, ct);
        using var space = (spaceReservations ?? VolumeSpaceReservations.Shared).Reserve("file-export:" + plan.Id,
            new VolumeSpaceRequirement(directory, FilePackageReader.MaximumUploadBytes + 65536L));
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous);
            using (var bounded = new FileOutputBudget(output, FilePackageReader.MaximumUploadBytes))
            using (var archive = new ZipArchive(bounded, ZipArchiveMode.Create, true))
            {
                var entries = new List<FileExportEntry>();
                for (var i = 0; i < plan.Sources.Count; i++)
                {
                    var source = plan.Sources[i];
                    await using var lease = await assets.OpenReadAsync(source.Asset.Id, "file-read:" + plan.Id, FilePackageReader.MaximumUploadBytes, ct);
                    if (lease.Snapshot != source.Asset) throw new OperationException("storage.assetDamaged", "固定导出源元数据已变化");
                    var relative = "files/" + i.ToString("D4") + "/" + FileNames.Validate(source.Name);
                    var entry = archive.CreateEntry(relative, CompressionLevel.Fastest);
                    await using (var content = entry.Open()) await lease.Content.CopyToAsync(content, 65536, ct);
                    entries.Add(new(relative, source.Asset.ModuleId, source.Asset.Id, source.Asset.Revision,
                        source.Asset.MediaType, source.Asset.Sha256, source.Asset.SizeBytes));
                }
                await using var manifest = archive.CreateEntry("manifest.json", CompressionLevel.Fastest).Open();
                await JsonSerializer.SerializeAsync(manifest, new FileExportManifest(1, plan.Id, plan.CreatedAt, plan.Categories, entries), ContractJson.Options, ct);
            }
            await output.FlushAsync(ct); output.Flush(true); output.Position = 0;
            return await files.PublishExportAsync(owner, plan, output, ct);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        await admission.WaitAsync(ct);
        try { accepting = false; queue.Writer.TryComplete(); lock (gate) foreach (var job in jobs.Values) job.Stop.Cancel(); }
        finally { admission.Release(); }
        await base.StopAsync(ct);
    }
}

internal sealed class FileOutputBudget(Stream output, long maximum) : Stream
{
    public override bool CanRead => false; public override bool CanSeek => output.CanSeek; public override bool CanWrite => true;
    public override long Length => output.Length;
    public override long Position { get => output.Position; set => output.Position = value; }
    private void Check(int count) { if (output.Position + count > maximum) throw new OperationException("storage.quotaExceeded", "导出包超过文件预算"); }
    public override void Write(byte[] buffer, int offset, int count) { Check(count); output.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); output.Write(buffer); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Check(buffer.Length); return output.WriteAsync(buffer, ct); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override void Flush() => output.Flush();
    public override Task FlushAsync(CancellationToken ct) => output.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => output.Seek(offset, origin);
    public override void SetLength(long value) { if (value > maximum) throw new OperationException("storage.quotaExceeded", "导出包超过文件预算"); output.SetLength(value); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

using System.Security.Cryptography;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Files;

/// <summary>原生桌面桥在用户选定文件后调用；HTTP 接口只使用已登记的选择 ID。</summary>
public sealed class FileSelectionRegistry(LocalAuthService auth, TimeProvider? clock = null) : BackgroundService
{
    private sealed record Evidence(long Length, string Sha256, DateTime LastWritten, DateTime Created);
    private sealed record Selection(FileSelectionSnapshot Snapshot, string Path, Evidence? Target, FileStream? Input,
        long SelectedAt, SemaphoreSlim Gate);
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, Selection> selections = new(StringComparer.Ordinal);
    private bool stopped;
    public bool Available { get; private set; }

    public async Task<FileSelectionSnapshot> RegisterReadAsync(AuthSession session, string selectedPath, CancellationToken ct = default)
    {
        Native(session); var path = ExternalPath(selectedPath); var name = FileNames.Validate(Path.GetFileName(path));
        FileStream? input = null;
        try
        {
            input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length > FilePackageReader.MaximumUploadBytes) throw new OperationException("storage.quotaExceeded", "已选文件超过上传预算");
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct)); input.Position = 0;
            var value = new FileSelectionSnapshot(Guid.NewGuid().ToString("N"), session.ClientId, name, "read", new(1),
                time.GetUtcNow() + Lifetime, new((ulong)input.Length), hash, false);
            Add(new(value, path, null, input, time.GetTimestamp(), new(1, 1)));
            input = null; return value;
        }
        catch (IOException) { throw new OperationException("storage.assetDamaged", "已选源文件不可读或正在变化"); }
        finally { if (input is not null) await input.DisposeAsync(); }
    }
    public async Task<FileSelectionSnapshot> RegisterSaveAsync(AuthSession session, string selectedPath, bool overwrite = false, CancellationToken ct = default)
    {
        Native(session); var path = ExternalPath(selectedPath);
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new OperationException("storage.pathRejected", "已选保存目录不存在");
        Evidence? evidence = null;
        if (overwrite && File.Exists(path))
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            if (file.Length > FilePackageReader.MaximumUploadBytes) throw new OperationException("storage.quotaExceeded", "覆盖目标超过文件预算");
            evidence = new(file.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)),
                File.GetLastWriteTimeUtc(path), File.GetCreationTimeUtc(path));
        }
        var value = new FileSelectionSnapshot(Guid.NewGuid().ToString("N"), session.ClientId, FileNames.Validate(Path.GetFileName(path)),
            "write", new(1), time.GetUtcNow() + Lifetime, evidence is null ? null : new((ulong)evidence.Length), evidence?.Sha256, overwrite);
        Add(new(value, path, evidence, null, time.GetTimestamp(), new(1, 1))); return value;
    }
    private void Add(Selection selection)
    {
        lock (gate)
        {
            if (stopped) throw new OperationException("provider.unavailable", "文件选择注册器正在关闭");
            if (selections.Count >= 32) throw new OperationException("resource.capacityExceeded", "文件选择数量达到上限");
            selections.Add(selection.Snapshot.Id, selection); Available = true;
        }
    }
    public FileSelectionSnapshot Get(string id, AuthSession session) => Find(id, session).Snapshot;
    private Selection Find(string id, AuthSession session)
    {
        lock (gate)
        {
            if (stopped || !selections.TryGetValue(id, out var value) || value.Snapshot.Owner != session.ClientId || session.Origin is not null)
                throw new OperationException("resource.notFound", "文件选择不存在");
            if (time.GetElapsedTime(value.SelectedAt) >= Lifetime) throw new OperationException("storage.selectionExpired", "文件选择已到期");
            if (!auth.HasPermission(session.ClientId, ApiPermissions.FilesTransfer)) throw new OperationException("permission.denied", "文件选择授权已结束");
            return value;
        }
    }
    private void Native(AuthSession session)
    {
        if (session.Origin is not null || !auth.HasPermission(session.ClientId, ApiPermissions.FilesTransfer))
            throw new OperationException("permission.denied", "文件选择须由已授权原生桌面客户端登记");
    }
    internal async Task<T> ReadAsync<T>(string id, AuthSession session, Func<FileSelectionSnapshot, Stream, Task<T>> consume, CancellationToken ct)
    {
        var value = Find(id, session);
        if (value.Snapshot.Action != "read") throw new OperationException("storage.scopeDenied", "文件选择不允许读取");
        await value.Gate.WaitAsync(ct);
        try
        {
            _ = Find(id, session);
            if (value.Input is null) throw new OperationException("storage.selectionExpired", "文件选择已释放");
            value.Input.Position = 0;
            return await consume(value.Snapshot, value.Input);
        }
        finally { value.Gate.Release(); }
    }
    internal bool ReadAuthorized(string id, AuthSession session)
    { try { _ = Find(id, session); return true; } catch (OperationException) { return false; } }

    internal async Task<FileSaveReceipt> SaveAsync(string id, AuthSession session, FileExportArtifact artifact, Stream source, CancellationToken ct)
    {
        var value = Find(id, session);
        if (value.Snapshot.Action != "write") throw new OperationException("storage.scopeDenied", "文件选择不允许保存");
        await value.Gate.WaitAsync(ct);
        var directory = Path.GetDirectoryName(value.Path)!;
        var staged = Path.Combine(directory, ".mabi-" + Guid.NewGuid().ToString("N") + ".part");
        var previous = Path.Combine(directory, ".mabi-" + Guid.NewGuid().ToString("N") + ".previous");
        var keepPrevious = false;
        try
        {
            _ = Find(id, session); ExternalPath(value.Path);
            await using var file = new FileStream(staged, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await source.CopyToAsync(file, 65536, ct); await file.FlushAsync(ct); file.Flush(true);
            if ((ulong)file.Length != artifact.Asset.SizeBytes.Value) throw new OperationException("storage.assetDamaged", "保存内容长度与导出不符");
            file.Position = 0;
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)) != artifact.Asset.Sha256)
                throw new OperationException("storage.assetDamaged", "保存内容哈希与导出不符");
            await file.DisposeAsync();
            _ = Find(id, session); ExternalPath(value.Path); ct.ThrowIfCancellationRequested();
            var target = value.Path; var overwritten = false;
            if (value.Snapshot.Overwrite)
            {
                if (value.Target is { } expected)
                {
                    if (!File.Exists(target)) throw Changed();
                    await using var guard = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
                    if (guard.Length != expected.Length || File.GetLastWriteTimeUtc(target) != expected.LastWritten || File.GetCreationTimeUtc(target) != expected.Created ||
                        Convert.ToHexStringLower(await SHA256.HashDataAsync(guard, ct)) != expected.Sha256) throw Changed();
                    ct.ThrowIfCancellationRequested();
                    File.Replace(staged, target, previous, true);
                    keepPrevious = true;
                    await using var old = new FileStream(previous, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
                    if (old.Length != expected.Length || Convert.ToHexStringLower(await SHA256.HashDataAsync(old)) != expected.Sha256)
                    {
                        File.Replace(previous, target, staged, true); keepPrevious = false; throw Changed();
                    }
                    keepPrevious = false;
                    overwritten = true;
                }
                else
                {
                    try { File.Move(staged, target, false); }
                    catch (IOException) when (File.Exists(target)) { throw Changed(); }
                }
            }
            else
            {
                var stem = Path.GetFileNameWithoutExtension(target); var extension = Path.GetExtension(target);
                for (var attempt = 0; ; attempt++)
                {
                    if (attempt > 1000) throw new OperationException("resource.capacityExceeded", "保存目录同名目标数量达到上限");
                    var suffix = " (" + attempt + ")";
                    var maxStem = Math.Max(1, 128 - extension.Length - suffix.Length);
                    var shortened = stem.Length > maxStem ? stem[..maxStem] : stem;
                    if (char.IsHighSurrogate(shortened[^1])) shortened = shortened[..^1];
                    target = attempt == 0 ? value.Path : Path.Combine(directory, shortened + suffix + extension);
                    FileNames.Validate(Path.GetFileName(target));
                    try { File.Move(staged, target, false); break; }
                    catch (IOException) when (File.Exists(target)) { }
                }
            }
            return new(artifact.Id, Path.GetFileName(target), artifact.Asset.Sha256, artifact.Asset.SizeBytes, overwritten);
        }
        catch (IOException) { throw new OperationException("storage.unavailable", "保存目录不可写或目标仍被占用"); }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); if (!keepPrevious && File.Exists(previous)) File.Delete(previous); }
            finally { value.Gate.Release(); }
        }
    }
    private static OperationException Changed() => new("storage.targetChanged", "保存目标内容已变化，请重新选择");
    private static string ExternalPath(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new OperationException("provider.unavailable", "原生文件选择需要 Windows 桌面桥");
        if (!Path.IsPathFullyQualified(path) || path.Length > 4096 ||
            path[(Path.GetPathRoot(path)?.Length ?? 0)..].Split(['/', '\\']).Any(x => x is "." or ".."))
            throw new OperationException("storage.pathRejected", "文件选择需要有效绝对路径");
        var full = Path.GetFullPath(path); FileNames.Validate(Path.GetFileName(full));
        try { LocalDiscoverySecurity.RejectLinks(full); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new OperationException("storage.pathRejected", "文件选择路径不允许链接或不可访问的目录"); }
        return full;
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                Selection[] expired;
                lock (gate) expired = selections.Values.Where(x => time.GetElapsedTime(x.SelectedAt) >= Lifetime ||
                    !auth.HasPermission(x.Snapshot.Owner, ApiPermissions.FilesTransfer)).ToArray();
                foreach (var value in expired)
                {
                    if (!await value.Gate.WaitAsync(0, ct)) continue;
                    try { lock (gate) selections.Remove(value.Snapshot.Id); if (value.Input is not null) await value.Input.DisposeAsync(); }
                    finally { value.Gate.Release(); }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        Selection[] active;
        lock (gate) { stopped = true; active = selections.Values.ToArray(); selections.Clear(); }
        foreach (var value in active)
        {
            await value.Gate.WaitAsync(ct);
            try { if (value.Input is not null) await value.Input.DisposeAsync(); }
            finally { value.Gate.Release(); }
        }
        await base.StopAsync(ct);
    }
}

using MabiTool.Contracts;

namespace MabiTool.Host.Extensions;

/// <summary>用户为扩展选定的一个文件：只有文件名、大小、SHA-256 与内容，扩展拿不到本机路径。</summary>
public sealed record ExtensionPickedFile(string Name, byte[] Content, string Sha256);

/// <summary>
/// 扩展读取用户所选文件的往返：扩展经 <c>files.pick</c> 发起请求，原生桌面客户端读取界面目录时看到请求并弹出文件选择框，
/// 用户选定后经上传接口把文件交给宿主，宿主核对大小后把内容一次性交给扩展；取消、过期或扩展停止时请求结束。
/// 每个扩展同时最多一个请求、两次请求至少间隔 5 秒；最近 10 秒内没有原生桌面客户端读取界面目录时视为没有前端窗口。
/// </summary>
public sealed class ExtensionFileRequests(TimeProvider time)
{
    public const int MaximumBytes = 512 * 1024, MaximumPending = 4;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2), MinimumInterval = TimeSpan.FromSeconds(5), DesktopWindow = TimeSpan.FromSeconds(10);
    private sealed record Pending(ExtensionFileRequest Request, TaskCompletionSource<ExtensionPickedFile?> Result);
    private readonly object gate = new();
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> lastRequest = new(StringComparer.Ordinal);
    private long? desktopSeen;

    /// <summary>原生桌面客户端读取了界面目录；用来判断有没有能弹出文件选择框的前端。</summary>
    public void DesktopPolled()
    {
        lock (gate) desktopSeen = time.GetTimestamp();
    }

    public IReadOnlyList<ExtensionFileRequest> Snapshot()
    {
        lock (gate)
        {
            Expire();
            return pending.Values.Select(x => x.Request).OrderBy(x => x.ExpiresAt).ToArray();
        }
    }

    /// <summary>发起请求并等待用户选择；取消时返回空。<paramref name="ct"/> 取消（撤销授权、扩展停止）时请求随之撤下。</summary>
    public async Task<ExtensionPickedFile?> RequestAsync(string extensionId, IReadOnlyDictionary<string, string> names, string title, CancellationToken ct)
    {
        Pending item;
        lock (gate)
        {
            Expire();
            if (desktopSeen is not { } seen || time.GetElapsedTime(seen) > DesktopWindow)
                throw new ExtensionCallException("extension.noDesktop", "没有打开的桌面窗口，无法显示文件选择框");
            if (pending.Values.Any(x => x.Request.ExtensionId == extensionId) ||
                lastRequest.TryGetValue(extensionId, out var last) && time.GetElapsedTime(last) < MinimumInterval || pending.Count >= MaximumPending)
                throw new ExtensionCallException("resource.capacityExceeded", "文件请求过于频繁，请稍后再试");
            lastRequest[extensionId] = time.GetTimestamp();
            item = new(new(Guid.NewGuid().ToString("N"), extensionId, names, title, new(MaximumBytes), time.GetUtcNow() + Lifetime),
                new(TaskCreationOptions.RunContinuationsAsynchronously));
            pending.Add(item.Request.Id, item);
        }
        try { return await item.Result.Task.WaitAsync(Lifetime, time, ct); }
        catch (TimeoutException) { throw new ExtensionCallException("extension.fileRequestExpired", "用户没有在两分钟内选择文件"); }
        finally
        {
            lock (gate) pending.Remove(item.Request.Id);
        }
    }

    /// <summary>前端的回复：<paramref name="file"/> 为空表示用户取消。请求不存在或已结束时返回假。</summary>
    public bool Complete(string requestId, ExtensionPickedFile? file)
    {
        if (file is not null && file.Content.Length > MaximumBytes)
            throw new MabiTool.Infrastructure.OperationException("storage.quotaExceeded", "所选文件超过 512 KiB");
        lock (gate)
        {
            if (!pending.Remove(requestId, out var item)) return false;
            return item.Result.TrySetResult(file);
        }
    }

    private void Expire()
    {
        foreach (var (id, item) in pending.Where(x => x.Value.Request.ExpiresAt <= time.GetUtcNow()).ToArray())
        {
            pending.Remove(id);
            item.Result.TrySetCanceled();
        }
    }
}

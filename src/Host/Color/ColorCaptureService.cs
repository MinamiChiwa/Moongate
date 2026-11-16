using System.Security.Cryptography;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Color;

public sealed class ColorCaptureService(IDesktopCaptureSource? source, TimeProvider clock) : BackgroundService
{
    /// <summary>4096×4096，容纳并排两块 4K（7680×2160）虚拟桌面；RGB24 单张 48 MiB。</summary>
    public const int MaximumPixels = 16_777_216;
    public const long MaximumTotalBytes = 128L * 1024 * 1024;
    private readonly SemaphoreSlim capturing = new(1, 1);
    private readonly object gate = new();
    private readonly Dictionary<string, Frozen> frames = [];
    private sealed record Frozen(string Owner, ColorScreenCapture Metadata, byte[] Rgb, long Timestamp);

    public async Task<ColorScreenCapture> CaptureAsync(AuthSession session, CancellationToken ct)
    {
        if (source is null) throw new OperationException("capability.unavailable", "当前宿主未配置桌面截图源");
        if (!await capturing.WaitAsync(0, ct)) throw new OperationException("resource.busy", "正在捕获另一张屏幕");
        try
        {
            DesktopRgbFrame frame;
            try { frame = await source.CaptureAsync(MaximumPixels, ct); }
            catch (ArgumentException) { throw new OperationException("capture.frameInvalid", "桌面截图超出像素预算"); }
            catch (InvalidOperationException) { throw new OperationException("capture.sourceFailed", "桌面设备不可用或显示布局已变化"); }
            var b = frame.Bounds;
            var size = (long)b.Width * b.Height * 3;
            if (b.Width <= 0 || b.Height <= 0 || size > MaximumPixels * 3L || size != frame.Rgb.LongLength ||
                (long)b.X + b.Width > int.MaxValue || (long)b.Y + b.Height > int.MaxValue)
                throw new OperationException("capture.frameInvalid", "截图尺寸或 RGB 长度无效");
            ct.ThrowIfCancellationRequested();
            var rgb = frame.Rgb.ToArray();
            var now = clock.GetUtcNow();
            var metadata = new ColorScreenCapture(Guid.NewGuid().ToString("N"), b, "rgb24",
                Convert.ToHexStringLower(SHA256.HashData(rgb)), rgb.Length, now, now.AddSeconds(60));
            lock (gate)
            {
                Prune();
                var previous = frames.Values.SingleOrDefault(f => f.Owner == session.ClientId);
                if (frames.Count - (previous is null ? 0 : 1) >= 4 ||
                    frames.Values.Sum(f => (long)f.Rgb.Length) - (previous?.Rgb.Length ?? 0) + size > MaximumTotalBytes)
                    throw new OperationException("resource.capacityExceeded", "冻结画面达到数量或 128 MiB 上限");
                if (previous is not null) frames.Remove(previous.Metadata.Id);
                frames.Add(metadata.Id, new(session.ClientId, metadata, rgb, clock.GetTimestamp()));
            }
            return metadata;
        }
        finally { capturing.Release(); }
    }
    public ColorPickedPixel Pixel(string id, ColorPixelQuery query, AuthSession session)
    {
        lock (gate)
        {
            var frame = Get(id, session);
            var x = (long)query.X - frame.Metadata.Bounds.X;
            var y = (long)query.Y - frame.Metadata.Bounds.Y;
            if (x < 0 || y < 0 || x >= frame.Metadata.Bounds.Width || y >= frame.Metadata.Bounds.Height)
                throw new OperationException("request.invalid", "物理屏幕坐标超出冻结画面");
            var offset = checked((int)((y * frame.Metadata.Bounds.Width + x) * 3));
            return new(id, query.X, query.Y, "#" + Convert.ToHexString(frame.Rgb.AsSpan(offset, 3)));
        }
    }
    public ColorAssetContent Content(string id, ColorAssetRangeRequest query, AuthSession session)
    {
        lock (gate)
        {
            var frame = Get(id, session);
            if (query.Offset < 0 || query.Offset > frame.Rgb.Length || query.Count is < 1 or > 262144)
                throw new OperationException("request.invalid", "画面读取范围无效，每次最多 256 KiB");
            return new(id, frame.Metadata.Sha256, query.Offset, frame.Rgb.Length,
                Convert.ToBase64String(frame.Rgb, query.Offset, Math.Min(query.Count, frame.Rgb.Length - query.Offset)));
        }
    }
    public ColorCaptureRelease Release(string id, AuthSession session)
    {
        lock (gate) { _ = Get(id, session); frames.Remove(id); return new(id, true); }
    }
    private Frozen Get(string id, AuthSession session)
    {
        Prune();
        if (!frames.TryGetValue(id, out var frame)) throw new OperationException("resource.notFound", "冻结画面不存在或已过期");
        if (frame.Owner != session.ClientId) throw new OperationException("permission.denied", "冻结画面属于其他客户端");
        return frame;
    }
    private void Prune()
    {
        foreach (var frame in frames.Values.Where(f => clock.GetElapsedTime(f.Timestamp) >= TimeSpan.FromSeconds(60)).ToArray())
            frames.Remove(frame.Metadata.Id);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) lock (gate) Prune(); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { lock (gate) frames.Clear(); }
    }
}

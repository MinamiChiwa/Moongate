using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.Modules.Color.Domain;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Color;

/// <summary>
/// 目标色集合图由原 palette 算法在独立计算 Worker 中生成，PNG 作为平台资产缓存。
/// 判定依赖 OpenCV 浮点 Lab，.NET 重写无法保证逐像素一致，因此不在宿主进程计算。
/// </summary>
public sealed class ColorPaletteService : IHostedService, IAsyncDisposable
{
    public const int MaximumCachedAssets = 64;
    public const long MaximumCachedBytes = 64L * 1024 * 1024;
    public const int MaximumPendingRenders = 4;
    public const int MaximumPngBytes = 32 * 1024 * 1024;
    public static readonly TimeSpan RenderLimit = TimeSpan.FromSeconds(60);
    private const string ScopePrefix = "palette:v1:";
    private readonly ColorWorkerOptions? options;
    private readonly AssetStore assets;
    private readonly ActivityBarrier? activities;
    private readonly TimeProvider clock;
    private readonly ColorWorkerPool pool;
    private readonly SemaphoreSlim rendering = new(1, 1);
    private readonly object gate = new();
    private readonly Dictionary<string, Task<ColorPalette>> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> used = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopped = new();

    private readonly int maximumAssets;
    private readonly long maximumBytes;

    public ColorPaletteService(ColorWorkerOptions? options, AssetStore assets, ActivityBarrier? activities = null, TimeProvider? clock = null,
        int maximumAssets = MaximumCachedAssets, long maximumBytes = MaximumCachedBytes)
    {
        this.options = options; this.assets = assets; this.activities = activities;
        this.maximumAssets = maximumAssets; this.maximumBytes = maximumBytes;
        this.clock = clock ?? TimeProvider.System;
        // 运行可长时间租用共享通道；集合图使用独立按需通道，空闲保温沿用同一配置。
        pool = new(options, this.clock);
    }

    public ColorWorkerPool Workers => pool;

    public async Task<ColorPalette> RenderAsync(ColorPaletteRequest request, CancellationToken ct)
    {
        var (target, exact, tolerance, image) = Validate(request);
        var key = ScopePrefix + target + ":" + (exact ? "exact" : tolerance.ToString("R", CultureInfo.InvariantCulture)) + ":" + image + ":";
        if (Lookup(key, target, exact, tolerance) is { } cached) return cached;
        if (options is null || !OperatingSystem.IsWindows())
            throw new OperationException("capability.unavailable", "当前宿主未配置原颜色计算引擎");
        Task<ColorPalette> task;
        lock (gate)
        {
            if (stopped.IsCancellationRequested) throw new OperationException("provider.unavailable", "宿主正在停止");
            if (!pending.TryGetValue(key, out task!))
            {
                if (pending.Count >= MaximumPendingRenders)
                    throw new OperationException("resource.busy", "目标色集合图排队已满，请稍后重试");
                task = Task.Run(() => OperatingSystem.IsWindows() ? ComputeAsync(key, target, exact, tolerance, image)
                    : throw new OperationException("capability.unavailable", "原颜色计算引擎需要 Windows"));
                pending.Add(key, task);
            }
        }
        // 请求断开只结束等待；已开始的计算完成后进入缓存，供相同参数复用。
        return await task.WaitAsync(ct);
    }

    private static (string Target, bool Exact, double Tolerance, string Image) Validate(ColorPaletteRequest? request)
    {
        if (request is null || !ColorProfiles.IsHex(request.TargetHex) || request.MatchMode.KnownValue is not { } mode ||
            request.Image.KnownValue is not { } image)
            throw new OperationException("request.invalid", "需要大写 #RRGGBB 目标色、已知判定模式与图像类型");
        var exact = mode == ColorMatchMode.ExactHex;
        if (!exact && (!double.IsFinite(request.Tolerance) || request.Tolerance is < 1 or > 35))
            throw new OperationException("request.invalid", "相似颜色的容差须为 1–35");
        return (request.TargetHex, exact, exact ? 0 : request.Tolerance, image == ColorPaletteImage.Overview ? "overview" : "atlas");
    }

    private ColorPalette? Lookup(string key, string target, bool exact, double tolerance)
    {
        foreach (var asset in assets.ListByScopePrefix("color", key))
        {
            var parts = asset.Scope[key.Length..].Split(':');
            if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var height)) continue;
            lock (gate) used[asset.Id] = clock.GetTimestamp();
            var image = key.EndsWith(":overview:", StringComparison.Ordinal) ? ColorPaletteImage.Overview : ColorPaletteImage.Atlas;
            return new(target, exact ? ColorMatchMode.ExactHex : ColorMatchMode.Similar, tolerance, image, count, width, height, asset);
        }
        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task<ColorPalette> ComputeAsync(string key, string target, bool exact, double tolerance, string image)
    {
        var runId = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(options!.AssetRoot, runId);
        var entered = false;
        try
        {
            await rendering.WaitAsync(stopped.Token); entered = true;
            if (Lookup(key, target, exact, tolerance) is { } cached) return cached;
            using var activity = activities?.EnterActivity("color", "color.palette");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
            deadline.CancelAfter(RenderLimit);
            var assetId = Guid.NewGuid().ToString("N");
            JsonElement payload;
            await using (var rental = await pool.RentAsync(runId, _ => { }, deadline.Token))
            {
                var call = await rental.Worker.BeginAsync(new("color.palette", JsonSerializer.SerializeToElement(new
                    { targetHex = target, tolerance = exact ? (double?)null : tolerance, image, assetId }), runId, "0", "0"), deadline.Token);
                rental.Call = call;
                var result = await call.Completion.WaitAsync(deadline.Token);
                if (result.Kind != "result")
                    throw new WorkerException(result.Payload.TryGetProperty("code", out var code) ? code.GetString() ?? "worker.resultInvalid" : "worker.resultInvalid");
                payload = result.Payload;
            }
            var count = payload.GetProperty("colorCount").GetInt32();
            var width = payload.GetProperty("width").GetInt32();
            var height = payload.GetProperty("height").GetInt32();
            var size = payload.GetProperty("sizeBytes").GetInt64();
            var hash = payload.GetProperty("sha256").GetString();
            if (count < 1 || width < 1 || height < 1 || size is < 1 or > MaximumPngBytes || hash is not { Length: 64 })
                throw new WorkerException("worker.resultInvalid");
            var path = Path.Combine(folder, assetId + ".png");
            if (new FileInfo(path) is not { Exists: true, LinkTarget: null } file || file.Length != size ||
                new DirectoryInfo(folder).LinkTarget is not null)
                throw new WorkerException("worker.assetInvalid");
            var bytes = await File.ReadAllBytesAsync(path, deadline.Token);
            if (bytes.LongLength != size || Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash)
                throw new WorkerException("worker.assetInvalid");
            var scope = key + count.ToString(CultureInfo.InvariantCulture) + ":" + width.ToString(CultureInfo.InvariantCulture) +
                ":" + height.ToString(CultureInfo.InvariantCulture);
            AssetSnapshot asset;
            await using (var content = new MemoryStream(bytes, writable: false))
                asset = await assets.PutAsync("color", scope, "image/png", content, MaximumPngBytes, deadline.Token);
            lock (gate) used[asset.Id] = clock.GetTimestamp();
            await EvictAsync(asset.Id, deadline.Token);
            return new(target, exact ? ColorMatchMode.ExactHex : ColorMatchMode.Similar, tolerance,
                image == "overview" ? ColorPaletteImage.Overview : ColorPaletteImage.Atlas, count, width, height, asset);
        }
        catch (OperationCanceledException) when (!stopped.IsCancellationRequested)
        { throw new OperationException("color.paletteTimeout", "目标色集合图超过 60 秒期限"); }
        catch (WorkerException error)
        {
            throw new OperationException(error.Code == "worker.busy" ? "resource.busy" : "provider.unavailable", "原颜色计算引擎暂不可用",
                new Dictionary<string, JsonElement> { ["reason"] = JsonSerializer.SerializeToElement(error.Code) });
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new OperationException("provider.unavailable", "原颜色计算引擎返回无效结果",
                new Dictionary<string, JsonElement> { ["reason"] = JsonSerializer.SerializeToElement("worker.resultInvalid") });
        }
        finally
        {
            try { if (Directory.Exists(folder) && new DirectoryInfo(folder).LinkTarget is null) Directory.Delete(folder, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            if (entered) rendering.Release();
            lock (gate) pending.Remove(key);
        }
    }

    /// <summary>超过份数或字节上限时，先淘汰本进程最久未用、再按创建先后淘汰；正在下载的资产保留。</summary>
    private async Task EvictAsync(string keep, CancellationToken ct)
    {
        var rows = assets.ListByScopePrefix("color", ScopePrefix);
        var count = rows.Count;
        var bytes = rows.Sum(row => (long)row.SizeBytes.Value);
        if (count <= maximumAssets && bytes <= maximumBytes) return;
        Dictionary<string, long> snapshot;
        lock (gate) snapshot = new(used, StringComparer.Ordinal);
        var removed = 0;
        foreach (var row in rows.Select((row, index) => (row, index))
                     .OrderBy(x => snapshot.ContainsKey(x.row.Id) ? 1 : 0).ThenBy(x => snapshot.GetValueOrDefault(x.row.Id)).ThenBy(x => x.index)
                     .Select(x => x.row))
        {
            if (count <= maximumAssets && bytes <= maximumBytes) break;
            if (row.Id == keep) continue;
            try
            {
                if (!await assets.RemoveAsync(row.Id, ct)) continue;
                count--; bytes -= (long)row.SizeBytes.Value; removed++;
                lock (gate) used.Remove(row.Id);
            }
            catch (StorageException error) when (error.Code == "storage.assetInUse") { }
        }
        if (removed > 0) await assets.CollectGarbageAsync(ct);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync();
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (stopped.IsCancellationRequested) return; stopped.Cancel(); }
        await pool.DisposeAsync();
    }
}

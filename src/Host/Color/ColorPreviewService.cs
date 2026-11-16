using System.Text.Json;
using System.Globalization;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.Modules.Color.Domain;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Color;

public sealed class ColorPreviewService(ColorWorkerOptions? options, ActivityBarrier? activities = null,
    ColorWorkerPool? sharedPool = null) : IAsyncDisposable
{
    private readonly ColorWorkerPool pool = sharedPool ?? new(options);
    private readonly SemaphoreSlim evaluating = new(1, 1);
    public async Task<ColorPreview> EvaluateAsync(ColorPreviewRequest request, CancellationToken ct)
    {
        ColorProfiles.Validate(request.Profile, requireEnabled: false);
        if (request.Colors is not { Count: 2 or 3 } || request.Colors.Any(c => c is not null && !ColorProfiles.IsHex(c)))
            throw new OperationException("request.invalid", "预览需要两或三个大写 #RRGGBB 色码或空值");
        var profile = ColorProfiles.Copy(request.Profile);
        var colors = request.Colors.ToArray();
        if (options is null || !OperatingSystem.IsWindows())
            throw new OperationException("capability.unavailable", "当前宿主未配置原颜色评价引擎");
        if (!await evaluating.WaitAsync(0, ct)) throw new OperationException("resource.busy", "另一份颜色预览正在评价");
        try
        {
            using var activity = activities?.EnterActivity("color", "color.preview");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            var id = Guid.NewGuid().ToString("N");
            await using var rental = await pool.RentAsync(id, _ => { }, deadline.Token);
            var worker = rental.Worker;
            var call = await worker.BeginAsync(new("color.evaluate", JsonSerializer.SerializeToElement(new
                { profile = ColorProfiles.EngineProfile(profile), colors }), id, "0", "0"), deadline.Token);
            rental.Call = call;
            var result = await call.Completion.WaitAsync(deadline.Token);
            if (result.Kind != "result") throw new WorkerException("worker.resultInvalid");
            var raw = result.Payload.GetProperty("result");
            var count = result.Payload.GetProperty("regionCount").GetInt32();
            if (count != colors.Length) throw new WorkerException("worker.resultInvalid");
            IReadOnlyDictionary<string, T> Map<T>(string field) => raw.GetProperty(field).EnumerateArray()
                .Select((row, index) => (profile.Regions[index].Id, Value: row.Deserialize<T>(ContractJson.Options)!))
                .ToDictionary(x => x.Id, x => x.Value);
            return new(true, raw.GetProperty("matching_policy").GetString()!, raw.GetProperty("verified").GetBoolean(),
                raw.GetProperty("accepted").GetBoolean(), Map<string?>("actual_colors"), Map<double?>("actual_deltas"), Map<double?>("match_scores"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new OperationException("color.previewTimeout", "颜色评价超过 60 秒期限"); }
        catch (WorkerException error)
        { throw new OperationException(error.Code == "worker.busy" ? "resource.busy" : "provider.unavailable", "原颜色评价引擎暂不可用",
            new Dictionary<string, JsonElement> { ["reason"] = JsonSerializer.SerializeToElement(error.Code) }); }
        finally { evaluating.Release(); }
    }
    public ValueTask DisposeAsync() => sharedPool is null ? pool.DisposeAsync() : ValueTask.CompletedTask;
    public static ColorWorkerOptions? FromEnvironment(PathService paths)
    {
        var python = Environment.GetEnvironmentVariable("MABITOOL_COLOR_PYTHON");
        var directory = Environment.GetEnvironmentVariable("MABITOOL_COLOR_WORKER_DIRECTORY");
        if (python is null && directory is null) return null;
        if (string.IsNullOrWhiteSpace(python) || string.IsNullOrWhiteSpace(directory) ||
            !Path.IsPathFullyQualified(python) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("颜色评价需要两个绝对路径：MABITOOL_COLOR_PYTHON 和 MABITOOL_COLOR_WORKER_DIRECTORY");
        var assets = paths.Resolve("temp", "color-previews");
        Directory.CreateDirectory(assets);
        var options = new ColorWorkerOptions(python, directory, assets);
        int Budget(string name, int fallback, int minimum, int maximum)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is null) return fallback;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var budget) || budget < minimum || budget > maximum)
                throw new ArgumentException($"{name} 须为 {minimum}–{maximum} 的整数");
            return budget;
        }
        return options with
        {
            MaximumParallelism = Budget("MABITOOL_COLOR_MAXIMUM_PARALLELISM", options.MaximumParallelism, 1, 4),
            IdleRetentionSeconds = Budget("MABITOOL_COLOR_IDLE_RETENTION_SECONDS", options.IdleRetentionSeconds, 0, 300)
        };
    }
}

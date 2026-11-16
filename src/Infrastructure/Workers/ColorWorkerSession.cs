using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;

namespace MabiTool.Infrastructure.Workers;

public sealed record ColorWorkerOptions(string PythonExecutable, string WorkerDirectory, string AssetRoot)
{
    public int MaximumParallelism { get; init; } = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
    public int IdleRetentionSeconds { get; init; } = 60;
    public string EntryModule { get; init; } = "mabitool_color.worker";
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan CancellationGrace { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>染色进程配置与交互适配；认证和故障回收复用通用 Worker 通道。</summary>
[SupportedOSPlatform("windows")]
public sealed class ColorWorkerSession : IAsyncDisposable
{
    private readonly WorkerSession session;
    private ColorWorkerSession(WorkerSession session) => this.session = session;
    public int ProcessId => session.ProcessId;
    public bool IsAlive => session.IsAlive;
    public string DiagnosticTail => session.DiagnosticTail;
    public int? ExitCode => session.ExitCode;
    public bool TerminatedByHost => session.TerminatedByHost;
    public static async Task<ColorWorkerSession> StartAsync(ColorWorkerOptions options,
        Action<WorkerFailure> cleanup, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumParallelism is < 1 or > 4 || options.IdleRetentionSeconds is < 0 or > 300 ||
            string.IsNullOrWhiteSpace(options.EntryModule))
            throw new ArgumentException("Worker 并行度为 1–4，保温期为 0–300 秒。");
        var threads = options.MaximumParallelism.ToString(CultureInfo.InvariantCulture);
        var environment = new Dictionary<string, string>
        {
            ["PYTHONPATH"] = options.WorkerDirectory, ["MABITOOL_COLOR_THREADS"] = threads
        };
        foreach (var variable in new[] { "OPENBLAS_NUM_THREADS", "OMP_NUM_THREADS", "MKL_NUM_THREADS", "NUMEXPR_NUM_THREADS" })
            environment[variable] = threads;
        var process = new WorkerProcessOptions(options.PythonExecutable, options.WorkerDirectory, options.AssetRoot, "color-engine")
        {
            Arguments = ["-u", "-B", "-s", "-m", options.EntryModule, .. options.Arguments],
            EnvironmentVariables = environment, StartupTimeout = options.StartupTimeout,
            HeartbeatInterval = options.HeartbeatInterval, HeartbeatTimeout = options.HeartbeatTimeout,
            CancellationGrace = options.CancellationGrace, InactiveRequestCode = "color.runInactive"
        };
        return new(await WorkerSession.StartAsync(process, cleanup, token).ConfigureAwait(false));
    }
    public Task<WorkerCall> BeginAsync(WorkerRequest request, CancellationToken token = default,
        Func<WorkerFrame, CancellationToken, Task<JsonElement>>? portHandler = null) => session.BeginAsync(request, token, portHandler);
    public Task CancelAsync(WorkerCall call, CancellationToken token = default) => session.CancelAsync(call, token);
    /// <summary>确认只表示命令受理；定位和实测结果从运行事件读取。</summary>
    public Task<JsonElement> InteractAsync(WorkerCall call, string action, string revision,
        string? batchId = null, string? candidateId = null, CancellationToken token = default)
    {
        if (!WorkerProtocol.IsUInt64(revision) || action is not ("select" or "retry") ||
            (action == "select" && (string.IsNullOrEmpty(batchId) || batchId.Length > 256 ||
                                   string.IsNullOrEmpty(candidateId) || candidateId.Length > 256)) ||
            (action == "retry" && (batchId is not null || candidateId is not null)))
            throw new ArgumentException("染色交互命令无效。");
        return session.CommandAsync(call, id => action == "retry" ? new { commandId = id, action, revision } :
            (object)new { commandId = id, action, revision, batchId, candidateId }, ValidateCommandResult, token);
    }
    private static void ValidateCommandResult(JsonElement payload)
    {
        if (payload.EnumerateObject().Count() != 4 ||
            !payload.TryGetProperty("accepted", out var accepted) || accepted.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !payload.TryGetProperty("code", out var code) ||
            (accepted.GetBoolean() ? code.ValueKind != JsonValueKind.Null : code.ValueKind != JsonValueKind.String) ||
            !payload.TryGetProperty("interaction", out var interaction) || interaction.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null) ||
            accepted.GetBoolean() && interaction.ValueKind != JsonValueKind.Object)
            throw new WorkerException("worker.commandInvalid");
        if (interaction.ValueKind == JsonValueKind.Object)
        {
            if (interaction.EnumerateObject().Count() != 4) throw new WorkerException("worker.commandInvalid");
            try { _ = ColorWorkerResults.ReadInteraction(interaction); }
            catch (Exception error) when (error is WorkerException or KeyNotFoundException or InvalidOperationException or FormatException)
            { throw new WorkerException("worker.commandInvalid", error); }
        }
    }
    public ValueTask DisposeAsync() => session.DisposeAsync();
}

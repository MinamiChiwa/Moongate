using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Runtime.ExceptionServices;
using MabiTool.Contracts;

namespace MabiTool.Providers.Tunnels;

public sealed record QuickTunnelBudgets(TimeSpan StartupTimeout, TimeSpan HealthInterval)
{
    public static QuickTunnelBudgets Default { get; } = new(TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(15));
    internal void Validate()
    {
        if (StartupTimeout <= TimeSpan.Zero || StartupTimeout > Default.StartupTimeout ||
            HealthInterval <= TimeSpan.Zero || HealthInterval > Default.HealthInterval)
            throw new ArgumentException("隧道启动及健康间隔超出预算");
    }
}

public sealed class QuickTunnelFactory(TunnelComponentOptions? component, ITunnelProcessFactory? processes = null,
    IRoomTunnelProbe? probe = null, TimeProvider? time = null, QuickTunnelBudgets? budgets = null)
{
    public QuickTunnelSession Open(int roomPort, string epoch, Func<RoomTunnelSnapshot, Task> changed)
    {
        if (roomPort is < 1 or > 65535 || !Guid.TryParse(epoch, out _))
            throw new ArgumentException("隧道需要本次房间端口及有效代次");
        var limits = budgets ?? QuickTunnelBudgets.Default; limits.Validate();
        return new(component, processes ?? new WindowsTunnelProcessFactory(), probe ?? new RoomTunnelProbe(time),
            time ?? TimeProvider.System, limits, roomPort, epoch, changed);
    }
}

public sealed class QuickTunnelSession : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource<RoomTunnelSnapshot> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task completion;
    private readonly object gate = new();
    private Task? disposal;
    public Task<RoomTunnelSnapshot> Ready => ready.Task;
    public Task Completion => completion;

    internal QuickTunnelSession(TunnelComponentOptions? component, ITunnelProcessFactory processes, IRoomTunnelProbe probe,
        TimeProvider time, QuickTunnelBudgets budgets, int roomPort, string epoch, Func<RoomTunnelSnapshot, Task> changed)
    { completion = RunAsync(component, processes, probe, time, budgets, roomPort, epoch, changed); }

    private async Task RunAsync(TunnelComponentOptions? component, ITunnelProcessFactory processes, IRoomTunnelProbe probe,
        TimeProvider time, QuickTunnelBudgets budgets, int roomPort, string epoch, Func<RoomTunnelSnapshot, Task> changed)
    {
        ITunnelProcess? process = null; FileStream? pin = null; Task? pumps = null, health = null, finding = null;
        ApiProblem? problem = null; var cancelled = false; Exception? completionFailure = null;
        using var startup = new CancellationTokenSource(budgets.StartupTimeout, time);
        using var starting = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, startup.Token);
        try
        {
            if (component is null) throw new TunnelException("room.tunnelNotConfigured", "公网建房需要已配置的隧道组件");
            pin = await component.VerifyAndPinAsync(starting.Token);
            await VerifyVersionAsync(component, processes, time, starting.Token);
            starting.Token.ThrowIfCancellationRequested();
            process = processes.Start(component.ExecutablePath,
                ["tunnel", "--url", $"http://127.0.0.1:{roomPort}", "--no-autoupdate", "--protocol", "http2"]);
            var candidates = Channel.CreateBounded<Uri>(new BoundedChannelOptions(4) { SingleReader = true });
            var output = PumpAsync(process.StandardOutput, candidates.Writer, stop.Token);
            var error = PumpAsync(process.StandardError, candidates.Writer, stop.Token);
            pumps = Task.WhenAll(output, error);
            var exited = process.WaitForExitAsync();
            var verified = FindEndpointAsync(candidates.Reader, probe, epoch, time, starting.Token); finding = verified;
            var startingCancelled = Task.Delay(Timeout.InfiniteTimeSpan, starting.Token);
            var first = await Task.WhenAny(verified, exited, output, error, startingCancelled);
            if (first != verified)
            {
                starting.Token.ThrowIfCancellationRequested();
                if (first.IsFaulted) await first;
                throw new TunnelException("room.tunnelUnavailable", "隧道在发布邀请前退出或停止输出");
            }
            var endpoint = await verified;
            if (exited.IsCompleted) throw new TunnelException("room.tunnelUnavailable", "隧道已退出");
            stop.Token.ThrowIfCancellationRequested();
            var available = new RoomTunnelSnapshot(RoomTunnelState.Available, endpoint.AbsoluteUri, null);
            await changed(available); stop.Token.ThrowIfCancellationRequested(); ready.TrySetResult(available);
            health = MonitorHealthAsync(endpoint, probe, epoch, time, budgets.HealthInterval, stop.Token);
            var ended = await Task.WhenAny(health, exited, output, error, Task.Delay(Timeout.InfiniteTimeSpan, stop.Token));
            stop.Token.ThrowIfCancellationRequested();
            if (ended == health) { await health; throw new TunnelException("room.tunnelHealthFailed", "公网房间连续三次健康核对失败"); }
            if (ended.IsFaulted) await ended;
            throw new TunnelException("room.tunnelUnavailable", "隧道进程或输出通道已关闭");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { cancelled = true; }
        catch (Exception error)
        {
            var code = error is TunnelException tunnel ? tunnel.Code : "room.tunnelUnavailable";
            problem = ApiErrors.Create(code, error is TunnelException known ? known.Message : "公网隧道启动或运行失败", "room-tunnel");
        }
        finally
        {
            try { stop.Cancel(); } catch (Exception error) { completionFailure = error; }
            var releaseFailure = await ReleaseAsync(process, [pumps, health, finding], pin);
            completionFailure ??= releaseFailure;
        }
        if (cancelled && completionFailure is null) ready.TrySetCanceled(stop.Token);
        else
        {
            problem ??= ApiErrors.Create("room.tunnelUnavailable", "公网隧道资源释放失败", "room-tunnel");
            var failed = new RoomTunnelSnapshot(RoomTunnelState.Failed, null, problem);
            try { await changed(failed); }
            catch (Exception error) { completionFailure ??= error; }
            finally { ready.TrySetResult(failed); }
        }
        if (completionFailure is not null) ExceptionDispatchInfo.Capture(completionFailure).Throw();
    }

    private static async Task<Exception?> ReleaseAsync(ITunnelProcess? process, IReadOnlyList<Task?> pending, FileStream? pin = null)
    {
        List<Exception> failures = [];
        if (process is not null)
        {
            try { process.Terminate(); } catch (Exception error) { failures.Add(error); }
            try { await process.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
        }
        foreach (var task in pending)
            if (task is not null)
                try { await task; }
                catch (Exception error) when (error is OperationCanceledException or IOException or TunnelException or ObjectDisposedException) { }
                catch (Exception error) { failures.Add(error); }
        if (pin is not null)
            try { await pin.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
        return failures.Count == 0 ? null : new AggregateException(failures);
    }

    private static async Task VerifyVersionAsync(TunnelComponentOptions component, ITunnelProcessFactory processes,
        TimeProvider time, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var process = processes.Start(component.ExecutablePath, ["--version"]);
        Task<string>? output = null, error = null;
        try
        {
            output = ReadVersionAsync(process.StandardOutput, attempt.Token);
            error = ReadVersionAsync(process.StandardError, attempt.Token);
            await Task.WhenAll(output, error).WaitAsync(attempt.Token);
            if (await process.WaitForExitAsync(attempt.Token) != 0 ||
                !Regex.IsMatch(output.Result, @"\Acloudflared version " + Regex.Escape(component.Version) + @"(?:\s|\z)"))
                throw new TunnelException("room.tunnelVersionMismatch", "隧道组件实际版本与配置不匹配");
        }
        finally
        {
            if (await ReleaseAsync(process, [output, error]) is { } failure)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
    private static async Task<string> ReadVersionAsync(TextReader reader, CancellationToken ct)
    {
        var buffer = new char[4096]; var result = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            if (result.Length + count > 8192) throw new TunnelException("room.tunnelOutputExceeded", "隧道版本输出超过预算");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    private static async Task PumpAsync(TextReader reader, ChannelWriter<Uri> candidates, CancellationToken ct)
    {
        var buffer = new char[4096]; var line = new StringBuilder(); int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            for (var at = 0; at < count; at++)
            {
                if (buffer[at] is '\r' or '\n')
                {
                    PublishCandidates(line.ToString(), candidates); line.Clear();
                }
                else
                {
                    if (line.Length >= 8192) throw new TunnelException("room.tunnelOutputExceeded", "隧道输出分块超过预算");
                    line.Append(buffer[at]);
                }
            }
        }
        if (line.Length != 0) PublishCandidates(line.ToString(), candidates);
    }
    private static void PublishCandidates(string line, ChannelWriter<Uri> candidates)
    {
        foreach (Match found in Regex.Matches(line,
            @"https://[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.trycloudflare\.com(?=\z|[\s/|""'])",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            var endpoint = new UriBuilder(found.Value) { Scheme = "wss", Path = "/ws", Port = 443 }.Uri;
            if (!candidates.TryWrite(endpoint)) throw new TunnelException("room.tunnelOutputExceeded", "隧道候选端点超过预算");
        }
    }
    private static async Task<Uri> FindEndpointAsync(ChannelReader<Uri> candidates, IRoomTunnelProbe probe,
        string epoch, TimeProvider time, CancellationToken ct)
    {
        var seen = new Dictionary<string, Uri>(StringComparer.Ordinal);
        while (true)
        {
            if (seen.Count == 0 && !await candidates.WaitToReadAsync(ct))
                throw new TunnelException("room.tunnelUnavailable", "隧道未提供匹配本次房间的端点");
            while (candidates.TryRead(out var candidate))
            {
                seen.TryAdd(candidate.AbsoluteUri, candidate);
                if (seen.Count > 4) throw new TunnelException("room.tunnelOutputExceeded", "隧道候选端点超过预算");
            }
            foreach (var endpoint in seen.Values)
                if (await probe.VerifyAsync(endpoint, epoch, ct)) return endpoint;
            await Task.Delay(TimeSpan.FromMilliseconds(500), time, ct);
        }
    }
    private static async Task MonitorHealthAsync(Uri endpoint, IRoomTunnelProbe probe, string epoch,
        TimeProvider time, TimeSpan interval, CancellationToken ct)
    {
        var failures = 0;
        while (failures < 3)
        {
            await Task.Delay(interval, time, ct);
            failures = await probe.VerifyAsync(endpoint, epoch, ct) ? 0 : failures + 1;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= CloseAsync());
    }
    private async Task CloseAsync()
    {
        stop.Cancel();
        try { await completion; }
        finally { stop.Dispose(); }
    }
}

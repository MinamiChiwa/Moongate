using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using MabiTool.Contracts;

namespace MabiTool.Host.Extensions;

/// <summary>
/// 带代码扩展的生命周期：启用即启动、停用即停止，宿主退出时全部停止；崩溃、挂死或超内存后按 1、5、15 秒退避重启，
/// 十分钟内超过 3 次即停止并写明原因；协议违规与主动停止不自动重启。所有启停经一个串行队列处理，状态转换不交错。
/// 每个扩展进程：工作目录为安装目录，环境变量只含白名单，Job 限制内存 256 MiB、CPU 25%、进程数 4，并禁止使用 Job 外窗口句柄。
/// </summary>
public sealed class ExtensionRuntime : IHostedService, IAsyncDisposable
{
    public const long MemoryLimitBytes = 256L * 1024 * 1024;
    public const int CpuPercent = 25, ProcessLimit = 4, MaximumRunning = 8, MaximumRestarts = 3;
    public static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(10);
    public static IReadOnlyList<TimeSpan> Backoff { get; set; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];

    private sealed class Slot(string id)
    {
        public string Id { get; } = id;
        public ExtensionSession? Session;
        public string State = "stopped";
        public string? Code, Reason;
        public readonly List<long> Crashes = [];
        public DateTimeOffset? NextStart;
        public CancellationTokenSource? Delay;
        public bool Failed;
    }

    private sealed record Work(string Id, string Kind, ExtensionSession? Session, string? Code, string? Reason, TaskCompletionSource Done);

    private readonly ExtensionInstaller installer;
    private readonly ExtensionCapabilities broker;
    private readonly TimeProvider time;
    private readonly Func<ExtensionLaunch, IExtensionProcess> launcher;
    private readonly Func<string?> dotnetHost;
    private readonly Dictionary<string, Slot> slots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExtensionRuntimeState> states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExtensionLog> logs = new(StringComparer.Ordinal);
    private readonly Channel<Work> queue = Channel.CreateUnbounded<Work>(new() { SingleReader = true });
    private readonly CancellationTokenSource stopping = new();
    private Task? loop;
    private volatile bool shuttingDown;

    public ExtensionRuntime(ExtensionInstaller installer, ExtensionCapabilities broker, TimeProvider time,
        Func<ExtensionLaunch, IExtensionProcess>? launcher = null, Func<string?>? dotnetHost = null)
    {
        this.installer = installer;
        this.broker = broker;
        this.time = time;
        this.launcher = launcher ?? (OperatingSystem.IsWindows() ? WindowsExtensionProcess.Start
            : _ => throw new ExtensionCallException("extension.runtimeUnavailable", "带代码的扩展需要 Windows"));
        this.dotnetHost = dotnetHost ?? DotnetHost;
    }

    /// <summary>某个扩展会话变化（启动、结束）时调用；界面注册表据此撤下已结束扩展的界面。</summary>
    public event Action<string>? SessionChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        installer.Changed += id => Enqueue(id, "reconcile");
        installer.BeforeReplace = (id, ct) => EnqueueAsync(id, "stop", "extension.replaced", "扩展正在升级或卸载");
        installer.RuntimeState = State;
        loop = Task.Run(LoopAsync, CancellationToken.None);
        foreach (var item in installer.Runnable()) Enqueue(item.Id, "reconcile");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        shuttingDown = true;
        await EnqueueAsync("*", "shutdown", "extension.hostStopping", "宿主退出");
        queue.Writer.TryComplete();
        await stopping.CancelAsync();
        if (loop is not null) await loop.WaitAsync(TimeSpan.FromSeconds(15)).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        if (!shuttingDown) await StopAsync(CancellationToken.None);
        stopping.Dispose();
    }

    public ExtensionRuntimeState? State(string id) => states.GetValueOrDefault(id);

    public ExtensionLog Log(string id) => logs.GetOrAdd(id, key => new ExtensionLog(installer.LogDirectory(key), time));

    /// <summary>运行中且已完成握手的会话；界面事件与快捷键通知经它转给扩展。</summary>
    public ExtensionSession? Running(string id)
    {
        lock (slots) return slots.TryGetValue(id, out var slot) && slot.Session is { Ready: true } session ? session : null;
    }

    public IReadOnlyList<ExtensionSession> RunningSessions()
    {
        lock (slots) return slots.Values.Select(x => x.Session).OfType<ExtensionSession>().Where(x => x.Ready).ToArray();
    }

    /// <summary>
    /// 扩展命令在快捷键设置里的动作：已安装扩展清单声明的全部命令，名称为“扩展名：命令名”；
    /// 扩展停用、未运行或未授予 ui.shortcut 时写明不可用原因，绑定保留但不注册。
    /// </summary>
    public IReadOnlyList<ShortcutActionSnapshot> ShortcutActions()
    {
        var result = new List<ShortcutActionSnapshot>();
        foreach (var (id, manifest, enabled, grants) in installer.Declared())
            foreach (var command in manifest.CommandItems)
            {
                var names = command.Labels.ToDictionary(x => x.Key, x => (manifest.DisplayNames.GetValueOrDefault(x.Key) ?? manifest.DisplayNames.Values.First()) + "：" + x.Value);
                var reason = !enabled ? "extension.disabled" : !grants.Contains("ui.shortcut") ? "extension.permissionDenied"
                    : Running(id) is null ? "extension.notRunning" : null;
                result.Add(new(ExtensionShortcuts.ActionId(id, command.Id), "extension." + id, names, "extension:" + id, reason));
            }
        return result;
    }

    /// <summary>扩展命令被按下：只在扩展运行中、已授予 ui.shortcut 且命令仍在清单里时通知扩展。</summary>
    public bool TriggerShortcut(string actionId)
    {
        var parts = actionId.Split('.');
        if (parts.Length != 4 || parts[0] != "extension") return false;
        var id = parts[1] + "." + parts[2];
        if (Running(id) is not { } session || !session.Grants.Contains("ui.shortcut") ||
            session.Extension.Manifest.CommandItems.All(x => x.Id != parts[3])) return false;
        _ = session.NotifyAsync("shortcut/triggered", new System.Text.Json.Nodes.JsonObject { ["command"] = parts[3], ["actionId"] = actionId });
        Log(id).Write("info", "host", "快捷键命令 " + parts[3] + " 已通知扩展");
        return true;
    }

    /// <summary>扩展命令的可用性变化（启停、授权、安装）时调用，由快捷键设置按保存的绑定重新注册。</summary>
    public Func<Task>? ShortcutsChanged { get; set; }
    private string shortcutSignature = string.Empty;

    private async Task NotifyShortcutsAsync()
    {
        if (ShortcutsChanged is not { } changed) return;
        var signature = string.Join("|", ShortcutActions().Select(x => x.ActionId + ":" + x.UnavailableReason));
        if (signature == shortcutSignature) return;
        shortcutSignature = signature;
        try { await changed(); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    /// <summary>用户请求重启：清除失败与重启计数，停止后按当前安装与授权重新启动。</summary>
    public Task RestartAsync(string id) => EnqueueAsync(id, "restart", "extension.restarting", "用户请求重启");

    /// <summary>按当前安装记录立即对齐运行状态并等待完成；授权撤销、停用与卸载在接口返回前已经生效。</summary>
    public Task ApplyAsync(string id) => EnqueueAsync(id, "reconcile", "extension.disabled", "扩展已停用");

    private void Enqueue(string id, string kind, ExtensionSession? session = null) =>
        queue.Writer.TryWrite(new(id, kind, session, null, null, new(TaskCreationOptions.RunContinuationsAsynchronously)));

    private Task EnqueueAsync(string id, string kind, string code, string reason)
    {
        var work = new Work(id, kind, null, code, reason, new(TaskCreationOptions.RunContinuationsAsynchronously));
        return queue.Writer.TryWrite(work) ? work.Done.Task : Task.CompletedTask;
    }

    private async Task LoopAsync()
    {
        await foreach (var work in queue.Reader.ReadAllAsync())
        {
            try
            {
                switch (work.Kind)
                {
                    case "shutdown":
                        Slot[] all;
                        lock (slots) all = slots.Values.ToArray();
                        foreach (var slot in all) slot.Delay?.Cancel();
                        await Task.WhenAll(all.Where(x => x.Session is not null).Select(x => StopSlotAsync(x, work.Code!, work.Reason!)));
                        break;
                    case "ended": await EndedAsync(work.Id, work.Session!); break;
                    case "stop": await StopSlotAsync(SlotOf(work.Id), work.Code!, work.Reason!); break;
                    case "restart":
                    {
                        var slot = SlotOf(work.Id);
                        slot.Crashes.Clear(); slot.Failed = false; slot.Delay?.Cancel(); slot.NextStart = null;
                        if (slot.Session is not null) await StopSlotAsync(slot, work.Code!, work.Reason!);
                        await ReconcileAsync(slot);
                        break;
                    }
                    default: await ReconcileAsync(SlotOf(work.Id)); break;
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Log(work.Id).Write("error", "host", "扩展启停失败：" + error.Message);
            }
            finally
            {
                if (!shuttingDown) await NotifyShortcutsAsync();
                work.Done.TrySetResult();
            }
        }
    }

    private Slot SlotOf(string id)
    {
        lock (slots)
        {
            if (!slots.TryGetValue(id, out var slot)) slots[id] = slot = new(id);
            return slot;
        }
    }

    private async Task ReconcileAsync(Slot slot)
    {
        if (shuttingDown) return;
        var target = installer.Runnable().FirstOrDefault(x => x.Id == slot.Id);
        if (target is null)
        {
            slot.Delay?.Cancel(); slot.NextStart = null; slot.Failed = false; slot.Crashes.Clear();
            var (code, reason) = StopReason(slot.Id);
            if (slot.Session is not null) await StopSlotAsync(slot, code, reason);
            Publish(slot, "stopped", code, reason);
            return;
        }
        if (slot.Session is { } session)
        {
            if (session.Extension.Directory != target.Directory || session.Extension.Version != target.Version)
            {
                await StopSlotAsync(slot, "extension.replaced", "扩展已更新，按新版本重新启动");
                await StartSlotAsync(slot, target);
            }
            else if (!session.Grants.SetEquals(target.Grants))
            {
                await session.UpdateGrantsAsync(target.Grants);
                Log(slot.Id).Write("info", "host", "授权已更新：" + (target.Grants.Count == 0 ? "无" : string.Join("、", target.Grants.Order(StringComparer.Ordinal))));
            }
            return;
        }
        if (slot.Failed || slot.Delay is { IsCancellationRequested: false }) return;
        await StartSlotAsync(slot, target);
    }

    private (string Code, string Reason) StopReason(string id)
    {
        try
        {
            var item = installer.Get(id, includePaths: false);
            if (!item.Enabled) return ("extension.disabled", "扩展已停用");
            if (item.State != "ready") return ("extension.installDamaged", item.Problem ?? "扩展已损坏");
            if (item.Development && !installer.DevelopmentMode) return ("extension.developmentModeOff", "开发模式已关闭");
            return ("extension.disabled", "扩展没有运行入口");
        }
        catch (MabiTool.Infrastructure.OperationException) { return ("extension.uninstalled", "扩展已卸载"); }
    }

    private async Task StartSlotAsync(Slot slot, RunnableExtension target)
    {
        lock (slots)
            if (slots.Values.Count(x => x.Session is not null) >= MaximumRunning)
            {
                Publish(slot, "failed", "extension.capacityExceeded", "同时运行的扩展已达 " + MaximumRunning + " 个");
                slot.Failed = true;
                return;
            }
        var log = Log(slot.Id);
        ExtensionLaunch launch;
        try { launch = Launch(target); }
        catch (ExtensionCallException error)
        {
            slot.Failed = true;
            log.Write("error", "host", error.Message);
            Publish(slot, "failed", error.Code, error.Message);
            return;
        }
        Publish(slot, "starting", null, null);
        log.Write("info", "host", "启动 " + target.Id + " " + target.Version + (target.Development ? "（开发模式）" : string.Empty));
        IExtensionProcess process;
        try { process = launcher(launch); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("error", "host", "无法启动扩展进程：" + error.Message);
            Crash(slot, new("extension.startFailed", "无法启动扩展进程：" + error.Message, true, null));
            return;
        }
        var session = new ExtensionSession(target, process, log, broker, time);
        slot.Session = session;
        _ = session.Completion.ContinueWith(_ => Enqueue(slot.Id, "ended", session), TaskScheduler.Default);
        if (await session.StartAsync())
        {
            slot.Code = null; slot.Reason = null;
            Publish(slot, "running", null, null);
            SessionChanged?.Invoke(slot.Id);
        }
    }

    private async Task StopSlotAsync(Slot slot, string code, string reason)
    {
        if (slot.Session is not { } session) return;
        Publish(slot, "stopping", code, reason);
        await session.StopAsync(code, reason);
        await FinishAsync(slot, session, await session.Completion);
    }

    private async Task EndedAsync(string id, ExtensionSession session)
    {
        var slot = SlotOf(id);
        if (!ReferenceEquals(slot.Session, session)) return;
        var end = await session.Completion;
        await FinishAsync(slot, session, end);
        if (end.Restartable && !shuttingDown) Crash(slot, end);
    }

    private async Task FinishAsync(Slot slot, ExtensionSession session, ExtensionSessionEnd end)
    {
        if (!ReferenceEquals(slot.Session, session)) return;
        slot.Session = null;
        await session.DisposeAsync();
        Publish(slot, end.Restartable || end.Code is "extension.stopped" or "extension.disabled" or "extension.uninstalled" or "extension.replaced"
            or "extension.restarting" or "extension.hostStopping" or "extension.developmentModeOff" or "extension.installDamaged" ? "stopped" : "failed",
            end.Code, end.Reason);
        if (!end.Restartable && states[slot.Id].State == "failed") slot.Failed = true;
        SessionChanged?.Invoke(slot.Id);
    }

    /// <summary>崩溃后按退避安排重启；窗口内次数用尽时停止并写明原因。</summary>
    private void Crash(Slot slot, ExtensionSessionEnd end)
    {
        var now = time.GetTimestamp();
        slot.Crashes.RemoveAll(x => time.GetElapsedTime(x, now) > RestartWindow);
        if (slot.Crashes.Count >= MaximumRestarts)
        {
            slot.Failed = true;
            Publish(slot, "failed", "extension.crashLoop", $"十分钟内连续异常 {MaximumRestarts + 1} 次，已停止自动重启（最后一次：{end.Reason}）");
            return;
        }
        var delay = Backoff[Math.Min(slot.Crashes.Count, Backoff.Count - 1)];
        slot.Crashes.Add(now);
        slot.NextStart = time.GetUtcNow() + delay;
        var cancel = slot.Delay = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        Publish(slot, "restarting", end.Code, end.Reason);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, time, cancel.Token); }
            catch (OperationCanceledException) { return; }
            slot.Delay = null; slot.NextStart = null;
            Enqueue(slot.Id, "reconcile");
        });
    }

    private void Publish(Slot slot, string state, string? code, string? reason)
    {
        slot.State = state;
        if (code is not null) { slot.Code = code; slot.Reason = reason; }
        var session = slot.Session;
        states[slot.Id] = new(state, slot.Code, slot.Reason, state == "running" ? session?.ProcessId : null,
            state == "running" ? session?.StartedAt : null, slot.Crashes.Count, slot.NextStart);
    }

    /// <summary>
    /// 按清单入口确定程序与参数。程序与脚本都必须是安装目录内的普通文件（不跟随链接）；
    /// dotnet 入口使用宿主自身所用的 .NET 运行时，不搜索 PATH 或其他系统位置。
    /// </summary>
    private ExtensionLaunch Launch(RunnableExtension target)
    {
        var runtime = target.Manifest.Runtime!;
        string Resolve(string relative)
        {
            var path = Path.GetFullPath(Path.Combine([target.Directory, .. relative.Split('/')]));
            if (!path.StartsWith(Path.TrimEndingDirectorySeparator(target.Directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new ExtensionCallException("extension.entryMissing", "运行入口文件不存在或不是普通文件：" + relative);
            return path;
        }
        var (executable, arguments) = runtime.Kind switch
        {
            "executable" => (Resolve(runtime.Entry), runtime.Arguments.ToArray()),
            "script" => (Resolve(runtime.Interpreter!), [Resolve(runtime.Entry), .. runtime.Arguments]),
            "dotnet" => (dotnetHost() ?? throw new ExtensionCallException("extension.runtimeUnavailable", "找不到工具所用的 .NET 运行时"),
                (string[])["exec", Resolve(runtime.Entry), .. runtime.Arguments]),
            _ => throw new ExtensionCallException("extension.entryMissing", "未知的运行方式 " + runtime.Kind)
        };
        var data = installer.DataDirectory(target.Id);
        var temp = installer.TempDirectory(target.Id);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(temp);
        var system = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = system, ["windir"] = system,
            ["SystemDrive"] = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:",
            ["PATH"] = Path.Combine(system, "System32") + ";" + system,
            ["TEMP"] = temp, ["TMP"] = temp,
            ["NUMBER_OF_PROCESSORS"] = Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PROCESSOR_ARCHITECTURE"] = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "ARM64" : "AMD64",
            ["MOONGATE_EXTENSION_ID"] = target.Id, ["MOONGATE_EXTENSION_VERSION"] = target.Version,
            ["MOONGATE_EXTENSION_DATA"] = data, ["MOONGATE_EXTENSION_PROTOCOL"] = ExtensionProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_NOLOGO"] = "1"
        };
        return new(executable, arguments, target.Directory, environment, MemoryLimitBytes, CpuPercent, ProcessLimit);
    }

    /// <summary>宿主所用 .NET 运行时的 dotnet.exe：宿主由 dotnet.exe 启动时取进程路径，否则由共享框架目录上溯三级。</summary>
    private static string? DotnetHost()
    {
        if (Environment.ProcessPath is { } path && Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return path;
        var framework = RuntimeEnvironment.GetRuntimeDirectory();
        var candidate = Path.GetFullPath(Path.Combine(framework, "..", "..", "..", "dotnet.exe"));
        return File.Exists(candidate) ? candidate : null;
    }
}

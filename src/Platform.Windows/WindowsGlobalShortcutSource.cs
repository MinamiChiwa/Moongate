using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Runtime.CompilerServices;
using MabiTool.ModuleSdk;

[assembly: InternalsVisibleTo("MabiTool.Host.Tests")]

namespace MabiTool.Platform.Windows;

/// <summary>注册与消息接收共用专用 Windows 线程，停止时完整注销。</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGlobalShortcutSource : IGlobalShortcutSource
{
    private readonly object gate = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ShortcutRequest[] requests;
    private readonly ConcurrentQueue<ConfigurationCommand> pending = new();
    private readonly SemaphoreSlim reconfigurationSlots = new(MaximumReconfigurations, MaximumReconfigurations);
    private readonly IGlobalShortcutRegistrar registrar;
    public const int MaximumReconfigurations = 2;
    public const string CapacityErrorCode = "resource.capacityExceeded";
    private const uint ConfigureMessage = 0x8001;
    private IReadOnlyList<ShortcutRegistrationResult> registrations = [];
    private Thread? thread;
    private uint threadId;
    private bool stopping;
    public bool CanReconfigure => true;
    public event Action<string>? ActionReceived;

    public WindowsGlobalShortcutSource(IEnumerable<ShortcutRequest>? requests = null) : this(requests, new NativeRegistrar()) { }
    internal WindowsGlobalShortcutSource(IEnumerable<ShortcutRequest>? requests, IGlobalShortcutRegistrar registrar)
    { this.requests = Freeze(requests ?? []); this.registrar = registrar; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (thread is null)
            {
                thread = new Thread(ReadMessages) { Name = "MabiTool shortcuts", IsBackground = true };
                thread.Start();
            }
        }
        return ready.Task.WaitAsync(cancellationToken);
    }

    public IReadOnlyList<ShortcutRegistrationResult> GetRegistrations()
    {
        lock (gate) return registrations.ToArray();
    }

    public async Task<IReadOnlyList<ShortcutRegistrationResult>> ReconfigureAsync(IReadOnlyList<ShortcutRequest> values, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        var changes = Freeze(values);
        var combined = requests.Where(value => !changes.Any(change => change.ActionId == value.ActionId)).Concat(changes).ToArray();
        GlobalShortcutRegistry.ValidateRequests(combined);
        cancellationToken.ThrowIfCancellationRequested();
        if (!reconfigurationSlots.Wait(0)) throw new InvalidOperationException(CapacityErrorCode);
        try
        {
            await StartAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var command = new ConfigurationCommand(combined, cancellationToken);
            lock (gate)
            {
                if (stopping || finished.Task.IsCompleted) throw new ObjectDisposedException(nameof(WindowsGlobalShortcutSource));
                if (!PostThreadMessage(threadId, ConfigureMessage, 0, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                pending.Enqueue(command);
            }
            // 入队后等待注册结果，取消的命令仍占预算直至消息线程取出。
            return await command.Completion.Task;
        }
        finally { reconfigurationSlots.Release(); }
    }

    private bool TryTake(out ConfigurationCommand command)
    { lock (gate) return pending.TryDequeue(out command!); }

    private static ShortcutRequest[] Freeze(IEnumerable<ShortcutRequest> values)
    {
        var input = values.ToArray();
        if (input.Any(value => value is null || value.Preferred is null || value.Alternatives is null))
            throw new ArgumentException("shortcut.requestInvalid", nameof(values));
        var snapshot = input.Select(value => value with { Alternatives = value.Alternatives.ToArray() }).ToArray();
        GlobalShortcutRegistry.ValidateRequests(snapshot);
        return snapshot;
    }

    private void ReadMessages()
    {
        try
        {
            // 建立线程消息队列之后才能接受退出消息。
            PeekMessage(out _, 0, 0, 0, 0);
            threadId = GetCurrentThreadId();
            using var registry = new GlobalShortcutRegistry(registrar);
            var result = registry.Configure(requests);
            lock (gate) registrations = result;
            ready.TrySetResult();
            int status;
            while ((status = GetMessage(out var message, 0, 0, 0)) > 0)
            {
                if (message.Message == ConfigureMessage)
                {
                    while (TryTake(out var command))
                    {
                        if (command.Completion.Task.IsCompleted) continue;
                        lock (gate)
                        {
                            if (stopping)
                            { command.Completion.TrySetException(new ObjectDisposedException(nameof(WindowsGlobalShortcutSource))); continue; }
                        }
                        if (command.Token.IsCancellationRequested) { command.Completion.TrySetCanceled(command.Token); continue; }
                        try
                        {
                            var configured = registry.Configure(command.Requests);
                            lock (gate) registrations = configured;
                            command.Completion.TrySetResult(configured);
                        }
                        catch (Exception error) { command.Completion.TrySetException(error); }
                    }
                    continue;
                }
                if (message.Message != 0x0312 || !registry.TryGetAction(checked((int)message.WParam), out var action)) continue;
                // 一个执行者的释放异常不能终止中央消息源。
                foreach (var handler in ActionReceived?.GetInvocationList() ?? [])
                    try { ((Action<string>)handler)(action!); }
                    catch { }
            }
            if (status < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        catch (Exception error) { ready.TrySetException(error); }
        finally
        {
            lock (gate)
            {
                stopping = true;
                registrations = registrations.Select(value => value with
                    { Registered = null, Status = ShortcutRegistrationStatus.Unavailable, FailureCode = "shortcut.stopped" }).ToArray();
            }
            while (TryTake(out var command)) command.Completion.TrySetException(new ObjectDisposedException(nameof(WindowsGlobalShortcutSource)));
            finished.TrySetResult();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (gate) { stopping = true; if (thread is null) return; }
        try { await ready.Task.WaitAsync(cancellationToken); }
        catch when (ready.Task.IsFaulted) { }
        if (!finished.Task.IsCompleted && !PostThreadMessage(threadId, 0x0012, 0, 0) && !finished.Task.IsCompleted)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        await finished.Task.WaitAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    private sealed class NativeRegistrar : IGlobalShortcutRegistrar
    {
        public bool TryRegister(int registrationId, ShortcutChord chord, out string? failureCode)
        {
            var flags = 0x4000u | (chord.Alt ? 1u : 0) | (chord.Control ? 2u : 0) |
                (chord.Shift ? 4u : 0) | (chord.Windows ? 8u : 0);
            var key = checked((uint)ShortcutKeys.VirtualKey(chord.Key));
            var registered = RegisterHotKey(0, registrationId, flags, key);
            failureCode = registered ? null : Marshal.GetLastPInvokeError() == 1409 ? "shortcut.inUse" : "shortcut.registrationFailed";
            return registered;
        }
        public void Unregister(int registrationId) => UnregisterHotKey(0, registrationId);
    }

    private sealed record ConfigurationCommand(ShortcutRequest[] Requests, CancellationToken Token)
    {
        public TaskCompletionSource<IReadOnlyList<ShortcutRegistrationResult>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window; public uint Message; public nuint WParam; public nint LParam;
        public uint Time; public int X, Y; public uint Private;
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)] private static extern int GetMessage(out NativeMessage message, nint window, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out NativeMessage message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint id, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint window, int id, uint flags, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(nint window, int id);
}

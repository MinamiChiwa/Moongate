using MabiTool.Contracts;
using MabiTool.Host.Color;
using MabiTool.Host.Game;
using MabiTool.Host.Rooms;
using MabiTool.ModuleSdk;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Shortcuts;

public sealed class ShortcutService(ColorRunOperations color, IGlobalShortcutSource? source = null,
    GameControlContext? controls = null, RoomSessionManager? rooms = null, ShortcutSettingsService? settings = null,
    GameControlService? gameControl = null, StateEventHub? events = null, TimeProvider? clock = null,
    ILogger<ShortcutService>? logger = null) : IHostedService, IDisposable
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Channel<ShortcutTriggered> notifications = Channel.CreateBounded<ShortcutTriggered>(new BoundedChannelOptions(32)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource notificationStop = new();
    private Task dispatch = Task.CompletedTask;
    private int running;
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (source is null) { if (settings is not null) await settings.RestoreAsync(cancellationToken); return; }
        source.ActionReceived += OnAction;
        Volatile.Write(ref running, 1);
        dispatch = DispatchAsync(notificationStop.Token);
        try
        {
            await source.StartAsync(cancellationToken);
            if (settings is not null) await settings.RestoreAsync(cancellationToken);
        }
        catch { await StopAsync(CancellationToken.None); throw; }
    }
    private void OnAction(string action)
    {
        if (Volatile.Read(ref running) == 0) return;
        if (action != "emergency-stop")
        {
            if (!ShortcutSettingsStore.IsFrontendAction(action)) return;
            Notify(action);
            return;
        }
        try
        {
            if (gameControl is not null) { gameControl.StopLocal(); return; }
            try { rooms?.EmergencyStop(); }
            finally { try { controls?.Input.EmergencyStop(); } finally { color.EmergencyStop(); } }
        }
        // 后端已完成停止；再通知在线前端，由它取消进行中的定时测量并在界面说明。
        finally { Notify(action); }
    }

    private void Notify(string action)
    {
        if (events is null) return;
        var registration = source?.GetRegistrations().FirstOrDefault(value => value.ActionId == action &&
            value.Status == ShortcutRegistrationStatus.Registered);
        if (registration?.Registered is not { } chord) return;
        var now = time.GetUtcNow();
        notifications.Writer.TryWrite(new(action, Chord(chord), now, now.AddSeconds(2)));
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (source is null) return;
        Volatile.Write(ref running, 0);
        source.ActionReceived -= OnAction;
        try { await source.StopAsync(cancellationToken); }
        finally
        {
            notificationStop.Cancel(); notifications.Writer.TryComplete();
            await dispatch;
            while (notifications.Reader.TryRead(out _)) { }
        }
    }
    private async Task DispatchAsync(CancellationToken token)
    {
        try
        {
            await foreach (var value in notifications.Reader.ReadAllAsync(token))
            {
                if (Volatile.Read(ref running) == 0 || value.ExpiresAt <= time.GetUtcNow()) continue;
                try { if (events is not null) await events.PublishShortcutAsync(value, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception error) { logger?.LogError(error, "快捷键触发通知发布失败"); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public void Dispose() => notificationStop.Dispose();
    public ApiList<GlobalShortcutSnapshot> Snapshot()
    {
        var items = (source?.GetRegistrations() ?? []).Select(value => new GlobalShortcutSnapshot(value.ActionId, Chord(value.Configured),
            value.Registered is { } key ? Chord(key) : null,
            value.Status == ShortcutRegistrationStatus.Registered ? "registered" : "unavailable", value.FailureCode, value.UsedFallback)).ToArray();
        return new(items, null, "shortcuts:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(items, ContractJson.Options))));
    }
    private static ShortcutChordSnapshot Chord(ShortcutChord chord) => new(chord.Key.ToString(), chord.Control, chord.Alt, chord.Shift, chord.Windows);
    public static bool Conflicts(InputBindingPreference binding, IGlobalShortcutSource? shortcuts)
    {
        if (binding.Kind.KnownValue != LocalBindingKind.Key) return false;
        var registered = (shortcuts?.GetRegistrations() ?? []).Select(x => x.Registered).OfType<ShortcutChord>()
            .Append(new(ShortcutKey.F9));
        return registered.Any(chord => !chord.Windows && binding.Control == chord.Control && binding.Alt == chord.Alt && binding.Shift == chord.Shift &&
            binding.Key == ShortcutKeys.VirtualKey(chord.Key));
    }
}

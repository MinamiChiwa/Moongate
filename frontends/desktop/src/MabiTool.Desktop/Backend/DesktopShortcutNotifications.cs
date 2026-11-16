using System.Globalization;
using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Desktop.Backend;

/// <summary>在线动作通知：连接隔离、递增水位去重，以及有界的 UI 投递。</summary>
internal sealed class DesktopShortcutNotifications(Action<Action> dispatch, Action<string> trigger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Lock gate = new();
    private string? epoch;
    private ulong watermark;
    private long connection;
    private bool connected;
    private CancellationToken cancellation;
    private int pending;

    internal long Connect(string backendEpoch, CancellationToken token)
    {
        lock (gate)
        {
            if (epoch != backendEpoch) watermark = 0;
            epoch = backendEpoch;
            cancellation = token;
            connected = true;
            return ++connection;
        }
    }

    internal void Disconnect(long generation)
    {
        lock (gate)
            if (connection == generation) connected = false;
    }

    internal void Receive(long generation, StreamMessage message)
    {
        if (message.Event != "shortcut.triggered") return;
        EventEnvelope<ShortcutTriggered>? envelope;
        try { envelope = message.Data.Deserialize<EventEnvelope<ShortcutTriggered>>(ContractJson.Options); }
        catch (JsonException) { return; }
        if (envelope is not { Type: "shortcut.triggered", SchemaVersion: 1, Module: "presentation", Data: { } value } ||
            value.ActionId is not ("overlay-toggle" or "overlay-click-through-toggle" or "combat-measurement-toggle" or "emergency-stop") || value.Chord is null ||
            envelope.ResourceId != value.ActionId || !SameEvent(envelope.EventId, message.Id) ||
            value.ExpiresAt - value.TriggeredAt != TimeSpan.FromSeconds(2)) return;

        lock (gate)
        {
            var prefix = epoch + ":";
            if (!Current(generation, value) || envelope.EventId is null ||
                !envelope.EventId.StartsWith(prefix, StringComparison.Ordinal) ||
                !ulong.TryParse(envelope.EventId.AsSpan(prefix.Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var sequence) || sequence <= watermark) return;
            watermark = sequence;
            if (pending >= 32) return;
            pending++;
        }
        var invoked = false;
        try
        {
            dispatch(() =>
            {
                lock (gate)
                {
                    invoked = true;
                    pending--;
                    if (Current(generation, value)) trigger(value.ActionId);
                }
            });
        }
        catch
        {
            lock (gate)
                if (!invoked) pending--;
            throw;
        }
    }

    /// <summary>
    /// SSE 的 <c>id</c> 是宿主的续传游标“epoch:序号:订阅绑定”（<c>EventStreamEndpoints</c> 写 <c>subscription.Cursor</c>），
    /// 信封的 <c>eventId</c> 是“epoch:序号”。两者的 epoch 与序号相同才是同一条通知。此前要求两者完全相等，
    /// 真实宿主发来的 F7、F8 通知因此全部被丢弃（单元测试用的消息 id 与信封相同，没有暴露）。
    /// </summary>
    internal static bool SameEvent(string? eventId, string? streamId) => eventId is { Length: > 0 } && streamId is not null &&
        (streamId == eventId || streamId.StartsWith(eventId + ":", StringComparison.Ordinal));

    private bool Current(long generation, ShortcutTriggered value) => connected && connection == generation &&
        !cancellation.IsCancellationRequested && value.TriggeredAt <= clock.GetUtcNow() && value.ExpiresAt > clock.GetUtcNow();
}

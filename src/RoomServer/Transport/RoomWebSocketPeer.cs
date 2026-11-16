using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using MabiTool.RoomServer.Domain;

namespace MabiTool.RoomServer.Transport;

internal sealed class RoomWebSocketPeer(WebSocket socket, RoomServerOptions options, RoomSendBudget budget) : IRoomPeer
{
    private readonly Channel<byte[]> outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.MaximumQueuedFrames)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource lifetime = new();
    private long queuedBytes;
    private int closing;
    private int closeStatus = 1000;
    private string closeReason = "Closed";
    public bool Open => Volatile.Read(ref closing) == 0 && socket.State == WebSocketState.Open;

    public void Send(byte[] bytes)
    {
        if (!Open) return;
        if (!budget.Reserve(bytes.Length)) { AbortSlowConnection(); return; }
        if (!RoomSendBudget.Reserve(ref queuedBytes, bytes.Length, options.MaximumConnectionQueueBytes))
        { budget.Release(bytes.Length); AbortSlowConnection(); return; }
        if (!outgoing.Writer.TryWrite(bytes)) { Release(bytes); AbortSlowConnection(); }
    }
    public void Close(int status, string reason)
    {
        if (Interlocked.Exchange(ref closing, 1) != 0) return;
        closeStatus = status; closeReason = reason; outgoing.Writer.TryComplete();
    }
    private void AbortSlowConnection()
    {
        Close(4008, "Slow connection"); lifetime.Cancel(); socket.Abort();
    }
    private void Release(byte[] bytes) { Interlocked.Add(ref queuedBytes, -bytes.Length); budget.Release(bytes.Length); }

    internal async Task RunAsync(RoomKernel kernel, CancellationToken stopped)
    {
        using var cancellation = stopped.Register(() => { lifetime.Cancel(); socket.Abort(); });
        var connection = kernel.Connect(this);
        if (connection is null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await socket.CloseOutputAsync((WebSocketCloseStatus)1013, "Server busy", timeout.Token); }
            catch (WebSocketException) { }
            lifetime.Dispose(); return;
        }
        var sending = SendLoop();
        try { await ReceiveLoop(kernel, connection); }
        catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException) { }
        finally
        {
            Close(1000, "Closed");
            try { await sending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) when (error is TimeoutException or WebSocketException or IOException or OperationCanceledException)
            { lifetime.Cancel(); socket.Abort(); }
            try { await sending; } catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException) { }
            while (outgoing.Reader.TryRead(out var pending)) Release(pending);
            kernel.Disconnect(connection); lifetime.Dispose();
        }
    }
    private async Task SendLoop()
    {
        try
        {
            await foreach (var bytes in outgoing.Reader.ReadAllAsync(lifetime.Token))
            {
                try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, lifetime.Token); }
                finally { Release(bytes); }
            }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await socket.CloseOutputAsync((WebSocketCloseStatus)closeStatus, closeReason, timeout.Token);
            }
        }
        finally { lifetime.Cancel(); }
    }
    private async Task ReceiveLoop(RoomKernel kernel, RoomConnection connection)
    {
        var buffer = new byte[8192];
        while (!lifetime.IsCancellationRequested)
        {
            using var content = new MemoryStream(); ValueWebSocketReceiveResult message;
            do
            {
                message = await socket.ReceiveAsync(buffer.AsMemory(), lifetime.Token);
                if (message.MessageType == WebSocketMessageType.Close) return;
                if (message.MessageType != WebSocketMessageType.Text) { Close(1003, "Text frames required"); return; }
                if (content.Length + message.Count > options.MaximumFrameBytes) { Close(1009, "Message too large"); return; }
                content.Write(buffer, 0, message.Count);
            } while (!message.EndOfMessage);
            var receivedAt = kernel.Now;
            try
            {
                using var document = JsonDocument.Parse(content.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
                kernel.Receive(connection, document.RootElement, receivedAt);
            }
            catch (JsonException) { kernel.Receive(connection, default, receivedAt); }
        }
    }
}

using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using Microsoft.AspNetCore.Http.Features;

namespace MabiTool.Host;

internal static class EventStreamEndpoints
{
    private static StateEventHub Hub(HttpContext context) => context.RequestServices.GetService<StateEventHub>()
        ?? throw new OperationException("storage.unavailable", "当前嵌入式宿主未配置持久存储");

    private static string? Header(HttpContext context, string name)
    {
        var values = context.Request.Headers[name];
        if (values.Count > 1 || values.ToString().Length > 8192)
            throw new OperationException("request.invalid", "事件请求头无效：" + name);
        return values.Count == 0 ? null : values.ToString();
    }
    private static EventFilter Filter(HttpContext context)
    {
        static string[]? Parse(string? value) => value?.Split(',', StringSplitOptions.TrimEntries);
        return EventFilter.Create(Parse(Header(context, "X-Event-Topics")), Parse(Header(context, "X-Event-Resources")));
    }
    public static Task<ProjectionSnapshot> SnapshotAsync(HttpContext context)
    {
        if (Header(context, "Last-Event-ID") is not null)
            throw new OperationException("request.invalid", "快照请求不接受续传游标");
        return Hub(context).SnapshotAsync(LocalApiMiddleware.Session(context), Filter(context), context.RequestAborted);
    }
    public static async Task StreamAsync(HttpContext context)
    {
        var hub = Hub(context);
        var session = LocalApiMiddleware.Session(context);
        await using var subscription = await hub.SubscribeAsync(session, Filter(context), Header(context, "Last-Event-ID"), context.RequestAborted);
        var auth = context.RequestServices.GetRequiredService<LocalAuthService>();
        var lifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        var ct = stopped.Token;
        var token = context.Request.Headers.Authorization.ToString()[7..];
        var origin = context.Request.Headers.Origin.Count == 0 ? null : context.Request.Headers.Origin.ToString();
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        async Task Write(string frame)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await context.Response.WriteAsync(frame, deadline.Token);
            await context.Response.Body.FlushAsync(deadline.Token);
        }
        Task Control(string code, string reason) => Write("event: control\ndata: " + JsonSerializer.Serialize(new StreamControl(code, reason), ContractJson.Options) + "\n\n");
        Task Deliver(EventDelivery item) => Write("event: " + item.Type + "\nid: " + subscription.Cursor(item) + "\ndata: " + item.Json + "\n\n");
        try
        {
            await Write(": connected\n\n");
            if (subscription.InitialSnapshot is { } snapshot)
                await Write("event: snapshot\nid: " + snapshot.EventCursor + "\ndata: " + JsonSerializer.Serialize(snapshot, ContractJson.Options) + "\n\n");
            var heartbeatAt = Environment.TickCount64 + 15000;
            // 一个待处理的读取等待与周期检查共用，避免每次心跳累积读取任务。
            Task<bool>? pending = null;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            var tick = timer.WaitForNextTickAsync(ct).AsTask();
            var replayIndex = 0;
            while (!ct.IsCancellationRequested)
            {
                if (auth.Authenticate(token, origin) is null)
                { await Control("auth.required", "访问凭据已失效"); break; }
                if (subscription.NeedsResync)
                { await Control("events.resyncRequired", "连接消费超过队列预算，请重新读取快照"); break; }
                if (replayIndex < subscription.Replay.Count)
                { await Deliver(subscription.Replay[replayIndex++]); continue; }
                subscription.ReleaseReplay();
                if (subscription.TryRead(out var item)) { await Deliver(item!); continue; }
                pending ??= subscription.WaitAsync(ct);
                await Task.WhenAny(pending, tick);
                if (tick.IsCompleted)
                {
                    if (!await tick) break;
                    tick = timer.WaitForNextTickAsync(ct).AsTask();
                    if (Environment.TickCount64 >= heartbeatAt)
                    { await Write(": heartbeat\n\n"); heartbeatAt = Environment.TickCount64 + 15000; }
                }
                if (pending.IsCompleted) { if (!await pending && !subscription.NeedsResync) break; pending = null; }
            }
        }
        catch (OperationCanceledException) { if (!ct.IsCancellationRequested) context.Abort(); }
        catch (IOException) { context.Abort(); }
        finally { await stopped.CancelAsync(); }
    }
}

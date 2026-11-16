using System.Net;
using MabiTool.RoomServer.Domain;
using MabiTool.RoomServer.Transport;

namespace MabiTool.RoomServer;

public static class RoomServerApplication
{
    public static WebApplication Build(RoomServerOptions? options = null, TimeProvider? time = null)
    {
        options ??= new(); options.Validate(); var configured = options;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        { server.Listen(IPAddress.Parse(configured.Host), configured.Port); server.Limits.MaxConcurrentConnections = configured.MaximumConnections; });
        builder.Services.AddSingleton(_ => new RoomKernel(configured, time));
        builder.Services.AddSingleton(new RoomSendBudget(configured.MaximumQueuedBytes));
        var application = builder.Build();
        application.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(15) });
        application.MapGet("/health", (RoomKernel kernel) => Results.Json(kernel.Health()));
        async Task HandleSocket(HttpContext context, RoomKernel kernel, RoomSendBudget budget)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            { context.Response.StatusCode = 404; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await new RoomWebSocketPeer(socket, configured, budget).RunAsync(kernel, application.Lifetime.ApplicationStopping);
        }
        application.Map("/", HandleSocket);
        application.Map("/ws", HandleSocket);
        return application;
    }
    public static string ServerEpoch(WebApplication application) => application.Services.GetRequiredService<RoomKernel>().Epoch;
}

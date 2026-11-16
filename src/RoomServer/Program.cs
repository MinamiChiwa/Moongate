using System.Text.Json;
using MabiTool.RoomServer;

try
{
    await using var application = RoomServerApplication.Build(RoomServerOptions.FromArguments(args));
    await application.StartAsync();
    var endpoint = new Uri(application.Urls.Single());
    var host = endpoint.Host is "0.0.0.0" or "[::]" ? "127.0.0.1" : endpoint.Host;
    Console.WriteLine(JsonSerializer.Serialize(new { url = $"ws://{host}:{endpoint.Port}", port = endpoint.Port,
        serverEpoch = RoomServerApplication.ServerEpoch(application) }));
    await application.WaitForShutdownAsync();
}
catch (Exception error) when (error is ArgumentException or IOException)
{
    Console.Error.WriteLine(error.Message); Environment.ExitCode = 2;
}

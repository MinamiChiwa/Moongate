using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.FrontendExamples;
using MabiTool.ModuleSdk;

try
{
    if (args.Length is < 1 or > 2) throw new ArgumentException("用法：<instance.json> [normal|partial|failure]");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var handshake = await LocalBootstrapClient.ConnectAsync(args[0], "dotnet-replay-example", deadline.Token);
    using var client = new MabiToolClient(new(handshake.ApiBaseUri), _ => ValueTask.FromResult(handshake.Grant.Token));
    var result = await ReplayExample.RunAsync(client, args.Length == 2 ? args[1] : "normal", onState: observed =>
        Console.WriteLine(JsonSerializer.Serialize(new { source = observed.Source, quality = observed.Quality, state = observed.Value }, ContractJson.Options)),
        cancellationToken: deadline.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { operationId = result.Operation.Id, state = result.Operation.State,
        problemCode = result.Operation.Problem?.Code, result.Disconnected, result.Recoveries, result.Steps }, ContractJson.Options));
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or HttpRequestException or
    MabiToolApiException or JsonException or OperationCanceledException)
{
    Console.Error.WriteLine(error is MabiToolApiException api ? api.Problem?.Code ?? $"http.{api.Status}" : error.Message);
    Environment.ExitCode = 1;
}

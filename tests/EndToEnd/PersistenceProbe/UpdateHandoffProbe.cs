using System.Diagnostics;
using System.Text.Json;
using MabiTool.Infrastructure.Updates;

internal static class UpdateHandoffProbe
{
    private sealed record Input(UpdatePeerIdentity Parent, string TransactionId, string Mode);
    private sealed record Payload(string Value);

    internal static async Task<int> RunAsync(string pipe, string inputPath, string resultPath)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        try
        {
            var input = JsonSerializer.Deserialize<Input>(File.ReadAllBytes(inputPath), UpdateHandoffProtocol.Json)!;
            using var startDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(inputPath + ".go")) await Task.Delay(10, startDeadline.Token);
            using var own = Process.GetCurrentProcess();
            var expected = input.Mode == "wrong-server" ? UpdatePeerIdentity.Capture(own) : input.Parent;
            using var connection = await UpdateHandoffConnection.ConnectAsync(pipe, expected, input.TransactionId);
            await connection.SendAsync("accepted", new Payload("child"));
            var response = await connection.ReceiveAsync<Payload>("healthCheck");
            File.WriteAllText(resultPath + ".tmp", JsonSerializer.Serialize(new { response.Value, connection.Peer.ProcessId }));
            File.Move(resultPath + ".tmp", resultPath);
            // 父进程完成双向复核后才允许退出，避免将已退出的对端当作有效回执。
            _ = await connection.ReceiveAsync<Payload>("abort");
            try { _ = await connection.ReceiveAsync<Payload>("abort"); }
            catch (EndOfStreamException) { return 0; }
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(resultPath, error is UpdatePlanException plan ? plan.Code : error.GetType().Name);
            return 3;
        }
    }
}

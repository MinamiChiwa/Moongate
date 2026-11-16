using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;

namespace MabiTool.FrontendExamples;

public sealed record ReplayExampleResult(OperationSnapshot Operation, IReadOnlyList<int> Steps, bool Disconnected, int Recoveries);

public static class ReplayExample
{
    public static async Task<ReplayExampleResult> RunAsync(MabiToolClient client, string scenario = "normal", int intervalMs = 500,
        Action<Observation<ReplayState>>? onState = null, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30)); var token = deadline.Token;
        if (!(await client.GetReplayCatalogAsync(token)).Enabled) throw new InvalidOperationException("请连接 --mock 后端");
        var started = await client.StartReplayAsync(new(scenario, intervalMs), idempotencyKey ?? Guid.NewGuid().ToString("N"), token);
        var filter = new EventStreamOptions(["replay.observation-changed", "operation.changed"], [started.Id], null);
        var steps = new List<int>(); string? cursor = null; OperationSnapshot? result = null;
        var disconnected = false; var recoveries = 0; var lastStep = 0;
        void Accept(string type, JsonElement data)
        {
            if (type == "operation.changed")
            {
                var operation = data.Deserialize<OperationSnapshot>(ContractJson.Options)!;
                if (!Terminal(operation)) return;
                if (operation.Result is { } observed) Accept("replay.observation-changed", observed);
                result = operation;
            }
            else if (type == "replay.observation-changed")
            {
                var observed = data.Deserialize<Observation<ReplayState>>(ContractJson.Options)!;
                if (observed.Value.OperationId != started.Id || observed.Value.Step <= lastStep) return;
                lastStep = observed.Value.Step; steps.Add(lastStep); onState?.Invoke(observed);
            }
        }
        void Snapshot(ProjectionSnapshot snapshot)
        {
            foreach (var resource in snapshot.Resources) Accept(resource.Type, resource.Data);
        }
        while (result is null)
        {
            try
            {
                if (cursor is null)
                {
                    var snapshot = await client.GetEventSnapshotAsync(filter, token);
                    Snapshot(snapshot); cursor = snapshot.EventCursor;
                    var current = await client.GetOperationAsync(started.Id, token);
                    if (Terminal(current)) Accept("operation.changed", JsonSerializer.SerializeToElement(current, ContractJson.Options));
                    if (result is not null) break;
                }
                var reconnect = false;
                await foreach (var message in client.StreamEventsAsync(filter with { LastEventId = cursor }, token))
                {
                    if (message.Event == "control")
                    {
                        var control = message.Data.Deserialize<StreamControl>(ContractJson.Options)!;
                        if (control.Code != "events.resyncRequired") throw new InvalidDataException(control.Code);
                        if (++recoveries > 3) throw new IOException("重同步次数已达上限");
                        cursor = null; reconnect = true; break;
                    }
                    if (message.Event == "snapshot") Snapshot(message.Data.Deserialize<ProjectionSnapshot>(ContractJson.Options)!);
                    else Accept(message.Event, message.Data.GetProperty("data"));
                    // 应用观测后再推进游标，界面重连不会重复应用已消费步序。
                    cursor = message.Id;
                    if (result is not null) break;
                    if (!disconnected && message.Event == "replay.observation-changed")
                    { disconnected = true; reconnect = true; break; }
                }
                if (result is null && !reconnect) throw new EndOfStreamException("事件连接已结束");
            }
            catch (MabiToolApiException error) when (error.Problem?.Code == "events.resyncRequired")
            {
                cursor = null;
                if (++recoveries > 3) throw;
            }
            catch (Exception error) when (error is HttpRequestException or EndOfStreamException && !token.IsCancellationRequested)
            {
                if (++recoveries > 3) throw;
                await Task.Delay(TimeSpan.FromMilliseconds(100 * recoveries), token);
            }
        }
        return new(result, steps.ToArray(), disconnected, recoveries);
    }

    private static bool Terminal(OperationSnapshot value) => value.State.KnownValue is
        OperationState.Succeeded or OperationState.Failed or OperationState.Cancelled or OperationState.Interrupted;
}

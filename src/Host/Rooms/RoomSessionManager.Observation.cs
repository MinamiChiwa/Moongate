using System.Security.Cryptography;
using System.Text;
using MabiTool.Contracts;

namespace MabiTool.Host.Rooms;

public sealed partial class RoomSessionManager
{
    public async Task PublishGameObservationAsync(GameObservationSnapshot snapshot, IReadOnlyList<string> principals, CancellationToken ct)
    {
        foreach (var pair in entries)
        {
            var entry = pair.Value;
            if (!entry.Client.IsConnected || !entry.Client.SupportsGameObservation || !authorized(pair.Key)) continue;
            var value = principals.Contains(pair.Key, StringComparer.Ordinal) && snapshot.State == "running" &&
                snapshot.Latest?.Quality.KnownValue is not (ObservationQuality.Stale or ObservationQuality.Unknown or ObservationQuality.Conflict)
                    ? snapshot.Latest?.Value : null;
            if (value is null && !entry.HasReportedObservation) continue;
            var generation = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                backendEpoch + ":" + snapshot.CaptureId + ":" + value?.CaptureGeneration)))[..32];
            var round = GetRound(pair.Key);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                await entry.Client.ReportGameObservationAsync(value, generation, Interlocked.Increment(ref entry.ObservationSequence),
                    round.Phase is "stopped" or "cancelled" ? null : round.Id, deadline.Token);
                entry.HasReportedObservation = value is not null;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
    }
}

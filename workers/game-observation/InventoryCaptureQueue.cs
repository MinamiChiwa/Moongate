using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Observation.Worker;

public sealed class InventoryCaptureQueue
{
    private readonly object gate = new();
    private readonly Queue<(InventoryCaptureReport Report, int Bytes)> queue = [];
    private (string Interest, ulong Generation)? blocked;
    private int bytes;
    public int Count { get { lock (gate) return queue.Count; } }
    public bool TryWrite(InventoryCaptureReport value)
    {
        var encoded = JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options);
        lock (gate)
        {
            var key = (value.InterestId, value.Frame.Generation.Value);
            if (blocked == key) return false;
            if (queue.Count >= 64 || encoded.Length > 64 * 1024 * 1024 || bytes + (long)encoded.Length > 64 * 1024 * 1024)
            {
                queue.Clear(); bytes = 0; blocked = key;
                var gap = value with { Frame = value.Frame with { Kind = InventoryFrameKind.Invalidated,
                    Items = [], Changes = [], Coverage = [], Gold = null, BodySha256 = null, Reason = "inventory.transportCapacity" } };
                queue.Enqueue((gap, 0)); return false;
            }
            blocked = null;
            queue.Enqueue((JsonSerializer.Deserialize<InventoryCaptureReport>(encoded, ContractJson.Options)!, encoded.Length));
            bytes += encoded.Length; return true;
        }
    }
    public IReadOnlyList<InventoryCaptureReport> Take(int maximum = 8)
    {
        if (maximum is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maximum));
        lock (gate)
        {
            var result = new List<InventoryCaptureReport>();
            while (result.Count < maximum && queue.TryDequeue(out var value)) { bytes -= value.Bytes; result.Add(value.Report); }
            return result;
        }
    }
}

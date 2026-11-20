using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Observation.Worker;

/// <summary>
/// Worker 到宿主的库存帧队列。超出容量时清空并以 inventory.transportCapacity 使当前代次失效，宿主需等下一份完整库存（下次登录数据）。
/// 容量以字节（64 MiB）为主要上限；条数上限按战斗中连续的小型变更帧留足余量，写出侧每 100 ms 至多取 <see cref="TakePerTick"/> 条。
/// 每帧入队时编码一次，编码随帧保存，写出时直接使用（入队时的编码即该帧的固定副本）。
/// </summary>
public sealed class InventoryCaptureQueue
{
    public const int MaximumFrames = 1024, TakePerTick = 64;
    private readonly object gate = new();
    private readonly Queue<(InventoryCaptureReport Report, int Bytes, byte[]? Encoded)> queue = [];
    private (string Interest, ulong Generation)? blocked;
    private int bytes;
    public int Count { get { lock (gate) return queue.Count; } }
    public int Bytes { get { lock (gate) return bytes; } }
    public bool TryWrite(InventoryCaptureReport value)
    {
        var encoded = JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options);
        lock (gate)
        {
            var key = (value.InterestId, value.Frame.Generation.Value);
            if (blocked == key) return false;
            if (queue.Count >= MaximumFrames || encoded.Length > 64 * 1024 * 1024 || bytes + (long)encoded.Length > 64 * 1024 * 1024)
            {
                queue.Clear(); bytes = 0; blocked = key;
                var gap = value with { Frame = value.Frame with { Kind = InventoryFrameKind.Invalidated,
                    Items = [], Changes = [], Coverage = [], Gold = null, BodySha256 = null, Reason = "inventory.transportCapacity" } };
                queue.Enqueue((gap, 0, null)); return false;
            }
            blocked = null;
            queue.Enqueue((value, encoded.Length, encoded));
            bytes += encoded.Length; return true;
        }
    }
    public IReadOnlyList<InventoryCaptureReport> Take(int maximum = 8) =>
        TakeEncoded(maximum).Select(value => value.Encoded is { } bytes ? JsonSerializer.Deserialize<InventoryCaptureReport>(bytes, ContractJson.Options)! : value.Report).ToArray();
    /// <summary>取出至多 <paramref name="maximum"/> 帧及其入队时的编码（容量失效帧没有编码）。</summary>
    internal IReadOnlyList<(InventoryCaptureReport Report, byte[]? Encoded)> TakeEncoded(int maximum = 8)
    {
        if (maximum is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maximum));
        lock (gate)
        {
            var result = new List<(InventoryCaptureReport, byte[]?)>();
            while (result.Count < maximum && queue.TryDequeue(out var value)) { bytes -= value.Bytes; result.Add((value.Report, value.Encoded)); }
            return result;
        }
    }
}

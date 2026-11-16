namespace MabiTool.RoomServer.Transport;

internal sealed class RoomSendBudget(long maximum)
{
    private long queued;
    internal long QueuedBytes => Interlocked.Read(ref queued);
    internal bool Reserve(int bytes) => Reserve(ref queued, bytes, maximum);
    internal void Release(int bytes) => Interlocked.Add(ref queued, -bytes);
    internal static bool Reserve(ref long counter, int bytes, long maximum)
    {
        while (true)
        {
            var current = Interlocked.Read(ref counter);
            if (bytes < 0 || current > maximum - bytes) return false;
            if (Interlocked.CompareExchange(ref counter, current + bytes, current) == current) return true;
        }
    }
}

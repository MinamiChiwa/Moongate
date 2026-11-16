namespace MabiTool.Infrastructure;

internal sealed class EventReplayWindow(TimeProvider clock)
{
    private readonly Queue<EventDelivery> entries = new();
    private int bytes;
    public ulong Floor { get; private set; }
    public void Add(EventDelivery item)
    {
        entries.Enqueue(item); bytes += item.Bytes; Prune();
    }
    public void Prune()
    {
        while (entries.TryPeek(out var first) && (entries.Count > StateEventHub.MaximumReplayEvents || bytes > StateEventHub.MaximumReplayBytes ||
            clock.GetElapsedTime(first.PublishedAt) >= StateEventHub.ReplayLifetime))
        {
            entries.Dequeue(); bytes -= first.Bytes; Floor = first.Sequence;
        }
    }
    public IReadOnlyList<EventDelivery> After(ulong sequence, Func<EventDelivery, bool> visible) => entries.Where(e => e.Sequence > sequence && visible(e)).ToArray();
}

using MabiTool.Providers.TwObservation;

namespace MabiTool.Observation.Worker;

/// <summary>按初始化时点选择身份来源；来源切换时重新分配报告代次，等待状态不回退到旧连接。</summary>
internal sealed class CaptureObservationSelection
{
    private readonly GameObservation waiting = new TwGameObserver().Current;
    private double initializerFloor = double.NegativeInfinity;
    private TwGameObserver? selected;
    private string? sourceGeneration;
    private string outputGeneration = Guid.NewGuid().ToString("N");
    private long sequence;

    internal GameObservation Select(IEnumerable<(TwGameObserver Source, double InitializedAt)> candidates)
    {
        TwGameObserver? next = null;
        var latest = initializerFloor;
        foreach (var candidate in candidates)
        {
            if (!double.IsFinite(candidate.InitializedAt) || candidate.InitializedAt < latest) continue;
            if (next is null || candidate.InitializedAt > latest || ReferenceEquals(candidate.Source, selected))
            {
                next = candidate.Source;
                latest = candidate.InitializedAt;
            }
        }
        if (next is not null) initializerFloor = latest;
        var observation = next?.Current ?? waiting;
        if (!ReferenceEquals(next, selected) || observation.Generation != sourceGeneration)
        {
            outputGeneration = Guid.NewGuid().ToString("N");
            sequence = 0;
            selected = next;
            sourceGeneration = observation.Generation;
        }
        return observation with { Generation = outputGeneration, Sequence = checked(++sequence) };
    }
}

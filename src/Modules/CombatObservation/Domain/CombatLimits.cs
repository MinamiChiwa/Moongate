namespace MabiTool.Modules.CombatObservation.Domain;

public sealed record CombatLimits(int MaximumEntities = 2048, int MaximumEffectsPerEntity = 256,
    int MaximumSkills = 4096, int MaximumDeduplicationEntries = 10000, int MaximumTimelineItems = 10000,
    int MaximumCacheBytes = 16 * 1024 * 1024, int MaximumTimelineBytes = 16 * 1024 * 1024,
    int MaximumQueueEvents = 10000, int MaximumQueueBytes = 16 * 1024 * 1024)
{
    public void Validate()
    {
        if (MaximumEntities is < 1 or > 2048 || MaximumEffectsPerEntity is < 1 or > 256 ||
            MaximumSkills is < 1 or > 4096 || MaximumDeduplicationEntries is < 1 or > 10000 ||
            MaximumTimelineItems is < 1 or > 10000 || MaximumCacheBytes is < 1024 or > 16 * 1024 * 1024 ||
            MaximumTimelineBytes is < 1024 or > 16 * 1024 * 1024 || MaximumQueueEvents is < 1 or > 10000 ||
            MaximumQueueBytes is < 1024 or > 16 * 1024 * 1024)
            throw new ArgumentException("战斗资源限制超出已登记上限");
    }
}

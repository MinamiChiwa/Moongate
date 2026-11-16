using System.Text.Json;

namespace MabiTool.Contracts;

public sealed record ColorStudioPreferences(UInt64String Revision, string Server, string Language,
    OpenEnum<ColorStrategy> DefaultStrategy, JsonElement PresentationPreferences, JsonElement? OriginalDraft,
    OpenEnum<ColorStrategy>? DraftStrategy)
{
    public static ColorStudioPreferences Default => new(new(0), "tw", "简体中文", ColorStrategy.Screenshot,
        JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>()), null, null);
}
public sealed record ImportedColorRegion(int Region, string? Color, bool? Enabled, bool? Available,
    IReadOnlyList<string>? Targets, double? Delta);
public sealed record ImportedColorResult(IReadOnlyList<ImportedColorRegion> Regions, int? RegionCount,
    double? Maximum, double? Average);
public sealed record ImportedColorHistoryItem(string Id, string SourceId, string SourceHash, string OldId,
    string? LocalTime, string? Outcome, bool? Restored, ImportedColorResult Result, ImportedColorResult? Best,
    OpenEnum<ObservationQuality> Quality, DateTimeOffset ImportedAt);
public sealed record ImportedColorHistoryQuery(int PageSize = 20, string? BeforeId = null);

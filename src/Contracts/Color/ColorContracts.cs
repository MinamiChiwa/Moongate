namespace MabiTool.Contracts;

public sealed record ColorLibrary(UInt64String Revision, ColorProfile Draft, IReadOnlyList<ColorProfile> Profiles);
public sealed record ColorLibraryEdit(IReadOnlyList<ColorProfile> PutProfiles, IReadOnlyList<string> DeleteProfileIds,
    ColorProfile? Draft);
public sealed record ColorLibraryChanged(UInt64String Revision, int ProfileCount, bool DraftChanged);
public sealed record ColorHistoryQuery(int PageSize = 20, string? BeforeOperationId = null);
public sealed record ColorHistoryItem(string Id, string OperationId, string Owner, OpenEnum<OperationState> State,
    DateTimeOffset CompletedAt, ColorProfile RulesSnapshot, ColorRunResult? Result,
    ColorMeasurement? BestMeasured, ColorMeasurement? LastMeasured);
public sealed record ColorAssetRangeRequest(int Offset, int Count);
public sealed record ColorAssetContent(string AssetId, string Sha256, int Offset, int TotalBytes, string Base64);
public sealed record ColorScreenCapture(string Id, PhysicalWindowRect Bounds, string Encoding, string Sha256,
    int SizeBytes, DateTimeOffset CapturedAt, DateTimeOffset ExpiresAt);
public sealed record ColorPixelQuery(int X, int Y);
public sealed record ColorCaptureRelease(string CaptureId, bool Released);
public sealed record ColorEngineRuntime(string State, int MaximumParallelism, int IdleRetentionSeconds,
    int MaximumConcurrentRequests, UInt64String StartedWorkers, UInt64String? WorkerWorkingSetBytes);
public sealed record ColorInteractionPresence(string RunId, string HostClockEpoch, int ValidForMs);
public sealed record ColorPickedPixel(string CaptureId, int X, int Y, string Hex);
public sealed record ColorPreviewRequest(ColorProfile Profile, IReadOnlyList<string?> Colors);
public sealed record ColorPreview(bool PreviewOnly, string MatchingPolicy, bool Complete, bool Accepted,
    IReadOnlyDictionary<string, string?> HexByRegion, IReadOnlyDictionary<string, double?> DeltaByRegion,
    IReadOnlyDictionary<string, double?> MatchScoreByRegion);

public enum ColorStrategy { Screenshot, Precise }
public enum ColorMatchMode { ExactHex, Similar }
public enum ColorPaletteImage { Overview, Atlas }
/// <summary>目标色集合图：精准 HEX 只含目标色，相似颜色为 CIE76 ΔE ≤ 容差的全部 8 位 sRGB；不含替代色。</summary>
public sealed record ColorPaletteRequest(string TargetHex, OpenEnum<ColorMatchMode> MatchMode, double Tolerance,
    OpenEnum<ColorPaletteImage> Image);
public sealed record ColorPalette(string TargetHex, OpenEnum<ColorMatchMode> MatchMode, double Tolerance,
    OpenEnum<ColorPaletteImage> Image, int ColorCount, int Width, int Height, AssetSnapshot Asset);
/// <summary>原区域卡徽标：达标、未达标、未开始、未参与、未启用。</summary>
public enum ColorRegionState { Met, Unmet, NotStarted, Excluded, Disabled }
public sealed record ColorRegionJudgement(string RegionId, OpenEnum<ColorRegionState> State);
public sealed record ColorRegion(string Id, string TargetHex, IReadOnlyList<string> AlternativeHex,
    OpenEnum<ColorMatchMode> MatchMode, double Tolerance, int Priority, bool Enabled);
public sealed record ColorProfile(string Id, int SchemaVersion, string Name, OpenEnum<ColorStrategy> Strategy,
    IReadOnlyList<ColorRegion> Regions, UInt64String Revision);
public sealed record ColorMeasurement(IReadOnlyDictionary<string, string> HexByRegion,
    DateTimeOffset ObservedAt, ObservationSource Source, UInt64String RunGeneration, OpenEnum<ObservationQuality> Quality)
{
    public IReadOnlyDictionary<string, double?> DeltaByRegion { get; init; } = new Dictionary<string, double?>();
    public bool Accepted { get; init; }
    public bool TargetExact { get; init; }
    public IReadOnlyList<ColorRegionJudgement> Regions { get; init; } = [];
}
public sealed record ColorInteractionState(UInt64String Revision, string? BatchId, bool SelectionEnabled, bool RetryEnabled);
public sealed record ColorCandidate(string Id, int SortIndex, IReadOnlyDictionary<string, string?> HexByRegion,
    IReadOnlyDictionary<string, double?> DeltaByRegion, bool Predicted, bool Available, bool IsCurrent,
    bool Accepted, bool TargetExact)
{
    public IReadOnlyList<ColorRegionJudgement> Regions { get; init; } = [];
    public double? MaximumDelta { get; init; }
    public double? AverageDelta { get; init; }
    public int? ExactMatches { get; init; }
    public int? ExactTotal { get; init; }
    public bool? FamilyConsistent { get; init; }
}
public sealed record ColorCandidateBatch(string Id, IReadOnlyList<ColorCandidate> Candidates,
    string? CurrentCandidateId, DateTimeOffset ExpiresAt, ColorInteractionState Interaction);
public sealed record ColorCandidateSelectionRequest(string BatchId, string CandidateId, UInt64String ExpectedRevision);
public sealed record ColorRetryRequest(UInt64String ExpectedRevision);
public sealed record ColorRunStartRequest(string GameSessionId, UInt64String ExpectedBindingRevision, ColorProfile Profile);
public sealed record ColorInteractionReceipt(bool Accepted, ColorInteractionState Interaction);
public sealed record ColorRunResult(string RunId, int RegionCount, string Outcome, string? StopReason,
    ColorMeasurement? BestMeasured, ColorMeasurement? LastMeasured, ColorMeasurement? CurrentVerified);
/// <summary>原引擎当前阶段；单调时间均为 hostClockEpoch 标识的宿主时钟毫秒。</summary>
public sealed record ColorRunProgress(string? Stage, int? Current, int? Total, double StageStartedMonotonicMs,
    DateTimeOffset StageStartedAt, double? DeadlineMonotonicMs, double UpdatedMonotonicMs, DateTimeOffset UpdatedAt);
public sealed record ColorRun(string Id, string OperationId, GameSession BindingSnapshot, ColorProfile RulesSnapshot,
    string Phase, double DeadlineMonotonicMs, string? CandidateBatchId, ColorMeasurement? BestMeasured,
    ColorMeasurement? LastMeasured, ColorMeasurement? CurrentVerified, string? StopReason)
{
    public string Owner { get; init; } = "";
    public string HostClockEpoch { get; init; } = "";
    public UInt64String Revision { get; init; }
    public ColorCandidateBatch? Candidates { get; init; }
    public ColorInteractionState? Interaction { get; init; }
    public ColorRunResult? Result { get; init; }
    public ColorRunProgress? Progress { get; init; }
    public int? RegionCount { get; init; }
    public IReadOnlyDictionary<string, bool>? AvailableByRegion { get; init; }
}

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
/// <summary>原区域卡徽标：达标、未达标、未开始、未参与、未启用；运行中的读数另有未读取（启用的区域这次没读出色码）。</summary>
public enum ColorRegionState { Met, Unmet, NotStarted, Excluded, Disabled, Unread }
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
/// <summary>
/// 引擎的一局结果。<see cref="StopReason"/> 是引擎原停止原因（如 unavailable、interrupted、palette_ended、target_observed）；
/// <see cref="StopDetail"/> 是它的细分原因（精准准备失败的 unavailable_reason、中断的 interruption_reason，
/// 或截图核验与观察失败的 stop_detail，如 occluded、ocr_timeout、hex_mismatch.2、display_scale）；
/// <see cref="FailedStage"/> 是精准准备停在的步骤（ocr_assets、game_build、window_context、screenshot_layout、input_backend、process_access）；
/// <see cref="BestCurrent"/> 为假表示当前颜色没有回到此前保留的较好结果。
/// </summary>
public sealed record ColorRunResult(string RunId, int RegionCount, string Outcome, string? StopReason,
    ColorMeasurement? BestMeasured, ColorMeasurement? LastMeasured, ColorMeasurement? CurrentVerified)
{
    public string? StopDetail { get; init; }
    public string? FailedStage { get; init; }
    public bool? BestCurrent { get; init; }
}
/// <summary>运行中引擎对当前画面的一次读数：色码、色差（引擎没有给出时为空）与按区域规则的判定。</summary>
public sealed record ColorRunReading(IReadOnlyDictionary<string, string?> HexByRegion, IReadOnlyDictionary<string, double?> DeltaByRegion)
{
    public IReadOnlyList<ColorRegionJudgement> Regions { get; init; } = [];
}
/// <summary>
/// 原引擎当前阶段；单调时间均为 hostClockEpoch 标识的宿主时钟毫秒。
/// <see cref="Message"/> 是引擎给出的本阶段说明原文；<see cref="Status"/> 是阶段内的状态：
/// 细化的 trial、restoring、recovering、retained，或重试的 retry-waiting、retry-zooming、retry-searching、retry-rejected；
/// <see cref="Reason"/> 是重试被拒绝或不可用的原因码，或精准寻色等待色卡核验时未通过的原因码；<see cref="Reading"/> 是引擎对当前画面最新一次读到的游戏色码（不带读数的阶段事件保留它，重试与选用候选后清空），<see cref="Retained"/> 是细化时保留的配色。
/// </summary>
public sealed record ColorRunProgress(string? Stage, int? Current, int? Total, double StageStartedMonotonicMs,
    DateTimeOffset StageStartedAt, double? DeadlineMonotonicMs, double UpdatedMonotonicMs, DateTimeOffset UpdatedAt)
{
    public string? Message { get; init; }
    public string? Status { get; init; }
    public string? Reason { get; init; }
    public ColorRunReading? Reading { get; init; }
    public ColorRunReading? Retained { get; init; }
}
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

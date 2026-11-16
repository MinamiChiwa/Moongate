using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.Modules.CombatObservation.Application;
using MabiTool.Modules.CombatObservation.Domain;
using MabiTool.Providers.TwObservation.Combat;

namespace MabiTool.ObservationDebugTool;

public sealed record CombatScenarioOptions(int PreparationSeconds = 900, int CombatSeconds = 120, int SetupSeconds = 120,
    SegmentationOptions? Segmentation = null);

/// <summary>
/// 战斗实采验收（原 CombatNativeProbe 交互流程）：点击开始即开始观察网络；准备阶段含启动、切图与找怪，
/// 身份确认后由操作者开始独立的战斗倒计时。报告字段与验收口径保持不变。
/// </summary>
public sealed class CombatScenario(IGameEnvironment environment, CombatScenarioOptions? options = null) : IDebugScenario
{
    private readonly CombatScenarioOptions settings = options ?? new();

    public ScenarioPlan Plan => settings.Segmentation is { } seg ? SegmentationPlan(seg) : CombatPlan;

    private ScenarioPlan SegmentationPlan(SegmentationOptions seg) => new("segments", "分段：长停手不拆段与手动拆段", "unified-modular-backend 11.16",
        "native-host-combat-segmentation", "combat-segmentation", settings.PreparationSeconds, settings.CombatSeconds,
        [
            $"点击“开始”即开始观察网络。准备阶段最长 {settings.PreparationSeconds / 60} 分钟，包含启动、切图和找怪，不占采集时间。",
            "面板显示“可以切图”后，切换一次地图以取得本机身份；取得身份后整个采集期间留在同一张地图，不再切图。",
            $"显示“准备完成”后，站到怪物旁；准备好后点击“开始 {settings.CombatSeconds} 秒分段采集”。",
            $"第 0–{seg.QuietFromSeconds:0} 秒持续攻击；第 {seg.QuietFromSeconds:0}–{seg.QuietToSeconds:0} 秒完全停手（不攻击、不切图）；第 {seg.QuietToSeconds:0} 秒起恢复攻击。",
            $"第 {seg.SplitAtSeconds:0} 秒工具执行手动拆段，之后继续攻击到倒计时结束。",
            "任何时候都可以点“停止”。",
        ]);

    private ScenarioPlan CombatPlan => new("combat", "战斗：本机 DPS、自身/怪物 Buff 与破防", "unified-modular-backend 11.9（战斗三项能力）、11.13 切图分段",
        "native-host-combat-acceptance", "combat-native-interactive", settings.PreparationSeconds, settings.CombatSeconds,
        [
            $"点击“开始”即开始观察网络。准备阶段最长 {settings.PreparationSeconds / 60} 分钟，包含启动、切图和找怪，不占战斗时间。",
            "面板显示“可以切图”后，切换一次地图（进入另一张地图）以取得本机身份。",
            "显示“准备完成”后，慢慢寻找怪物；准备好后点击“开始战斗采集”。",
            $"{settings.CombatSeconds} 秒内持续攻击怪物，触发自身 Buff、怪物 Debuff 和破防条变化；需要验证切图分段时可在战斗中切图后继续攻击。",
            "倒计时结束自动停止；任何时候都可以点“停止”。",
        ]);

    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        var interrupted = context.Stopped;
        var window = new ProbeCaptureWindow(settings.PreparationSeconds, settings.CombatSeconds, context.Clock);
        window.StartPreparation();
        var evidence = settings.Segmentation is { } segmentation ? new SegmentationEvidence(segmentation) : null;
        context.Show(() =>
        {
            var value = window.Snapshot();
            double? remaining = value.RemainingSeconds > 0 ? value.RemainingSeconds : null;
            if (evidence is not null && window.CombatStartedAt is { } startedAt && value.Stage == "战斗采集中")
                return evidence.Options.Phase((context.Clock.GetUtcNow() - startedAt).TotalSeconds, evidence.SplitApplied, remaining);
            return new(value.Stage, value.Instruction, remaining);
        });
        context.Offer(evidence is null ? $"开始 {settings.CombatSeconds} 秒战斗采集" : $"开始 {settings.CombatSeconds} 秒分段采集", () => window.Snapshot().CanStart, () =>
        {
            if (window.StartCombat()) context.Mark("combat.windowStarted");
        });
        var totals = new CombatAggregate();
        var identityPreparation = new CombatIdentityPreparation();
        var combatCaptureReadyAtMs = new List<double>();
        string? failure = null, failureReason = null;
        var host = ObservationHost.Create(environment, context, storage: false);
        ICombatObservationSubscription? subscription = null;
        var sample = new Stopwatch();
        // Host 与 Worker 帧时间使用同一 QPC 单调时钟；报告把缺口及拒绝时点换算为相对采集开始的毫秒。
        var captureStartMono = double.NaN;
        CombatCaptureDiagnostics? combatDiagnostics = null;
        using var duration = CancellationTokenSource.CreateLinkedTokenSource(interrupted);
        Task budgetMonitor = Task.CompletedTask;
        long preparationFrames = 0;
        try
        {
            using var setup = CancellationTokenSource.CreateLinkedTokenSource(interrupted);
            setup.CancelAfter(TimeSpan.FromSeconds(settings.SetupSeconds));
            budgetMonitor = Task.Run(async () =>
            {
                while (!duration.IsCancellationRequested)
                {
                    if (window.Expired) { duration.Cancel(); setup.Cancel(); break; }
                    try { await Task.Delay(50, duration.Token); }
                    catch (OperationCanceledException) { break; }
                }
            });
            await host.StartAsync(setup.Token);
            var observations = host.Observations!; var owner = host.Owner!; var binding = host.Game!;
            sample.Start(); captureStartMono = Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency);
            await host.StartCaptureAsync(duration.Token, sample);
            context.Mark("capture.running");
            host.Stage = "combat.identity";
            host.Phases["identityOpenStartedMs"] = sample.Elapsed.TotalMilliseconds;
            var identityOpen = Stopwatch.StartNew();
            try
            {
                subscription = await identityPreparation.OpenAsync(
                    ready =>
                    {
                        window.AwaitingCapture();
                        return observations.OpenCombatAsync(owner, "native-probe-" + Guid.NewGuid().ToString("N"),
                            new(binding.Session.Id), environment.CombatProfile, duration.Token, ready);
                    },
                    () =>
                    {
                        var elapsed = sample.Elapsed.TotalMilliseconds;
                        combatCaptureReadyAtMs.Add(elapsed);
                        host.Phases.TryAdd("combatCaptureReadyMs", elapsed);
                        host.Phases["combatCaptureReadyLastMs"] = elapsed;
                        context.Mark("combat.captureReady");
                        window.CaptureReady();
                    },
                    () => observations.Runtime().State == "running", duration.Token);
            }
            finally { host.Phases["identityOpenElapsedMs"] = identityOpen.Elapsed.TotalMilliseconds; }
            host.Phases["identityReadyMs"] = sample.Elapsed.TotalMilliseconds;
            totals.LocalIdentityObserved = true;
            window.IdentityReady();
            context.Mark("combat.identityReady");
            host.Stage = "combat.read";
            var session = new CombatSession(owner, "native-probe", subscription.Source, subscription.LocalEntity,
                new(binding.Session.Id), DateTimeOffset.UtcNow);
            await foreach (var frame in subscription.ReadAsync(duration.Token))
            {
                if (window.CombatStartedAt is not { } began || frame.ObservedAt < began)
                {
                    preparationFrames++;
                    // 准备期间持续消费观测，找怪不会使订阅溢出。
                    continue;
                }
                session.Apply(frame);
                var localDamageBefore = totals.LocalDamageEvents;
                var elapsedMs = (frame.ObservedAt - began).TotalMilliseconds;
                if (evidence is not null && !evidence.SplitApplied && elapsedMs >= evidence.Options.SplitAtSeconds * 1000)
                {
                    var applied = session.Split(frame.ObservedAt, frame.MonotonicMs);
                    evidence.RecordSplitAttempt(elapsedMs, applied);
                    if (applied) context.Mark("combat.manualSplit");
                }
                var checkpoint = session.Capture();
                totals.Accept(frame, checkpoint.Workspace);
                evidence?.Observe(elapsedMs, checkpoint.Workspace.Encounter?.Id, totals.LocalDamageEvents > localDamageBefore);
                totals.Segments(checkpoint);
                if (checkpoint.Workspace.Encounter?.Summary is { } summary)
                    totals.BusinessSummary = new(summary.AttributedDamage.ToString(), summary.DurationMs, summary.EncounterDps,
                        summary.RollingDps, summary.CalculationVersion, summary.Quality.KnownValue?.ToString() ?? "unknown", summary.Reasons);
                session.Acknowledge(checkpoint);
            }
            if (!duration.IsCancellationRequested) failure = "probe.streamEndedEarly";
        }
        catch (OperationCanceledException) when (duration.IsCancellationRequested)
        {
            failure = interrupted.IsCancellationRequested ? "probe.interrupted" : window.CombatStartedAt is null ? "probe.preparationTimedOut" :
                subscription is null ? "combat.identityNotObserved" : null;
        }
        catch (OperationCanceledException) when (subscription is null && host.Observations?.Runtime().State == "running")
        { failure = "combat.identityNotObserved"; }
        catch (Exception error)
        {
            failure = SafeCodes.Of(error);
            if (error is OperationException { Code: "combat.observationGap" })
                failureReason = SharedObservationDiagnostics.SafeReason(error.Message);
        }
        finally
        {
            context.ClearOffer();
            context.Show("正在停止", "正在释放 Worker、订阅和捕获资源，请稍候。", null);
            duration.Cancel();
            await budgetMonitor;
            window.Stop();
            host.Phases["captureBudgetElapsedMs"] = sample.Elapsed.TotalMilliseconds;
            await host.CleanupAsync(async () =>
            {
                if (subscription is null) return;
                await host.CleanupStepAsync("subscription.close", async () =>
                {
                    foreach (var gap in await subscription.CloseAsync()) totals.AddGap(gap);
                });
                await host.CleanupStepAsync("subscription.dispose", async () => await subscription.DisposeAsync());
            }, () => combatDiagnostics = host.Observations?.CombatDiagnostics());
        }
        double? CaptureRelative(double? value) => value is { } mono && double.IsFinite(captureStartMono) ? mono - captureStartMono : null;
        var report = JsonNodes.From(new
        {
            schemaVersion = 1, evidenceKind = Plan.EvidenceKind, checkedAtUtc = DateTimeOffset.UtcNow,
            profile = Tw20261110CombatProtocol.Profile, gameBuild = Tw20261110CombatProtocol.GameBuild,
            clientFingerprint = new { Tw20261110CombatProtocol.AssemblySha256, Tw20261110CombatProtocol.MetadataSha256, TwClientFingerprint.UnitySha256 },
            requestedDurationSeconds = settings.CombatSeconds, phases = host.Phases, stageAtEnd = host.Stage,
            interrupted = interrupted.IsCancellationRequested, failure, failureReason,
            interactive = true, preparationLimitSeconds = settings.PreparationSeconds, preparationFrames,
            combatWindowStartedAt = window.CombatStartedAt,
            sharedObservation = host.Shared, identityPreparation, combatCaptureReadyAtMs,
            frameRejection = combatDiagnostics?.FrameRejection is { } rejection ? new { rejection.Branch, rejection.FrameAgeMs, rejection.ArrivalIntervalMs,
                atCaptureMs = CaptureRelative(rejection.HostMonotonicMs) } : null,
            combatGaps = combatDiagnostics?.Gaps.Select(gap => new { gap.Reason, fromCaptureMs = CaptureRelative(gap.FromMs), toCaptureMs = CaptureRelative(gap.ToMs),
                droppedEvents = gap.DroppedEvents.ToString(CultureInfo.InvariantCulture) }).ToArray(),
            combatGapsTruncated = combatDiagnostics?.GapsTruncated,
            // 每个积压段一条 combat.captureLag；区间为段内首包抓包时点至处理时点，即该段最早包的滞后。
            captureLag = combatDiagnostics is null ? null : new
            {
                episodes = combatDiagnostics.Gaps.Count(gap => gap.Reason == "combat.captureLag"),
                maximumLagMs = combatDiagnostics.Gaps.Where(gap => gap.Reason == "combat.captureLag" && gap.ToMs is not null)
                    .Select(gap => (double?)(gap.ToMs!.Value - gap.FromMs)).Max(),
            },
            aggregate = new { totals.Frames, totals.LocalIdentityObserved, totals.DamageEvents, totals.LocalDamageEvents,
                localDamage = totals.LocalDamage.ToString(CultureInfo.InvariantCulture), totals.SelfEffects, totals.MonsterEffects, totals.Gaps, totals.DroppedEvents },
            observedCapabilities = new { localDamage = totals.LocalDamageEvents > 0, selfEffects = totals.SelfEffects > 0, monsterEffects = totals.MonsterEffects > 0 },
            businessSummary = totals.BusinessSummary,
            // 切图分段：每段独立统计；时点为相对采集开始的毫秒，未结束的末段 endReason 为 active。
            segments = totals.SegmentSummaries().Select(value => new
            {
                ordinal = value.Segment?.Ordinal, startReason = value.Segment?.StartReason,
                startCaptureMs = CaptureRelative(value.StartedMonotonicMs), endCaptureMs = CaptureRelative(value.EndedMonotonicMs),
                endReason = value.Summary.BoundaryReason, quality = value.Summary.Quality.KnownValue?.ToString() ?? "unknown",
                reasons = value.Summary.Reasons, damage = value.Summary.AttributedDamage.ToString(),
                durationMs = value.Summary.DurationMs, encounterDps = value.Summary.EncounterDps,
            }).ToArray(),
            monsterBreakGauge = new { totals.MonsterGaugeUpdates, totals.MonsterGaugesWithCurrent, totals.MonsterGaugesWithMaximum,
                totals.GaugeProjectionMismatches, totals.GaugeSamplesTruncated, samples = totals.MonsterGauges },
            memory = host.MemoryReport(),
            release = host.ReleaseReport(),
            totalElapsedMs = context.ElapsedMs
        });
        var unmet = totals.UnmetCapabilities();
        IReadOnlyDictionary<string, bool>? checks = null;
        if (evidence is not null)
        {
            checks = evidence.Evaluate(totals.SegmentSummaries().Select(value => new SegmentOutcome(value.Segment?.StartReason,
                value.Summary.BoundaryReason, value.Summary.AttributedDamage.Value > 0)).ToArray());
            var seg = evidence.Options;
            report["segmentation"] = JsonNodes.From(new
            {
                schedule = new { seg.QuietFromSeconds, seg.QuietToSeconds, seg.SplitAtSeconds },
                requiredQuietMs = seg.RequiredQuietSeconds * 1000, longestQuietMs = evidence.LongestQuietMs, longestQuietFromMs = evidence.LongestQuietFromMs,
                splitAttempts = evidence.SplitAttempts, splitAppliedAtMs = evidence.SplitAppliedAtMs, splitEntry = "CombatSession.Split", checks,
            });
        }
        var accepted = checks is null ? failure is null && host.Released && unmet.Count == 0
            : failure is null && host.Released && checks.Values.All(value => value);
        report["acceptance"] = new JsonObject
        {
            ["accepted"] = accepted, ["released"] = host.Released,
            ["unmetCapabilities"] = new JsonArray(unmet.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        };
        var outcome = failure switch
        {
            null => ScenarioResult.Completed,
            "probe.interrupted" => ScenarioResult.Cancelled,
            "probe.preparationTimedOut" => ScenarioResult.TimedOut,
            _ => ScenarioResult.Failed
        };
        var failedChecks = checks?.Where(pair => !pair.Value).Select(pair => pair.Key).ToArray() ?? [];
        var summaryText = checks is not null
            ? accepted ? $"分段验收通过：最长停手 {evidence!.LongestQuietMs / 1000:0.#} 秒未拆段，手动拆段于 {evidence.SplitAppliedAtMs / 1000:0.#} 秒生效，分段 {totals.SegmentSummaries().Count}"
                : "结果：" + (failure ?? (!host.Released ? "资源释放未通过" : "未通过 " + string.Join("、", failedChecks)))
        : accepted ? $"采集完成：本机伤害 {totals.LocalDamageEvents} 条，自身效果 {totals.SelfEffects}，怪物效果 {totals.MonsterEffects}，分段 {totals.SegmentSummaries().Count}"
            : "结果：" + (failure ?? (totals.Gaps.Count > 0 ? "观测不连续：" + string.Join("、", totals.Gaps.Keys.Take(3))
                : unmet.Count > 0 ? "缺少 " + string.Join("、", unmet) : "资源释放未通过"));
        return new(outcome, accepted, summaryText, report);
    }
}

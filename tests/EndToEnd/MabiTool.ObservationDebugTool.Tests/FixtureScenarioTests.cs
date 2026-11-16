using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using MabiTool.ObservationDebugTool;
using Xunit;

namespace MabiTool.ObservationDebugTool.Tests;

/// <summary>以真实进程内 Host、认证观察 Worker 及 fixture 捕获源跑通场景；不接触游戏或网卡。</summary>
[SupportedOSPlatform("windows")]
[Collection("Fixture worker")]
public sealed class FixtureScenarioTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task CombatScenarioKeepsReportFieldsAndReleasesWorkerSubscriptionsAndActivities()
    {
        using var environment = FixtureEnvironment.CurrentCombat();
        var scenario = new CombatScenario(environment, new(PreparationSeconds: 60, CombatSeconds: 3, SetupSeconds: 30));
        var (session, loop) = Session(environment, scenario);
        Assert.True(session.Start());
        await DebugSessionTests.Until(session, value => value.ActionEnabled || value.Results.Count > 0, TimeSpan.FromSeconds(60));
        session.Act();
        await DebugSessionTests.Until(session, value => value.Results.Count == 1, TimeSpan.FromSeconds(60));
        var result = session.Results[0];
        var report = await Report(result);
        Assert.True(result.Outcome == ScenarioResult.Completed, report.ToJsonString());
        Assert.Equal("native-host-combat-acceptance", (string?)report["evidenceKind"]);
        Assert.Null((string?)report["failure"]);
        Assert.Equal(3, (int)report["requestedDurationSeconds"]!);
        Assert.True((bool)report["interactive"]!);
        Assert.True((long)report["aggregate"]!["LocalDamageEvents"]! > 0);
        Assert.True((long)report["aggregate"]!["SelfEffects"]! > 0);
        Assert.True((long)report["preparationFrames"]! >= 0);
        Assert.NotNull(report["phases"]!["identityReadyMs"]);
        Assert.NotNull(report["segments"]);
        Assert.NotNull(report["captureLag"]);
        AssertReleased(report);
        // fixture 只含本机实体，怪物效果缺失使验收按原口径不通过。
        Assert.Contains("monsterEffects", report["acceptance"]!["unmetCapabilities"]!.AsArray().Select(value => (string?)value));
        Assert.False(result.Accepted);
        Assert.StartsWith("combat-native-interactive-", Path.GetFileName(result.ReportPath));
        session.RequestExit(); await loop;
    }

    [Fact]
    public async Task SegmentationScenarioReportsManualSplitEvidenceAndReleasesResources()
    {
        using var environment = FixtureEnvironment.CurrentCombat();
        var schedule = new SegmentationOptions(QuietFromSeconds: 0.5, QuietToSeconds: 1, SplitAtSeconds: 1.5, RequiredQuietSeconds: 0.1);
        var scenario = new CombatScenario(environment, new(PreparationSeconds: 60, CombatSeconds: 4, SetupSeconds: 30, Segmentation: schedule));
        Assert.Equal("segments", scenario.Plan.Id);
        var (session, loop) = Session(environment, scenario);
        Assert.True(session.Start());
        await DebugSessionTests.Until(session, value => value.ActionEnabled || value.Results.Count > 0, TimeSpan.FromSeconds(60));
        session.Act();
        await DebugSessionTests.Until(session, value => value.Results.Count == 1, TimeSpan.FromSeconds(60));
        var result = session.Results[0];
        var report = await Report(result);
        Assert.True(result.Outcome == ScenarioResult.Completed, report.ToJsonString());
        Assert.Equal("native-host-combat-segmentation", (string?)report["evidenceKind"]);
        Assert.StartsWith("combat-segmentation-", Path.GetFileName(result.ReportPath));
        Assert.Null((string?)report["failure"]);
        var segmentation = report["segmentation"]!;
        Assert.Equal("CombatSession.Split", (string?)segmentation["splitEntry"]);
        var checks = segmentation["checks"]!.AsObject();
        foreach (var name in new[] { "quietHeldWithoutSplit", "noIdleTimeoutBoundary", "manualSplitApplied", "manualSplitClosedSegment", "manualSplitStartedSegment" })
            Assert.True(checks.ContainsKey(name), name);
        Assert.True((int)segmentation["splitAttempts"]! >= 1, report.ToJsonString());
        Assert.True((bool)checks["manualSplitApplied"]! && (bool)checks["manualSplitClosedSegment"]!, report.ToJsonString());
        Assert.True((bool)checks["noIdleTimeoutBoundary"]!, report.ToJsonString());
        // 验收只取决于分段检查项与资源释放；三项战斗能力缺口（fixture 无怪物效果）不参与判定。
        Assert.Equal(checks.All(pair => (bool)pair.Value!), result.Accepted);
        Assert.Equal(result.Accepted, (bool)report["acceptance"]!["accepted"]!);
        Assert.NotNull(report["acceptance"]!["unmetCapabilities"]);
        output.WriteLine("segmentation=" + segmentation.ToJsonString());
        AssertReleased(report);
        session.RequestExit(); await loop;
    }

    [Fact]
    public async Task CombatStopAndPreparationTimeoutBothReleaseResources()
    {
        using var environment = FixtureEnvironment.CurrentCombat();
        var (session, loop) = Session(environment, new CombatScenario(environment, new(PreparationSeconds: 60, CombatSeconds: 30, SetupSeconds: 30)));
        session.Start();
        await DebugSessionTests.Until(session, value => value.ActionEnabled, TimeSpan.FromSeconds(60));
        session.Stop();
        await DebugSessionTests.Until(session, value => value.Results.Count == 1, TimeSpan.FromSeconds(60));
        Assert.Equal(ScenarioResult.Cancelled, session.Results[0].Outcome);
        var stopped = await Report(session.Results[0]);
        Assert.Equal("probe.interrupted", (string?)stopped["failure"]);
        AssertReleased(stopped);
        session.RequestExit(); await loop;

        using var slow = FixtureEnvironment.CurrentCombat();
        var (timed, timedLoop) = Session(slow, new CombatScenario(slow, new(PreparationSeconds: 4, CombatSeconds: 30, SetupSeconds: 30)));
        timed.Start();
        await DebugSessionTests.Until(timed, value => value.Results.Count == 1, TimeSpan.FromSeconds(60));
        Assert.Equal(ScenarioResult.TimedOut, timed.Results[0].Outcome);
        var expired = await Report(timed.Results[0]);
        Assert.Equal("probe.preparationTimedOut", (string?)expired["failure"]);
        AssertReleased(expired);
        timed.RequestExit(); await timedLoop;
    }

    [Fact]
    public async Task CoexistenceScenarioKeepsOneWorkerWhileSubscriptionsStartAndStop()
    {
        using var environment = FixtureEnvironment.SharedInventory();
        var (session, loop) = Session(environment, new CoexistenceScenario(environment, new(PreparationSeconds: 90, TimeScale: 0.04, SetupSeconds: 30)));
        session.Start();
        await DebugSessionTests.Until(session, value => value.ActionEnabled || value.Results.Count > 0, TimeSpan.FromSeconds(90));
        session.Act();
        await DebugSessionTests.Until(session, value => value.Results.Count == 1, TimeSpan.FromSeconds(90));
        var result = session.Results[0];
        var report = await Report(result);
        Assert.True(result.Outcome == ScenarioResult.Completed, report.ToJsonString());
        Assert.Equal("native-host-shared-observation-coexistence", (string?)report["evidenceKind"]);
        var checks = report["checks"]!.AsObject();
        foreach (var name in new[] { "timelineCompleted", "workerProcessStable", "subscriberCountsExpected", "combatFramesInEachWindow",
            "noUnexpectedCombatGaps", "inventoryStoppedCleanly", "musicDemandHeldUntilRelease", "workerRuntimeMemoryWithinBudget", "released" })
            Assert.True((bool)checks[name]!, name + ":" + report.ToJsonString());
        Assert.Equal([3, 2, 3, 2, 1, 0], report["checkpoints"]!.AsArray().Select(value => (int)value!["SubscriberCount"]!).ToArray());
        Assert.True(result.Accepted);
        AssertReleased(report);
        session.RequestExit(); await loop;
    }

    /// <summary>
    /// 共用观察性能预算：当前协议真实共用流，战斗身份所在的个人数据帧同时是 8,000 项完整库存登录体。
    /// 战斗帧到达读取端的帧龄 p99 ≤ 1000 ms、最大 &lt; 2000 ms，Worker 私有字节峰值 ≤ 160 MiB，完整库存同步不打断战斗。
    /// </summary>
    [Fact]
    public async Task CoexistenceOnCurrentStreamKeepsCombatFreshAndWorkerMemoryWithinBudget()
    {
        using var environment = FixtureEnvironment.CurrentCombatInventory();
        var (session, loop) = Session(environment, new CoexistenceScenario(environment, new(PreparationSeconds: 90, TimeScale: 0.1, SetupSeconds: 30)));
        session.Start();
        await DebugSessionTests.Until(session, value => value.ActionEnabled || value.Results.Count > 0, TimeSpan.FromSeconds(90));
        session.Act();
        await DebugSessionTests.Until(session, value => value.Results.Count == 1, TimeSpan.FromSeconds(90));
        var result = session.Results[0];
        var report = await Report(result);
        Assert.True(result.Outcome == ScenarioResult.Completed, report.ToJsonString());
        var checks = report["checks"]!.AsObject();
        foreach (var name in new[] { "timelineCompleted", "workerProcessStable", "subscriberCountsExpected", "combatFramesInEachWindow",
            "noUnexpectedCombatGaps", "inventoryStoppedCleanly", "workerRuntimeMemoryWithinBudget", "released" })
            Assert.True((bool)checks[name]!, name + ":" + report.ToJsonString());
        Assert.Null(report["frameRejection"]);
        var age = report["frameAge"]!;
        Assert.True((long)age["Count"]! > 0, report.ToJsonString());
        Assert.True((double)age["P99Ms"]! <= 1000, age.ToJsonString());
        Assert.True((double)age["MaxMs"]! < 2000, age.ToJsonString());
        Assert.True((long)report["memory"]!["WorkerPrivateBytes"]! <= 160L * 1024 * 1024, report["memory"]!.ToJsonString());
        // Worker 内存轨迹：布局准备窗口单独记录，运行期峰值、结束值与托管/非托管估计按共用观察内存预算判定。
        var trace = report["memory"]!["WorkerTrace"]!;
        Assert.True((bool)trace["RuntimeWithinBudget"]!, trace.ToJsonString());
        var worker = trace["Workers"]!.AsArray().Single()!;
        Assert.Contains(worker["Windows"]!.AsArray(), value => (string?)value!["kind"] == "layout");
        Assert.True((int)worker["Runtime"]!["Samples"]! > 0);
        Assert.True((long)worker["Runtime"]!["PeakManagedHeapBytes"]! > 0);
        Assert.NotNull(worker["End"]!["UnmanagedEstimateBytes"]);
        // 每次库存操作的完整同步：首个操作期间 20590 同帧取得 8,000 项完整同步；重启后的操作没有新的登录数据。
        var operations = report["inventoryOperations"]!.AsArray();
        Assert.True((bool)operations[0]!["inventory"]!["FullSyncObserved"]!, operations.ToJsonString());
        Assert.Equal(8000, (int)operations[0]!["inventory"]!["FullItems"]!);
        Assert.False((bool)operations[1]!["inventory"]!["FullSyncObserved"]!);
        Assert.Contains(report["inventoryFrames"]!.AsArray(), value => (string?)value!["Reason"] == "inventory.awaitingIdentity");
        AssertReleased(report);
        output.WriteLine("frameAge=" + age.ToJsonString() + " memory=" + report["memory"]!.ToJsonString());
        session.RequestExit(); await loop;
    }

    [Fact]
    public async Task WorkerCrashDuringCoexistenceEndsTheScenarioAndStillReleasesEverything()
    {
        using var environment = FixtureEnvironment.SharedInventory("crash");
        var (session, loop) = Session(environment, new CoexistenceScenario(environment, new(PreparationSeconds: 90, TimeScale: 0.2, SetupSeconds: 30)));
        session.Start();
        await DebugSessionTests.Until(session, value => value.ActionEnabled || value.Results.Count > 0, TimeSpan.FromSeconds(90));
        session.Act();
        await DebugSessionTests.Until(session, value => value.Display?.Stage == "三方共用观察", TimeSpan.FromSeconds(30));
        await File.WriteAllTextAsync(Path.Combine(environment.Root, "crash-now"), "1");
        await DebugSessionTests.Until(session, value => value.Results.Count == 1, TimeSpan.FromSeconds(90));
        var result = session.Results[0];
        var report = await Report(result);
        Assert.Equal(ScenarioResult.Failed, result.Outcome);
        Assert.False(result.Accepted);
        Assert.NotNull((string?)report["failure"]);
        Assert.True((bool)report["release"]!["workersExited"]!);
        Assert.Equal(0, (int)report["release"]!["subscribers"]!);
        Assert.Equal(0, (int)report["release"]!["activities"]!);
        session.RequestExit(); await loop;
    }

    [Fact]
    public async Task InventoryScenarioSummarisesCategoriesAndRecordsOperatorVerification()
    {
        using var environment = FixtureEnvironment.SharedInventory("debugtool");
        var (session, loop) = Session(environment, new InventoryScenario(environment, new(PreparationSeconds: 120, RunSeconds: 2, SetupSeconds: 30)));
        session.Start();
        await DebugSessionTests.Until(session, value => value.Question is not null || value.Results.Count > 0, TimeSpan.FromSeconds(120));
        var question = session.Snapshot();
        Assert.True(question.Question is not null, question.Results.Count > 0 ? (await Report(question.Results[0])).ToJsonString() : "");
        Assert.Contains("EquipmentTool", question.QuestionDetail);
        Assert.True(session.Answer("一致"));
        await DebugSessionTests.Until(session, value => value.Results.Count == 1, TimeSpan.FromSeconds(60));
        var result = session.Results[0];
        var report = await Report(result);
        Assert.True(result.Outcome == ScenarioResult.Completed, report.ToJsonString());
        Assert.Equal("native-host-inventory-acceptance", (string?)report["evidenceKind"]);
        Assert.Equal(2000, (int)report["catalog"]!["Entries"]!);
        Assert.True((bool)report["fullSync"]!["observed"]!);
        Assert.NotNull(report["fullSync"]!["loginSignals"]!["inventory.frame.full"]);
        Assert.NotEmpty(report["inventoryFrames"]!.AsArray());
        Assert.Equal("一致", (string?)report["operatorVerification"]!["answer"]);
        Assert.True((bool)report["checks"]!["inventoryStopKeptSharedCapture"]!, report.ToJsonString());
        Assert.True((int)report["items"]!["total"]! > 0);
        Assert.DoesNotContain("南千和", report.ToJsonString());
        Assert.True(result.Accepted, report.ToJsonString());
        AssertReleased(report);
        session.RequestExit(); await loop;
    }

    private static (DebugSession Session, Task Loop) Session(FixtureEnvironment environment, IDebugScenario scenario)
    {
        var session = new DebugSession([scenario], new ReportDirectory(environment.Reports, TimeProvider.System), TimeProvider.System,
            environment.Scratch, environment.Describe);
        return (session, session.RunAsync());
    }

    private static async Task<JsonNode> Report(CompletedScenario result)
    {
        Assert.NotNull(result.ReportPath);
        return JsonNode.Parse(await File.ReadAllTextAsync(result.ReportPath!))!;
    }

    private static void AssertReleased(JsonNode report)
    {
        var release = report["release"]!;
        Assert.True((bool)release["workersExited"]!, report.ToJsonString());
        Assert.Equal(0, (int)release["subscribers"]!);
        Assert.Equal(0, (int)release["activities"]!);
        Assert.Empty(release["cleanupErrors"]!.AsArray());
        Assert.Empty(report["scenario"]!["toolCleanupErrors"]!.AsArray());
    }
}

[CollectionDefinition("Fixture worker", DisableParallelization = true)]
public sealed class FixtureWorkerCollection;

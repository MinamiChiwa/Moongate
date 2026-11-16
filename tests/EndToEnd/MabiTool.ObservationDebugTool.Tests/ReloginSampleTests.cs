using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.ObservationDebugTool;
using Xunit;

namespace MabiTool.ObservationDebugTool.Tests;

[SupportedOSPlatform("windows")]
[Collection("Fixture worker")]
public sealed class ReloginSampleTests : IDisposable
{
    private readonly string root = Path.Combine(FixtureEnvironment.RepositoryRoot(), "artifacts", "test-data", "debugtool-relogin-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AnalyzerReportsPortsLifecycleHistogramAndProfileOnAnotherConnection()
    {
        var directory = Path.Combine(root, "split");
        SyntheticReloginSample.Write(directory, profileOnLoginConnection: false);
        var analysis = ReloginSampleAnalyzer.Analyze(directory, ReloginAnalysisProtocol.From(SyntheticReloginSample.Layout));
        Assert.Equal(2, analysis.Connections.Count);
        var login = analysis.Connections.Single(value => value.RemotePort == 1111);
        var other = analysis.Connections.Single(value => value.RemotePort == 2222);
        Assert.Equal(1, login.Logins); Assert.Equal(0, login.Profiles);
        Assert.Equal(1, other.Profiles);
        Assert.Equal(2, login.Protocols[SyntheticReloginSample.Chat]);
        Assert.True(login.CapturedFromStart);
        Assert.False(other.CapturedFromStart);
        Assert.Equal(1, login.PrearmedPackets);
        Assert.Equal(2000, other.ClosedTableMs);
        Assert.True(analysis.Findings.LoginObserved);
        Assert.False(analysis.Findings.ProfileOnLoginConnection);
        Assert.Empty(analysis.Findings.LoginOutsidePort1111);
        var decoded = Assert.Single(analysis.Logins);
        Assert.True(decoded.Decoded); Assert.Equal(3, decoded.Items);
        var profile = Assert.Single(analysis.Profiles);
        Assert.False(profile.MatchesLoginOnConnection); Assert.True(profile.MatchesAnyLogin);
        // 库存观察器按连接独立：登录数据所在连接在身份等待时长内没有身份关联，重放给出 identityTimeout 且无完整同步。
        Assert.False(analysis.Findings.ReplayFullSync);
        Assert.Contains("inventory.identityTimeout", analysis.Findings.ReplayReasons.Keys);
        Assert.Contains(analysis.Events, value => value.Kind == "login" && value.Compressed && value.Connection == login.Connection);
        AssertNoPayload(analysis);
    }

    [Fact]
    public void AnalyzerReplaysFullSyncWhenLoginAndProfileShareAConnection()
    {
        var directory = Path.Combine(root, "joined");
        SyntheticReloginSample.Write(directory, profileOnLoginConnection: true);
        var analysis = ReloginSampleAnalyzer.Analyze(directory, ReloginAnalysisProtocol.From(SyntheticReloginSample.Layout));
        Assert.True(analysis.Findings.ProfileOnLoginConnection);
        Assert.True(analysis.Findings.ReplayFullSync);
        var full = Assert.Single(analysis.ObserverStates, value => value.Kind == "Full");
        Assert.Equal(3, full.Items); Assert.True(full.IdentityPresent);
        Assert.True(Assert.Single(analysis.Profiles).MatchesLoginOnConnection);
        AssertNoPayload(analysis);

        // 无布局时仍给出连接、直方图与登录/身份关联时点，不做解码与重放。
        var bare = ReloginSampleAnalyzer.Analyze(directory, ReloginAnalysisProtocol.From(null));
        Assert.Empty(bare.Logins); Assert.Empty(bare.ObserverStates);
        Assert.Contains(bare.Events, value => value.Kind == "profile");
    }

    [Fact]
    public async Task ScenarioRunsInventoryAndTrackedCaptureThenAnalysesTheSample()
    {
        using var environment = FixtureEnvironment.SharedInventory("debugtool");
        var capture = new SampleCapture(complete: true); environment.RawSamples = capture;
        var scenario = new ReloginSampleScenario(environment, _ => SyntheticReloginSample.Layout,
            new(PreparationSeconds: 90, SetupSeconds: 30, CaptureSeconds: 4, TimeScale: 0.02));
        var (session, loop) = Session(environment, scenario);
        Assert.True(session.Start());
        await DebugSessionTests.Until(session, value => value.Results.Count == 1, TimeSpan.FromSeconds(120));
        var result = session.Results[0];
        var report = JsonNode.Parse(await File.ReadAllTextAsync(result.ReportPath!))!;
        Assert.True(result.Outcome == ScenarioResult.Completed, report.ToJsonString());
        Assert.True(result.Accepted, report.ToJsonString());
        Assert.StartsWith("relogin-sample-", Path.GetFileName(result.ReportPath));
        Assert.True(capture.Tracked);
        Assert.Equal("native-relogin-sample", (string?)report["evidenceKind"]);
        Assert.False((bool)report["sampleAnalysis"]!["Findings"]!["ProfileOnLoginConnection"]!);
        Assert.NotEmpty(report["inventory"]!["frames"]!.AsArray());
        Assert.NotNull(report["memory"]!["WorkerTrace"]);
        var codes = report["operatorTimeline"]!.AsArray().Select(value => (string?)value!["Code"]).ToArray();
        foreach (var code in new[] { "inventory.observing", "sample.started", "relogin.characterSelect", "relogin.enter", "relogin.mapChange", "sample.analyzed" })
            Assert.Contains(code, codes);
        Assert.True(Array.IndexOf(codes, "inventory.observing") < Array.IndexOf(codes, "sample.started"));
        Assert.True((bool)report["release"]!["workersExited"]!);
        Assert.Equal(0, (int)report["release"]!["subscribers"]!);
        Assert.DoesNotContain(SyntheticReloginSample.SecretName, report.ToJsonString());
        Assert.True(capture.Disposed);
        session.RequestExit(); await loop;
    }

    [Fact]
    public async Task StoppingDuringTheTimelineEndsCaptureAndReleasesEverything()
    {
        using var environment = FixtureEnvironment.SharedInventory("debugtool");
        var capture = new SampleCapture(complete: false); environment.RawSamples = capture;
        var (session, loop) = Session(environment, new ReloginSampleScenario(environment, _ => SyntheticReloginSample.Layout,
            new(PreparationSeconds: 90, SetupSeconds: 30, CaptureSeconds: 60)));
        session.Start();
        await DebugSessionTests.Until(session, value => value.Display?.Stage == "站定", TimeSpan.FromSeconds(90));
        session.Stop();
        await DebugSessionTests.Until(session, value => value.Results.Count == 1, TimeSpan.FromSeconds(90));
        Assert.Equal(ScenarioResult.Cancelled, session.Results[0].Outcome);
        var report = JsonNode.Parse(await File.ReadAllTextAsync(session.Results[0].ReportPath!))!;
        Assert.Equal("probe.interrupted", (string?)report["failure"]);
        Assert.True((bool)report["release"]!["workersExited"]!);
        Assert.Equal(0, (int)report["release"]!["subscribers"]!);
        Assert.True(capture.Disposed);
        session.RequestExit(); await loop;

        Assert.NotNull(new ReloginSampleScenario(new FixtureEnvironmentWithoutSamples(), _ => null).Plan.Unavailable);
    }

    private static void AssertNoPayload(ReloginSampleAnalysis analysis)
    {
        var text = JsonSerializer.Serialize(analysis);
        Assert.DoesNotContain(SyntheticReloginSample.SecretName, text);
        Assert.DoesNotContain("hello", text);
        Assert.DoesNotContain(SyntheticReloginSample.Character.ToString(System.Globalization.CultureInfo.InvariantCulture), text);
    }

    private (DebugSession, Task) Session(FixtureEnvironment environment, IDebugScenario scenario)
    {
        var session = new DebugSession([scenario], new ReportDirectory(environment.Reports, TimeProvider.System), TimeProvider.System,
            environment.Scratch, environment.Describe);
        return (session, session.RunAsync());
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class FixtureEnvironmentWithoutSamples : IGameEnvironment
    {
        public string CaptureProfile => "tw-music-v1";
        public MabiTool.Contracts.ObservationCombatProfile CombatProfile => throw new NotSupportedException();
        public Microsoft.AspNetCore.Builder.WebApplication BuildHost(string ownerToken, string assetRoot, MabiTool.Infrastructure.StorageOptions? storage, string? memoryTraceDirectory = null) => throw new NotSupportedException();
        public Task<BoundGame> BindAsync(Microsoft.AspNetCore.Builder.WebApplication app, string owner, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> SelectChannelAsync(MabiTool.Host.Game.GameObservationService observations, string owner, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyDictionary<string, object?> Describe() => new Dictionary<string, object?>();
        public IRawSampleCapture? Samples => null;
    }

    /// <summary>写合成样本的采集替身：complete=true 时 4 秒后（时间线 3.5 秒之后）写出样本并以 durationComplete 结束。</summary>
    internal sealed class SampleCapture(bool complete) : IRawSampleCapture
    {
        public bool Disposed { get; private set; }
        public bool Tracked { get; private set; }
        public string? Unavailable => null;
        public Task<IRawSampleRun> StartAsync(string outputDirectory, int durationSeconds, CancellationToken ct, bool trackProcessConnections = false)
        {
            Tracked = trackProcessConnections;
            return Task.FromResult<IRawSampleRun>(new Run(this, outputDirectory, complete));
        }
        private sealed class Run : IRawSampleRun
        {
            private readonly SampleCapture owner;
            private readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Run(SampleCapture owner, string output, bool complete)
            {
                this.owner = owner;
                if (complete) _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(4));
                    SyntheticReloginSample.Write(output, profileOnLoginConnection: false);
                    Result = "{\"reason\":\"capture.durationComplete\",\"writtenPackets\":9}";
                    exit.TrySetResult(0);
                });
            }
            public int ProcessId => Environment.ProcessId;
            public Task<int> Completion => exit.Task;
            public string? Result { get; private set; }
            public ValueTask DisposeAsync() { owner.Disposed = true; exit.TrySetResult(1); return ValueTask.CompletedTask; }
        }
    }
}

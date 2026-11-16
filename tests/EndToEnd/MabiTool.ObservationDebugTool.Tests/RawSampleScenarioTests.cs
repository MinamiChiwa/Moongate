using System.Text.Json.Nodes;
using MabiTool.ObservationDebugTool;
using Xunit;

namespace MabiTool.ObservationDebugTool.Tests;

public sealed class RawSampleScenarioTests : IDisposable
{
    private readonly string root = Path.Combine(FixtureEnvironment.RepositoryRoot(), "artifacts", "test-data", "debugtool-sample-" + Guid.NewGuid().ToString("N"));
    private static readonly RawSampleDefinition Short = new("sample-fixture", "样本", "4.6", "离线用途", 1,
        [(0, "站定", "原地站定"), (0.3, "动作", "执行动作"), (0.6, "停止", "停止动作")]);

    [Fact]
    public async Task CompletedSampleRecordsStepTimelineAndCaptureSummary()
    {
        var capture = new FakeCapture(complete: true);
        var (session, loop) = Start(new RawSampleScenario(Short, capture));
        session.Start();
        await DebugSessionTests.Until(session, value => value.Results.Count == 1);
        var result = session.Results[0];
        Assert.Equal(ScenarioResult.Completed, result.Outcome);
        Assert.True(result.Accepted);
        var report = JsonNode.Parse(await File.ReadAllTextAsync(result.ReportPath!))!;
        Assert.Equal("native-raw-sample-capture", (string?)report["evidenceKind"]);
        Assert.Equal("capture.durationComplete", (string?)report["captureResult"]!["reason"]);
        var codes = report["operatorTimeline"]!.AsArray().Select(value => (string?)value!["Code"]).ToArray();
        Assert.Contains("sample.started", codes);
        Assert.Contains("timeline.step2", codes);
        Assert.True(capture.Disposed);
        Assert.StartsWith("raw-sample-fixture-", Path.GetFileName(result.ReportPath));
        session.RequestExit(); await loop;
    }

    [Fact]
    public async Task StoppingTerminatesTheCaptureProcessAndMissingPythonBlocksStart()
    {
        var capture = new FakeCapture(complete: false);
        var (session, loop) = Start(new RawSampleScenario(Short with { DurationSeconds = 60 }, capture));
        session.Start();
        await DebugSessionTests.Until(session, value => value.Display?.Stage == "站定");
        session.Stop();
        await DebugSessionTests.Until(session, value => value.Results.Count == 1);
        Assert.Equal(ScenarioResult.Cancelled, session.Results[0].Outcome);
        Assert.True(capture.Disposed);
        session.RequestExit(); await loop;

        var missing = new RawSampleScenario(Short, null);
        Assert.NotNull(missing.Plan.Unavailable);
        Assert.Equal(3, RawSampleScenario.Definitions.Count);
        Assert.All(RawSampleScenario.Definitions, definition => Assert.InRange(definition.DurationSeconds, 1, 180));
    }

    private (DebugSession, Task) Start(IDebugScenario scenario)
    {
        var session = new DebugSession([scenario], new ReportDirectory(Path.Combine(root, "reports"), TimeProvider.System), TimeProvider.System,
            Path.Combine(root, "scratch"), DebugSessionTests.Describe);
        return (session, session.RunAsync());
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class FakeCapture(bool complete) : IRawSampleCapture
    {
        public bool Disposed { get; private set; }
        public string? Unavailable => null;
        public Task<IRawSampleRun> StartAsync(string outputDirectory, int durationSeconds, CancellationToken ct, bool trackProcessConnections = false)
        {
            Directory.CreateDirectory(outputDirectory);
            return Task.FromResult<IRawSampleRun>(new Run(this, outputDirectory, durationSeconds, complete));
        }
        private sealed class Run : IRawSampleRun
        {
            private readonly FakeCapture owner;
            private readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Run(FakeCapture owner, string output, int seconds, bool complete)
            {
                this.owner = owner;
                if (complete) _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds));
                    await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), "{}");
                    Result = "{\"reason\":\"capture.durationComplete\",\"writtenPackets\":3}";
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

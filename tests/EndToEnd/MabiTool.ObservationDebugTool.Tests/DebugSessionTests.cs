using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.ObservationDebugTool;
using Xunit;

namespace MabiTool.ObservationDebugTool.Tests;

public sealed class DebugSessionTests : IDisposable
{
    private readonly string root = Path.Combine(FixtureEnvironment.RepositoryRoot(), "artifacts", "test-data", "debugtool-session-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ConsecutiveScenariosEachWriteTheirOwnReportWithEnvironmentAndTimeline()
    {
        var first = new ScriptedScenario("first", async context => { context.Mark("first.step"); await Task.Delay(50); return Done(); });
        var second = new ScriptedScenario("second", async context => { await Task.Delay(10); return Done(); });
        var (session, loop) = Start(first, second);
        Assert.True(session.Start());
        Assert.False(session.Start());
        await Until(session, value => value.Results.Count == 1 && value.State == DebugSession.Idle);
        Assert.True(session.Select("second"));
        Assert.True(session.Start());
        await Until(session, value => value.Results.Count == 2 && value.State == DebugSession.Idle);
        session.RequestExit(); await loop;
        var paths = session.Results.Select(value => value.ReportPath!).ToArray();
        Assert.Equal(2, paths.Distinct().Count());
        Assert.All(paths, path => Assert.StartsWith(Path.Combine(root, "reports"), path));
        var report = JsonNode.Parse(await File.ReadAllTextAsync(paths[0]))!;
        Assert.Equal("fixture-evidence", (string?)report["evidenceKind"]);
        Assert.Equal("completed", (string?)report["scenario"]!["outcome"]);
        Assert.Equal("fixture", (string?)report["environment"]!["mode"]);
        Assert.NotNull(report["environment"]!["load"]!["logicalProcessors"]);
        Assert.Contains(report["operatorTimeline"]!.AsArray(), entry => (string?)entry!["Code"] == "first.step");
        Assert.Equal(DebugSession.Exited, session.State);
    }

    [Fact]
    public async Task StopCancelsRunningScenarioAndReleasesTrackedResources()
    {
        var resource = new Resource();
        var scenario = new ScriptedScenario("stoppable", async context =>
        {
            context.Track(resource);
            await Task.Delay(Timeout.Infinite, context.Stopped);
            return Done();
        });
        var (session, loop) = Start(scenario);
        session.Start();
        await Until(session, value => value.CanStop);
        session.Stop();
        await Until(session, value => value.Results.Count == 1);
        Assert.Equal(ScenarioResult.Cancelled, session.Results[0].Outcome);
        Assert.True(resource.Disposed);
        Assert.NotNull(session.Results[0].ReportPath);
        session.RequestExit(); await loop;
    }

    [Fact]
    public async Task ScenarioCrashIsReportedAsFailureAfterReleasingResources()
    {
        var resource = new Resource();
        var scenario = new ScriptedScenario("crash", async context =>
        {
            context.Track(resource); await Task.Yield();
            throw new ProbeException("fixture.crashed");
        });
        var (session, loop) = Start(scenario);
        session.Start();
        await Until(session, value => value.Results.Count == 1);
        var result = session.Results[0];
        Assert.Equal(ScenarioResult.Failed, result.Outcome);
        Assert.True(resource.Disposed);
        var report = JsonNode.Parse(await File.ReadAllTextAsync(result.ReportPath!))!;
        Assert.Equal("fixture.crashed", (string?)report["failure"]);
        session.RequestExit(); await loop;
    }

    [Fact]
    public async Task ScenarioExceedingTheTotalCeilingTimesOut()
    {
        var resource = new Resource();
        var scenario = new ScriptedScenario("ceiling", async context =>
        {
            context.Track(resource);
            await Task.Delay(Timeout.Infinite, context.Stopped);
            return Done();
        }, preparation: 0, runSeconds: 1, cleanup: 0);
        var (session, loop) = Start(scenario);
        session.Start();
        await Until(session, value => value.Results.Count == 1, TimeSpan.FromSeconds(10));
        Assert.Equal(ScenarioResult.TimedOut, session.Results[0].Outcome);
        Assert.True(resource.Disposed);
        session.RequestExit(); await loop;
    }

    [Fact]
    public async Task OperatorActionAndVerificationAnswerReachTheScenario()
    {
        var acted = new TaskCompletionSource();
        string? answer = null;
        var scenario = new ScriptedScenario("interactive", async context =>
        {
            var enabled = false;
            context.Offer("开始", () => enabled, () => acted.TrySetResult());
            Assert.False(context.CurrentOffer()!.Enabled);
            enabled = true;
            await acted.Task.WaitAsync(context.Stopped);
            context.ClearOffer();
            answer = await context.AskAsync("是否一致？", "明细", ["一致", "有差异"], TimeSpan.FromSeconds(30));
            return Done();
        });
        var (session, loop) = Start(scenario);
        session.Start();
        await Until(session, value => value.ActionEnabled);
        session.Act();
        await Until(session, value => value.Question is not null);
        Assert.False(session.Answer("其他"));
        Assert.True(session.Answer("一致"));
        await Until(session, value => value.Results.Count == 1);
        Assert.Equal("一致", answer);
        session.RequestExit(); await loop;
    }

    [Fact]
    public async Task ExitDuringRunStopsFirstAndPreviewOrUnavailableScenariosNeverStart()
    {
        var resource = new Resource();
        var scenario = new ScriptedScenario("exit", async context =>
        {
            context.Track(resource);
            await Task.Delay(Timeout.Infinite, context.Stopped);
            return Done();
        });
        var (session, loop) = Start(scenario);
        session.Start();
        await Until(session, value => value.CanStop);
        session.RequestExit();
        await loop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DebugSession.Exited, session.State);
        Assert.True(resource.Disposed);
        Assert.False(session.Start());

        var preview = new DebugSession([new ScriptedScenario("preview", _ => Task.FromResult(Done()))], new ReportDirectory(root, TimeProvider.System),
            TimeProvider.System, root, Describe, preview: true);
        Assert.False(preview.Start());
        var unavailable = new DebugSession([new ScriptedScenario("missing", _ => Task.FromResult(Done()), unavailable: "sample.pythonNotConfigured")],
            new ReportDirectory(root, TimeProvider.System), TimeProvider.System, root, Describe);
        Assert.False(unavailable.Start());
        Assert.False(unavailable.Snapshot().CanStart);
    }

    [Fact]
    public void ReportsNeverOverwriteWithinTheSameSecondAndRejectUnsafePrefixes()
    {
        var clock = new FixedClock();
        var sink = new ReportDirectory(Path.Combine(root, "same-second"), clock);
        var first = sink.Write("combat-native-interactive", new JsonObject { ["n"] = 1 });
        var second = sink.Write("combat-native-interactive", new JsonObject { ["n"] = 2 });
        Assert.NotEqual(first, second);
        Assert.EndsWith("-2.json", second);
        Assert.Equal(1, JsonNode.Parse(File.ReadAllText(first))!["n"]!.GetValue<int>());
        Assert.Throws<ArgumentException>(() => sink.Write("../escape", new JsonObject()));
    }

    [Fact]
    public async Task TimelineRunsActionsInOrderAgainstTheMonotonicClock()
    {
        var order = new List<string>();
        var scenario = new ScriptedScenario("timeline", async context =>
        {
            await ScenarioTimeline.RunAsync(context,
            [
                new(0, "a", "first", _ => { order.Add("a"); return Task.CompletedTask; }),
                new(0.2, "b", "second", _ => { order.Add("b"); return Task.CompletedTask; }),
                new(0.4, "c", "third", _ => { order.Add("c"); return Task.CompletedTask; }),
            ], 0.6, context.Stopped);
            return Done();
        });
        var (session, loop) = Start(scenario);
        session.Start();
        await Until(session, value => value.Results.Count == 1);
        Assert.Equal(["a", "b", "c"], order);
        Assert.Equal(ScenarioResult.Completed, session.Results[0].Outcome);
        session.RequestExit(); await loop;
        var context = new List<TimelineStep> { new(1, "late start", "x") };
        var invalid = new ScriptedScenario("invalid", async value => { await ScenarioTimeline.RunAsync(value, context, 2, value.Stopped); return Done(); });
        var (other, otherLoop) = Start(invalid);
        other.Start();
        await Until(other, value => value.Results.Count == 1);
        Assert.Equal(ScenarioResult.Failed, other.Results[0].Outcome);
        other.RequestExit(); await otherLoop;
    }

    internal static IReadOnlyDictionary<string, object?> Describe() => new Dictionary<string, object?> { ["mode"] = "fixture" };
    private static ScenarioResult Done() => new(ScenarioResult.Completed, true, "ok", new JsonObject());

    private (DebugSession Session, Task Loop) Start(params IDebugScenario[] scenarios)
    {
        var session = new DebugSession(scenarios, new ReportDirectory(Path.Combine(root, "reports"), TimeProvider.System), TimeProvider.System,
            Path.Combine(root, "scratch"), Describe);
        return (session, session.RunAsync());
    }

    internal static async Task Until(DebugSession session, Func<DebugSnapshot, bool> condition, TimeSpan? limit = null)
    {
        using var timeout = new CancellationTokenSource(limit ?? TimeSpan.FromSeconds(30));
        while (!condition(session.Snapshot()))
        {
            try { await Task.Delay(20, timeout.Token); }
            catch (OperationCanceledException) { throw new TimeoutException(JsonSerializer.Serialize(session.Snapshot())); }
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class Resource : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);
    }
}

internal sealed class ScriptedScenario(string id, Func<ScenarioContext, Task<ScenarioResult>> body, int preparation = 10, int runSeconds = 10,
    int cleanup = 10, string? unavailable = null) : IDebugScenario
{
    public ScenarioPlan Plan => new(id, id, "fixture", "fixture-evidence", "fixture-" + id, preparation, runSeconds, ["动作"], unavailable)
    { CleanupAllowanceSeconds = cleanup };
    public Task<ScenarioResult> RunAsync(ScenarioContext context) => body(context);
}

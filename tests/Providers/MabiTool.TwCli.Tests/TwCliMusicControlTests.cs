using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwCli;
using Xunit;

namespace MabiTool.TwCli.Tests;

public sealed class TwCliMusicControlTests
{
    private static readonly GameActivityProof Proof = new("lease-1", new(7), "owner", "game-1", new(3));
    private static readonly string[] Everything =
        ["get_music_scores", "play_music_score", "get_instruments", "change_instrument", "get_activity", "stop_action"];

    private sealed class Rig
    {
        public ScriptedRunner Runner { get; } = new();
        public bool Available { get; set; } = true;
        public string? ProofProblem { get; set; }
        public Dictionary<string, TwCliRun> Responses { get; } = [];
        public string[] CatalogCommands { get; set; } = Everything;
        public bool CatalogLoading { get; set; }
        public TwCliService Service { get; }
        public TwCliMusicControl Music { get; }
        public Rig()
        {
            Runner.Respond = (command, _) => command == "capabilities" ? ScriptedRunner.Ok(ScriptedRunner.Catalog(CatalogLoading, CatalogCommands))
                : Responses.TryGetValue(command, out var run) ? run : ScriptedRunner.Ok("{}");
            Service = new(() => new("cli.exe", "game", "s1"), Runner);
            Music = new(Service, () => Available ? new(true, "available") : new(false, "gameCli.disabled"), _ => ProofProblem);
        }
        public string[] GameCommands => Runner.Commands.Where(x => x != "capabilities").ToArray();
    }

    private static TwCliRun Ok(string json) => ScriptedRunner.Ok(json);

    [Fact]
    public async Task OnlyScoresInTheGameListAreProvidedAndTitleMatchingIgnoresRichTextAndEdgeWhitespace()
    {
        var rig = new Rig();
        Assert.Null(rig.Music.TryIsGameProvided("少年冒險家"));
        rig.Responses["get_music_scores"] = Ok("[{\"Location\":\"inventory\",\"DisplayTitle\":\"少年冒險家\",\"IsCopyingAllowed\":false,\"IsLocked\":false}]");
        var listing = await rig.Music.ListScoresAsync(TimeSpan.FromMinutes(1), default);
        Assert.True(listing.Available);
        Assert.Equal("少年冒險家", Assert.Single(listing.Scores).DisplayTitle);
        Assert.True(rig.Music.TryIsGameProvided(" <color=#ffffff>少年冒險家</color> "));
        Assert.False(rig.Music.TryIsGameProvided("新寶島"));
        await rig.Music.ListScoresAsync(TimeSpan.FromMinutes(1), default);
        Assert.Single(rig.GameCommands, "get_music_scores");
    }

    [Fact]
    public async Task UserComposedRefusalIsRememberedUntilTheListIsReloaded()
    {
        var rig = new Rig();
        rig.Responses["get_music_scores"] = Ok("[{\"DisplayTitle\":\"少年冒險家\"}]");
        await rig.Music.ListScoresAsync(TimeSpan.Zero, default);
        rig.Responses["play_music_score"] = Ok("{\"error\":\"user_composed_not_allowed\",\"message\":\"no\"}");
        var refused = await rig.Music.PlayScoreAsync(Proof, "少年冒險家", default);
        Assert.Equal((GameMusicOutcome.Rejected, "gameCli.userComposedNotAllowed"), (refused.Outcome, refused.Code));
        Assert.False(rig.Music.TryIsGameProvided("少年冒險家"));
        await rig.Music.ListScoresAsync(TimeSpan.Zero, default);
        Assert.True(rig.Music.TryIsGameProvided("少年冒險家"));
    }

    [Fact]
    public async Task ExecutiveCommandsNeedTheConnectorTheLeaseAndACatalogThatHasTheCommand()
    {
        var rig = new Rig { Available = false };
        var result = await rig.Music.StopAsync(Proof, default);
        Assert.Equal((GameMusicOutcome.Unavailable, "gameCli.disabled"), (result.Outcome, result.Code));
        Assert.True(result.NotSent);
        Assert.Empty(rig.Runner.Calls);

        rig.Available = true; rig.ProofProblem = "input.activityLeaseRequired";
        Assert.Equal("input.activityLeaseRequired", (await rig.Music.PlayScoreAsync(Proof, "x", default)).Code);
        Assert.Empty(rig.Runner.Calls);

        rig.ProofProblem = null; rig.CatalogCommands = ["get_activity"];
        var missing = await rig.Music.StopAsync(Proof, default);
        Assert.Equal((GameMusicOutcome.Unavailable, "gameCli.commandMissing"), (missing.Outcome, missing.Code));
        Assert.DoesNotContain("stop_action", rig.Runner.Commands);
    }

    [Fact]
    public async Task LoadingCatalogBlocksExecutiveCommands()
    {
        var rig = new Rig { CatalogLoading = true };
        var result = await rig.Music.ChangeInstrumentAsync(Proof, "鋼琴", default);
        Assert.Equal((GameMusicOutcome.Unavailable, "gameCli.catalogLoading"), (result.Outcome, result.Code));
        Assert.DoesNotContain("change_instrument", rig.Runner.Commands);
    }

    [Fact]
    public async Task OutcomesDistinguishAcceptedRejectedNotConnectedInterruptedAndFailed()
    {
        var rig = new Rig();
        rig.Responses["stop_action"] = Ok("{\"status\":\"accepted\",\"body\":{\"message\":\"stopped\"}}");
        Assert.True((await rig.Music.StopAsync(Proof, default)).Accepted);

        rig.Responses["stop_action"] = Ok("{\"status\":\"rejected\",\"body\":{\"error\":\"invalid_state\"}}");
        var invalid = await rig.Music.StopAsync(Proof, default);
        Assert.Equal((GameMusicOutcome.Rejected, "gameCli.invalidState"), (invalid.Outcome, invalid.Code));

        rig.Responses["play_music_score"] = new(5, "", "option_off", false, TimeSpan.Zero);
        var notConnected = await rig.Music.PlayScoreAsync(Proof, "x", default);
        Assert.Equal((GameMusicOutcome.Unavailable, "gameCli.notConnected"), (notConnected.Outcome, notConnected.Code));

        rig.Responses["play_music_score"] = new(7, "", "boom", false, TimeSpan.Zero);
        Assert.Equal(GameMusicOutcome.Failed, (await rig.Music.PlayScoreAsync(Proof, "x", default)).Outcome);

        rig.Responses["play_music_score"] = new(-1, "", "", true, TimeSpan.Zero);
        var interrupted = await rig.Music.PlayScoreAsync(Proof, "x", default);
        Assert.Equal((GameMusicOutcome.Interrupted, "gameCli.interrupted"), (interrupted.Outcome, interrupted.Code));
        Assert.False(interrupted.Accepted);
    }

    [Fact]
    public async Task InstrumentsAndPerformanceActivityAreParsedFromTheGameResponses()
    {
        var rig = new Rig();
        rig.Responses["get_instruments"] = Ok("[{\"Name\":\"鋼琴\",\"Durability\":88.5,\"IsEquipped\":true},{\"Name\":\"小提琴\",\"IsEquipped\":false}]");
        var instruments = (await rig.Music.ListInstrumentsAsync(default))!;
        Assert.Equal(["鋼琴", "小提琴"], instruments.Select(x => x.Name));
        Assert.True(instruments[0].Equipped);
        Assert.Equal(88.5, instruments[0].Durability);
        Assert.Null(instruments[1].Durability);

        rig.Responses["get_activity"] = Ok("""
            {"Performance":{"IsPlaying":true,"InstrumentName":"鋼琴","MusicTitle":"少年冒險家","IsLoop":true,"TotalDurationSeconds":120.5,
             "ElapsedSeconds":30,"RemainingSeconds":90.5,"ChannelCount":2},"combatState":{"IsDead":false,"IsInCombat":true}}
            """);
        var activity = (await rig.Music.ReadActivityAsync(default))!;
        Assert.True(activity.IsPlaying);
        Assert.Equal((120.5, 90.5, 2), (activity.TotalSeconds, activity.RemainingSeconds, activity.ChannelCount));
        Assert.True(activity.IsInCombat);

        rig.Responses["get_activity"] = Ok("{\"autoPlay\":{}}");
        Assert.Null(await rig.Music.ReadActivityAsync(default));
        rig.Available = false;
        Assert.Null(await rig.Music.ListInstrumentsAsync(default));
    }

    [Fact]
    public void InstrumentMatchingPrefersTheFullNameThenAUniqueContainingName()
    {
        GameInstrument[] owned = [new("鋼琴", false, null), new("高級豎琴", true, null), new("小提琴", false, null), new("洛奇小提琴", false, null)];
        Assert.Equal("鋼琴", GameMusicNames.MatchInstrument(owned, " 鋼琴 ", out _)!.Name);
        Assert.Equal("高級豎琴", GameMusicNames.MatchInstrument(owned, "豎琴", out var ambiguous)!.Name);
        Assert.False(ambiguous);
        Assert.Null(GameMusicNames.MatchInstrument(owned, "吉他", out ambiguous));
        Assert.False(ambiguous);
        Assert.Equal("小提琴", GameMusicNames.MatchInstrument(owned, "小提琴", out _)!.Name);
        GameInstrument[] twins = [new("A提琴", false, null), new("B提琴", false, null)];
        Assert.Null(GameMusicNames.MatchInstrument(twins, "提琴", out ambiguous));
        Assert.True(ambiguous);
    }
}

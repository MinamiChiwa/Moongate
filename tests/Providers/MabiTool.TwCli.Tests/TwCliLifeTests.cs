using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwCli;
using Xunit;

namespace MabiTool.TwCli.Tests;

public sealed class TwCliLifeTests
{
    private static readonly GameActivityProof Proof = new("lease-1", new(7), "life", "game-1", new(3));
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void ReadResponsesKeepMissingFieldsEmptyAndSkipRowsWithoutAName()
    {
        var gatherables = TwCliLifeParser.Gatherables(Json("{\"items\":[{\"DisplayName\":\"原木\",\"ToolOk\":true},{\"DisplayName\":\"堅硬原木\"},{\"ToolOk\":true},5]}"))!;
        Assert.Equal([("原木", (bool?)true), ("堅硬原木", null)], gatherables.Select(x => (x.Name, x.ToolOk)));
        Assert.Empty(TwCliLifeParser.Gatherables(Json("{\"items\":[]}"))!);
        Assert.Null(TwCliLifeParser.Gatherables(Json("{\"other\":1}")));
        Assert.Null(TwCliLifeParser.Gatherables(Json("[]")));

        var alterable = TwCliLifeParser.Alterables(Json("{\"items\":[{\"DisplayName\":\"高級絲綢\",\"Alterable\":false,\"ProducedPerWork\":1,\"Reason\":\"not_enough_ingredient\"," +
            "\"MissingIngredients\":[{\"DisplayName\":\"壯壯蘑菇汁液\",\"Required\":8,\"Owned\":0},{\"DisplayName\":\"絲\",\"Required\":2}]},{\"DisplayName\":\"壯壯蘑菇粉末\",\"Alterable\":true}]}"))!;
        Assert.Equal(("高級絲綢", (bool?)false, (long?)1, "not_enough_ingredient"), (alterable[0].Name, alterable[0].Alterable, alterable[0].ProducedPerWork, alterable[0].Reason));
        Assert.Equal([("壯壯蘑菇汁液", (long?)8, (long?)0), ("絲", 2, null)], alterable[0].Missing.Select(x => (x.Name, x.Required, x.Owned)));
        Assert.Equal(((long?)null, (string?)null, 0), (alterable[1].ProducedPerWork, alterable[1].Reason, alterable[1].Missing.Count));

        var queue = TwCliLifeParser.Works(Json("{\"completedCount\":6,\"works\":[{\"DisplayName\":\"皮革+\",\"FacilityName\":\"皮革加工設備\",\"State\":\"Completed\",\"IsCompleted\":true,\"RemainingSeconds\":0}," +
            "{\"DisplayName\":\"合金鋼錠\",\"FacilityName\":\"金屬加工設備\",\"State\":\"InProgress\",\"IsCompleted\":false,\"RemainingSeconds\":93.5},{\"DisplayName\":\"沒有設施\"}]}"))!;
        Assert.Equal(6, queue.CompletedCount);
        Assert.Equal([("皮革+", "皮革加工設備", (bool?)true, (double?)0), ("合金鋼錠", "金屬加工設備", false, 93.5)], queue.Works.Select(x => (x.Name, x.Facility, x.Completed, x.RemainingSeconds)));
        Assert.Null(TwCliLifeParser.Works(Json("{\"completedCount\":0}")));
        Assert.Null(TwCliLifeParser.Works(Json("{\"works\":[]}"))!.CompletedCount);

        var crafts = TwCliLifeParser.Craftables(Json("{\"craftingUnlocked\":true,\"items\":[{\"DisplayName\":\"長劍\",\"Variant\":2,\"Craftable\":false,\"ProducedPerCraft\":1," +
            "\"SuccessRate\":87.5,\"Reason\":\"insufficient_facility_level\",\"Ingredients\":[{\"DisplayName\":\"鋼錠\",\"Required\":3,\"Owned\":1}]},{\"DisplayName\":\"木盾\"}]}"))!;
        Assert.Equal((true, 2), (crafts.Unlocked, crafts.Items.Count));
        Assert.Equal((2, (double?)87.5, (double?)null, "insufficient_facility_level"), (crafts.Items[0].Variant, crafts.Items[0].SuccessRate, crafts.Items[0].CriticalSuccessRate, crafts.Items[0].Reason));
        Assert.Equal((null, null, 0), (crafts.Items[1].Variant, crafts.Items[1].Craftable, crafts.Items[1].Ingredients.Count));
        Assert.Null(TwCliLifeParser.Craftables(Json("{\"craftingUnlocked\":false,\"items\":[]}"))!.Items.FirstOrDefault());
    }

    [Fact]
    public void CostsComeFromTheCatalogNoteAndTheSentenceTheClientReturns()
    {
        Assert.Equal(("5 精靈之翼", 5L, "金幣"), (TwCliLifeCost.FromNote("Works. Running this command consumes 5 精靈之翼. It is rejected with not_enough_currency.")!.Text,
            TwCliLifeCost.FromNote("Running this command consumes 5 精靈之翼. It is")!.Amount, TwCliLifeCost.FromNote("consumes 1,200 `金幣`.")!.Currency));
        Assert.Null(TwCliLifeCost.FromNote("Collects all completed altering works at ONE facility only."));
        Assert.Null(TwCliLifeCost.FromNote(null));

        Assert.Equal(("精靈之翼", 5L, 11530L), (TwCliLifeCost.FromResult("精靈之翼 5 spent, 11530 left.")!.Currency, TwCliLifeCost.FromResult("精靈之翼 5 spent, 11530 left.")!.Spent,
            TwCliLifeCost.FromResult("精靈之翼 5 spent, 11,530 left")!.Left));
        Assert.Null(TwCliLifeCost.FromResult("花费未知"));
        Assert.Null(TwCliLifeCost.FromResult(null));
    }

    [Theory]
    [InlineData("{\"status\":\"accepted\",\"body\":{\"result\":\"completed\",\"gained\":16,\"target\":10,\"cost\":\"精靈之翼 5 spent, 11530 left.\"}}", TwCliLifeKind.Completed, null, 16L, 10L)]
    [InlineData("{\"result\":\"started\",\"message\":\"Altering work queued.\",\"cost\":\"精靈之翼 5 spent, 11525 left.\"}", TwCliLifeKind.Started, null, null, null)]
    [InlineData("{\"result\":\"stopped\",\"gained\":3,\"target\":10,\"message\":\"m\"}", TwCliLifeKind.Stopped, null, 3L, 10L)]
    [InlineData("{\"result\":\"stopped_by_user\"}", TwCliLifeKind.StoppedByUser, null, null, null)]
    [InlineData("{\"error\":\"blocked\",\"kind\":\"unknown_modal\",\"message\":\"covered\"}", TwCliLifeKind.Rejected, "gameCli.blocked", null, null)]
    [InlineData("{\"status\":\"accepted\",\"body\":{\"error\":\"overweight\",\"gained\":4,\"target\":10}}", TwCliLifeKind.Rejected, "gameCli.overweight", 4L, 10L)]
    [InlineData("{\"status\":\"rejected\",\"body\":{\"error\":\"not_enough_currency\",\"message\":\"m\"}}", TwCliLifeKind.Rejected, "gameCli.notEnoughCurrency", null, null)]
    [InlineData("{\"status\":\"invalid_body\",\"body\":{\"message\":\"displayName missing\"}}", TwCliLifeKind.Rejected, "gameCli.invalidBody", null, null)]
    [InlineData("{\"unknown\":1}", TwCliLifeKind.Failed, "gameCli.unparseable", null, null)]
    public void ExecutionResponsesAreClassifiedWithTheGameReason(string json, TwCliLifeKind kind, string? code, long? gained, long? target)
    {
        var outcome = TwCliLifeOutcome.From(TwCliResponse.Classify("execute_gathering", new(0, json, "", false, TimeSpan.FromMilliseconds(5))));
        Assert.Equal((kind, code, gained, target), (outcome.Kind, outcome.Code, outcome.Gained, outcome.Target));
    }

    [Fact]
    public void BlockedCarriesItsKindCollectCarriesRewardsAndTheCatalogLimitIsKept()
    {
        TwCliLifeOutcome Parse(string json) => TwCliLifeOutcome.From(TwCliResponse.Classify("x", new(0, json, "", false, TimeSpan.Zero)));
        var blocked = Parse("{\"error\":\"blocked\",\"kind\":\"unknown_modal\",\"message\":\"covered\"}");
        Assert.Equal(("unknown_modal", "covered"), (blocked.BlockedKind, blocked.Message));
        var collected = Parse("{\"collected\":5,\"rewards\":[{\"Name\":\"皮革+\",\"Amount\":3},{\"Name\":\"皮革\"}],\"criticalRewards\":[],\"message\":\"Collected 5 altering works.\"}");
        Assert.Equal((TwCliLifeKind.Collected, 5L, 0), (collected.Kind, collected.Collected, collected.CriticalRewards!.Count));
        Assert.Equal([("皮革+", (long?)3), ("皮革", null)], collected.Rewards!.Select(x => (x.Name, x.Amount)));
        var crafted = Parse("{\"result\":\"completed\",\"craftCount\":2,\"rewards\":[]}");
        Assert.Equal((TwCliLifeKind.Completed, 2L), (crafted.Kind, crafted.CraftCount));
        var limit = Parse("{\"status\":\"rejected\",\"body\":{\"error\":\"invalid_count\",\"maxCount\":3}}");
        Assert.Equal(("gameCli.invalidCount", 3L), (limit.Code, limit.MaxCount));

        Assert.Equal(TwCliLifeKind.Interrupted, TwCliLifeOutcome.From(TwCliResponse.Classify("x", new(-1, "", "", true, TimeSpan.Zero))).Kind);
        Assert.Equal((TwCliLifeKind.Unavailable, true), (TwCliLifeOutcome.From(TwCliResponse.Classify("x", new(5, "", "option_off", false, TimeSpan.Zero))).Kind,
            TwCliLifeOutcome.From(TwCliResponse.Classify("x", new(5, "", "option_off", false, TimeSpan.Zero))).NotSent));
        Assert.Equal(TwCliLifeKind.Failed, TwCliLifeOutcome.From(TwCliResponse.Classify("x", new(7, "", "boom", false, TimeSpan.Zero))).Kind);
    }

    private sealed class Rig
    {
        public ScriptedRunner Runner { get; } = new();
        public bool Available { get; set; } = true;
        public string? ProofProblem { get; set; }
        public string[] CatalogCommands { get; set; } = [.. TwCliLifeControl.GatherCommands, .. TwCliLifeControl.AlterCommands, .. TwCliLifeControl.CraftCommands];
        public Dictionary<string, string> Responses { get; } = [];
        public TwCliLifeControl Control { get; }
        public Rig()
        {
            Runner.Respond = (command, _) => command == "capabilities" ? ScriptedRunner.Ok(ScriptedRunner.Catalog(false, CatalogCommands))
                : ScriptedRunner.Ok(Responses.GetValueOrDefault(command, "{}"));
            Control = new(new TwCliService(() => new("cli.exe", "game", "s1"), Runner), () => Available ? new(true, "available") : new(false, "gameCli.disabled"), _ => ProofProblem);
        }
        public string[] GameCommands => Runner.Commands.Where(x => x != "capabilities").ToArray();
    }

    [Fact]
    public async Task ReadsSendPlainTextFiltersOnlyWhenGivenAndNeedTheConnectorAndTheCatalogCommand()
    {
        var rig = new Rig();
        rig.Responses["get_gatherable_items"] = "{\"items\":[{\"DisplayName\":\"原木\",\"ToolOk\":false}]}";
        var read = await rig.Control.ReadGatherablesAsync(" 原木 ", default);
        Assert.True(read.Ok);
        Assert.False(read.Value![0].ToolOk);
        var gathered = rig.Runner.Calls.Single(x => x[0] == "get_gatherable_items");
        Assert.Equal("base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("原木")), gathered[1]);
        await rig.Control.ReadGatherablesAsync(null, default);
        Assert.Equal(1, rig.Runner.Calls.Count(x => x[0] == "get_gatherable_items" && x.Length == 1));

        rig.Responses["get_altering_works"] = "{\"error\":\"system_error\",\"message\":\"x\"}";
        Assert.Equal("gameCli.systemError", (await rig.Control.ReadWorksAsync(default)).Code);
        rig.Responses["get_craftable_items"] = "not json";
        Assert.Equal("gameCli.unparseable", (await rig.Control.ReadCraftablesAsync(null, default)).Code);

        rig.CatalogCommands = ["get_gatherable_items"];
        var missing = new Rig { CatalogCommands = ["get_gatherable_items"] };
        Assert.Equal("gameCli.commandMissing", (await missing.Control.ReadAlterablesAsync(null, default)).Code);
        Assert.DoesNotContain("get_alterable_items", missing.GameCommands);
        var off = new Rig { Available = false };
        Assert.Equal("gameCli.disabled", (await off.Control.ReadGatherablesAsync(null, default)).Code);
        Assert.Empty(off.Runner.Calls);
    }

    [Fact]
    public async Task ExecutionNeedsTheConnectorTheLeaseAndACatalogWithTheCommandAndNothingIsSentOtherwise()
    {
        var rig = new Rig { Available = false };
        var body = TwCliBody.Json(new { displayName = "原木" });
        var refused = await rig.Control.ExecuteAsync(Proof, "execute_gathering", body, default);
        Assert.Equal((TwCliLifeKind.Unavailable, "gameCli.disabled", true), (refused.Kind, refused.Code, refused.NotSent));
        Assert.Empty(rig.Runner.Calls);

        rig.Available = true; rig.ProofProblem = "input.activityLeaseRequired";
        Assert.Equal("input.activityLeaseRequired", (await rig.Control.ExecuteAsync(Proof, "execute_gathering", body, default)).Code);
        Assert.Empty(rig.Runner.Calls);

        rig.ProofProblem = null; rig.CatalogCommands = ["get_gatherable_items"];
        var missing = await rig.Control.ExecuteAsync(Proof, "execute_gathering", body, default);
        Assert.Equal(("gameCli.commandMissing", "execute_gathering"), (missing.Code, missing.Message));
        Assert.DoesNotContain("execute_gathering", rig.Runner.Commands);

        var ready = new Rig();
        ready.Responses["execute_gathering"] = "{\"result\":\"completed\",\"gained\":16,\"target\":10}";
        var done = await ready.Control.ExecuteAsync(Proof, "execute_gathering", body, default);
        Assert.Equal((TwCliLifeKind.Completed, 16L), (done.Kind, done.Gained));
        Assert.Equal("base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"displayName\":\"原木\"}")), ready.Runner.Calls.Single(x => x[0] == "execute_gathering")[1]);

        var loading = new Rig();
        loading.Runner.Respond = (command, _) => command == "capabilities" ? ScriptedRunner.Ok(ScriptedRunner.Catalog(true, "execute_gathering")) : ScriptedRunner.Ok("{}");
        Assert.Equal("gameCli.catalogLoading", (await loading.Control.ExecuteAsync(Proof, "execute_gathering", body, default)).Code);
        Assert.DoesNotContain("execute_gathering", loading.Runner.Commands);
    }

    [Fact]
    public async Task StopActionTreatsNothingToStopAsStoppedAndIsNotSentWithoutTheLease()
    {
        var rig = new Rig { CatalogCommands = ["stop_action"] };
        rig.Responses["stop_action"] = "{\"status\":\"accepted\",\"body\":{\"message\":\"stopped\"}}";
        Assert.True(await rig.Control.StopActionAsync(Proof, default));
        rig.Responses["stop_action"] = "{\"status\":\"rejected\",\"body\":{\"error\":\"invalid_state\"}}";
        Assert.True(await rig.Control.StopActionAsync(Proof, default));
        rig.Responses["stop_action"] = "{\"status\":\"rejected\",\"body\":{\"error\":\"timeout\"}}";
        Assert.False(await rig.Control.StopActionAsync(Proof, default));
        rig.ProofProblem = "input.activityLeaseRequired";
        var calls = rig.GameCommands.Length;
        Assert.False(await rig.Control.StopActionAsync(Proof, default));
        Assert.Equal(calls, rig.GameCommands.Length);
    }
}

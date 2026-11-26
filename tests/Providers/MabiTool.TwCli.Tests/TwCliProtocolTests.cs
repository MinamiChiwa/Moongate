using System.Text;
using System.Text.Json;
using MabiTool.Providers.TwCli;
using Xunit;

namespace MabiTool.TwCli.Tests;

public sealed class TwCliProtocolTests
{
    private static TwCliResult Classify(int exit, string stdout = "", string stderr = "", bool interrupted = false) =>
        TwCliResponse.Classify("cmd", new(exit, stdout, stderr, interrupted, TimeSpan.Zero));

    [Fact]
    public void BodyIsOneBase64ArgumentOfUtf8TextAndPassesThroughExistingBase64()
    {
        var json = TwCliBody.Json(new { category = "Food", name = "馬賽魚湯" });
        Assert.StartsWith("base64:", json.Argument);
        Assert.Equal("{\"category\":\"Food\",\"name\":\"馬賽魚湯\"}", Encoding.UTF8.GetString(Convert.FromBase64String(json.Argument[7..])));
        Assert.Equal("原木", Encoding.UTF8.GetString(Convert.FromBase64String(TwCliBody.Plain("原木").Argument[7..])));
        Assert.Equal("base64:AAAA", TwCliBody.Plain("base64:AAAA").Argument);
    }

    [Fact]
    public void ExitZeroBusinessRefusalsAreRejectedWhileDataAndAcceptedBodiesAreOk()
    {
        var composed = Classify(0, "{\"error\":\"user_composed_not_allowed\",\"message\":\"x\"}");
        Assert.Equal(TwCliResultKind.Rejected, composed.Kind);
        Assert.Equal("user_composed_not_allowed", composed.Error);
        var wrapped = Classify(0, "{\"status\":\"rejected\",\"body\":{\"error\":\"invalid_state\",\"message\":\"nothing\"}}");
        Assert.Equal((TwCliResultKind.Rejected, "invalid_state"), (wrapped.Kind, wrapped.Error));
        Assert.Equal(TwCliResultKind.Rejected, Classify(0, "{\"status\":\"invalid_body\"}").Kind);
        Assert.True(Classify(0, "{\"message\":\"Play started.\"}").IsOk);
        var accepted = Classify(0, "{\"status\":\"accepted\",\"body\":{\"message\":\"ok\"}}");
        Assert.True(accepted.IsOk);
        Assert.Equal("ok", accepted.Body!.Value.GetProperty("message").GetString());
        Assert.True(Classify(0, "[]").IsOk);
        Assert.Equal(JsonValueKind.Array, Classify(0, "[{\"DisplayName\":\"a\"}]").Body!.Value.ValueKind);
    }

    [Theory]
    [InlineData(5, "{\"error\":\"not_connected\",\"reason\":\"option_off\"}", "", "option_off")]
    [InlineData(5, "", "game_off: pipe missing", "game_off")]
    [InlineData(5, "", "", null)]
    public void ExitFiveIsNotConnectedWithTheReason(int exit, string stdout, string stderr, string? reason)
    {
        var result = Classify(exit, stdout, stderr);
        Assert.Equal(TwCliResultKind.NotConnected, result.Kind);
        Assert.Equal(reason, result.Reason);
    }

    [Theory]
    [InlineData(2, TwCliResultKind.Usage)]
    [InlineData(3, TwCliResultKind.Cancelled)]
    [InlineData(4, TwCliResultKind.UnknownCommand)]
    [InlineData(9, TwCliResultKind.ProcessFailure)]
    public void OtherExitCodesKeepTheirMeaning(int exit, TwCliResultKind kind) => Assert.Equal(kind, Classify(exit, "", "bad").Kind);

    [Fact]
    public void UnparseableOutputAndInterruptionAreDistinct()
    {
        Assert.Equal(TwCliResultKind.Unparseable, Classify(0, "not json").Kind);
        Assert.Equal(TwCliResultKind.Interrupted, Classify(-1, "", "", true).Kind);
    }

    [Fact]
    public void CatalogAcceptsBooleanAndStringRequiresConfirmAndLoadingFlag()
    {
        var root = TwCliResponse.TryParse("""
            {"commands":[{"Command":"write_chat","Description":"d","Metadata":{"requiresConfirm":"true"}},
            {"Command":"x","Metadata":{"requiresConfirm":true}},{"Command":"get_activity","Metadata":{}},{"Description":"no name"}],"loading":true}
            """);
        var catalog = TwCliCatalog.Parse(root)!;
        Assert.True(catalog.Loading);
        Assert.Equal(3, catalog.Count);
        Assert.True(catalog.Commands["write_chat"].RequiresConfirm);
        Assert.True(catalog.Commands["x"].RequiresConfirm);
        Assert.False(catalog.Commands["get_activity"].RequiresConfirm);
        Assert.Equal(["stop_action"], catalog.Missing(["get_activity", "stop_action"]));
        Assert.Equal(2, TwCliCatalog.Parse(TwCliResponse.TryParse("[{\"Command\":\"a\"},{\"Command\":\"b\"}]"))!.Count);
        Assert.Null(TwCliCatalog.Parse(TwCliResponse.TryParse("{\"other\":1}")));
    }

    [Theory]
    [InlineData("not_available_on_combat", "gameCli.notAvailableOnCombat")]
    [InlineData("user_composed_not_allowed", "gameCli.userComposedNotAllowed")]
    [InlineData("invalid_state", "gameCli.invalidState")]
    [InlineData("", "gameCli.failed")]
    public void GameCodesBecomeCamelCaseCodes(string game, string expected) => Assert.Equal(expected, TwCliCodes.FromGame(game));
}

using System.Security.Cryptography;
using System.Text;
using MabiTool.Contracts;
using MabiTool.Providers.TwCli;
using Xunit;

namespace MabiTool.TwCli.Tests;

public sealed class TwCliFilesTests
{
    private static readonly byte[] Good = Encoding.UTF8.GetBytes("registered cli bytes");
    private static readonly string GoodHash = Convert.ToHexStringLower(SHA256.HashData(Good));

    private static TwCliFiles Create(string? package, params string[] korean) => new(() => package, () => korean, GoodHash);

    [Fact]
    public void PackageSourceWinsOverTheLocalKoreanFileAndBothAreHashChecked()
    {
        using var workspace = new TwCliWorkspace();
        var package = workspace.PathFor("package", "cli.exe"); var korean = workspace.PathFor("kr", "cli.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(package)!); Directory.CreateDirectory(Path.GetDirectoryName(korean)!);
        File.WriteAllBytes(package, Good); File.WriteAllBytes(korean, Good);
        var source = Create(package, korean).Resolve();
        Assert.Equal((GameConnectorCliSource.Package, GameConnectorCliFileState.Verified, package), (source.Source, source.State, source.Path));

        File.WriteAllBytes(package, Encoding.UTF8.GetBytes("changed"));
        Assert.Equal(GameConnectorCliSource.LocalKorean, Create(package, korean).Resolve().Source);

        File.WriteAllBytes(korean, Encoding.UTF8.GetBytes("changed too"));
        var mismatch = Create(package, korean).Resolve();
        Assert.Equal(GameConnectorCliFileState.HashMismatch, mismatch.State);
        Assert.Equal(GameConnectorCliSource.Package, mismatch.Source);
        Assert.Equal(GameConnectorCliFileState.Missing, Create(null, workspace.PathFor("none.exe")).Resolve().State);
    }

    [Fact]
    public void DeployCopiesWhenAbsentSkipsWhenIdenticalAndRefusesADifferentFileWithoutTouchingIt()
    {
        using var workspace = new TwCliWorkspace();
        var package = workspace.PathFor("package.exe"); var game = workspace.PathFor("game");
        Directory.CreateDirectory(game);
        File.WriteAllBytes(package, Good);
        var files = Create(package);
        Assert.Equal("gameCli.directoryInvalid", Assert.Throws<TwCliFileException>(() => files.Deploy(game)).Code);

        File.WriteAllText(Path.Combine(game, TwCliFile.GameExecutable), "game");
        Assert.False(files.Inspect(game).Verified);
        files.Deploy(game);
        var target = Path.Combine(game, TwCliFile.FileName);
        Assert.Equal(Good, File.ReadAllBytes(target));
        Assert.True(files.Inspect(game).Verified);
        var stamp = File.GetLastWriteTimeUtc(target);
        files.Deploy(game);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(target));

        File.WriteAllBytes(target, Encoding.UTF8.GetBytes("somebody else's cli"));
        Assert.True(files.Inspect(game).Differs);
        Assert.Equal("gameCli.deployedDiffers", Assert.Throws<TwCliFileException>(() => files.Deploy(game)).Code);
        Assert.Equal("somebody else's cli", File.ReadAllText(target));
    }

    [Fact]
    public void DeployWithoutAVerifiedSourceIsRefused()
    {
        using var workspace = new TwCliWorkspace();
        var game = workspace.PathFor("game"); Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, TwCliFile.GameExecutable), "game");
        var bad = workspace.PathFor("bad.exe"); File.WriteAllText(bad, "wrong");
        Assert.Equal("gameCli.cliUnavailable", Assert.Throws<TwCliFileException>(() => Create(bad).Deploy(game)).Code);
        Assert.False(File.Exists(Path.Combine(game, TwCliFile.FileName)));
    }
}

using System.Text;
using MabiTool.Providers.TwCli;
using Xunit;

namespace MabiTool.TwCli.Tests;

public sealed class MmPlayerPrefsTests
{
    private const string Base = "STRING\nsome_text : hello\nINT\nvolume : 7\nBOOL\nother_flag : True\n";

    private sealed class FakeProbe(GameProcessState state = GameProcessState.NotRunning) : IGameProcessProbe
    {
        public GameProcessState State { get; set; } = state;
        public Action? OnProbe { get; set; }
        public int Calls { get; private set; }
        public GameProcessInfo Probe(string gameDirectory) { Calls++; OnProbe?.Invoke(); return new(State); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TwCliWorkspace workspace = new();
        public FakeProbe Probe { get; }
        public string Prefs => workspace.PathFor("prefs");
        public string State => workspace.PathFor("state");
        public string BaseFile => Path.Combine(Prefs, "BaseMMPlayerPrefs.txt");
        public string ChangedFile => Path.Combine(Prefs, "ChangedMMPlayerPrefs.txt");
        public MmPlayerPrefs Subject { get; }
        public Fixture(string? baseText = Base, string? changed = null, GameProcessState state = GameProcessState.NotRunning)
        {
            Directory.CreateDirectory(Prefs);
            Probe = new(state);
            if (baseText is not null) File.WriteAllText(BaseFile, baseText, new UTF8Encoding(false));
            if (changed is not null) File.WriteAllText(ChangedFile, changed, new UTF8Encoding(false));
            Subject = new(Prefs, State, Probe, new Clock());
        }
        public void Dispose() => workspace.Dispose();
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public void EnableAppendsOnlyTheTwoRecordsWithCurrentUtcTicksAndKeepsOtherContent()
    {
        using var fixture = new Fixture(changed: "+4 : other_flag : False");
        var result = fixture.Subject.Enable("game");
        var ticks = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero).UtcTicks.ToString();
        Assert.Equal("True", result.Enabled);
        Assert.Equal(ticks, result.LastUsedTicks);
        Assert.True(result.EnabledFlag);
        Assert.Equal("+4 : other_flag : False\n+4 : game_use_cli_for_ai : True\n+1 : game_cli_for_ai_last_used_utc_ticks : " + ticks + "\n",
            File.ReadAllText(fixture.ChangedFile));
        Assert.Equal(Base, File.ReadAllText(fixture.BaseFile).Replace("\r\n", "\n"));
        Assert.Empty(Directory.GetFiles(fixture.Prefs, "*.tmp"));
    }

    [Fact]
    public void FirstChangeBacksUpTheFilesAndOriginalValuesOnceAndRestoreBringBackTheOriginals()
    {
        using var fixture = new Fixture(
            baseText: "BOOL\ngame_use_cli_for_ai : False\nSTRING\ngame_cli_for_ai_last_used_utc_ticks : 123\n", changed: "+4 : unrelated : 1\n");
        fixture.Subject.Enable("game");
        var state = Path.Combine(fixture.State, "connector-state.json");
        Assert.True(File.Exists(state));
        var stamp = Assert.Single(Directory.GetDirectories(Path.Combine(fixture.State, "backups")));
        Assert.Equal("+4 : unrelated : 1\n", File.ReadAllText(Path.Combine(stamp, "ChangedMMPlayerPrefs.txt")));
        var stateText = File.ReadAllText(state);
        fixture.Subject.Disable("game");
        Assert.Equal("False", fixture.Subject.Read().Enabled);
        Assert.Single(Directory.GetDirectories(Path.Combine(fixture.State, "backups")));
        Assert.Contains("\"enable\"", File.ReadAllText(state));
        Assert.NotEqual(stateText, File.ReadAllText(state));

        var restored = fixture.Subject.Restore("game");
        Assert.Equal(("False", "123"), (restored.Enabled, restored.LastUsedTicks));
        Assert.Contains("+4 : unrelated : 1", File.ReadAllText(fixture.ChangedFile));
    }

    [Fact]
    public void RestoreRemovesKeysThatDidNotExistBeforeTheFirstChange()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Subject.Read().Enabled);
        fixture.Subject.Enable("game");
        var restored = fixture.Subject.Restore("game");
        Assert.Null(restored.Enabled);
        Assert.Null(restored.LastUsedTicks);
        Assert.Contains("-4 : game_use_cli_for_ai : \n", File.ReadAllText(fixture.ChangedFile));
    }

    [Fact]
    public void RestoreWithoutARecordIsRefused()
    {
        using var fixture = new Fixture();
        Assert.False(fixture.Subject.HasBackup);
        Assert.Equal("gameCli.noBackup", Assert.Throws<TwCliPreferencesException>(() => fixture.Subject.Restore("game")).Code);
    }

    [Theory]
    [InlineData(GameProcessState.Running, "gameCli.gameRunning")]
    [InlineData(GameProcessState.Unverifiable, "gameCli.processUnverifiable")]
    public void WritesAreRefusedWhileTheGameRunsOrCannotBeVerifiedAndNothingChanges(GameProcessState state, string code)
    {
        using var fixture = new Fixture(changed: "+4 : x : y\n", state: state);
        var before = File.ReadAllBytes(fixture.ChangedFile);
        foreach (var action in new Func<MmPlayerPrefsValues>[] { () => fixture.Subject.Enable("game"), () => fixture.Subject.Disable("game"), () => fixture.Subject.Restore("game") })
            Assert.Equal(code, Assert.Throws<TwCliPreferencesException>(action).Code);
        Assert.Equal(before, File.ReadAllBytes(fixture.ChangedFile));
        Assert.False(fixture.Subject.HasBackup);
    }

    [Fact]
    public void ChangeBetweenPreparingAndReplacingKeepsTheOriginalFileAndRemovesTheTemporaryFile()
    {
        using var fixture = new Fixture(changed: "+4 : x : y\n");
        var probes = 0;
        fixture.Probe.OnProbe = () =>
        {
            // 第一次核对在写临时文件之前，第二次在替换之前：此时另一个程序改了增量文件。
            if (++probes == 2) File.AppendAllText(fixture.ChangedFile, "+4 : other : z\n");
        };
        Assert.Equal("gameCli.preferencesChanged", Assert.Throws<TwCliPreferencesException>(() => fixture.Subject.Enable("game")).Code);
        Assert.Equal("+4 : x : y\n+4 : other : z\n", File.ReadAllText(fixture.ChangedFile));
        Assert.Empty(Directory.GetFiles(fixture.Prefs, "*.tmp"));
    }

    [Fact]
    public void ProcessThatStartsBeforeReplacingBlocksTheWrite()
    {
        using var fixture = new Fixture(changed: "+4 : x : y\n");
        var probes = 0;
        fixture.Probe.OnProbe = () => { if (++probes == 2) fixture.Probe.State = GameProcessState.Running; };
        Assert.Equal("gameCli.gameRunning", Assert.Throws<TwCliPreferencesException>(() => fixture.Subject.Enable("game")).Code);
        Assert.Equal("+4 : x : y\n", File.ReadAllText(fixture.ChangedFile));
        Assert.Empty(Directory.GetFiles(fixture.Prefs, "*.tmp"));
    }

    [Theory]
    [InlineData("STRING\ngame_use_cli_for_ai : True\n", null)]
    [InlineData("BOOL\ngame_use_cli_for_ai : True\n", "+1 : game_use_cli_for_ai : True\n")]
    [InlineData("BOOL\ngame_use_cli_for_ai : True\n", "+4 : game_use_cli_for_ai\n")]
    public void UnexpectedSectionOrIncrementTokenIsRefusedBeforeAnyWrite(string baseText, string? changed)
    {
        using var fixture = new Fixture(baseText, changed);
        Assert.Equal("gameCli.preferencesInvalid", Assert.Throws<TwCliPreferencesException>(() => fixture.Subject.Read()).Code);
        Assert.Equal("gameCli.preferencesInvalid", Assert.Throws<TwCliPreferencesException>(() => fixture.Subject.Enable("game")).Code);
        Assert.False(fixture.Subject.HasBackup);
    }

    [Fact]
    public void MissingBaseFileIsReportedAndBomAndCrlfAreTolerated()
    {
        using (var missing = new Fixture(baseText: null))
            Assert.Equal("gameCli.preferencesMissing", Assert.Throws<TwCliPreferencesException>(() => missing.Subject.Enable("game")).Code);
        using var fixture = new Fixture("﻿BOOL\r\ngame_use_cli_for_ai : True\r\n", "+4 : game_use_cli_for_ai : False");
        Assert.Equal("False", fixture.Subject.Read().Enabled);
        fixture.Subject.Enable("game");
        Assert.StartsWith("+4 : game_use_cli_for_ai : False\n+4 : game_use_cli_for_ai : True\n", File.ReadAllText(fixture.ChangedFile));
    }
}

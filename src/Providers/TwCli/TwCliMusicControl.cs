using System.Text.Json;
using MabiTool.ModuleSdk;

namespace MabiTool.Providers.TwCli;

/// <summary>
/// 经台服命令行实现 <see cref="IGameMusicControl"/>：乐谱与乐器读取、换乐器、选曲演奏、停止与演奏状态。
/// 执行类命令先核对活动凭证与连接状态，再检查目录含该指令，最后进入单通道发出；游戏拒绝与未发出分别用不同结果表达。
/// </summary>
public sealed class TwCliMusicControl(TwCliService cli, Func<GameMusicAvailability> availability,
    Func<GameActivityProof, string?> validateProof, TimeProvider? time = null) : IGameMusicControl
{
    public static readonly IReadOnlyList<string> PlayCommands = ["play_music_score", "get_music_scores", "get_activity", "stop_action"];
    public static readonly IReadOnlyList<string> InstrumentCommands = ["get_instruments", "change_instrument"];
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object gate = new();
    private IReadOnlyList<GameProvidedScore>? scores;
    private string? scoreSession;
    private long scoresFetched;
    private readonly HashSet<string> userComposed = new(StringComparer.Ordinal);
    private HashSet<string> titles = new(StringComparer.Ordinal);

    public GameMusicAvailability Availability => availability();
    public double RecentCallLatencyMs => cli.RecentLatencyMs;

    public async ValueTask<GameScoreListing> ListScoresAsync(TimeSpan maxAge, CancellationToken ct)
    {
        var state = Availability;
        if (!state.Available) return new(false, state.Reason, []);
        var session = cli.SessionKey;
        lock (gate)
            if (scores is not null && scoreSession == session && clock.GetElapsedTime(scoresFetched) <= maxAge) return new(true, null, scores);
        var result = await cli.ExecuteAsync("get_music_scores", null, ct).ConfigureAwait(false);
        if (!result.IsOk || result.Body is not { ValueKind: JsonValueKind.Array } rows)
            return new(false, result.Error ?? "gameCli.failed", []);
        var list = new List<GameProvidedScore>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || Text(row, "DisplayTitle") is not { Length: > 0 } title) continue;
            list.Add(new(title, Text(row, "Location") ?? string.Empty, Flag(row, "IsLocked")));
        }
        lock (gate)
        {
            scores = list; scoreSession = session; scoresFetched = clock.GetTimestamp();
            titles = new(list.Select(x => GameMusicNames.Title(x.DisplayTitle)), StringComparer.Ordinal);
            userComposed.Clear();
        }
        return new(true, null, list);
    }

    public bool? TryIsGameProvided(string title)
    {
        var key = GameMusicNames.Title(title);
        lock (gate)
        {
            if (scores is null || scoreSession != cli.SessionKey) return null;
            return key.Length > 0 && titles.Contains(key) && !userComposed.Contains(key);
        }
    }

    public async ValueTask<IReadOnlyList<GameInstrument>?> ListInstrumentsAsync(CancellationToken ct)
    {
        if (!Availability.Available) return null;
        var result = await cli.ExecuteAsync("get_instruments", null, ct).ConfigureAwait(false);
        if (!result.IsOk || result.Body is not { ValueKind: JsonValueKind.Array } rows) return null;
        var list = new List<GameInstrument>();
        foreach (var row in rows.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Object && Text(row, "Name") is { Length: > 0 } name)
                list.Add(new(name, Flag(row, "IsEquipped"), row.TryGetProperty("Durability", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null));
        return list;
    }

    public ValueTask<GameMusicResult> ChangeInstrumentAsync(GameActivityProof proof, string name, CancellationToken ct) =>
        ExecuteAsync(proof, "change_instrument", TwCliBody.Json(new { name }), ct);

    public async ValueTask<GameMusicResult> PlayScoreAsync(GameActivityProof proof, string title, CancellationToken ct)
    {
        var result = await ExecuteAsync(proof, "play_music_score", TwCliBody.Json(new { title }), ct).ConfigureAwait(false);
        if (result is { Outcome: GameMusicOutcome.Rejected, Code: "gameCli.userComposedNotAllowed" })
            lock (gate) userComposed.Add(GameMusicNames.Title(title));
        return result;
    }

    public ValueTask<GameMusicResult> StopAsync(GameActivityProof proof, CancellationToken ct) => ExecuteAsync(proof, "stop_action", null, ct);

    public async ValueTask<GamePerformanceActivity?> ReadActivityAsync(CancellationToken ct)
    {
        if (!Availability.Available) return null;
        var result = await cli.ExecuteAsync("get_activity", null, ct).ConfigureAwait(false);
        if (!result.IsOk || result.Body is not { ValueKind: JsonValueKind.Object } body ||
            !body.TryGetProperty("Performance", out var performance) || performance.ValueKind != JsonValueKind.Object) return null;
        var combat = body.TryGetProperty("combatState", out var state) && state.ValueKind == JsonValueKind.Object ? state : (JsonElement?)null;
        return new(Flag(performance, "IsPlaying"), Text(performance, "InstrumentName"), Text(performance, "MusicTitle"), Flag(performance, "IsLoop"),
            Number(performance, "TotalDurationSeconds"), Number(performance, "ElapsedSeconds"), Number(performance, "RemainingSeconds"),
            (int)(Number(performance, "ChannelCount") ?? 0), combat is { } c && Flag(c, "IsDead"), combat is { } d && Flag(d, "IsInCombat"));
    }

    private async ValueTask<GameMusicResult> ExecuteAsync(GameActivityProof proof, string command, TwCliBody? body, CancellationToken ct)
    {
        var state = Availability;
        if (!state.Available) return new(GameMusicOutcome.Unavailable, state.Reason);
        if (validateProof(proof) is { } problem) return new(GameMusicOutcome.Unavailable, problem);
        if (await cli.CatalogAsync(ct).ConfigureAwait(false) is not { } catalog) return new(GameMusicOutcome.Unavailable, "gameCli.catalogLoading");
        if (catalog.Loading) return new(GameMusicOutcome.Unavailable, "gameCli.catalogLoading");
        if (!catalog.Has(command)) return new(GameMusicOutcome.Unavailable, "gameCli.commandMissing", command);
        // 取得目录期间租约可能已被撤销（紧急停止），发出前再核对一次。
        if (validateProof(proof) is { } revoked) return new(GameMusicOutcome.Unavailable, revoked);
        var result = await cli.ExecuteAsync(command, body, ct).ConfigureAwait(false);
        return result.Kind switch
        {
            TwCliResultKind.Ok => new(GameMusicOutcome.Accepted),
            TwCliResultKind.Rejected => new(GameMusicOutcome.Rejected, TwCliCodes.FromGame(result.Error), result.Message),
            TwCliResultKind.NotConnected or TwCliResultKind.Unavailable or TwCliResultKind.UnknownCommand =>
                new(GameMusicOutcome.Unavailable, result.Kind == TwCliResultKind.NotConnected ? "gameCli.notConnected" : result.Error, result.Reason),
            TwCliResultKind.Interrupted => new(GameMusicOutcome.Interrupted, "gameCli.interrupted"),
            _ => new(GameMusicOutcome.Failed, result.Error ?? "gameCli.failed", result.Message)
        };
    }

    private static string? Text(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Flag(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static double? Number(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}

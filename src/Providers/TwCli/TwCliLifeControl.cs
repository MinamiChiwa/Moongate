using System.Text.Json;
using MabiTool.ModuleSdk;

namespace MabiTool.Providers.TwCli;

/// <summary>读取类指令的结果：<see cref="Code"/> 为空表示成功（<see cref="Value"/> 可能是空列表，空结果是正常结果）。</summary>
public sealed record TwCliRead<T>(T? Value, string? Code)
{
    public bool Ok => Code is null && Value is not null;
}

/// <summary>
/// 经台服命令行的生活生产（采集、加工、制作）读取与执行。读取与执行都先核对连接器状态与指令目录；执行类命令另须有效活动凭证，
/// 核对后才进入单通道发出，游戏拒绝与未发出分别用不同结果表达，不自动重发。
/// </summary>
public sealed class TwCliLifeControl(TwCliService cli, Func<GameMusicAvailability> availability, Func<GameActivityProof, string?> validateProof)
{
    public const string Gatherables = "get_gatherable_items", Gather = "execute_gathering";
    public const string Alterables = "get_alterable_items", Works = "get_altering_works", Alter = "execute_altering", Collect = "complete_altering_work";
    public const string Craftables = "get_craftable_items", Craft = "execute_crafting";

    /// <summary>三项能力各自要求目录里有的指令。</summary>
    public static readonly IReadOnlyList<string> GatherCommands = [Gatherables, Gather];
    public static readonly IReadOnlyList<string> AlterCommands = [Alterables, Works, Alter, Collect];
    public static readonly IReadOnlyList<string> CraftCommands = [Craftables, Craft];

    public GameMusicAvailability Availability => availability();

    public ValueTask<TwCliRead<IReadOnlyList<TwCliGatherable>>> ReadGatherablesAsync(string? query, CancellationToken ct) =>
        ReadAsync(Gatherables, query, TwCliLifeParser.Gatherables, ct);

    public ValueTask<TwCliRead<IReadOnlyList<TwCliAlterable>>> ReadAlterablesAsync(string? query, CancellationToken ct) =>
        ReadAsync(Alterables, query, TwCliLifeParser.Alterables, ct);

    public ValueTask<TwCliRead<TwCliAlterQueue>> ReadWorksAsync(CancellationToken ct) => ReadAsync(Works, null, TwCliLifeParser.Works, ct);

    public ValueTask<TwCliRead<TwCliCraftList>> ReadCraftablesAsync(string? query, CancellationToken ct) =>
        ReadAsync(Craftables, query, TwCliLifeParser.Craftables, ct);

    /// <summary>当前游戏会话已取得的指令目录（缓存）；没有或仍在加载时为空。</summary>
    public TwCliCatalog? CachedCatalog() => cli.CachedCatalog();

    /// <summary>取得目录（缓存优先）；用于读取目录备注里的花费。</summary>
    public ValueTask<TwCliCatalog?> CatalogAsync(CancellationToken ct) => cli.CatalogAsync(ct);

    /// <summary>
    /// 执行一条生活指令。先核对连接器状态、活动凭证与目录（含该指令，目录仍在加载时不执行），再发出；
    /// 发出前再核对一次凭证（取得目录期间租约可能被紧急停止撤销）。
    /// </summary>
    public async ValueTask<TwCliLifeOutcome> ExecuteAsync(GameActivityProof proof, string command, TwCliBody body, CancellationToken ct)
    {
        var state = Availability;
        if (!state.Available) return new(TwCliLifeKind.Unavailable, state.Reason);
        if (validateProof(proof) is { } problem) return new(TwCliLifeKind.Unavailable, problem);
        if (await cli.CatalogAsync(ct).ConfigureAwait(false) is not { } catalog || catalog.Loading) return new(TwCliLifeKind.Unavailable, "gameCli.catalogLoading");
        if (!catalog.Has(command)) return new(TwCliLifeKind.Unavailable, "gameCli.commandMissing", command);
        if (validateProof(proof) is { } revoked) return new(TwCliLifeKind.Unavailable, revoked);
        return TwCliLifeOutcome.From(await cli.ExecuteAsync(command, body, ct).ConfigureAwait(false));
    }

    /// <summary>发出停止动作（用于命令行已开启自动钓鱼时立即关掉）；不要求目录里的备注，目录里没有该指令时不发出。</summary>
    public async ValueTask<bool> StopActionAsync(GameActivityProof proof, CancellationToken ct)
    {
        if (!Availability.Available || validateProof(proof) is not null) return false;
        if (await cli.CatalogAsync(ct).ConfigureAwait(false) is not { } catalog || !catalog.Has("stop_action")) return false;
        var result = await cli.ExecuteAsync("stop_action", null, ct).ConfigureAwait(false);
        return result.IsOk || result is { Kind: TwCliResultKind.Rejected, Error: "invalid_state" };
    }

    private async ValueTask<TwCliRead<T>> ReadAsync<T>(string command, string? query, Func<JsonElement?, T?> parse, CancellationToken ct) where T : class
    {
        var state = Availability;
        if (!state.Available) return new(null, state.Reason);
        if (await cli.CatalogAsync(ct).ConfigureAwait(false) is not { } catalog || catalog.Loading) return new(null, "gameCli.catalogLoading");
        if (!catalog.Has(command)) return new(null, "gameCli.commandMissing");
        var body = string.IsNullOrWhiteSpace(query) ? (TwCliBody?)null : TwCliBody.Plain(query.Trim());
        var result = await cli.ExecuteAsync(command, body, ct).ConfigureAwait(false);
        switch (result.Kind)
        {
            case TwCliResultKind.Ok:
                return parse(result.Body) is { } value ? new(value, null) : new(null, "gameCli.unparseable");
            case TwCliResultKind.Rejected:
                return new(null, TwCliCodes.FromGame(result.Error));
            case TwCliResultKind.NotConnected:
                return new(null, "gameCli.notConnected");
            case TwCliResultKind.Interrupted:
                return new(null, "gameCli.interrupted");
            default:
                return new(null, result.Error ?? "gameCli.failed");
        }
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using MabiTool.Contracts;
using MabiTool.Host.Combat;
using MabiTool.Host.Game;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwObservation.ItemCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>客户端 Buff 目录：没有绑定时给出原因；读到名称表后列出全部可显示的 Buff（含极性与描述），去掉开发占位，同名都列出。</summary>
public sealed class CombatClientBuffApiTests
{
    private const string Token = "client_buffs_012345678901234567890123456789012345678901";

    [Fact]
    public async Task CatalogListsDisplayableBuffsAfterTheNameTablesLoad()
    {
        // 名称表缓存随战斗持久存储一起装配：数据根放在仓库 artifacts/tmp 下，结束时删除。
        var root = Path.Combine(StorageTestWorkspace.Repository, "artifacts", "tmp", "combat-client-buffs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await Run(root); }
        finally { Directory.Delete(root, true); }
    }

    private static async Task Run(string root)
    {
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>()), storage: new(Path.Combine(root, "bootstrap"), true, PortableRoot: root));
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var catalog = await Catalog(http);
        Assert.Equal((CombatClientBuffCatalogState.BindingRequired, "game.bindingRequired"), (catalog.State.KnownValue, catalog.Reason));
        Assert.Empty(catalog.Buffs);

        var release = new ManualResetEventSlim();
        app.Services.GetRequiredService<CombatNameCatalog>().Reader = (_, ct) =>
        {
            release.Wait(ct);
            var effects = new Dictionary<uint, string> { [835644119] = "集中攻擊", [1822550260] = "集中攻擊", [1806658202] = "不顯示", [475664460] = "假人_增益(顯示X)", [7] = "減速" };
            return new(new string('b', 64), new Dictionary<uint, string>(), new Dictionary<uint, string>(), effects, new Dictionary<string, string>(),
                new Dictionary<uint, string> { [835644119] = "buff", [1822550260] = "debuff", [1806658202] = "buff" },
                EffectDescriptions: new Dictionary<uint, string> { [835644119] = "破防成功時，攻擊力增加 150 持續 10 秒", [1822550260] = "集中攻擊目標" });
        };
        app.Services.GetRequiredService<GameControlContext>().Bindings.Update(new GameWindowObservation("TW", 4343, DateTimeOffset.UtcNow.AddMinutes(-5),
            new(7), null, new PhysicalWindowRect(0, 0, 800, 600), 96, 96, false));
        catalog = await Catalog(http);
        Assert.Equal((CombatClientBuffCatalogState.Loading, "combat.nameCatalogLoading"), (catalog.State.KnownValue, catalog.Reason));
        release.Set();
        for (var attempt = 0; attempt < 100 && catalog.State.KnownValue != CombatClientBuffCatalogState.Ready; attempt++)
        {
            await Task.Delay(50);
            catalog = await Catalog(http);
        }

        Assert.Equal((CombatClientBuffCatalogState.Ready, new string('b', 64)), (catalog.State.KnownValue, catalog.ClientId));
        // 开发占位（不顯示、带下划线的开发命名）不列出；同名的两个编号都列出，各带自己的极性与描述；表里没有描述与极性的为空。
        Assert.Equal(["7|減速||", "835644119|集中攻擊|buff|破防成功時，攻擊力增加 150 持續 10 秒", "1822550260|集中攻擊|debuff|集中攻擊目標"],
            catalog.Buffs.Select(buff => $"{buff.Id}|{buff.Name}|{buff.Polarity}|{buff.Description}"));
        await app.StopAsync();
    }

    private static async Task<CombatClientBuffCatalog> Catalog(HttpClient http) =>
        (await http.GetFromJsonAsync<CombatClientBuffCatalog>(ApiContract.CombatClientBuffs.Path, ContractJson.Options))!;
}

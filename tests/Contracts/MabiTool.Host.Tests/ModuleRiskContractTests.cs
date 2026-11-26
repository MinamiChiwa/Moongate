using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

public sealed class ModuleRiskContractTests
{
    private const string Token = "risk_test_0123456789012345678901234567890123456789";

    [Fact]
    public async Task GeneratedClientReturnsRiskForEveryBuiltInModuleAndKeepsThemIdle()
    {
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>()));
        await app.StartAsync();
        try
        {
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var first = new MabiToolClient(address, _ => ValueTask.FromResult(Token));
            using var second = new MabiToolClient(address, _ => ValueTask.FromResult(Token));
            var a = await first.GetModulesAsync();
            var b = await second.GetModulesAsync();
            Assert.Equal(a.SnapshotRevision, b.SnapshotRevision);
            Assert.Equal(12, a.Items.Count);
            Assert.All(a.Items, module =>
            {
                Assert.NotNull(module.Risk);
                Assert.Empty(module.ActiveOperationIds);
                if (module.Id == "color")
                {
                    Assert.Equal(ModuleRiskLevel.High, module.Risk.Level.KnownValue);
                    Assert.Equal(ModuleRiskBehavior.MemoryRead, Assert.Single(module.Risk.HighRiskBehaviors).KnownValue);
                }
                else
                {
                    Assert.Equal(ModuleRiskLevel.Low, module.Risk.Level.KnownValue);
                    Assert.Empty(module.Risk.HighRiskBehaviors);
                }
                Assert.NotEqual("running", module.State);
                // 名称与功能说明随清单返回，简繁两种都有。
                Assert.All(new[] { module.DisplayNames, module.Descriptions }, texts =>
                {
                    Assert.NotNull(texts);
                    Assert.False(string.IsNullOrWhiteSpace(texts["zh-Hans"]));
                    Assert.False(string.IsNullOrWhiteSpace(texts["zh-Hant"]));
                });
            });
            Assert.Equal(JsonSerializer.Serialize(a, ContractJson.Options), JsonSerializer.Serialize(b, ContractJson.Options));
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public void OlderModuleSnapshotKeepsMissingRiskUnknown()
    {
        var module = new ModuleSnapshot("sample", "0.1.0", "1.0", "2.0", [], true, "idle", [], [], 8, 30, "{}");
        var json = JsonSerializer.SerializeToElement(module, ContractJson.Options);
        var properties = json.EnumerateObject().Where(value => value.Name != "risk").ToDictionary(value => value.Name, value => value.Value);
        var oldJson = JsonSerializer.Serialize(properties);
        Assert.Null(JsonSerializer.Deserialize<ModuleSnapshot>(oldJson, ContractJson.Options)!.Risk);
    }

    [Fact]
    public async Task EssentialModulesAreMarkedInTheModuleList()
    {
        await using var app = BackendApplication.Build(new(0, Token, new HashSet<string>()));
        await app.StartAsync();
        try
        {
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var sdk = new MabiToolClient(address, _ => ValueTask.FromResult(Token));
            var modules = await sdk.GetModulesAsync();
            // 核心模块由模块定义声明，随清单返回；功能模块可以启停。
            Assert.Equal(["core", "game-access"], modules.Items.Where(module => module.Essential == true).Select(module => module.Id).Order());
            Assert.All(modules.Items.Where(module => module.Id is not ("core" or "game-access")), module => Assert.False(module.Essential));
        }
        finally { await app.StopAsync(); }
    }
}
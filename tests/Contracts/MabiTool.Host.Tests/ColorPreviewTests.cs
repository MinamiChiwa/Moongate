using System.Diagnostics;
using System.Runtime.Versioning;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Color;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.Modules.Color.Domain;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
[SupportedOSPlatform("windows")]
public sealed class ColorPreviewTests
{
    [Fact]
    public async Task HttpPreviewUsesOriginalAlternativeAndExactRulesWithMissingAndDisabledRegions()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
        var root = Path.Combine(repository!.FullName, "artifacts", "tmp", "preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var start = new ProcessStartInfo("python") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("import sys; print(sys.executable)");
            using var process = Process.Start(start)!;
            var python = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit(); Assert.Equal(0, process.ExitCode);
            const string token = "preview_owner_0123456789012345678901234567890123456789";
            await using var app = BackendApplication.Build(new(0, token, new HashSet<string>()),
                storage: new(Path.Combine(root, "bootstrap"), true, DataRoot: Path.Combine(root, "data")),
                colorPreviewOptions: _ => new(python, Path.Combine(repository.FullName, "workers", "color-engine"), root));
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var sdk = new MabiToolClient(new(address), _ => ValueTask.FromResult(token));
            var p = ColorProfiles.Default with { Regions = ColorProfiles.Default.Regions.Select((r, i) => r with
                { Id = "stable-" + i, AlternativeHex = ["#212121"], MatchMode = i == 1 ? ColorMatchMode.Similar : ColorMatchMode.ExactHex }).ToArray() };
            var accepted = await sdk.PreviewColorAsync(new(p, ["#212121", "#202020"]));
            Assert.True(accepted.PreviewOnly); Assert.True(accepted.Complete); Assert.True(accepted.Accepted);
            Assert.Equal(2, accepted.HexByRegion.Count); Assert.False(accepted.HexByRegion.ContainsKey("stable-2"));
            Assert.Equal(0, accepted.DeltaByRegion["stable-0"]); Assert.Equal(0, accepted.MatchScoreByRegion["stable-0"]);
            var mismatch = await sdk.PreviewColorAsync(new(p, ["#222222", "#202020"]));
            Assert.True(mismatch.Complete); Assert.False(mismatch.Accepted); Assert.True(mismatch.DeltaByRegion["stable-0"] > 0);
            var absent = await sdk.PreviewColorAsync(new(p, ["#212121", null, "#202020"]));
            Assert.False(absent.Complete); Assert.False(absent.Accepted); Assert.Null(absent.HexByRegion["stable-1"]);
            p = p with { Regions = p.Regions.Select((r, i) => r with { Enabled = i != 1 }).ToArray() };
            var disabled = await sdk.PreviewColorAsync(new(p, ["#212121", null, "#202020"]));
            Assert.True(disabled.Complete); Assert.True(disabled.Accepted); Assert.Null(disabled.DeltaByRegion["stable-1"]);
            Assert.Empty((await sdk.QueryColorHistoryAsync(new())).Items);
            await app.StopAsync();
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(repository!.FullName, "artifacts", "tmp")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task InvalidAndUnconfiguredPreviewsReturnSpecificErrors()
    {
        await using var service = new ColorPreviewService(null);
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() => service.EvaluateAsync(new(ColorProfiles.Default, ["#202020"]), default))).Code);
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() => service.EvaluateAsync(new(ColorProfiles.Default, ["#aabbcc", null]), default))).Code);
        Assert.Equal("capability.unavailable", (await Assert.ThrowsAsync<OperationException>(() => service.EvaluateAsync(new(ColorProfiles.Default, ["#202020", "#202020"]), default))).Code);
    }
}

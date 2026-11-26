using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Color;
using MabiTool.Infrastructure;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
[SupportedOSPlatform("windows")]
public sealed class ColorPaletteTests
{
    /// <summary>染色运行目录只留创建时间最近的三个；集合图正在使用的目录、非运行标识命名的目录不删。</summary>
    [Fact]
    public void ColorRunFoldersKeepTheThreeMostRecentRuns()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
        var root = Path.Combine(repository!.FullName, "artifacts", "tmp", "color-runs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
            var runs = Enumerable.Range(0, 5).Select(index =>
            {
                var folder = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N")));
                File.WriteAllBytes(Path.Combine(folder.FullName, "frame.png"), [1, 2, 3]);
                folder.CreationTimeUtc = start.AddHours(index);
                return folder.FullName;
            }).ToArray();
            var palette = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N")));
            palette.CreationTimeUtc = start.AddHours(-1);
            var other = Directory.CreateDirectory(Path.Combine(root, "notes"));
            using (ColorRunFolders.Use(palette.FullName))
                Assert.Equal(2, ColorRunFolders.Prune(root));
            Assert.Equal(runs[2..], runs.Where(Directory.Exists));
            Assert.True(palette.Exists && other.Exists);
            // 释放后的集合图目录是最旧的一个，下次回收时删除。
            Assert.Equal(1, ColorRunFolders.Prune(root));
            Assert.False(Directory.Exists(palette.FullName));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HttpPaletteUsesWorkerCachesPngAssetsAndAllowsReadOnlyDownload()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
        var root = Path.Combine(repository!.FullName, "artifacts", "tmp", "palette-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var start = new ProcessStartInfo("python") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("import sys; print(sys.executable)");
            using var process = Process.Start(start)!;
            var python = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit(); Assert.Equal(0, process.ExitCode);
            const string token = "palette_owner_0123456789012345678901234567890123456789";
            await using var app = BackendApplication.Build(new(0, token, new HashSet<string>()),
                storage: new(Path.Combine(root, "bootstrap"), true, DataRoot: Path.Combine(root, "data")),
                colorPreviewOptions: _ => new(python, Path.Combine(repository.FullName, "workers", "color-engine"), root));
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var sdk = new MabiToolClient(new(address), _ => ValueTask.FromResult(token));

            // 原工具 #7F3F9A、ΔE ≤ 3 的集合为 1497 色；全集图至少 240×150。精准 HEX 只含目标色本身。
            var request = new ColorPaletteRequest("#7F3F9A", ColorMatchMode.Similar, 3, ColorPaletteImage.Overview);
            var overview = await sdk.RenderColorPaletteAsync(request);
            Assert.Equal((1497, 240, 64, "image/png"), (overview.ColorCount, overview.Width, overview.Height, overview.Asset.MediaType));
            var atlas = await sdk.RenderColorPaletteAsync(request with { Image = ColorPaletteImage.Atlas });
            Assert.Equal((1497, 240, 150), (atlas.ColorCount, atlas.Width, atlas.Height));
            var exact = await sdk.RenderColorPaletteAsync(request with { MatchMode = ColorMatchMode.ExactHex, Tolerance = 20 });
            Assert.Equal((1, 0d), (exact.ColorCount, exact.Tolerance));
            var starts = app.Services.GetRequiredService<ColorPaletteService>().Workers.Snapshot().StartedWorkers;
            Assert.Equal(overview.Asset.Id, (await sdk.RenderColorPaletteAsync(request)).Asset.Id);
            Assert.Equal(starts, app.Services.GetRequiredService<ColorPaletteService>().Workers.Snapshot().StartedWorkers);

            var grant = await sdk.RedeemPairingAsync(await Challenge(sdk));
            using var reader = new MabiToolClient(new(address), _ => ValueTask.FromResult(grant.Token));
            Assert.Equal(403, (await Assert.ThrowsAsync<MabiToolApiException>(() => reader.RenderColorPaletteAsync(request))).Status);
            await using (var download = await reader.DownloadAssetAsync(atlas.Asset.Id))
            {
                using var content = new MemoryStream(); await download.Content.CopyToAsync(content);
                var bytes = content.ToArray();
                Assert.Equal(atlas.Asset.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
                Assert.Equal((240, 150), (BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20))));
            }
            Assert.Equal("request.invalid", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
                sdk.RenderColorPaletteAsync(request with { Tolerance = 36 }))).Problem!.Code);
            Assert.Equal("request.invalid", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
                sdk.RenderColorPaletteAsync(request with { TargetHex = "#7f3f9a" }))).Problem!.Code);
            await app.StopAsync();
            Assert.DoesNotContain(Directory.GetDirectories(root), path => Guid.TryParseExact(Path.GetFileName(path), "N", out _));
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(repository!.FullName, "artifacts", "tmp")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CacheEvictsLeastRecentlyUsedPalettesBeyondTheCountBudget()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MabiTool.slnx"))) repository = repository.Parent;
        var root = Path.Combine(repository!.FullName, "artifacts", "tmp", "palette-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var lease = DataRootLease.Open(new(Path.Combine(root, "bootstrap"), true, Path.Combine(root, "data")));
            await using var database = SqliteStore.Open(lease, AssetStore.Migrations);
            await using var assets = new AssetStore(database, lease.Paths);
            var python = Python();
            await using var service = new ColorPaletteService(new(python, Path.Combine(repository.FullName, "workers", "color-engine"), root),
                assets, maximumAssets: 2);
            ColorPaletteRequest Exact(string hex) => new(hex, ColorMatchMode.ExactHex, 0, ColorPaletteImage.Overview);
            var first = await service.RenderAsync(Exact("#111111"), default);
            var second = await service.RenderAsync(Exact("#222222"), default);
            Assert.Equal(first.Asset.Id, (await service.RenderAsync(Exact("#111111"), default)).Asset.Id);
            await service.RenderAsync(Exact("#333333"), default);
            Assert.Null(assets.GetMetadata(second.Asset.Id));
            Assert.NotNull(assets.GetMetadata(first.Asset.Id));
            Assert.Equal(2, assets.ListByScopePrefix("color", "palette:").Count);
            // 未配置计算引擎时仍可复用已缓存的集合图；未缓存的参数返回能力错误，非法参数先返回请求错误。
            await using var unconfigured = new ColorPaletteService(null, assets);
            Assert.Equal(first.Asset.Id, (await unconfigured.RenderAsync(Exact("#111111"), default)).Asset.Id);
            Assert.Equal("capability.unavailable", (await Assert.ThrowsAsync<OperationException>(() =>
                unconfigured.RenderAsync(Exact("#444444"), default))).Code);
            Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() =>
                unconfigured.RenderAsync(Exact("#444444") with { MatchMode = new OpenEnum<ColorMatchMode>("later") }, default))).Code);
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(repository!.FullName, "artifacts", "tmp")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(root, true);
        }
    }

    private static async Task<PairingRequest> Challenge(MabiToolClient owner)
    {
        var challenge = await owner.CreatePairingChallengeAsync(new("reader", null, [ApiPermissions.SystemRead]));
        return new(challenge.ChallengeId, challenge.Code);
    }

    private static string Python()
    {
        var start = new ProcessStartInfo("python") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("import sys; print(sys.executable)");
        using var process = Process.Start(start)!;
        var python = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit();
        return process.ExitCode == 0 ? python : throw new InvalidOperationException();
    }
}

using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Color;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class ColorCaptureTests
{
    private static AuthSession Session(string id) => new(id, id, [ApiPermissions.DesktopCapture], null, null, false);
    private sealed class Source : IDesktopCaptureSource
    {
        public DesktopRgbFrame Frame { get; set; } = new(new(-2, -1, 2, 2), [0x20, 0x21, 0x22, 0xA0, 0xB0, 0xC0, 1, 2, 3, 0xFF, 0, 0]);
        public List<PhysicalWindowRect?> Regions { get; } = [];
        /// <summary>不带区域时返回整张 <see cref="Frame"/>（虚拟桌面）；带区域时从中裁出该区域，区域不在桌面内时按接口约定抛出。</summary>
        public ValueTask<DesktopRgbFrame> CaptureAsync(PhysicalWindowRect? region, int maximumPixels, CancellationToken ct)
        {
            Regions.Add(region);
            if (region is not { } r) return ValueTask.FromResult(Frame);
            var d = Frame.Bounds;
            if (r.X < d.X || r.Y < d.Y || r.X + r.Width > d.X + d.Width || r.Y + r.Height > d.Y + d.Height)
                throw new ArgumentOutOfRangeException(nameof(region));
            var rgb = new byte[r.Width * r.Height * 3];
            for (var y = 0; y < r.Height; y++)
                Array.Copy(Frame.Rgb, ((r.Y - d.Y + y) * d.Width + r.X - d.X) * 3, rgb, y * r.Width * 3, r.Width * 3);
            return ValueTask.FromResult(new DesktopRgbFrame(r, rgb));
        }
    }
    private sealed class Clock : TimeProvider
    {
        public long Ticks { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(Ticks);
    }
    [Fact]
    public async Task FrozenPhysicalPixelsIgnoreLaterSourceChangesAndRejectBoundsAndOtherOwners()
    {
        var source = new Source(); var clock = new Clock();
        using var service = new ColorCaptureService(source, clock);
        var owner = Session("one");
        var capture = await service.CaptureAsync(owner, null, default);
        source.Frame.Rgb[0] = 0;
        Assert.Equal("#202122", service.Pixel(capture.Id, new(-2, -1), owner).Hex);
        Assert.Equal("#FF0000", service.Pixel(capture.Id, new(-1, 0), owner).Hex);
        Assert.Equal("request.invalid", Assert.Throws<OperationException>(() => service.Pixel(capture.Id, new(0, 0), owner)).Code);
        Assert.Equal("permission.denied", Assert.Throws<OperationException>(() => service.Content(capture.Id, new(0, 10), Session("two"))).Code);
        var part = service.Content(capture.Id, new(3, 5), owner);
        Assert.Equal(new byte[] { 0xA0, 0xB0, 0xC0, 1, 2 }, Convert.FromBase64String(part.Base64));
        Assert.Equal(capture.Sha256, part.Sha256);
        Assert.Equal("request.invalid", Assert.Throws<OperationException>(() => service.Content(capture.Id, new(0, 262145), owner)).Code);
        clock.Ticks = 60_000;
        Assert.Equal("resource.notFound", Assert.Throws<OperationException>(() => service.Pixel(capture.Id, new(-2, -1), owner)).Code);
    }
    [Fact]
    public async Task CaptureCapacityReplacementReleaseAndMalformedFramesAreBounded()
    {
        var source = new Source();
        using var service = new ColorCaptureService(source, TimeProvider.System);
        var first = await service.CaptureAsync(Session("one"), null, default);
        var replacement = await service.CaptureAsync(Session("one"), null, default);
        Assert.Equal("resource.notFound", Assert.Throws<OperationException>(() => service.Pixel(first.Id, new(-2, -1), Session("one"))).Code);
        foreach (var id in new[] { "two", "three", "four" }) await service.CaptureAsync(Session(id), null, default);
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(() => service.CaptureAsync(Session("five"), null, default))).Code);
        Assert.True(service.Release(replacement.Id, Session("one")).Released);
        await service.CaptureAsync(Session("five"), null, default);
        source.Frame = source.Frame with { Rgb = [1, 2] };
        Assert.Equal("capture.frameInvalid", (await Assert.ThrowsAsync<OperationException>(() => service.CaptureAsync(Session("five"), null, default))).Code);
        using var missing = new ColorCaptureService(null, TimeProvider.System);
        Assert.Equal("capability.unavailable", (await Assert.ThrowsAsync<OperationException>(() => missing.CaptureAsync(Session("one"), null, default))).Code);
    }
    [Fact]
    public async Task ByteCapacityIsEnforcedBeforeReplacingExistingCapture()
    {
        // 并排两块 4K 可冻结；单张上限 4096×4096，两张满额画面后超过 128 MiB 全局预算。
        var source = new Source { Frame = new(new(-3840, 0, 7680, 2160), new byte[7680 * 2160 * 3]) };
        using var service = new ColorCaptureService(source, TimeProvider.System);
        Assert.Equal(7680 * 2160 * 3, (await service.CaptureAsync(Session("dual"), null, default)).SizeBytes);
        Assert.True(service.Release((await service.CaptureAsync(Session("dual"), null, default)).Id, Session("dual")).Released);
        source.Frame = new(new(0, 0, 4097, 4096), new byte[4097 * 4096 * 3]);
        Assert.Equal("capture.frameInvalid", (await Assert.ThrowsAsync<OperationException>(() => service.CaptureAsync(Session("one"), null, default))).Code);
        source.Frame = new(new(0, 0, 4096, 4096), new byte[ColorCaptureService.MaximumPixels * 3]);
        await service.CaptureAsync(Session("one"), null, default); await service.CaptureAsync(Session("two"), null, default);
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(() => service.CaptureAsync(Session("three"), null, default))).Code);
    }
    [Fact]
    public async Task RegionCapturesFreezeOnlyThatAreaAndInvalidRegionsKeepTheExistingFrame()
    {
        // 虚拟桌面 (-2, -1) 2×2：左列 x=-2，右列 x=-1；每块“显示器”是一列。
        var source = new Source();
        using var service = new ColorCaptureService(source, TimeProvider.System);
        var owner = Session("one");
        var right = await service.CaptureAsync(owner, new(-1, -1, 1, 2), default);
        Assert.Equal(new PhysicalWindowRect(-1, -1, 1, 2), right.Bounds);
        Assert.Equal(6, right.SizeBytes);
        Assert.Equal("#A0B0C0", service.Pixel(right.Id, new(-1, -1), owner).Hex);
        Assert.Equal("#FF0000", service.Pixel(right.Id, new(-1, 0), owner).Hex);
        Assert.Equal("request.invalid", Assert.Throws<OperationException>(() => service.Pixel(right.Id, new(-2, -1), owner)).Code);
        Assert.Equal(new PhysicalWindowRect?[] { new(-1, -1, 1, 2) }, source.Regions);

        // 无效区域在请求截图源之前或由截图源拒绝，不替换本客户端已有的画面。
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<OperationException>(() => service.CaptureAsync(owner, new(-2, -1, 0, 2), default))).Code);
        Assert.Equal("capture.regionTooLarge", (await Assert.ThrowsAsync<OperationException>(() => service.CaptureAsync(owner, new(0, 0, 4097, 4096), default))).Code);
        Assert.Equal("capture.regionOutsideDesktop", (await Assert.ThrowsAsync<OperationException>(() => service.CaptureAsync(owner, new(-1, -1, 2, 2), default))).Code);
        Assert.Single(source.Regions, region => region == new PhysicalWindowRect(-1, -1, 2, 2));
        Assert.Equal("#A0B0C0", service.Pixel(right.Id, new(-1, -1), owner).Hex);

        // 一次取色里先后冻结两块显示器，每块读回后即释放：两次释放都成功，不残留画面。
        Assert.True(service.Release(right.Id, owner).Released);
        var left = await service.CaptureAsync(owner, new(-2, -1, 1, 2), default);
        Assert.Equal("#202122", service.Pixel(left.Id, new(-2, -1), owner).Hex);
        Assert.True(service.Release(left.Id, owner).Released);
        Assert.Equal("resource.notFound", Assert.Throws<OperationException>(() => service.Release(left.Id, owner)).Code);

        // 截图源返回的边界与请求的区域不同。
        var shifted = new Source();
        using var wrong = new ColorCaptureService(new Shifted(shifted), TimeProvider.System);
        Assert.Equal("capture.frameInvalid", (await Assert.ThrowsAsync<OperationException>(() => wrong.CaptureAsync(owner, new(-1, -1, 1, 2), default))).Code);
    }
    private sealed class Shifted(Source inner) : IDesktopCaptureSource
    {
        public async ValueTask<DesktopRgbFrame> CaptureAsync(PhysicalWindowRect? region, int maximumPixels, CancellationToken ct) =>
            await inner.CaptureAsync(region is { } r ? r with { X = r.X - 1 } : null, maximumPixels, ct);
    }
    [Fact]
    public async Task CaptureHttpRequiresSeparatePermissionAndSdkCanReadAndRelease()
    {
        const string token = "capture_owner_0123456789012345678901234567890123456789";
        await using var app = BackendApplication.Build(new(0, token, new HashSet<string>()), desktopCapture: new Source());
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var owner = new MabiToolClient(new(address), _ => ValueTask.FromResult(token));
        var readerChallenge = await owner.CreatePairingChallengeAsync(new("reader", null, [ApiPermissions.SystemRead]));
        var readerGrant = await owner.RedeemPairingAsync(new(readerChallenge.ChallengeId, readerChallenge.Code));
        using var reader = new MabiToolClient(new(address), _ => ValueTask.FromResult(readerGrant.Token));
        Assert.Equal(403, (await Assert.ThrowsAsync<MabiToolApiException>(() => reader.CaptureColorScreenAsync(new()))).Status);
        // 旧客户端的请求正文是空对象：照旧冻结整个虚拟桌面。
        using (var http = new HttpClient { BaseAddress = new(address) })
        {
            using var legacy = new HttpRequestMessage(HttpMethod.Post, "/api/v1/color/captures")
                { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
            legacy.Headers.Authorization = new("Bearer", token);
            using var response = await http.SendAsync(legacy);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            var legacyCapture = System.Text.Json.JsonSerializer.Deserialize<ColorScreenCapture>(await response.Content.ReadAsStringAsync(), ContractJson.Options)!;
            Assert.Equal(new PhysicalWindowRect(-2, -1, 2, 2), legacyCapture.Bounds);
            Assert.True((await owner.ReleaseColorCaptureAsync(legacyCapture.Id)).Released);
        }
        var region = await owner.CaptureColorScreenAsync(new(new(-1, -1, 1, 2)));
        Assert.Equal(new PhysicalWindowRect(-1, -1, 1, 2), region.Bounds);
        Assert.True((await owner.ReleaseColorCaptureAsync(region.Id)).Released);
        Assert.Equal("capture.regionOutsideDesktop", (await Assert.ThrowsAsync<MabiToolApiException>(() => owner.CaptureColorScreenAsync(new(new(0, 0, 1, 1))))).Problem!.Code);
        var capture = await owner.CaptureColorScreenAsync(new());
        Assert.Equal("#202122", (await owner.PickColorPixelAsync(capture.Id, new(-2, -1))).Hex);
        Assert.Equal(12, (await owner.ReadColorCaptureAsync(capture.Id, new(0, 12))).TotalBytes);
        var otherChallenge = await owner.CreatePairingChallengeAsync(new("other", null, [ApiPermissions.SystemRead, ApiPermissions.DesktopCapture]));
        var otherGrant = await owner.RedeemPairingAsync(new(otherChallenge.ChallengeId, otherChallenge.Code));
        using var other = new MabiToolClient(new(address), _ => ValueTask.FromResult(otherGrant.Token));
        Assert.Equal("permission.denied", (await Assert.ThrowsAsync<MabiToolApiException>(() => other.PickColorPixelAsync(capture.Id, new(-2, -1)))).Problem!.Code);
        Assert.True((await owner.ReleaseColorCaptureAsync(capture.Id)).Released);
        Assert.Equal(404, (await Assert.ThrowsAsync<MabiToolApiException>(() => owner.ReadColorCaptureAsync(capture.Id, new(0, 1)))).Status);
        await app.StopAsync();
    }
}

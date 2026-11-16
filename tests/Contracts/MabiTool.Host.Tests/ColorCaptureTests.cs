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
        public ValueTask<DesktopRgbFrame> CaptureAsync(int maximumPixels, CancellationToken ct) => ValueTask.FromResult(Frame);
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
        var capture = await service.CaptureAsync(owner, default);
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
        var first = await service.CaptureAsync(Session("one"), default);
        var replacement = await service.CaptureAsync(Session("one"), default);
        Assert.Equal("resource.notFound", Assert.Throws<OperationException>(() => service.Pixel(first.Id, new(-2, -1), Session("one"))).Code);
        foreach (var id in new[] { "two", "three", "four" }) await service.CaptureAsync(Session(id), default);
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(() => service.CaptureAsync(Session("five"), default))).Code);
        Assert.True(service.Release(replacement.Id, Session("one")).Released);
        await service.CaptureAsync(Session("five"), default);
        source.Frame = source.Frame with { Rgb = [1, 2] };
        Assert.Equal("capture.frameInvalid", (await Assert.ThrowsAsync<OperationException>(() => service.CaptureAsync(Session("five"), default))).Code);
        using var missing = new ColorCaptureService(null, TimeProvider.System);
        Assert.Equal("capability.unavailable", (await Assert.ThrowsAsync<OperationException>(() => missing.CaptureAsync(Session("one"), default))).Code);
    }
    [Fact]
    public async Task ByteCapacityIsEnforcedBeforeReplacingExistingCapture()
    {
        // 并排两块 4K 可冻结；单张上限 4096×4096，两张满额画面后超过 128 MiB 全局预算。
        var source = new Source { Frame = new(new(-3840, 0, 7680, 2160), new byte[7680 * 2160 * 3]) };
        using var service = new ColorCaptureService(source, TimeProvider.System);
        Assert.Equal(7680 * 2160 * 3, (await service.CaptureAsync(Session("dual"), default)).SizeBytes);
        Assert.True(service.Release((await service.CaptureAsync(Session("dual"), default)).Id, Session("dual")).Released);
        source.Frame = new(new(0, 0, 4097, 4096), new byte[4097 * 4096 * 3]);
        Assert.Equal("capture.frameInvalid", (await Assert.ThrowsAsync<OperationException>(() => service.CaptureAsync(Session("one"), default))).Code);
        source.Frame = new(new(0, 0, 4096, 4096), new byte[ColorCaptureService.MaximumPixels * 3]);
        await service.CaptureAsync(Session("one"), default); await service.CaptureAsync(Session("two"), default);
        Assert.Equal("resource.capacityExceeded", (await Assert.ThrowsAsync<OperationException>(() => service.CaptureAsync(Session("three"), default))).Code);
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
        Assert.Equal(403, (await Assert.ThrowsAsync<MabiToolApiException>(() => reader.CaptureColorScreenAsync())).Status);
        var capture = await owner.CaptureColorScreenAsync();
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

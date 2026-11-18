using System.Buffers.Binary;
using System.Runtime.Versioning;
using MabiTool.Observation.Worker;
using MabiTool.Providers.TwObservation.Combat;
using Xunit;

namespace MabiTool.Host.Tests;

[SupportedOSPlatform("windows")]
public sealed class NativeCombatCaptureTests
{
    [Theory]
    [InlineData(262144, 262144, true)]
    [InlineData(65537, 65537, true)]
    [InlineData(65536, 65537, false)]
    [InlineData(262145, 262145, false)]
    [InlineData(-1, -1, false)]
    [InlineData(12, 11, false)]
    public void CapturedExtentMustEqualCompleteWireExtent(int captured, int wire, bool valid) =>
        Assert.Equal(valid, NativeCaptureBoundary.CompletePacket(captured, wire));

    [Fact]
    public void CaptureParametersAreFixedBeforeActivation()
    {
        var calls = new List<string>(); var closed = 0;
        var handle = NativeCaptureBoundary.OpenConfigured(() => { calls.Add("create"); return 7; },
            (_, value) => { calls.Add("snap:" + value); return 0; }, (_, value) => { calls.Add("buffer:" + value); return 0; },
            (_, value) => { calls.Add("promiscuous:" + value); return 0; }, (_, value) => { calls.Add("timeout:" + value); return 0; },
            _ => { calls.Add("activate"); return 0; }, _ => closed++);
        Assert.Equal((nint)7, handle);
        Assert.Equal(new[] { "create", "snap:262144", "buffer:16777216", "promiscuous:0", "timeout:100", "activate" }, calls);
        Assert.Equal(0, closed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedConfigurationOrActivationClosesCreatedHandle(bool failActivation)
    {
        var closed = 0;
        Assert.Throws<IOException>(() => NativeCaptureBoundary.OpenConfigured(() => 7,
            (_, _) => 0, (_, _) => failActivation ? 0 : -1, (_, _) => 0, (_, _) => 0,
            _ => -1, handle => { Assert.Equal((nint)7, handle); closed++; }));
        Assert.Equal(1, closed);
    }

    [Fact]
    public void DelegateFailureAlsoClosesCreatedHandle()
    {
        var closed = 0;
        Assert.Throws<InvalidOperationException>(() => NativeCaptureBoundary.OpenConfigured(() => 7,
            (_, _) => throw new InvalidOperationException(), (_, _) => 0, (_, _) => 0, (_, _) => 0, _ => 0, _ => closed++));
        Assert.Equal(1, closed);
    }

    [Fact]
    public void CleanupFailureStillDisposesRemainingChannels()
    {
        var closed = new List<int>();
        Assert.Throws<IOException>(() => NativeCaptureBoundary.DisposeAll([
            new Resource(() => { closed.Add(1); throw new IOException(); }),
            new Resource(() => closed.Add(2))]));
        Assert.Equal(new[] { 1, 2 }, closed);
    }

    private sealed class Resource(Action dispose) : IDisposable { public void Dispose() => dispose(); }

    [Fact]
    public void OversizedPayloadIsSplitAndOnlyOneByteAckKeepaliveIsSkipped()
    {
        var ingress = new NativeTcpIngress(); var chunks = new List<(uint Sequence, int Length)>();
        void Feed(uint sequence, ReadOnlyMemory<byte> bytes) => chunks.Add((sequence, bytes.Length));
        ingress.Feed(100, 16, new byte[131073], Feed);
        Assert.Equal(new[] { (100U, 65536), (65636U, 65536), (131172U, 1) }, chunks);
        ingress.Feed(131172, 16, new byte[] { 255 }, Feed);
        Assert.Equal(3, chunks.Count);
        ingress.Feed(100, 16, new byte[2], Feed);
        Assert.Equal((100U, 2), chunks[^1]);
        ingress.Reset(); ingress.Feed(131172, 16, new byte[] { 255 }, Feed);
        Assert.Equal(5, chunks.Count);
    }

    [Fact]
    public void RawIpv4Link228IsDecodedAsOwnedTcpCandidate()
    {
        var bytes = new byte[40]; bytes[0] = 0x45; bytes[9] = 6; bytes[12] = 127; bytes[15] = 1; bytes[16] = 127; bytes[19] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 40);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(20), 1111);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(22), 4321); bytes[32] = 0x50;
        var packet = Assert.IsType<MabiTool.Providers.TwObservation.GameTcpPacket>(NativeCaptureBoundary.Read(228, bytes));
        Assert.Equal(1111, packet.SourcePort); Assert.Equal(4321, packet.DestinationPort);
    }

    [Fact]
    public void NativeSourceDeclaresOnlyCurrentProfileForMatchingCapture()
    {
        var source = new NpcapObservationSource();
        Assert.Equal(Tw20261110CombatProtocol.Profile, Assert.Single(source.CombatProfiles).Id);
        var request = new ObservationCaptureRequest("synthetic", "TW", 1, DateTimeOffset.UtcNow, new string('a', 32), "tw-music-v1");
        Assert.Empty(source.CombatProfilesFor(request));
        Assert.Single(source.CombatProfilesFor(request with { Profile = Tw20261110CombatProtocol.CaptureProfile }));
    }
}

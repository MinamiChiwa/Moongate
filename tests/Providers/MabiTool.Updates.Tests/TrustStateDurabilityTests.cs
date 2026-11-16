using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Infrastructure.Updates;
using Xunit;
using Fixture = MabiTool.Updates.Tests.SignedReleaseFixture;

namespace MabiTool.Updates.Tests;

public sealed class TrustStateDurabilityTests
{
    [Fact]
    public void Failed_atomic_write_does_not_advance_index_release_root_or_clock()
    {
        using var f = new Fixture();
        var bytes = f.Release();
        using (var reader = new FileStream(f.Path("trust.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Code("update.trustStateUnavailable", () => f.AcceptIndex(bytes));
        Assert.Empty(f.Store.Channels);
        Assert.Equal(f.Now, f.Store.LastObservedUtc);
        var index = f.AcceptIndex(bytes);
        using (var reader = new FileStream(f.Path("trust.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Code("update.trustStateUnavailable", () => f.Store.VerifyReleaseManifest(bytes, index, "win-x64", f.Now.AddMinutes(1)));
        Assert.Empty(f.Store.Releases);
        Assert.Equal(f.Now, f.Store.LastObservedUtc);
        f.Store.VerifyReleaseManifest(bytes, index, "win-x64", f.Now);
        var candidate = f.Root with { RootVersion = 2 };
        var rotation = Fixture.Sign(JsonSerializer.SerializeToUtf8Bytes(candidate, Fixture.Options), ("root", f.RootSigner));
        using (var reader = new FileStream(f.Path("trust.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Code("update.trustStateUnavailable", () => f.Store.ApplyRootRotation(rotation, f.Now.AddMinutes(1)));
        Assert.Equal(1, f.Store.Root.RootVersion);
        Assert.Equal(f.Now, f.Store.LastObservedUtc);
        Assert.Equal(1, f.Reload().Root.RootVersion);
        Assert.Empty(Directory.GetFiles(f.Path(""), "*.tmp-*"));
        f.Store.ApplyRootRotation(rotation, f.Now.AddMinutes(1));
        Assert.Equal(2, f.Reload().Root.RootVersion);
    }

    [Fact]
    public void Stale_instance_reloads_disk_before_save_verify_or_rotation()
    {
        using var f = new Fixture();
        var stale = f.Reload();
        var bytes = f.Release();
        f.AcceptIndex(bytes, 5);
        stale.Save();
        Assert.Equal(5, stale.Channels["stable"].IndexSequence);
        Code("update.sequenceRollback", () => stale.VerifyChannelIndex(f.Index(bytes, 4), "mabitool", "stable", f.Now));
        var candidate = f.Root with { RootVersion = 2 };
        var rotation = Fixture.Sign(JsonSerializer.SerializeToUtf8Bytes(candidate, Fixture.Options), ("root", f.RootSigner));
        f.Store.ApplyRootRotation(rotation, f.Now);
        Code("update.rootVersionInvalid", () => stale.ApplyRootRotation(rotation, f.Now));
        Assert.Equal(2, stale.Root.RootVersion);
        Assert.Equal(5, f.Reload().Channels["stable"].IndexSequence);
    }

    [Fact]
    public void Writer_contention_returns_stable_error_and_can_retry()
    {
        using var f = new Fixture();
        var bytes = f.Release();
        using (var writer = new FileStream(f.Path("trust.json.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Code("update.trustStateBusy", () => f.AcceptIndex(bytes));
            Code("update.trustStateBusy", () => f.Reload());
        }
        Assert.Empty(f.Store.Channels);
        Assert.Equal(1, f.AcceptIndex(bytes).IndexSequence);
    }

    [Fact]
    public void Legacy_state_keeps_index_floor_and_binds_product_on_first_new_index()
    {
        using var f = new Fixture();
        var node = JsonSerializer.SerializeToNode(new TrustStateDocument(f.Root,
            new Dictionary<string, ChannelTrustState> { ["stable"] = new(8, new string('a', 64), f.Now) }, f.Now), Fixture.Options)!;
        node.AsObject().Remove("releases");
        node.AsObject().Remove("productId");
        var old = node["channels"]!["stable"]!.AsObject();
        old.Remove("targetReleaseSequence"); old.Remove("targetManifestSha256"); old.Remove("targetManifestLength");
        File.WriteAllText(f.Path("trust.json"), node.ToJsonString());
        var store = f.Reload();
        var bytes = f.Release();
        Code("update.sequenceRollback", () => store.VerifyChannelIndex(f.Index(bytes, 7), "mabitool", "stable", f.Now));
        store.VerifyChannelIndex(f.Index(bytes, 9), "mabitool", "stable", f.Now);
        Code("update.identityMismatch", () => store.VerifyChannelIndex(f.Index(bytes, 10, mutate: value => value["productId"] = "other"), "other", "stable", f.Now));
        Assert.Equal(9, f.Reload().Channels["stable"].IndexSequence);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("corrupt")]
    [InlineData("target-tuple")]
    [InlineData("unknown")]
    [InlineData("case-alias")]
    public void Missing_or_corrupt_persisted_state_is_not_reinitialized(string problem)
    {
        using var f = new Fixture();
        f.AcceptIndex(f.Release());
        var path = f.Path("trust.json");
        var text = File.ReadAllText(path);
        if (problem == "missing") File.Delete(path);
        if (problem == "corrupt") File.WriteAllText(path, "{}");
        if (problem == "duplicate") File.WriteAllText(path, "{\"productId\":\"a\"," + text[1..]);
        if (problem == "case-alias") File.WriteAllText(path, "{\"ProductId\":\"a\"," + text[1..]);
        if (problem is "target-tuple" or "unknown")
        {
            var node = JsonNode.Parse(text)!;
            if (problem == "target-tuple") node["channels"]!["stable"]!.AsObject().Remove("targetManifestLength");
            else node["extra"] = true;
            File.WriteAllText(path, node.ToJsonString());
        }
        Assert.Throws<TrustValidationException>(() => f.Store.Save());
        Assert.Equal(problem != "missing", File.Exists(path));
        Assert.Equal(1, f.Store.Channels["stable"].IndexSequence);
    }

    [Theory]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-material")]
    [InlineData("wrong-curve")]
    [InlineData("trailing-spki")]
    [InlineData("unknown-role")]
    [InlineData("wrong-algorithm")]
    public void Invalid_roots_cannot_authorize_metadata(string problem)
    {
        using var f = new Fixture();
        using var otherCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var keys = f.Root.Keys.ToList();
        switch (problem)
        {
            case "duplicate-id": keys.Add(keys[0]); break;
            case "duplicate-material": keys.Add(keys[0] with { KeyId = "alias" }); break;
            case "wrong-curve": keys[0] = keys[0] with { PublicKeyBase64 = Convert.ToBase64String(otherCurve.ExportSubjectPublicKeyInfo()) }; break;
            case "trailing-spki": keys[0] = keys[0] with { PublicKeyBase64 = Convert.ToBase64String([.. f.RootSigner.ExportSubjectPublicKeyInfo(), 0]) }; break;
            case "unknown-role": keys[0] = keys[0] with { Roles = ["admin"] }; break;
            case "wrong-algorithm": keys[0] = keys[0] with { Algorithm = "other" }; break;
        }
        Code("update.rootInvalid", () => new UpdateTrustStore(f.Path("invalid.json"), f.Root with { Keys = keys }, f.Now));
        Assert.False(File.Exists(f.Path("invalid.json")));
    }

    [Fact]
    public void Role_thresholds_count_distinct_keys_and_rotation_requires_both_roots()
    {
        using var f = new Fixture();
        using var second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var third = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = new TrustRoot(1, 2, [f.Root.Keys[0], Fixture.Key("second", second, "root"), Fixture.Key("third", third, "root"), f.Root.Keys[1], f.Root.Keys[2]], 1, 1);
        var store = new UpdateTrustStore(f.Path("threshold.json"), root, f.Now);
        var bytes = f.Release();
        var index = store.VerifyChannelIndex(f.Index(bytes), "mabitool", "stable", f.Now);
        store.VerifyReleaseManifest(bytes, index, "win-x64", f.Now);
        var candidate = root with { RootVersion = 2, Threshold = 1, Keys = [Fixture.Key("next", next, "root"), f.Root.Keys[1], f.Root.Keys[2]] };
        var payload = JsonSerializer.SerializeToUtf8Bytes(candidate, Fixture.Options);
        var duplicate = Fixture.Sign(payload, ("root", f.RootSigner), ("root", f.RootSigner), ("next", next));
        Code("update.signatureInvalid", () => store.ApplyRootRotation(duplicate, f.Now));
        Code("update.signatureInvalid", () => store.ApplyRootRotation(Fixture.Sign(payload, ("root", f.RootSigner), ("second", second)), f.Now));
        store.ApplyRootRotation(Fixture.Sign(payload, ("root", f.RootSigner), ("third", third), ("next", next)), f.Now);
        Assert.Equal(2, store.Root.RootVersion);
    }

    private static void Code(string expected, Action action) => Assert.Equal(expected, Assert.Throws<TrustValidationException>(action).Code);
}

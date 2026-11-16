using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Infrastructure.Updates;
using Xunit;
using Fixture = MabiTool.Updates.Tests.SignedReleaseFixture;

namespace MabiTool.Updates.Tests;

public sealed class ReleaseManifestTrustTests
{
    [Fact]
    public async Task Signed_release_drives_planning_and_incremental_or_package_rebuild()
    {
        using var f = new Fixture(buildArtifacts: true);
        var bytes = f.Release();
        var index = f.AcceptIndex(bytes);
        var release = f.Store.VerifyReleaseManifest(bytes, index, "win-x64", f.Now);
        var component = release.Components["core"];
        var plan = new UpdatePlanner().CreatePlan(release.Manifest,
            new(new(1, 0, 0), 1, 1, "win-x64", new(10, 0, 19045), 100_000_000, []));
        Assert.Equal(Fixture.Hash(bytes), plan.ManifestSha256);
        Assert.Equal(1, release.IndexSequence);
        Assert.Equal(f.Artifacts.TargetTreeHash, component.Artifacts.TargetTreeHash);
        Assert.True(component.Artifacts.Files[0].File.Executable);
        Assert.Equal(component.Artifacts.Files.Sum(item => item.DownloadLength) + component.Artifacts.FullPackage!.DownloadLength, plan.DownloadBytes);
        var budget = new UpdateIoBudget();
        using var downloader = new ResumableAssetDownloader(new BlobHandler(f), budget: budget);
        var stager = new FileTreeStager(downloader, budget);
        var incremental = await stager.StageAsync(f.Path("current"), f.Path("stage"), f.Path("cache"), component.Artifacts,
            item => [new("direct", new Uri("https://github.com/" + item.DownloadSha256))]);
        var full = await stager.StagePackageAsync(f.Path("current"), f.Path("full"), f.Path("cache"), component.Artifacts,
            item => [new("direct", new Uri("https://github.com/" + item.DownloadSha256))]);
        Assert.Equal(1, incremental.ReusedFiles);
        Assert.Equal(1, incremental.DownloadedFiles);
        await FileTreePlanner.VerifyAsync(full.StageRoot, component.Artifacts.Files.Select(item => item.File).ToArray());
        Assert.Equal("new", File.ReadAllText(f.Path("stage/app")));
        Assert.Equal("new", File.ReadAllText(f.Path("full/app")));
        Assert.Equal("old", File.ReadAllText(f.Path("current/app")));
        Assert.Equal(Fixture.Hash(bytes), f.Reload().Releases["stable"].ManifestSha256);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("hash")]
    [InlineData("signature")]
    [InlineData("product")]
    [InlineData("channel")]
    [InlineData("sequence")]
    public void Rejects_broken_index_manifest_binding(string field)
    {
        using var f = new Fixture();
        var node = f.Payload();
        if (field == "product") node["productId"] = "other";
        if (field == "channel") node["channel"] = "preview";
        if (field == "sequence") node["releaseSequence"] = 2;
        var bytes = f.Release(node, field == "signature" ? f.RootSigner : f.ReleaseSigner);
        var index = f.AcceptIndex(bytes, mutate: json =>
        {
            if (field == "length") json["targetManifestLength"] = bytes.Length + 1;
            if (field == "hash") json["targetManifestSha256"] = new string('0', 64);
        });
        var expected = field is "length" or "hash" ? "update.manifestBindingMismatch" :
            field == "signature" ? "update.signatureInvalid" : "update.identityMismatch";
        Code(expected, () => f.Store.VerifyReleaseManifest(bytes, index, "win-x64", f.Now));
        Assert.Empty(f.Store.Releases);
    }

    [Theory]
    [InlineData("notes-missing", "update.metadataFieldInvalid")]
    [InlineData("notes-type", "update.metadataFieldInvalid")]
    [InlineData("unknown", "update.unknownMetadataField")]
    [InlineData("version", "update.metadataFieldInvalid")]
    [InlineData("schema", "update.metadataFieldInvalid")]
    [InlineData("installed", "update.manifestInvalid")]
    [InlineData("tree", "update.targetTreeMismatch")]
    [InlineData("path", "update.pathRejected")]
    [InlineData("asset", "update.assetMetadataInvalid")]
    [InlineData("dependency", "update.incompatibleComponents")]
    [InlineData("migration", "update.migrationInvalid")]
    [InlineData("from-schema", "update.migrationInvalid")]
    [InlineData("range", "update.metadataFieldInvalid")]
    [InlineData("bool", "update.metadataFieldInvalid")]
    public void Strict_signed_schema_rejects_invalid_inputs(string field, string expected)
    {
        using var f = new Fixture();
        var node = f.Payload();
        var component = node["platforms"]![0]!["components"]![0]!;
        switch (field)
        {
            case "notes-missing": node.AsObject().Remove("releaseNotes"); break;
            case "notes-type": node["releaseNotes"] = 1; break;
            case "unknown": component["extra"] = true; break;
            case "version": node["version"] = "01.0.0"; break;
            case "schema": node["schemaVersion"] = "1"; break;
            case "installed": component["installedSize"] = 100; break;
            case "tree": component["targetTreeHash"] = new string('0', 64); break;
            case "path": component["files"]![0]!["path"] = "../outside"; break;
            case "asset": component["files"]![0]!["asset"]!["assetName"] = "https://example.com/x"; break;
            case "dependency": component["dependencies"] = JsonNode.Parse("""[{"componentId":"missing","minimumVersion":"1.0.0"}]"""); break;
            case "migration": node["data"]![0]!["requiresFullBackup"] = false; break;
            case "from-schema":
                node["data"]![0]!["oldReadableSchemaRange"]!["minimum"] = 2;
                node["data"]![0]!["oldReadableSchemaRange"]!["maximum"] = 2;
                break;
            case "range": component["moduleApiRange"]!["maximum"] = 0; break;
            case "bool": component["optional"] = "false"; break;
        }
        var bytes = f.Release(node);
        var index = f.AcceptIndex(bytes);
        Code(expected, () => f.Store.VerifyReleaseManifest(bytes, index, "win-x64", f.Now));
        Assert.Empty(f.Store.Releases);
    }

    [Fact]
    public void Nested_duplicate_fields_and_dependency_cycles_are_rejected()
    {
        using var f = new Fixture();
        var text = f.Payload().ToJsonString().Replace("\"optional\":false", "\"optional\":false,\"optional\":false", StringComparison.Ordinal);
        var bytes = f.ReleaseText(text);
        var index = f.AcceptIndex(bytes);
        Code("update.duplicateMetadataField", () => f.Store.VerifyReleaseManifest(bytes, index, "win-x64", f.Now));
        var node = f.Payload();
        var items = node["platforms"]![0]!["components"]!.AsArray();
        var second = items[0]!.DeepClone();
        second["componentId"] = "other";
        foreach (var file in second["files"]!.AsArray()) file!["componentId"] = "other";
        second["dependencies"] = JsonNode.Parse("""[{"componentId":"core","minimumVersion":"1.0.0"}]""");
        items[0]!["dependencies"] = JsonNode.Parse("""[{"componentId":"other","minimumVersion":"1.0.0"}]""");
        items.Add(second);
        bytes = f.Release(node);
        // 同一发行身份不可替换，使用独立状态验证循环清单。
        var separate = new UpdateTrustStore(f.Path("cycle.json"), f.Root, f.Now);
        index = separate.VerifyChannelIndex(f.Index(bytes), "mabitool", "stable", f.Now);
        Code("update.dependencyCycle", () => separate.VerifyReleaseManifest(bytes, index, "win-x64", f.Now));
    }

    [Fact]
    public void Refreshed_index_preserves_release_identity_and_restart_lower_bounds()
    {
        using var f = new Fixture();
        var bytes = f.Release();
        f.Store.VerifyReleaseManifest(bytes, f.AcceptIndex(bytes), "win-x64", f.Now);
        var refresh = f.AcceptIndex(bytes, 2);
        f.Store.VerifyReleaseManifest(bytes, refresh, "win-x64", f.Now);
        var reloaded = f.Reload();
        Code("update.releaseSequenceRollback", () => reloaded.VerifyChannelIndex(f.Index(bytes, 3, 0), "mabitool", "stable", f.Now));
        var changed = f.Payload(); changed["releaseNotes"] = "changed";
        Code("update.sameReleaseDifferentManifest", () => reloaded.VerifyChannelIndex(f.Index(f.Release(changed), 3), "mabitool", "stable", f.Now));
        Code("update.sameReleaseDifferentManifest", () => reloaded.VerifyChannelIndex(f.Index(bytes, 3,
            mutate: json => json["targetManifestLength"] = bytes.Length + 1), "mabitool", "stable", f.Now));
        Assert.Equal(2, reloaded.Channels["stable"].IndexSequence);
    }

    [Fact]
    public void Public_index_mutation_does_not_change_signed_authorization()
    {
        using var f = new Fixture();
        var bytes = f.Release();
        var index = f.AcceptIndex(bytes);
        Array.Fill(index.Payload, (byte)0);
        var modified = index with { TargetManifestSha256 = new string('0', 64), TargetReleaseSequence = 9, TargetManifestLength = 1 };
        Assert.Equal(1, f.Store.VerifyReleaseManifest(bytes, modified, "win-x64", f.Now).Manifest.ReleaseSequence);
        Code("update.indexUnverified", () => f.Store.VerifyReleaseManifest(bytes,
            new([], "mabitool", "stable", 1, f.Now, f.Now.AddHours(1), Fixture.Hash(bytes), new string('a', 64), 1, 1, bytes.Length), "win-x64", f.Now));
        Code("update.metadataExpired", () => f.Store.VerifyReleaseManifest(bytes, index, "win-x64", f.Now.AddHours(2)));
    }

    [Fact]
    public void Installation_revalidates_current_root_and_roles_after_rotation()
    {
        using var f = new Fixture();
        var bytes = f.Release();
        var index = f.AcceptIndex(bytes);
        var rotated = f.Root with { RootVersion = 2, Keys = f.Root.Keys.Select(key => key.KeyId == "release" ? key with { Revoked = true } : key).ToArray() };
        f.Store.ApplyRootRotation(Fixture.Sign(JsonSerializer.SerializeToUtf8Bytes(rotated, Fixture.Options), ("root", f.RootSigner)), f.Now);
        Code("update.signatureInvalid", () => f.Store.VerifyReleaseManifest(bytes, index, "win-x64", f.Now));
        var payload = JsonSerializer.SerializeToUtf8Bytes(rotated with { RootVersion = 3 }, Fixture.Options);
        Code("update.signatureInvalid", () => f.Store.ApplyRootRotation(Fixture.Sign(payload, ("release", f.ReleaseSigner)), f.Now));
        var indexPayload = Convert.FromBase64String(f.Index(bytes).PayloadBase64);
        Code("update.signatureInvalid", () => f.Store.VerifyChannelIndex(Fixture.Sign(indexPayload, ("release", f.ReleaseSigner)), "mabitool", "stable", f.Now));
        Assert.Equal(2, f.Store.Root.RootVersion);
    }

    [Fact]
    public void Legacy_root_does_not_implicitly_authorize_release_signatures()
    {
        using var f = new Fixture();
        var store = new UpdateTrustStore(f.Path("legacy.json"), new(1, 1, [f.Root.Keys[0] with { Roles = null }]), f.Now);
        var bytes = f.Release(signer: f.RootSigner);
        var indexPayload = Convert.FromBase64String(f.Index(bytes).PayloadBase64);
        var index = store.VerifyChannelIndex(Fixture.Sign(indexPayload, ("root", f.RootSigner)), "mabitool", "stable", f.Now);
        Code("update.signatureInvalid", () => store.VerifyReleaseManifest(bytes, index, "win-x64", f.Now));
    }

    [Fact]
    public void Rebound_tampered_payload_still_requires_valid_release_signature()
    {
        using var f = new Fixture();
        var envelope = JsonNode.Parse(f.Release())!;
        var payload = f.Payload();
        payload["releaseNotes"] = "tampered";
        envelope["payloadBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        var index = f.AcceptIndex(bytes);
        Code("update.signatureInvalid", () => f.Store.VerifyReleaseManifest(bytes, index, "win-x64", f.Now));
        Assert.Empty(f.Store.Releases);
    }

    [Fact]
    public void Old_index_is_rejected_when_its_signing_key_is_revoked()
    {
        using var f = new Fixture();
        var bytes = f.Release();
        var index = f.AcceptIndex(bytes);
        var rotated = f.Root with { RootVersion = 2, Keys = f.Root.Keys.Select(key => key.KeyId == "index" ? key with { Revoked = true } : key).ToArray() };
        f.Store.ApplyRootRotation(Fixture.Sign(JsonSerializer.SerializeToUtf8Bytes(rotated, Fixture.Options), ("root", f.RootSigner)), f.Now);
        Code("update.signatureInvalid", () => f.Store.VerifyReleaseManifest(bytes, index, "win-x64", f.Now));
    }

    [Fact]
    public void Missing_platform_and_mutable_collection_views_are_rejected()
    {
        using var f = new Fixture();
        var bytes = f.Release();
        var index = f.AcceptIndex(bytes);
        Code("update.platformUnsupported", () => f.Store.VerifyReleaseManifest(bytes, index, "win-arm64", f.Now));
        var release = f.Store.VerifyReleaseManifest(bytes, index, "win-x64", f.Now);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, ReleaseComponent>)release.Components).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<UpdateFileArtifact>)release.Components["core"].Artifacts.Files).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)f.Store.Root.Keys[0].Roles!).Clear());
    }

    private static void Code(string expected, Action action) => Assert.Equal(expected, Assert.Throws<TrustValidationException>(action).Code);

    private sealed class BlobHandler(Fixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var hash = request.RequestUri!.AbsolutePath.Trim('/');
            var path = hash == fixture.Artifacts.FullPackage!.DownloadSha256 ? fixture.Path("built/full.zip") : fixture.Path("built/blobs/" + hash + ".blob");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(File.ReadAllBytes(path)) });
        }
    }


}

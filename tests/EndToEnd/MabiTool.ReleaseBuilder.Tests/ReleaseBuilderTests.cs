using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Infrastructure.Updates;
using Xunit;

namespace MabiTool.ReleaseBuilder.Tests;

public sealed class ReleaseBuilderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Signed_updater_entry_survives_client_verification_and_controls_bootstrap(bool validEntry)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Path("updater"));
        await File.WriteAllBytesAsync(fixture.Path("updater/MabiTool.Updater.dll"),
            validEntry ? ManagedEntry() : await File.ReadAllBytesAsync(typeof(FileTreeArtifactBuilder).Assembly.Location));
        var input = fixture.Input() with { RuntimeMode = "framework-dependent", MinUpdaterVersion = "0.2.0",
            Components = fixture.Input().Components.Append(Fixture.Component("updater", "updater", false) with { Version = "0.2.0" }).ToArray() };
        await new ReleaseBuilderService().BuildAsync(input, fixture.RootDirectory, fixture.Path("entry-release"),
            [new("release", fixture.ReleaseSigner)], fixture.Root);
        await fixture.Publish("entry-release", 2);
        var trust = new UpdateTrustStore(fixture.Path("client-trust.json"), fixture.Root, fixture.Now);
        var index = trust.VerifyChannelIndex(ReleaseJson.Read<SignedPayloadEnvelope>(fixture.Path("channel/" + ChannelPublisher.IndexName)),
            "mabitool", "stable", fixture.Now);
        var release = trust.VerifyReleaseManifest(await File.ReadAllBytesAsync(fixture.Path("entry-release/" + ReleaseBuilderService.ManifestName)),
            index, "win-x64", fixture.Now);
        Assert.Equal(validEntry, Assert.Single(release.Components["updater"].Artifacts.Files).File.Executable);
        var planner = new UpdatePlanner(new FixedClock(fixture.Now));
        var context = new UpdatePlannerContext(new(0, 1, 0), 1, 1, "win-x64", new(10, 0, 19045), 100_000_000, []);
        if (validEntry)
            Assert.Equal(new Version(0, 2, 0), planner.CreateUpdaterBootstrapPlan(release, context,
                new(new(0, 1, 0), 1, "framework-dependent"), new HashSet<string>()).RequiredUpdaterVersion);
        else
            Assert.Equal("update.updaterRequired", Assert.Throws<UpdatePlanException>(() => planner.CreateUpdaterBootstrapPlan(release, context,
                new(new(0, 1, 0), 1, "framework-dependent"), new HashSet<string>())).Code);
    }

    private static byte[] ManagedEntry()
    {
        var metadata = new System.Reflection.Metadata.Ecma335.MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("entry"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString("entry"), new Version(1, 0), default, default, 0, System.Reflection.AssemblyHashAlgorithm.None);
        var signature = new System.Reflection.Metadata.BlobBuilder();
        signature.WriteBytes(new byte[] { 0, 0, 1 });
        var il = new System.Reflection.Metadata.BlobBuilder();
        var instructions = new System.Reflection.Metadata.BlobBuilder();
        instructions.WriteByte(0x2a);
        var body = new System.Reflection.Metadata.Ecma335.MethodBodyStreamEncoder(il).AddMethodBody(
            new System.Reflection.Metadata.Ecma335.InstructionEncoder(instructions));
        var method = metadata.AddMethodDefinition(System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            System.Reflection.MethodImplAttributes.IL, metadata.GetOrAddString("Main"), metadata.GetOrAddBlob(signature), body,
            System.Reflection.Metadata.Ecma335.MetadataTokens.ParameterHandle(1));
        metadata.AddTypeDefinition(System.Reflection.TypeAttributes.Public, default, metadata.GetOrAddString("Program"), default,
            System.Reflection.Metadata.Ecma335.MetadataTokens.FieldDefinitionHandle(1), method);
        var builder = new System.Reflection.PortableExecutable.ManagedPEBuilder(
            new System.Reflection.PortableExecutable.PEHeaderBuilder(imageCharacteristics:
                System.Reflection.PortableExecutable.Characteristics.ExecutableImage | System.Reflection.PortableExecutable.Characteristics.Dll),
            new System.Reflection.Metadata.Ecma335.MetadataRootBuilder(metadata), il, entryPoint: method);
        var bytes = new System.Reflection.Metadata.BlobBuilder();
        builder.Serialize(bytes);
        return bytes.ToArray();
    }
    [Theory]
    [InlineData("self-contained")]
    [InlineData("framework-dependent")]
    public async Task Builds_role_signed_release_with_real_files_full_packages_and_optional_empty_component(string runtimeMode)
    {
        using var fixture = new Fixture();
        var receipt = await new ReleaseBuilderService().BuildAsync(fixture.Input() with { RuntimeMode = runtimeMode }, fixture.RootDirectory,
            fixture.Path("release-one"), [new("release", fixture.ReleaseSigner)], fixture.Root);
        var verified = await new ReleaseBuilderService().VerifyAsync(fixture.Path("release-one"), fixture.Root);
        Assert.Equal(receipt, verified.Receipt with { Assets = receipt.Assets });
        Assert.Equal(new[] { "core", "optional-empty" }, verified.Manifest.Components.Keys);
        Assert.Empty(verified.Manifest.Components["optional-empty"].Artifacts.Files);
        var context = new UpdatePlannerContext(new(0, 1, 0), 1, 1, "win-x64", new(10, 0, 19045), 100_000_000, []);
        var plan = new UpdatePlanner(new FixedClock(fixture.Now)).CreatePlan(verified.Manifest, context,
            new(new(0, 1, 0), 1, runtimeMode), new HashSet<string>());
        Assert.Equal("core", Assert.Single(plan.Components).ComponentId);
        Assert.False(Directory.Exists(fixture.Path("release-one/built")));
        Assert.True(receipt.Assets.All(asset => File.Exists(fixture.Path("release-one/assets/" + asset.AssetName))));
        using var signer = fixture.ReleaseSigner;
        using var envelope = JsonDocument.Parse(await File.ReadAllBytesAsync(fixture.Path("release-one/release-manifest.v1.json")));
        Assert.Equal("release", envelope.RootElement.GetProperty("signatures")[0].GetProperty("keyId").GetString());
    }

    [Theory]
    [InlineData("asset-missing")]
    [InlineData("asset-changed")]
    [InlineData("manifest-changed")]
    public async Task Missing_asset_or_changed_manifest_does_not_advance_existing_index(string mutation)
    {
        using var fixture = new Fixture();
        await fixture.Build("release-one");
        await fixture.Publish("release-one", 1);
        var index = await File.ReadAllBytesAsync(fixture.Path("channel/channel-index.json"));
        var asset = Directory.GetFiles(fixture.Path("release-one/assets"))[0];
        if (mutation == "asset-missing") File.Delete(asset);
        else if (mutation == "asset-changed") File.WriteAllText(asset, "corrupted");
        else File.AppendAllText(fixture.Path("release-one/release-manifest.v1.json"), " ");
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Publish("release-one", 2));
        Assert.Equal(index, await File.ReadAllBytesAsync(fixture.Path("channel/channel-index.json")));
    }

    [Fact]
    public async Task Missing_publication_registry_cannot_weaken_release_immutability()
    {
        using var fixture = new Fixture();
        await fixture.Build("release-one");
        await fixture.Publish("release-one", 1);
        var pointer = await File.ReadAllBytesAsync(fixture.Path("channel/channel-index.json"));
        File.Delete(fixture.Path("channel/releases/release-1/release-manifest.v1.json"));
        var error = await Assert.ThrowsAsync<ReleaseBuildException>(() => fixture.Publish("release-one", 2));
        Assert.Equal("release.registryMissing", error.Code);
        Assert.Equal(pointer, await File.ReadAllBytesAsync(fixture.Path("channel/channel-index.json")));
    }

    [Fact]
    public async Task Channel_refresh_advances_index_without_rebuilding_or_resigning_release()
    {
        using var fixture = new Fixture();
        await fixture.Build("release-one");
        var manifest = await File.ReadAllBytesAsync(fixture.Path("release-one/release-manifest.v1.json"));
        var first = await fixture.Publish("release-one", 1);
        var second = await fixture.Publish("release-one", 2);
        Assert.Equal(2, second.IndexSequence);
        Assert.Equal(first.TargetManifestSha256, second.TargetManifestSha256);
        Assert.Equal(manifest, await File.ReadAllBytesAsync(fixture.Path("release-one/release-manifest.v1.json")));
        await Assert.ThrowsAsync<ReleaseBuildException>(() => fixture.Publish("release-one", 2));
    }

    [Fact]
    public async Task Reusing_release_id_with_changed_content_and_release_sequence_is_rejected()
    {
        using var fixture = new Fixture();
        await fixture.Build("release-one");
        await fixture.Publish("release-one", 1);
        File.WriteAllText(fixture.Path("component/app"), "changed");
        await fixture.Build("release-reused", sequence: 2);
        var before = await File.ReadAllBytesAsync(fixture.Path("channel/channel-index.json"));
        var error = await Assert.ThrowsAsync<ReleaseBuildException>(() => fixture.Publish("release-reused", 2));
        Assert.Equal("release.identityReused", error.Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Path("channel/channel-index.json")));
    }

    [Fact]
    public async Task Higher_index_cannot_point_to_older_release_sequence()
    {
        using var fixture = new Fixture();
        await fixture.Build("release-one", sequence: 1);
        await fixture.Build("release-two", sequence: 2, releaseId: "release-2");
        await fixture.Publish("release-two", 3);
        var error = await Assert.ThrowsAsync<TrustValidationException>(() => fixture.Publish("release-one", 4));
        Assert.Equal("update.releaseSequenceRollback", error.Code);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("index")]
    public async Task Unauthorized_signing_role_is_rejected(string role)
    {
        using var fixture = new Fixture();
        if (role == "release")
            await Assert.ThrowsAsync<TrustValidationException>(() => new ReleaseBuilderService().BuildAsync(fixture.Input(), fixture.RootDirectory,
                fixture.Path("wrong-role"), [new("index", fixture.IndexSigner)], fixture.Root));
        else
        {
            await fixture.Build("release-one");
            await Assert.ThrowsAsync<TrustValidationException>(() => new ChannelPublisher().PublishAsync(fixture.Path("release-one"), fixture.Path("channel"),
                fixture.Root, [new("release", fixture.ReleaseSigner)], 1, fixture.Now, fixture.Now.AddDays(1), fixture.Now));
        }
    }

    [Fact]
    public async Task Multi_signature_release_threshold_is_enforced()
    {
        using var fixture = new Fixture();
        using var secondSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = fixture.Root with { ReleaseThreshold = 2, Keys = fixture.Root.Keys.Append(Fixture.Key("release-2", secondSigner, "release-manifest")).ToArray() };
        await Assert.ThrowsAsync<TrustValidationException>(() => new ReleaseBuilderService().BuildAsync(fixture.Input(), fixture.RootDirectory,
            fixture.Path("threshold-failure"), [new("release", fixture.ReleaseSigner)], root));
        await new ReleaseBuilderService().BuildAsync(fixture.Input(), fixture.RootDirectory,
            fixture.Path("threshold-success"), [new("release", fixture.ReleaseSigner), new("release-2", secondSigner)], root);
    }

    [Fact]
    public async Task Adjacent_real_file_updates_and_full_package_fallback_rebuild_identical_targets()
    {
        using var fixture = new Fixture();
        await fixture.Build("release-one");
        var first = await new ReleaseBuilderService().VerifyAsync(fixture.Path("release-one"), fixture.Root);
        Directory.CreateDirectory(fixture.Path("old"));
        File.WriteAllText(fixture.Path("old/same"), "unchanged");
        File.WriteAllText(fixture.Path("old/app"), "previous");
        File.WriteAllText(fixture.Path("old/deleted"), "removed");
        using var downloader = new ResumableAssetDownloader(new AssetHandler(fixture.Path("release-one/assets")));
        var artifacts = first.Manifest.Components["core"].Artifacts;
        var incremental = await new FileTreeStager(downloader, new()).StageAsync(fixture.Path("old"), fixture.Path("first"), fixture.Path("cache"), artifacts,
            file => Sources(file.DownloadSha256));
        Assert.Equal(1, incremental.ReusedFiles);
        Assert.False(File.Exists(fixture.Path("first/deleted")));
        File.WriteAllText(fixture.Path("component/app"), "second version");
        File.WriteAllText(fixture.Path("component/added"), "new file");
        await fixture.Build("release-two", sequence: 2, releaseId: "release-2");
        var second = await new ReleaseBuilderService().VerifyAsync(fixture.Path("release-two"), fixture.Root);
        var target = second.Manifest.Components["core"].Artifacts;
        using var nextDownloader = new ResumableAssetDownloader(new AssetHandler(fixture.Path("release-two/assets")));
        var next = await new FileTreeStager(nextDownloader, new()).StageAsync(fixture.Path("first"), fixture.Path("second"), fixture.Path("cache-next"), target,
            file => Sources(file.DownloadSha256));
        using var fullDownloader = new ResumableAssetDownloader(new AssetHandler(fixture.Path("release-two/assets"), missingFileAssets: true));
        var fallback = await new FileTreeStager(fullDownloader, new()).StageWithFallbackAsync(fixture.Path("absent"), fixture.Path("full"), fixture.Path("cache-full"), target,
            file => [new("local", new Uri("https://local.invalid/missing/" + file.DownloadSha256 + ".blob"))], package => Sources(package.DownloadSha256));
        Assert.Equal(next.TargetTreeHash, fallback.TargetTreeHash);
        await FileTreePlanner.VerifyAsync(next.StageRoot, target.Files.Select(file => file.File).ToArray());
        await FileTreePlanner.VerifyAsync(fallback.StageRoot, target.Files.Select(file => file.File).ToArray());
    }

    [Fact]
    public async Task Signing_private_key_in_component_is_rejected_before_distribution()
    {
        using var fixture = new Fixture();
        var path = fixture.Path("component/release-key.pem");
        File.WriteAllText(path, fixture.ReleaseSigner.ExportECPrivateKeyPem());
        var error = await Assert.ThrowsAsync<ReleaseBuildException>(() => new ReleaseBuilderService().BuildAsync(fixture.Input(), fixture.RootDirectory,
            fixture.Path("private-key"), [new("release", fixture.ReleaseSigner, path)], fixture.Root));
        Assert.Equal("release.privateKeyInComponent", error.Code);
        Assert.False(Directory.Exists(fixture.Path("private-key")));
    }

    [Fact]
    public async Task Existing_output_and_duplicate_input_properties_are_rejected()
    {
        using var fixture = new Fixture();
        await fixture.Build("release-one");
        await Assert.ThrowsAsync<ReleaseBuildException>(() => fixture.Build("release-one"));
        File.WriteAllText(fixture.Path("duplicate.json"), "{\"rootVersion\":1,\"rootVersion\":2}");
        Assert.Throws<ReleaseBuildException>(() => ReleaseJson.Read<TrustRoot>(fixture.Path("duplicate.json")));
    }

    private static IReadOnlyList<AssetDownloadSource> Sources(string hash) => [new("local", new Uri("https://local.invalid/" + hash + ".blob"))];

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AssetHandler(string root, bool missingFileAssets = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (missingFileAssets && request.RequestUri!.AbsolutePath.StartsWith("/missing/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(File.OpenRead(System.IO.Path.Combine(root, request.RequestUri!.AbsolutePath.TrimStart('/')))) });
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string RootDirectory { get; } = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../tmp/release-builder-tests", Guid.NewGuid().ToString("N")));
        internal DateTimeOffset Now { get; } = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        internal ECDsa RootSigner { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal ECDsa IndexSigner { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal ECDsa ReleaseSigner { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal TrustRoot Root { get; }
        internal Fixture()
        {
            Root = new(1, 1, [Key("root", RootSigner, "root"), Key("index", IndexSigner, "channel-index"), Key("release", ReleaseSigner, "release-manifest")], 1, 1);
            Directory.CreateDirectory(Path("component"));
            Directory.CreateDirectory(Path("empty"));
            File.WriteAllText(Path("component/app"), "current");
            File.WriteAllText(Path("component/same"), "unchanged");
            File.WriteAllText(Path("sbom.json"), "{\"spdxVersion\":\"SPDX-2.3\"}");
            File.WriteAllText(Path("notices.txt"), "Source and applicable third-party notices");
        }
        internal static TrustKey Key(string id, ECDsa key, string role) => new(id, "ECDSA-P256-SHA256", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), false, [role]);
        internal string Path(string relative) => System.IO.Path.Combine(RootDirectory, relative);
        internal ReleaseBuildInput Input(long sequence = 1, string releaseId = "release-1") => new(1, "mabitool", "stable", releaseId, "0.1.0", sequence, Now,
            "0.1.0", "x64", "10.0.19041", "self-contained", "", "sbom.json", "notices.txt",
            [Component("core", "component", false), Component("optional-empty", "empty", true)], []);
        internal static ReleaseComponentInput Component(string id, string directory, bool optional) => new(id, directory, "0.1.0", optional,
            new(1, 1), new("0.1.0", "9.0.0"), new(1, 1), new(0, 100), []);
        internal Task<ReleaseBuildReceipt> Build(string directory, long sequence = 1, string releaseId = "release-1") => new ReleaseBuilderService()
            .BuildAsync(Input(sequence, releaseId), RootDirectory, Path(directory), [new("release", ReleaseSigner)], Root);
        internal Task<VerifiedChannelIndex> Publish(string directory, long sequence) => new ChannelPublisher().PublishAsync(Path(directory), Path("channel"), Root,
            [new("index", IndexSigner)], sequence, Now, Now.AddDays(1), Now);
        public void Dispose()
        {
            RootSigner.Dispose(); IndexSigner.Dispose(); ReleaseSigner.Dispose();
            var root = System.IO.Path.GetFullPath(RootDirectory);
            if (!root.Contains(System.IO.Path.Combine("artifacts", "tmp", "release-builder-tests"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected test output root");
            Directory.Delete(root, recursive: true);
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Infrastructure.Updates;

namespace MabiTool.Updates.Tests;

internal sealed class SignedReleaseFixture : IDisposable
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly UpdateTestWorkspace workspace = new();
    internal readonly ECDsa RootSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    internal readonly ECDsa IndexSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    internal readonly ECDsa ReleaseSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    internal readonly DateTimeOffset Now = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
    internal TrustRoot Root { get; }
    internal UpdateTrustStore Store { get; }
    internal FileTreeArtifacts Artifacts { get; }
    internal SignedReleaseFixture(bool buildArtifacts = false)
    {
        Root = new(1, 1, [Key("root", RootSigner, "root"), Key("index", IndexSigner, "channel-index"), Key("release", ReleaseSigner, "release-manifest")], 1, 1);
        Store = new(Path("trust.json"), Root, Now);
        Directory.CreateDirectory(Path("release"));
        Directory.CreateDirectory(Path("current"));
        File.WriteAllText(Path("release/app"), "new");
        File.WriteAllText(Path("release/same"), "same");
        File.WriteAllText(Path("current/app"), "old");
        File.WriteAllText(Path("current/same"), "same");
        if (buildArtifacts)
            Artifacts = new FileTreeArtifactBuilder().BuildAsync(Path("release"), Path("built")).GetAwaiter().GetResult();
        else
        {
            var files = new[] { new ManifestFile("app", 3, Hash("new"u8.ToArray())), new ManifestFile("same", 4, Hash("same"u8.ToArray())) };
            var tree = FileTreePlanner.Plan(Path("absent"), files).TargetTreeHash;
            Artifacts = new(files.Select(file => new UpdateFileArtifact(file, file.SizeBytes, file.Sha256, UpdateBlobEncoding.Identity)).ToArray(),
                tree, new(tree, 100, Hash("package"u8.ToArray())));
        }
    }
    internal string Path(string path) => workspace.PathFor(path);
    internal UpdateTrustStore Reload() => new(Path("trust.json"), Root, Now);
    internal static TrustKey Key(string id, ECDsa key, params string[] roles) => new(id, "ECDSA-P256-SHA256", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), false, roles);
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static SignedPayloadEnvelope Sign(byte[] payload, params (string Id, ECDsa Key)[] signers) => new(1, Convert.ToBase64String(payload),
        signers.Select(item => new EnvelopeSignature(item.Id, "ECDSA-P256-SHA256", Convert.ToBase64String(item.Key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)))).ToArray());
    internal byte[] Release(JsonNode? node = null, ECDsa? signer = null) => ReleaseText((node ?? Payload()).ToJsonString(), signer);
    internal byte[] ReleaseText(string text, ECDsa? signer = null) => JsonSerializer.SerializeToUtf8Bytes(Sign(Encoding.UTF8.GetBytes(text),
        (signer == RootSigner ? "root" : "release", signer ?? ReleaseSigner)), Options);
    internal SignedPayloadEnvelope Index(byte[] bytes, long sequence = 1, long releaseSequence = 1, Action<JsonNode>? mutate = null)
    {
        var node = JsonSerializer.SerializeToNode(new
        {
            schemaVersion = 1, productId = "mabitool", channel = "stable", indexSequence = sequence,
            issuedAt = Now, expiresAt = Now.AddHours(1), targetManifestSha256 = Hash(bytes),
            targetManifestLength = bytes.Length, targetReleaseSequence = releaseSequence
        }, Options)!;
        mutate?.Invoke(node);
        return Sign(Encoding.UTF8.GetBytes(node.ToJsonString()), ("index", IndexSigner));
    }
    internal VerifiedChannelIndex AcceptIndex(byte[] bytes, long sequence = 1, long releaseSequence = 1, Action<JsonNode>? mutate = null) =>
        Store.VerifyChannelIndex(Index(bytes, sequence, releaseSequence, mutate), "mabitool", "stable", Now);
    internal JsonNode Payload() => JsonSerializer.SerializeToNode(new
    {
        schemaVersion = 1, productId = "mabitool", channel = "stable", releaseId = "release-1", version = "1.0.0-beta.1+build",
        releaseSequence = 1, publishedAt = Now, minUpdaterVersion = "0.1.0",
        platforms = new[] { new { os = "windows", architecture = "x64", minimumOsBuild = "10.0.19041", runtimeMode = "self-contained",
            components = new[] { new { componentId = "core", version = "1.0.0", optional = false,
                moduleApiRange = new { minimum = 1, maximum = 1 }, hostVersionRange = new { minimum = "0.1.0", maximum = "9.0.0" },
                workerProtocolRange = new { minimum = 1, maximum = 1 }, dataSchemaRange = new { minimum = 0, maximum = 10 },
                dependencies = System.Array.Empty<object>(), installedSize = Artifacts.Files.Sum(item => item.File.SizeBytes), targetTreeHash = Artifacts.TargetTreeHash,
                files = Artifacts.Files.Select(item => new { path = item.File.RelativePath, size = item.File.SizeBytes, sha256 = item.File.Sha256,
                    componentId = "core", executable = true, encoding = item.Encoding == UpdateBlobEncoding.Gzip ? "gzip" : "identity", asset = Asset(item.DownloadLength, item.DownloadSha256) }),
                fullPackage = Asset(Artifacts.FullPackage!.DownloadLength, Artifacts.FullPackage.DownloadSha256) } } } },
        data = new[] { new { moduleId = "core", schemaFrom = 1, schemaTo = 2, oldReadableSchemaRange = new { minimum = 1, maximum = 1 }, migrationId = "core-2", requiresFullBackup = true } },
        releaseNotes = "", sbom = Asset(0, Hash([])), notices = Asset(0, Hash([]))
    }, Options)!;
    private static object Asset(long length, string sha256) => new { assetName = sha256 + ".blob", length, sha256 };
    public void Dispose() { RootSigner.Dispose(); IndexSigner.Dispose(); ReleaseSigner.Dispose(); workspace.Dispose(); }
}

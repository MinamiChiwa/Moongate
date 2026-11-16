using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MabiTool.Infrastructure.Updates;

internal static class ReleaseManifestParser
{
    internal const int MaximumPayloadBytes = 8 * 1024 * 1024;
    internal const int MaximumEnvelopeBytes = 12 * 1024 * 1024;

    internal static VerifiedReleaseManifest Parse(byte[] payload, string manifestHash, VerifiedChannelIndex index,
        string expectedPlatform, DateTimeOffset now)
    {
        using var document = StrictJson.Parse(payload, MaximumPayloadBytes);
        var root = document.RootElement;
        StrictJson.RequireObject(root, "update.manifestInvalid");
        StrictJson.RejectUnknown(root, "schemaVersion", "productId", "channel", "releaseId", "version", "releaseSequence", "publishedAt",
            "minUpdaterVersion", "platforms", "data", "releaseNotes", "sbom", "notices");
        if (StrictJson.Int32(root, "schemaVersion") != 1) throw new TrustValidationException("update.manifestVersionUnsupported");
        var product = Identifier(root, "productId");
        var channel = Identifier(root, "channel", 32);
        var release = Identifier(root, "releaseId");
        var sequence = StrictJson.Int64(root, "releaseSequence");
        if (product != index.ProductId || channel != index.Channel || sequence != index.TargetReleaseSequence)
            throw new TrustValidationException("update.identityMismatch");
        var displayVersion = StrictJson.String(root, "version", 128);
        if (!Regex.IsMatch(displayVersion, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            throw new TrustValidationException("update.metadataFieldInvalid");
        var updater = Version(root, "minUpdaterVersion");
        var published = Time(root, "publishedAt");
        if (published > now.AddMinutes(2)) throw new TrustValidationException("update.metadataExpired");
        if (!root.TryGetProperty("releaseNotes", out var notes) || notes.ValueKind != JsonValueKind.String || notes.GetString()!.Length > 16_384)
            throw new TrustValidationException("update.metadataFieldInvalid");
        var sbom = Asset(Object(root, "sbom"));
        var notices = Asset(Object(root, "notices"));
        var migrations = ParseMigrations(Array(root, "data", 0, 128));
        var platforms = Array(root, "platforms", 1, 8);
        var seenPlatforms = new HashSet<string>(StringComparer.Ordinal);
        var totalFiles = 0;
        Dictionary<string, ReleaseComponent>? selected = null;
        string? runtimeMode = null;
        foreach (var platform in platforms.EnumerateArray())
        {
            StrictJson.RequireObject(platform, "update.manifestInvalid");
            StrictJson.RejectUnknown(platform, "os", "architecture", "minimumOsBuild", "runtimeMode", "components");
            var os = StrictJson.String(platform, "os", 32);
            var architecture = StrictJson.String(platform, "architecture", 32);
            var mode = StrictJson.String(platform, "runtimeMode", 32);
            if (os != "windows" || architecture is not ("x64" or "arm64") || mode is not ("self-contained" or "framework-dependent"))
                throw new TrustValidationException("update.platformUnsupported");
            var platformId = "win-" + architecture;
            if (!seenPlatforms.Add(platformId)) throw new TrustValidationException("update.manifestInvalid");
            var minimumOs = Version(platform, "minimumOsBuild");
            var components = new Dictionary<string, ReleaseComponent>(StringComparer.Ordinal);
            foreach (var item in Array(platform, "components", 1, 64).EnumerateArray())
            {
                var parsed = Component(item, minimumOs, ref totalFiles);
                if (!components.TryAdd(parsed.Metadata.ComponentId, parsed)) throw new TrustValidationException("update.manifestInvalid");
            }
            // 所有平台的依赖都要自洽，未选平台同样不能夹带歧义元数据。
            ValidateDependencies(components);
            if (platformId == expectedPlatform) { selected = components; runtimeMode = mode; }
        }
        if (selected is null) throw new TrustValidationException("update.platformUnsupported");
        var manifest = new UpdateManifest(release, sequence, product, channel, expectedPlatform,
            System.Array.AsReadOnly(selected.Values.Select(item => item.Metadata).ToArray()), manifestHash);
        return new(manifest, displayVersion, updater, runtimeMode!, published, notes.GetString()!,
            new ReadOnlyDictionary<string, ReleaseComponent>(selected), migrations, sbom, notices, index.RootVersion, index.IndexSequence, index.ExpiresAt);
    }

    private static ReleaseComponent Component(JsonElement item, System.Version minimumOs, ref int totalFiles)
    {
        StrictJson.RequireObject(item, "update.manifestInvalid");
        StrictJson.RejectUnknown(item, "componentId", "version", "optional", "moduleApiRange", "hostVersionRange", "workerProtocolRange",
            "dataSchemaRange", "dependencies", "installedSize", "targetTreeHash", "files", "fullPackage");
        var id = Identifier(item, "componentId");
        var version = Version(item, "version");
        var optional = StrictJson.Boolean(item, "optional");
        var moduleApi = Integers(Object(item, "moduleApiRange"));
        var host = Versions(Object(item, "hostVersionRange"));
        var worker = Integers(Object(item, "workerProtocolRange"));
        var schema = Integers(Object(item, "dataSchemaRange"));
        var dependencies = new List<UpdateDependency>();
        var seenDependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in Array(item, "dependencies", 0, 64).EnumerateArray())
        {
            StrictJson.RequireObject(dependency, "update.manifestInvalid");
            StrictJson.RejectUnknown(dependency, "componentId", "minimumVersion");
            var dependencyId = Identifier(dependency, "componentId");
            if (dependencyId == id || !seenDependencies.Add(dependencyId)) throw new TrustValidationException("update.manifestInvalid");
            dependencies.Add(new(dependencyId, Version(dependency, "minimumVersion")));
        }
        var files = new List<UpdateFileArtifact>();
        long installed = 0;
        long download = 0;
        var uniqueBlobs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Array(item, "files", 0, 100_000).EnumerateArray())
        {
            if (++totalFiles > 100_000) throw new TrustValidationException("update.manifestTooLarge");
            StrictJson.RequireObject(file, "update.manifestInvalid");
            StrictJson.RejectUnknown(file, "path", "size", "sha256", "componentId", "executable", "encoding", "asset");
            if (Identifier(file, "componentId") != id) throw new TrustValidationException("update.identityMismatch");
            // Windows 首版不从此标记改变文件权限，仍保留签名声明供发行审查。
            var executable = StrictJson.Boolean(file, "executable");
            var raw = new ManifestFile(StrictJson.String(file, "path", 1024), Length(file, "size"), Hash(file, "sha256"), executable);
            var encoding = StrictJson.String(file, "encoding", 16) switch
            {
                "identity" => UpdateBlobEncoding.Identity, "gzip" => UpdateBlobEncoding.Gzip,
                _ => throw new TrustValidationException("update.assetMetadataInvalid")
            };
            var asset = Asset(Object(file, "asset"));
            installed = checked(installed + raw.SizeBytes);
            if (uniqueBlobs.Add(asset.Sha256)) download = checked(download + asset.Length);
            files.Add(new(raw, asset.Length, asset.Sha256, encoding));
        }
        if (StrictJson.Int64(item, "installedSize") != installed) throw new TrustValidationException("update.manifestInvalid");
        var treeHash = Hash(item, "targetTreeHash");
        var package = Asset(Object(item, "fullPackage"));
        var artifacts = new FileTreeArtifacts(System.Array.AsReadOnly(files.ToArray()), treeHash, new(treeHash, package.Length, package.Sha256));
        try { FileTreeStager.ValidateArtifacts(artifacts); }
        catch (FileTreeException error) { throw new TrustValidationException(error.Code); }
        // 预算包含文件增量失败后再取完整包的传输上界。
        var metadata = new UpdateComponent(id, version, System.Array.AsReadOnly(dependencies.ToArray()), checked(download + package.Length), installed,
            treeHash, host.Minimum.ToString(), host.Maximum.ToString(), worker.Minimum, worker.Maximum, schema.Minimum, schema.Maximum, minimumOs.ToString(), optional);
        return new(metadata, moduleApi, artifacts);
    }

    private static IReadOnlyList<ReleaseDataMigration> ParseMigrations(JsonElement array)
    {
        var result = new List<ReleaseDataMigration>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            StrictJson.RequireObject(item, "update.manifestInvalid");
            StrictJson.RejectUnknown(item, "moduleId", "schemaFrom", "schemaTo", "oldReadableSchemaRange", "migrationId", "requiresFullBackup");
            var module = Identifier(item, "moduleId");
            var from = StrictJson.Int32(item, "schemaFrom");
            var to = StrictJson.Int32(item, "schemaTo");
            var readable = Integers(Object(item, "oldReadableSchemaRange"));
            var backup = StrictJson.Boolean(item, "requiresFullBackup");
            if (!seen.Add(module) || to < from || from < readable.Minimum || from > readable.Maximum ||
                (to < readable.Minimum || to > readable.Maximum) && !backup)
                throw new TrustValidationException("update.migrationInvalid");
            result.Add(new(module, from, to, readable, Identifier(item, "migrationId"), backup));
        }
        return System.Array.AsReadOnly(result.ToArray());
    }

    private static void ValidateDependencies(Dictionary<string, ReleaseComponent> components)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in components.Keys) Visit(id);
        void Visit(string id)
        {
            if (done.Contains(id)) return;
            if (!visiting.Add(id)) throw new TrustValidationException("update.dependencyCycle");
            foreach (var dependency in components[id].Metadata.Dependencies)
            {
                if (!components.TryGetValue(dependency.ComponentId, out var found) || found.Metadata.Version < dependency.MinimumVersion)
                    throw new TrustValidationException("update.incompatibleComponents");
                Visit(dependency.ComponentId);
            }
            visiting.Remove(id);
            done.Add(id);
        }
    }

    internal static ReleaseAsset Asset(JsonElement element)
    {
        StrictJson.RejectUnknown(element, "assetName", "length", "sha256");
        var hash = Hash(element, "sha256");
        var name = StrictJson.String(element, "assetName", 128);
        if (name != hash + ".blob") throw new TrustValidationException("update.assetMetadataInvalid");
        return new(name, Length(element, "length"), hash);
    }
    private static long Length(JsonElement element, string name)
    {
        var length = StrictJson.Int64(element, name);
        if (length > 20L * 1024 * 1024 * 1024) throw new TrustValidationException("update.assetMetadataInvalid");
        return length;
    }
    internal static string Identifier(JsonElement element, string name, int maximum = 128)
    {
        var value = StrictJson.String(element, name, maximum);
        if (!Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            throw new TrustValidationException("update.metadataFieldInvalid");
        return value;
    }
    internal static string Hash(JsonElement element, string name)
    {
        var hash = StrictJson.String(element, name, 64);
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new TrustValidationException("update.metadataFieldInvalid");
        return hash.ToLowerInvariant();
    }
    internal static DateTimeOffset Time(JsonElement element, string name)
    {
        if (!DateTimeOffset.TryParse(StrictJson.String(element, name, 64), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) || value.Offset != TimeSpan.Zero)
            throw new TrustValidationException("update.metadataFieldInvalid");
        return value;
    }
    private static System.Version Version(JsonElement element, string name) => System.Version.TryParse(StrictJson.String(element, name, 64), out var version)
        ? version : throw new TrustValidationException("update.metadataFieldInvalid");
    private static ReleaseVersionRange Versions(JsonElement element)
    {
        StrictJson.RejectUnknown(element, "minimum", "maximum");
        var range = new ReleaseVersionRange(Version(element, "minimum"), Version(element, "maximum"));
        if (range.Minimum > range.Maximum) throw new TrustValidationException("update.metadataFieldInvalid");
        return range;
    }
    private static ReleaseIntegerRange Integers(JsonElement element)
    {
        StrictJson.RejectUnknown(element, "minimum", "maximum");
        var range = new ReleaseIntegerRange(StrictJson.Int32(element, "minimum"), StrictJson.Int32(element, "maximum"));
        if (range.Minimum > range.Maximum) throw new TrustValidationException("update.metadataFieldInvalid");
        return range;
    }
    private static JsonElement Object(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) throw new TrustValidationException("update.metadataFieldInvalid");
        StrictJson.RequireObject(value, "update.metadataFieldInvalid");
        return value;
    }
    private static JsonElement Array(JsonElement element, string name, int minimum, int maximum)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < minimum || value.GetArrayLength() > maximum)
            throw new TrustValidationException("update.metadataFieldInvalid");
        return value;
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MabiTool.Infrastructure.Updates;

namespace MabiTool.ReleaseBuilder;

public sealed record IntegerRange(int Minimum, int Maximum);
public sealed record VersionRange(string Minimum, string Maximum);
public sealed record ComponentDependency(string ComponentId, string MinimumVersion);
public sealed record ReleaseComponentInput(string ComponentId, string Directory, string Version, bool Optional,
    IntegerRange ModuleApiRange, VersionRange HostVersionRange, IntegerRange WorkerProtocolRange,
    IntegerRange DataSchemaRange, IReadOnlyList<ComponentDependency> Dependencies);
public sealed record ReleaseBuildInput(int SchemaVersion, string ProductId, string Channel, string ReleaseId,
    string Version, long ReleaseSequence, DateTimeOffset PublishedAt, string MinUpdaterVersion,
    string Architecture, string MinimumOsBuild, string RuntimeMode, string ReleaseNotes,
    string SbomPath, string NoticesPath, IReadOnlyList<ReleaseComponentInput> Components,
    IReadOnlyList<ReleaseDataMigrationInput> Data);
public sealed record ReleaseDataMigrationInput(string ModuleId, int SchemaFrom, int SchemaTo,
    IntegerRange OldReadableSchemaRange, string MigrationId, bool RequiresFullBackup);
public sealed record SigningKey(string KeyId, ECDsa Key, string? SourcePath = null);
public sealed record AssetDescriptor(string AssetName, long Length, string Sha256);
public sealed record ReleaseBuildReceipt(int SchemaVersion, string ProductId, string Channel, string ReleaseId,
    long ReleaseSequence, AssetDescriptor Manifest, IReadOnlyList<AssetDescriptor> Assets);
public sealed record ReleaseVerification(ReleaseBuildReceipt Receipt, VerifiedReleaseManifest Manifest);

public static class ReleaseJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 32
    };

    public static T Read<T>(string path, int maximumBytes = 16 * 1024 * 1024)
    {
        ReleaseFiles.RequireSafePath(path);
        if (new FileInfo(path).Length is < 1 || new FileInfo(path).Length > maximumBytes)
            throw new ReleaseBuildException("release.inputTooLarge");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is < 1 || bytes.Length > maximumBytes) throw new ReleaseBuildException("release.inputTooLarge");
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
        RejectDuplicateProperties(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new ReleaseBuildException("release.inputInvalid");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ReleaseBuildException("release.duplicateField");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
}

public sealed class ReleaseBuildException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

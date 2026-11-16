using System.Text.Json;
using System.Text.RegularExpressions;

namespace MabiTool.Infrastructure.Updates;

internal sealed record InstallTargetDescriptor(int SchemaVersion, string ProductId, string HostVersion,
    string HostApiVersion, int DataSchema, string RuntimeMode, string HostEntryPoint,
    IReadOnlyList<DatabaseMigrationStamp> Migrations, IReadOnlyList<InstallHealthModule> Modules)
{
    internal const string RelativePath = "components/core/install-target.json";
    internal static string Entry(string runtimeMode) => runtimeMode switch
    {
        "self-contained" => "components/core/MabiTool.Host.exe",
        "framework-dependent" => "components/core/MabiTool.Host.dll",
        _ => throw new UpdatePlanException("update.installTargetInvalid")
    };

    internal static async Task<InstallTargetDescriptor> ReadAsync(string versionRoot, string productId, CancellationToken ct = default)
    {
        var path = UpdatePaths.Local(versionRoot, RelativePath);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length is <= 0 or > 262144) throw Invalid();
        var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        try
        {
            using var json = StrictJson.Parse(bytes, 262144);
            var value = json.RootElement.Deserialize<InstallTargetDescriptor>(UpdateHandoffProtocol.Json) ?? throw Invalid();
            value.Validate(productId); return value;
        }
        catch (Exception error) when (error is JsonException or TrustValidationException) { throw Invalid(); }
    }

    internal void Validate(string productId)
    {
        if (SchemaVersion != 1 || ProductId != productId || !Version.TryParse(HostVersion, out _) ||
            !Version.TryParse(HostApiVersion, out _) || DataSchema < 0 || HostEntryPoint != Entry(RuntimeMode) ||
            Modules is not { Count: > 0 and <= 128 } || Modules.Any(item => item is null || !ModuleId(item.Id) || !Version.TryParse(item.Version, out _)) ||
            Modules.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != Modules.Count || Modules.All(item => item.Id != "core") ||
            Migrations is not { Count: >= 4 and <= 4096 }) throw Invalid();
        var versions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var stamp in Migrations.OrderBy(item => item?.ModuleId, StringComparer.Ordinal).ThenBy(item => item?.Version))
        {
            if (stamp is null || !ModuleId(stamp.ModuleId) || stamp.Version != versions.GetValueOrDefault(stamp.ModuleId) + 1 ||
                stamp.Checksum is not { Length: 64 } || !stamp.Checksum.All(Uri.IsHexDigit)) throw Invalid();
            versions[stamp.ModuleId] = stamp.Version;
        }
        if (versions.GetValueOrDefault("core") < 4 ||
            !Migrations.SequenceEqual(Migrations.OrderBy(item => item.ModuleId, StringComparer.Ordinal).ThenBy(item => item.Version)) ||
            !Modules.SequenceEqual(Modules.OrderBy(item => item.Id, StringComparer.Ordinal))) throw Invalid();
    }

    internal InstallHealthTarget HealthTarget(string rootId, string databaseId, string backendMode, IReadOnlySet<string> requiredModules)
    {
        Validate(ProductId);
        if (!requiredModules.Contains("core") || requiredModules.Any(id => Modules.All(module => module.Id != id)))
            throw new UpdatePlanException("update.incompatibleComponents");
        var api = Version.Parse(HostApiVersion);
        return new(HostVersion, api, new(api.Major + 1, 0), rootId, databaseId, backendMode,
            Migrations.ToArray(), Modules.Where(module => requiredModules.Contains(module.Id)).ToArray());
    }

    internal void RequireSame(InstallTargetDescriptor actual)
    {
        if (JsonSerializer.Serialize(this, UpdateHandoffProtocol.Json) != JsonSerializer.Serialize(actual, UpdateHandoffProtocol.Json)) throw Invalid();
    }
    private static bool ModuleId(string? value) => value is not null && Regex.IsMatch(value, @"\A[a-z][a-z0-9-]{0,63}\z");
    private static UpdatePlanException Invalid() => new("update.installTargetInvalid");
}

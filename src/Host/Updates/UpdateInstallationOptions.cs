using System.Text.Json;
using System.Text.Json.Serialization;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.Providers.GitHubReleases;

namespace MabiTool.Host.Updates;

public sealed record UpdateInstallationOptions(string ProductId, string InstallationRoot, UpdateSourceSettings Source,
    TrustRoot InitialRoot, long InstalledReleaseSequence, string InstalledManifestSha256,
    UpdatePlannerContext PlannerContext, ReleaseRuntimeContext Runtime, string? InstalledReleaseId = null, string? BootstrapDirectory = null)
{
    public static UpdateInstallationOptions? FromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("MABITOOL_UPDATE_CONFIGURATION");
        if (string.IsNullOrWhiteSpace(path)) return null;
        StoragePaths.ValidateLocal(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 262_144) throw new ArgumentException("安装更新配置超过 256 KiB");
        try
        {
            var options = JsonSerializer.Deserialize<UpdateInstallationOptions>(input,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
                ?? throw new ArgumentException("安装更新配置为空");
            options.Validate();
            return options;
        }
        catch (JsonException error) { throw new ArgumentException("安装更新配置格式无效", error); }
    }

    public void Validate()
    {
        StoragePaths.ValidateLocal(InstallationRoot);
        if (BootstrapDirectory is not null) StoragePaths.ValidateLocal(BootstrapDirectory);
        if (ProductId is not { Length: > 0 and <= 128 } || ProductId.Any(char.IsControl) || InitialRoot is null ||
            Source is null || PlannerContext is null || Runtime is null || InstalledReleaseSequence < 0 ||
            InstalledManifestSha256 is not { Length: 64 } || !InstalledManifestSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("安装更新身份、根信任或运行上下文无效");
        if (InstalledReleaseSequence > 0 && InstalledReleaseId is null ||
            InstalledReleaseId is not null && (InstalledReleaseId.Length is < 1 or > 128 ||
                !char.IsAsciiLetterOrDigit(InstalledReleaseId[0]) ||
                InstalledReleaseId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '_' or '-'))))
            throw new ArgumentException("已安装发行需要明确的发行 ID");
        if (InstalledReleaseId is not null) _ = UpdatePaths.Local(VersionsRoot, InstalledReleaseId);
        UpdatePaths.RequireDisjoint(StateRoot, VersionsRoot, StagingRoot);
        // 仅校验配置，不获取目录或资产。
        using var provider = new ReleaseIndexSourceProvider();
        _ = provider.BuildReleaseAssetSources(Source, "configuration-validation", InstalledManifestSha256);
    }

    internal string StateRoot => Path.Combine(Path.GetFullPath(InstallationRoot), "update-state");
    internal UpdateInstallValidation Validation => new(ProductId, Source.Channel, InitialRoot, InstalledReleaseSequence,
        InstalledManifestSha256, PlannerContext, Runtime);
    internal string VersionsRoot => Path.Combine(Path.GetFullPath(InstallationRoot), "versions");
    internal string StagingRoot => Path.Combine(Path.GetFullPath(InstallationRoot), "staging");
    internal string CacheRoot => Path.Combine(StateRoot, "download-cache");
    internal string? CurrentVersionRoot => InstalledReleaseId is null ? null : UpdatePaths.Local(VersionsRoot, InstalledReleaseId);
}

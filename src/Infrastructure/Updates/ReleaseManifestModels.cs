namespace MabiTool.Infrastructure.Updates;

public sealed record ReleaseVersionRange(Version Minimum, Version Maximum);
public sealed record ReleaseIntegerRange(int Minimum, int Maximum);
public sealed record ReleaseDataMigration(string ModuleId, int SchemaFrom, int SchemaTo,
    ReleaseIntegerRange OldReadableSchemaRange, string MigrationId, bool RequiresFullBackup);
public sealed record ReleaseAsset(string AssetName, long Length, string Sha256);
public sealed record ReleaseComponent(UpdateComponent Metadata, ReleaseIntegerRange ModuleApiRange, FileTreeArtifacts Artifacts);

/// <summary>仅由信任层构造；组件和文件均来自索引绑定的签名原始字节。</summary>
public sealed class VerifiedReleaseManifest
{
    public UpdateManifest Manifest { get; }
    public string DisplayVersion { get; }
    public Version MinimumUpdaterVersion { get; }
    public string RuntimeMode { get; }
    public DateTimeOffset PublishedAt { get; }
    public string ReleaseNotes { get; }
    public IReadOnlyDictionary<string, ReleaseComponent> Components { get; }
    public IReadOnlyList<ReleaseDataMigration> DataMigrations { get; }
    public ReleaseAsset Sbom { get; }
    public ReleaseAsset Notices { get; }
    public int RootVersion { get; }
    public long IndexSequence { get; }
    public DateTimeOffset AuthorizedUntil { get; }

    internal VerifiedReleaseManifest(UpdateManifest manifest, string version, Version minimumUpdaterVersion, string runtimeMode,
        DateTimeOffset publishedAt, string releaseNotes, IReadOnlyDictionary<string, ReleaseComponent> components,
        IReadOnlyList<ReleaseDataMigration> dataMigrations, ReleaseAsset sbom, ReleaseAsset notices,
        int rootVersion, long indexSequence, DateTimeOffset authorizedUntil)
    {
        Manifest = manifest; DisplayVersion = version; MinimumUpdaterVersion = minimumUpdaterVersion; RuntimeMode = runtimeMode;
        PublishedAt = publishedAt; ReleaseNotes = releaseNotes; Components = components; DataMigrations = dataMigrations;
        Sbom = sbom; Notices = notices; RootVersion = rootVersion; IndexSequence = indexSequence; AuthorizedUntil = authorizedUntil;
    }
}

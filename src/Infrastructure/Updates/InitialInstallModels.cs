using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MabiTool.Infrastructure.Updates;

internal sealed record InitialInstallRequest(int SchemaVersion, string PackageRoot, string ReleaseManifestPath,
    string ChannelIndexPath, string TrustRootPath, string InstallationRoot, string BootstrapDirectory,
    string DataRoot, string GithubRepository, string Channel, IReadOnlyList<string> OptionalComponents)
{
    internal InitialInstallRequest Normalize()
    {
        if (SchemaVersion != 1 || OptionalComponents is null || OptionalComponents.Count > 64 ||
            OptionalComponents.Any(value => value is null || !Regex.IsMatch(value, @"\A[a-z][a-z0-9-]{0,63}\z")) ||
            OptionalComponents.Distinct(StringComparer.Ordinal).Count() != OptionalComponents.Count ||
            Channel is null || !Regex.IsMatch(Channel, @"\A[a-zA-Z0-9_-]{1,32}\z") ||
            GithubRepository is null || !Regex.IsMatch(GithubRepository, @"\A[a-zA-Z0-9_-]{1,100}/[a-zA-Z0-9_-]{1,100}\z"))
            throw InitialInstallFiles.Invalid();
        var result = this with
        {
            PackageRoot = StoragePaths.ValidateLocal(PackageRoot), ReleaseManifestPath = StoragePaths.ValidateLocal(ReleaseManifestPath),
            ChannelIndexPath = StoragePaths.ValidateLocal(ChannelIndexPath), TrustRootPath = StoragePaths.ValidateLocal(TrustRootPath),
            InstallationRoot = StoragePaths.ValidateLocal(InstallationRoot), BootstrapDirectory = StoragePaths.ValidateLocal(BootstrapDirectory),
            DataRoot = StoragePaths.ValidateLocal(DataRoot), OptionalComponents = OptionalComponents.Order(StringComparer.Ordinal).ToArray()
        };
        // 工具根布局：引导与数据可以是安装根下固定的 bootstrap 与 data，与版本、暂存、启动器和更新状态目录互不包含。
        var managed = new[] { "versions", "staging", "launcher", "update-state" }.Select(name => Path.Combine(result.InstallationRoot, name));
        if (result.UsesToolRootLayout)
        {
            UpdatePaths.RequireDisjoint(result.InstallationRoot, result.PackageRoot);
            UpdatePaths.RequireDisjoint([.. managed, result.BootstrapDirectory, result.DataRoot]);
        }
        else UpdatePaths.RequireDisjoint(result.InstallationRoot, result.BootstrapDirectory, result.DataRoot, result.PackageRoot);
        foreach (var input in new[] { result.ReleaseManifestPath, result.ChannelIndexPath, result.TrustRootPath })
            foreach (var output in new[] { result.InstallationRoot, result.BootstrapDirectory, result.DataRoot })
                if (StoragePaths.IsWithin(input, output)) throw InitialInstallFiles.Invalid();
        foreach (var root in new[] { result.InstallationRoot, result.BootstrapDirectory, result.DataRoot })
            if (Path.GetPathRoot(root) == root) throw InitialInstallFiles.Invalid();
        return result;
    }

    private static bool InToolRoot(string installationRoot, string path, string name) =>
        string.Equals(path, Path.Combine(installationRoot, name), StringComparison.OrdinalIgnoreCase);

    /// <summary>引导与数据位于安装根下固定子目录（工具根布局）；首次安装的目录检查据此接受这两个顶层目录。</summary>
    internal bool UsesToolRootLayout => InToolRoot(InstallationRoot, BootstrapDirectory, ModuleSdk.ToolRoot.BootstrapName) &&
        InToolRoot(InstallationRoot, DataRoot, ModuleSdk.ToolRoot.DataName);

    internal StorageOptions Storage => new(BootstrapDirectory, DataRoot: DataRoot);
    internal string StateRoot => UpdatePaths.Local(InstallationRoot, "update-state");
    internal string JournalPath => UpdatePaths.Local(StateRoot, InitialInstallRecord.FileName);
}

internal sealed record InitialInstallRecord(int SchemaVersion, InitialInstallRequest Request, string InputSha256,
    byte[] TrustedRoot, byte[] SignedIndex, byte[] SignedManifest, string Phase,
    InstallTargetDescriptor? Target = null, InstallVersion? Version = null, IReadOnlyList<ManifestFile>? StableFiles = null,
    InstallHealthEvidence? Health = null, IReadOnlyList<ManifestFile>? DataFiles = null,
    IReadOnlyList<ManifestFile>? BootstrapFiles = null, byte[]? Configuration = null, string? FailureCode = null)
{
    internal const string FileName = "initial-install.json";
    internal const int MaximumBytes = 64 * 1024 * 1024;
    internal static readonly string[] Phases = ["captured", "prepared", "filesPublished", "dataInitialized", "pointerPublished", "configured", "complete"];
    internal string InstallationId => Identity("installation");
    internal string RootId => Identity("root");
    internal string DatabaseId => Identity("database");
    internal int Step => Array.IndexOf(Phases, Phase);
    internal string VersionRoot => UpdatePaths.Local(Request.InstallationRoot, "versions/" + (Version?.ReleaseId ?? throw InitialInstallFiles.Invalid()));
    internal string StableRoot => UpdatePaths.Local(Request.InstallationRoot, "launcher/stable");
    internal string Identity(string purpose) => InitialInstallFiles.Hash(Encoding.UTF8.GetBytes(InputSha256 + ":" + purpose))[..32];

    internal void Validate()
    {
        if (SchemaVersion != 1 || Request is null || Step < 0 || TrustedRoot is not { Length: > 0 and <= 1048576 } ||
            SignedIndex is not { Length: > 0 and <= 1048576 } || SignedManifest is not { Length: > 0 } ||
            SignedManifest.Length > ReleaseManifestParser.MaximumEnvelopeBytes || InputSha256 != InputHash(Request.Normalize(), TrustedRoot, SignedIndex, SignedManifest))
            throw InitialInstallFiles.Invalid();
        if (Step >= 1)
        {
            if (Target is null || Version is null || StableFiles is not { Count: > 0 } || Configuration is not { Length: > 0 and <= 262144 })
                throw InitialInstallFiles.Invalid();
            Target.Validate("mabitool");
            _ = UpdatePaths.Local(Request.InstallationRoot, "versions/" + Version.ReleaseId);
            _ = FileTreePlanner.ValidateManifest(Version.Files); _ = FileTreePlanner.ValidateManifest(StableFiles);
            if (Version.ManifestSha256 != InitialInstallFiles.Hash(SignedManifest) ||
                !InitialInstallFiles.Same(StableFiles, Version.Files.Where(file => file.RelativePath.StartsWith("components/launcher/", StringComparison.Ordinal)).ToArray()))
                throw InitialInstallFiles.Invalid();
        }
        if (Step >= 3 && (Health is null || DataFiles is null || BootstrapFiles is null ||
            Health.TransactionId != InstallationId || Health.Database.RootId != RootId || Health.Database.DatabaseId != DatabaseId))
            throw InitialInstallFiles.Invalid();
    }

    internal static string InputHash(InitialInstallRequest request, byte[] root, byte[] index, byte[] manifest)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var bytes in new[] { JsonSerializer.SerializeToUtf8Bytes(request, UpdateHandoffProtocol.Json), root, index, manifest })
        {
            hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static async Task<InitialInstallRecord> ReadAsync(string installationRoot, string? hash = null, CancellationToken token = default)
    {
        var root = StoragePaths.ValidateLocal(installationRoot);
        var record = await InitialInstallFiles.ReadJsonAsync<InitialInstallRecord>(UpdatePaths.Local(root, "update-state/" + FileName), MaximumBytes, token).ConfigureAwait(false);
        record.Validate();
        if (!record.Request.InstallationRoot.Equals(root, StringComparison.OrdinalIgnoreCase) || hash is not null && record.InputSha256 != hash)
            throw InitialInstallFiles.Invalid();
        return record;
    }
}

internal sealed record InitialInstallReference(int SchemaVersion, string InstallationRoot, string InputSha256, string RootId, string DatabaseId)
{
    internal const string FileName = "initial-install-reference.json";
    internal static InitialInstallReference For(InitialInstallRecord record) => new(1, record.Request.InstallationRoot,
        record.InputSha256, record.RootId, record.DatabaseId);
}

internal enum InitialInstallPersistencePoint
{
    Captured, Prepared, FileCopied, VersionPublished, StablePublished, FilesPublished,
    DatabaseReady, DataInitialized, InstalledRecordPublished, PointerPublished, ConfigurationPublished, MaintenanceGateRemoved, Completed
}

internal static class InitialInstallFiles
{
    internal static UpdatePlanException Invalid() => new("update.initialInstallStateInvalid");
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static bool Same<T>(T first, T second) => JsonSerializer.Serialize(first, UpdateHandoffProtocol.Json) == JsonSerializer.Serialize(second, UpdateHandoffProtocol.Json);

    internal static async Task<byte[]> ReadAsync(string path, int maximumBytes, CancellationToken token = default)
    {
        UpdatePaths.RejectLinks(path);
        await using var input = await UpdatePaths.OpenPublishedReadAsync(path, token).ConfigureAwait(false);
        if (input.Length is <= 0 || input.Length > maximumBytes) throw Invalid();
        var bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false); return bytes;
    }

    internal static async Task<T> ReadJsonAsync<T>(string path, int maximumBytes, CancellationToken token = default)
    {
        var bytes = await ReadAsync(path, maximumBytes, token).ConfigureAwait(false);
        using var json = StrictJson.Parse(bytes, maximumBytes);
        return json.RootElement.Deserialize<T>(UpdateHandoffProtocol.Json) ?? throw Invalid();
    }

    internal static async Task WriteAsync(string path, byte[] bytes, bool replace, CancellationToken token)
    {
        UpdatePaths.RejectLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            { await stream.WriteAsync(bytes, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); stream.Flush(true); }
            token.ThrowIfCancellationRequested();
            if (replace) await UpdatePaths.ReplaceOwnedFileAsync(temporary, path, token).ConfigureAwait(false);
            else File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task WriteExactAsync(string path, byte[] bytes, CancellationToken token)
    {
        UpdatePaths.RejectLinks(path);
        if (File.Exists(path))
        {
            if (!(await ReadAsync(path, Math.Max(bytes.Length, 1), token).ConfigureAwait(false)).AsSpan().SequenceEqual(bytes))
                throw new UpdatePlanException("update.initialInstallInputMismatch");
        }
        else await WriteAsync(path, bytes, false, token).ConfigureAwait(false);
    }

    internal static void RequireEmpty(string root)
    {
        UpdatePaths.RejectLinks(root);
        if (File.Exists(root) || Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new UpdatePlanException("update.initialInstallNotEmpty");
    }
}

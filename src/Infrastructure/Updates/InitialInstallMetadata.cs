using System.Buffers;
using System.Text.Json;

namespace MabiTool.Infrastructure.Updates;

internal static class InitialInstallMetadata
{
    private const string CapturePrefix = "initial-install-input-";
    internal static string CaptureName(string hash) => CapturePrefix + hash + ".claim";

    /// <summary>安装脚本默认把首次安装请求写在安装根的 update-state 下；完成后保留，与原来写在安装根旁边时相同。</summary>
    internal const string RequestFileName = "initial-install-request.json";

    /// <summary>update-state 中本次执行的请求文件（及脚本写入它时的临时文件）；其他请求文件仍按未知内容拒绝。</summary>
    internal static bool IsCurrentRequest(InitialInstallRequest request, string path, string? requestPath) =>
        requestPath is not null && string.Equals(Path.GetDirectoryName(requestPath), request.StateRoot, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(requestPath) == RequestFileName && Path.GetFileName(path) is var name &&
        (string.Equals(path, requestPath, StringComparison.OrdinalIgnoreCase) || IsTemporary(name, RequestFileName));

    internal static void RequireCaptureLayout(InitialInstallRecord expected, CancellationToken token, string? requestPath = null)
    {
        var request = expected.Request;
        UpdatePaths.RejectLinks(request.InstallationRoot);
        if (!Directory.Exists(request.InstallationRoot))
        {
            if (File.Exists(request.InstallationRoot)) throw new UpdatePlanException("update.initialInstallNotEmpty");
            return;
        }
        var claimName = CaptureName(expected.InputSha256);
        var claimPath = UpdatePaths.Local(request.StateRoot, claimName);
        var claimed = File.Exists(claimPath);
        var top = new HashSet<string>(["update-state", "versions", "staging", "launcher"], StringComparer.OrdinalIgnoreCase);
        if (request.UsesToolRootLayout) top.UnionWith([ModuleSdk.ToolRoot.BootstrapName, ModuleSdk.ToolRoot.DataName]);
        foreach (var entry in Directory.EnumerateFileSystemEntries(request.InstallationRoot))
        {
            token.ThrowIfCancellationRequested(); UpdatePaths.RejectLinks(entry);
            if (!Directory.Exists(entry) || !top.Contains(Path.GetFileName(entry))) throw new UpdatePlanException("update.initialInstallNotEmpty");
            if (Path.GetFileName(entry).Equals("update-state", StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.EnumerateFileSystemEntries(entry).Any()) throw new UpdatePlanException("update.initialInstallNotEmpty");
        }
        if (!Directory.Exists(request.StateRoot)) return;
        if (Directory.EnumerateFileSystemEntries(request.StateRoot).Any(path => Path.GetFileName(path).StartsWith(CapturePrefix, StringComparison.Ordinal) &&
            Path.GetFileName(path) != claimName)) throw new UpdatePlanException("update.initialInstallInputMismatch");
        var identity = JsonSerializer.SerializeToUtf8Bytes(new { expected.SchemaVersion, expected.Request, expected.InputSha256 }, UpdateHandoffProtocol.Json);
        foreach (var path in Directory.EnumerateFileSystemEntries(request.StateRoot))
        {
            token.ThrowIfCancellationRequested(); UpdatePaths.RejectLinks(path);
            var name = Path.GetFileName(path);
            if (!File.Exists(path)) throw new UpdatePlanException("update.initialInstallNotEmpty");
            if (name == "install.lock" || name == claimName) RequireCaptureIdentifier(path);
            else if (IsCurrentRequest(request, path, requestPath)) continue;
            else if (IsTemporary(name, InitialInstallRecord.FileName)) RequireJournal(path, expected, token, claimed ? 0 : identity.Length);
            else throw new UpdatePlanException("update.initialInstallNotEmpty");
        }
    }

    internal static void PublishCapture(InitialInstallRecord record)
    {
        var path = UpdatePaths.Local(record.Request.StateRoot, CaptureName(record.InputSha256));
        UpdatePaths.RejectLinks(path);
        if (File.Exists(path)) { RequireCaptureIdentifier(path); return; }
        using var capture = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        capture.Flush(true);
    }

    internal static void RequireCaptureIdentifier(string path)
    {
        UpdatePaths.RejectLinks(path);
        if (new FileInfo(path).Length != 0) throw InitialInstallFiles.Invalid();
    }

    internal static bool IsTemporary(string name, string document)
    {
        var prefix = document + ".tmp-";
        return name.StartsWith(prefix, StringComparison.Ordinal) && IsGuid(name[prefix.Length..]);
    }

    internal static bool IsAnonymousTemporary(string name) => name.EndsWith(".tmp", StringComparison.Ordinal) && IsGuid(name[..^4]);
    private static bool IsGuid(string value) => Guid.TryParseExact(value, "N", out var id) && value == id.ToString("N");

    internal static bool IsStorageTemporary(string relative, bool bootstrap) => IsAnonymousTemporary(relative) ||
        !bootstrap && IsTemporary(relative, UpdateInstallGate.RelativePath);

    internal static void RequireJournal(string path, InitialInstallRecord record, CancellationToken token, int minimumBytes = 0)
    {
        var prefix = JsonSerializer.SerializeToUtf8Bytes(new { record.SchemaVersion, record.Request, record.InputSha256,
            record.TrustedRoot, record.SignedIndex, record.SignedManifest }, UpdateHandoffProtocol.Json);
        prefix[^1] = (byte)',';
        RequirePrefix(path, [prefix], allowTrailing: true, maximumBytes: InitialInstallRecord.MaximumBytes, minimumBytes: minimumBytes, token: token);
    }

    internal static void RequireTrust(string path, InitialInstallRecord record, CancellationToken token)
    {
        var root = JsonSerializer.Deserialize<TrustRoot>(record.TrustedRoot, UpdateHandoffProtocol.Json) ?? throw InitialInstallFiles.Invalid();
        var prefix = JsonSerializer.SerializeToUtf8Bytes(new { root }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        prefix[^1] = (byte)',';
        RequirePrefix(path, [prefix], allowTrailing: true, maximumBytes: 1048576, token: token);
    }

    // 临时文件仅保留恢复证据；固定文档仍由当前验签输入重新发布。
    internal static void RequirePrefix(string path, IReadOnlyList<byte[]> prefixes, bool allowTrailing = false,
        int maximumBytes = 32 * 1024 * 1024, int minimumBytes = 0, CancellationToken token = default)
    {
        UpdatePaths.RejectLinks(path);
        if (!File.Exists(path)) throw InitialInstallFiles.Invalid();
        using var input = UpdatePaths.OpenPublishedReadAsync(path, token).GetAwaiter().GetResult();
        if (input.Length < minimumBytes || input.Length > maximumBytes) throw InitialInstallFiles.Invalid();
        var matches = prefixes.Where(prefix => allowTrailing || input.Length <= prefix.Length).ToList();
        if (matches.Count == 0) throw InitialInstallFiles.Invalid();
        var buffer = ArrayPool<byte>.Shared.Rent(UpdateIoBudget.ChunkSize);
        try
        {
            long offset = 0;
            while (offset < input.Length && matches.Any(prefix => prefix.Length > offset))
            {
                token.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, Math.Min(input.Length - offset, matches.Max(prefix => prefix.Length) - offset));
                input.ReadExactly(buffer.AsSpan(0, count));
                matches.RemoveAll(prefix =>
                {
                    var start = (int)Math.Min(offset, prefix.Length);
                    var length = Math.Min(count, prefix.Length - start);
                    return !prefix.AsSpan(start, length).SequenceEqual(buffer.AsSpan(0, length));
                });
                if (matches.Count == 0) throw InitialInstallFiles.Invalid();
                offset += count;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}

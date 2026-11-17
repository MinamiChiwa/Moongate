using System.IO.Compression;
using MabiTool.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;
using MabiTool.PortableUpdate;

namespace MabiTool.Infrastructure.Updates;

/// <summary>更新检查、下载、解压与安装各步骤的稳定失败代码。</summary>
public sealed class UpdatePackageException(string code, string? detail = null) : Exception(detail is null ? code : code + ": " + detail)
{
    public string Code { get; } = code;
}

/// <summary>一次检查得到的可安装发行：GitHub Release 的整包 zip、其 SHA-256 与下载地址。计划 ID 同时用作安装事务编号。</summary>
public sealed record UpdatePlan(int SchemaVersion, string PlanId, string Version, string DisplayVersion, string Channel, string Tag,
    string AssetName, long AssetLength, string Sha256, IReadOnlyList<UpdatePlanSource> Sources, string Source,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <param name="Id">来源种类：<c>GithubDirect</c>（github.com 发行下载地址）或 <c>GithubApi</c>（REST API 资产地址）。</param>
public sealed record UpdatePlanSource(string Id, Uri Uri);

/// <summary>计划保存在工具根的 <c>update/plans/&lt;计划&gt;.json</c>；读取拒绝未知字段、链接与超限内容。</summary>
public static class UpdatePlanStore
{
    private const int MaximumBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static async Task SaveAsync(string toolRoot, UpdatePlan plan, CancellationToken token = default)
    {
        Validate(plan);
        var directory = UpdateLayout.Plans(toolRoot);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, plan.PlanId + ".json");
        UpdatePaths.RejectLinks(path);
        var temporary = path + ".tmp";
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(output, plan, Json, token).ConfigureAwait(false);
            output.Flush(true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    public static async Task<UpdatePlan> ReadAsync(string toolRoot, string planId, CancellationToken token = default)
    {
        if (!UpdateLayout.IsTransactionId(planId)) throw new UpdatePackageException("update.planInvalid");
        var path = Path.Combine(UpdateLayout.Plans(toolRoot), planId + ".json");
        UpdatePaths.RejectLinks(path);
        if (!File.Exists(path)) throw new OperationException(ApiErrors.ResourceNotFound, "更新计划不存在");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is 0 or > MaximumBytes) throw new UpdatePackageException("update.planInvalid");
        try
        {
            var plan = await JsonSerializer.DeserializeAsync<UpdatePlan>(input, Json, token).ConfigureAwait(false)
                ?? throw new UpdatePackageException("update.planInvalid");
            Validate(plan);
            if (plan.PlanId != planId) throw new UpdatePackageException("update.planInvalid");
            return plan;
        }
        catch (JsonException) { throw new UpdatePackageException("update.planInvalid"); }
    }

    private static void Validate(UpdatePlan plan)
    {
        if (plan.SchemaVersion != 1 || !UpdateLayout.IsTransactionId(plan.PlanId) || !ReleaseIdentity.IsHash(plan.Sha256) ||
            plan.AssetLength is < 1 or > PortableUpdatePackage.MaximumArchiveBytes || plan.Sources is not { Count: > 0 } ||
            plan.Sources.Any(source => source.Uri.Scheme != Uri.UriSchemeHttps) || plan.ExpiresAt <= plan.CreatedAt ||
            plan.Channel is not ("stable" or "preview") || plan.DisplayVersion.Length is 0 or > 64 ||
            plan.AssetName != "Moongate-v" + plan.DisplayVersion + "-Windows-x64.zip")
            throw new UpdatePackageException("update.planInvalid");
    }
}

/// <summary>
/// 下载的整包 zip：解压前检查目录结构（顶层只有一个 <c>Moongate/</c>，其下只有 <c>Moongate.exe</c>、<c>components/</c>、<c>licenses/</c>），
/// 拒绝路径穿越、重名、设备名与超限条目；解压到工具根内的私有工作目录，核对包内发行身份的版本与各组件树哈希后，
/// 改名发布为 <c>update/staging/&lt;事务&gt;/Moongate</c>。
/// </summary>
public static class PortableUpdatePackage
{
    public const long MaximumArchiveBytes = 4L * 1024 * 1024 * 1024;
    private const long MaximumExtractedBytes = 8L * 1024 * 1024 * 1024;
    private const long MaximumEntryBytes = 4L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 100_000;

    public static async Task<string> ExtractAsync(string archive, string toolRoot, string transactionId, string expectedDisplayVersion,
        CancellationToken token = default)
    {
        if (!UpdateLayout.IsTransactionId(transactionId)) throw new UpdatePackageException("update.planInvalid");
        var staging = UpdateLayout.Staging(toolRoot, transactionId);
        var work = staging + ".work";
        UpdatePaths.RejectLinks(staging);
        UpdatePaths.DeleteOwnedTree(work);
        UpdatePaths.DeleteOwnedTree(staging);
        Directory.CreateDirectory(work);
        try
        {
            await using (var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous))
            {
                ZipPackageBounds.Validate(input, MaximumEntries);
                input.Position = 0;
                using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long total = 0;
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var relative = EntryPath(entry.FullName, out var directory);
                    if (relative is null) continue;
                    if (!names.Add(relative)) throw new UpdatePackageException("update.packageInvalid", entry.FullName);
                    seen.Add(relative.Split('/')[0]);
                    if (entry.Length > MaximumEntryBytes || (total += entry.Length) > MaximumExtractedBytes)
                        throw new UpdatePackageException("update.packageInvalid", "解压后超过大小上限");
                    var target = Path.Combine(work, "Moongate", relative.Replace('/', Path.DirectorySeparatorChar));
                    if (directory) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var source = entry.Open();
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
                    await CopyBoundedAsync(source, output, entry.Length, token).ConfigureAwait(false);
                }
                foreach (var required in UpdateLayout.Items)
                    if (!seen.Contains(required)) throw new UpdatePackageException("update.packageInvalid", "缺少 " + required);
            }
            var package = Path.Combine(work, "Moongate");
            ReleaseIdentity? identity;
            try { identity = ReleaseIdentity.Read(package); }
            catch (InvalidDataException error) { throw new UpdatePackageException("update.packageInvalid", error.Message); }
            if (identity is null) throw new UpdatePackageException("update.packageInvalid", "缺少发行身份");
            if (identity.DisplayVersion != expectedDisplayVersion) throw new UpdatePackageException("update.releaseIdentityMismatch");
            string? mismatch;
            try { mismatch = identity.Verify(package); }
            catch (InvalidDataException error) { throw new UpdatePackageException("update.packageInvalid", error.Message); }
            if (mismatch is not null) throw new UpdatePackageException("update.packageInvalid", "与发行身份不符：" + mismatch);
            Directory.Move(work, staging);
            return UpdateLayout.StagedPackage(toolRoot, transactionId);
        }
        catch (InvalidDataException error) { throw new UpdatePackageException("update.packageInvalid", error.Message); }
        catch (IOException error) when (UpdatePaths.IsDiskFull(error)) { throw new UpdatePackageException("update.diskSpaceInsufficient"); }
        finally { if (Directory.Exists(work)) UpdatePaths.DeleteOwnedTree(work); }
    }

    /// <summary>条目相对 <c>Moongate/</c> 的路径；顶层目录本身返回空。不合规的条目抛出 <c>update.packageInvalid</c>。</summary>
    internal static string? EntryPath(string name, out bool directory)
    {
        directory = name.EndsWith('/');
        if (name.Contains('\\') || !name.StartsWith("Moongate/", StringComparison.Ordinal))
            throw new UpdatePackageException("update.packageInvalid", name);
        var relative = name["Moongate/".Length..].TrimEnd('/');
        if (relative.Length == 0) return null;
        try { relative = UpdatePaths.Normalize(relative); }
        catch (UpdatePackageException) { throw new UpdatePackageException("update.packageInvalid", name); }
        var top = relative.Split('/')[0];
        var valid = top.Equals(ReleaseIdentity.EntryName, StringComparison.OrdinalIgnoreCase)
            ? relative == ReleaseIdentity.EntryName && !directory
            : top is "components" or "licenses";
        if (!valid) throw new UpdatePackageException("update.packageInvalid", name);
        return relative;
    }

    private static async Task CopyBoundedAsync(Stream source, Stream output, long length, CancellationToken token)
    {
        var buffer = new byte[1 << 16];
        long copied = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if ((copied += read) > length) throw new UpdatePackageException("update.packageInvalid", "条目长度与目录不符");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        if (copied != length) throw new UpdatePackageException("update.packageInvalid", "条目长度与目录不符");
    }
}

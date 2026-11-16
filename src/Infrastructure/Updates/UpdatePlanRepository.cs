using System.Text.Json;
using System.Text.Json.Serialization;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure.Updates;

public sealed record StoredUpdatePlan(int SchemaVersion, UpdatePlan Plan, byte[] SignedIndex, byte[] SignedManifest);

/// <summary>保存计划及原始签名材料；安装授权由当前信任状态重新裁决。</summary>
public sealed class UpdatePlanRepository
{
    private const int MaximumDocumentBytes = 20 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string root;

    public UpdatePlanRepository(string installationStateRoot)
    {
        if (!Path.IsPathFullyQualified(installationStateRoot)) throw new FileTreeException("update.pathRejected");
        root = Path.Combine(Path.GetFullPath(installationStateRoot), "plans");
        UpdatePaths.RejectLinks(root);
    }

    public async Task SaveAsync(StoredUpdatePlan document, CancellationToken token = default)
    {
        var plan = document.Plan ?? throw new UpdatePlanException("update.planInvalid");
        Validate(document, plan.PlanId);
        var path = PathFor(plan.PlanId);
        Directory.CreateDirectory(root);
        UpdatePaths.RejectLinks(root);
        var lockPath = Path.Combine(root, ".lock");
        UpdatePaths.RejectLinks(lockPath);
        await using var ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // 保留有界元数据；回收须由统一清理计划核对活动与恢复引用。
        if (Directory.EnumerateFiles(root, "*.json").Take(128).Count() >= 128)
            throw new UpdatePlanException("resource.capacityExceeded");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > MaximumDocumentBytes) throw new UpdatePlanException("update.planTooLarge");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            UpdatePaths.RejectLinks(path);
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<StoredUpdatePlan> ReadAsync(string planId, CancellationToken token = default)
    {
        try { return await ReadDocumentAsync(planId, token).ConfigureAwait(false); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { throw new UpdatePlanException(ApiErrors.ResourceNotFound); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new UpdatePlanException("update.storageUnavailable"); }
    }

    private async Task<StoredUpdatePlan> ReadDocumentAsync(string planId, CancellationToken token)
    {
        var path = PathFor(planId);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        if (stream.Length > MaximumDocumentBytes) throw new UpdatePlanException("update.planTooLarge");
        try
        {
            var document = await JsonSerializer.DeserializeAsync<StoredUpdatePlan>(stream, JsonOptions, token).ConfigureAwait(false)
                ?? throw new UpdatePlanException("update.planInvalid");
            Validate(document, planId);
            return document;
        }
        catch (JsonException) { throw new UpdatePlanException("update.planInvalid"); }
    }

    private string PathFor(string id)
    {
        if (id is not { Length: 32 } || id.AsSpan().IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new UpdatePlanException("update.planInvalid");
        var path = Path.Combine(root, id + ".json");
        UpdatePaths.RejectLinks(path);
        return path;
    }

    private static void Validate(StoredUpdatePlan document, string id)
    {
        if (document.SchemaVersion != 1 || document.Plan is null || document.Plan.PlanId != id ||
            document.Plan.Components is null || document.Plan.Reused is null || document.Plan.Removed is null ||
            document.Plan.Components.Count is < 1 or > 256 || document.Plan.Reused.Count > 256 || document.Plan.Removed.Count > 256 ||
            document.Plan.ReleaseId is not { Length: > 0 and <= 128 } || document.Plan.ReleaseSequence < 0 ||
            !HashValid(document.Plan.ManifestSha256) || !HashValid(document.Plan.BaseFingerprint) ||
            document.Plan.DownloadBytes < 0 || document.Plan.PeakBytes < document.Plan.DownloadBytes ||
            document.Plan.ExpiresAt <= document.Plan.CreatedAt ||
            document.Plan.RequiredUpdaterVersion is { } minimum && !document.Plan.Components.Any(item => item.ComponentId == "updater" && item.Version >= minimum) ||
            document.Plan.Components.Select(item => item?.ComponentId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Plan.Components.Count ||
            document.Plan.Components.Any(item => item is null || string.IsNullOrEmpty(item.ComponentId) ||
                item.Version is null || item.DownloadBytes < 0 || item.InstalledBytes < 0 || !HashValid(item.Sha256) ||
                item.Dependencies is null || item.Dependencies.Count > 256 || item.Dependencies.Any(dependency => dependency is null)) ||
            document.Plan.Reused.Concat(document.Plan.Removed).Any(item => item is null || item.InstalledBytes < 0 ||
                item.Version is null || string.IsNullOrEmpty(item.ComponentId) || !HashValid(item.Sha256)) ||
            document.SignedIndex is not { Length: > 0 and <= 1_048_576 } ||
            document.SignedManifest is not { Length: > 0 and <= 12 * 1024 * 1024 } ||
            JsonSerializer.SerializeToUtf8Bytes(document.Plan, JsonOptions).Length > 131_072)
            throw new UpdatePlanException("update.planInvalid");
    }

    private static bool HashValid(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
}

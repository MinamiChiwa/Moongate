using System.Security.Cryptography;
using MabiTool.Contracts;

namespace MabiTool.Providers.TwObservation.ItemCatalog;

/// <summary>
/// 按资源路径读出的一张精灵：<see cref="Bitmap"/> 不为空时可用（自上而下的 RGBA），否则 <see cref="Reason"/> 为原因码
/// （与客户端属性图标同一组 clientIcon.*）。
/// </summary>
public sealed record ClientSpriteResult(string Path, ClientIconBitmap? Bitmap, string? Reason);

/// <summary>
/// 维护工具用的精灵读取：列出 AssetPackages 里的资源路径（去掉 <see cref="ClientAttributeIcons.ResourcePrefix"/>），
/// 按路径把单精灵 UnityFS 包解码为位图。读取规则与上限同 <see cref="ClientAttributeIcons"/>；读取期间客户端文件变化时整体失败。
/// </summary>
public static class ClientSpriteResources
{
    /// <summary>以 <paramref name="prefix"/> 开头的资源路径（相对 Assets/PatchableAssets/），按序号排序、去重。</summary>
    public static IReadOnlyList<string> List(string appdataDirectory, string prefix, CancellationToken cancellationToken = default)
    {
        var assets = ClientResourcePackage.Open(Path.Combine(Path.GetFullPath(appdataDirectory), "AssetPackages"), cancellationToken);
        var result = assets.Entries.Select(entry => entry.Path)
            .Where(path => path.StartsWith(ClientAttributeIcons.ResourcePrefix + prefix, StringComparison.Ordinal))
            .Select(path => path[ClientAttributeIcons.ResourcePrefix.Length..])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        assets.EnsureUnchanged();
        return result;
    }

    /// <summary>按资源路径（相对 Assets/PatchableAssets/，以 .png 结尾）解码精灵；找不到或格式不符的路径只在该项给出原因。</summary>
    public static IReadOnlyList<ClientSpriteResult> Extract(string appdataDirectory, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var assets = ClientResourcePackage.Open(Path.Combine(Path.GetFullPath(appdataDirectory), "AssetPackages"), cancellationToken);
        var wanted = paths.Select(path => ClientAttributeIcons.ResourcePrefix + path).ToHashSet(StringComparer.Ordinal);
        var located = assets.Entries.Where(entry => wanted.Contains(entry.Path)).GroupBy(entry => entry.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var stamps = located.Values.SelectMany(list => list).Select(entry => entry.BlobPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, StableFileStamp.Capture, StringComparer.OrdinalIgnoreCase);
        var results = new List<ClientSpriteResult>(paths.Count);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (located.GetValueOrDefault(ClientAttributeIcons.ResourcePrefix + path) is not { } entries) { results.Add(new(path, null, ClientIconReasons.ResourceMissing)); continue; }
            if (entries.Count != 1) { results.Add(new(path, null, ClientIconReasons.ResourceInvalid)); continue; }
            try
            {
                var raw = assets.ReadRaw(entries[0], stamps[entries[0].BlobPath], UnityBundle.MaximumBundleBytes, cancellationToken);
                results.Add(new(path, ClientIconImage.DecodeSpriteBundle(raw, System.IO.Path.GetFileNameWithoutExtension(path), cancellationToken), null));
            }
            catch (ClientIconFormatException error) { results.Add(new(path, null, error.Code)); }
            catch (Exception error) when (error is InvalidDataException or OverflowException) { results.Add(new(path, null, ClientIconReasons.ResourceInvalid)); }
        }
        assets.EnsureUnchanged();
        foreach (var pair in stamps) pair.Value.EnsureUnchanged(pair.Key);
        return results;
    }

    /// <summary>位图编码为 8 位 RGBA PNG，并给出 SHA-256（小写十六进制）。</summary>
    public static (byte[] Png, string Sha256) Encode(ClientIconBitmap bitmap)
    {
        var png = PngEncoder.Encode(bitmap);
        return (png, Convert.ToHexStringLower(SHA256.HashData(png)));
    }
}

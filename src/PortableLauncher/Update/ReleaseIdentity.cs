using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace MabiTool.PortableUpdate;

/// <summary>
/// 发行身份 <c>components/release.json</c>：打包时由 package-inventory.py 写入，记录版本、渠道、构建时间、
/// 包根入口与各组件的树哈希。宿主据此得知自己的版本，更新时用它核对下载包与暂存目录。
/// 本文件同时编入包根 Moongate.exe（.NET Framework 4.8）与宿主基础设施，只用两边都有的接口。
/// </summary>
[DataContract]
internal sealed class ReleaseIdentity
{
    internal const string RelativePath = "components/release.json";
    internal const string EntryName = "Moongate.exe";
    private const int MaximumBytes = 64 * 1024;
    private const int MaximumFiles = 100_000;

    [DataMember(Name = "schemaVersion")] public int SchemaVersion { get; set; }
    [DataMember(Name = "product")] public string Product { get; set; } = "";
    [DataMember(Name = "version")] public string Version { get; set; } = "";
    [DataMember(Name = "channel")] public string Channel { get; set; } = "";
    [DataMember(Name = "displayVersion")] public string DisplayVersion { get; set; } = "";
    [DataMember(Name = "repository")] public string Repository { get; set; } = "";
    [DataMember(Name = "builtAt")] public string BuiltAt { get; set; } = "";
    [DataMember(Name = "entry")] public ReleaseFile? Entry { get; set; }
    [DataMember(Name = "components")] public ReleaseComponent[]? Components { get; set; }

    /// <summary>读取包根下的发行身份；文件缺失时返回空，内容无效时抛出 <see cref="InvalidDataException"/>。</summary>
    internal static ReleaseIdentity? Read(string packageRoot)
    {
        var path = Path.Combine(packageRoot, "components", "release.json");
        if (!File.Exists(path)) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is 0 or > MaximumBytes) throw new InvalidDataException("发行身份文件大小无效");
        ReleaseIdentity? identity;
        try { identity = new DataContractJsonSerializer(typeof(ReleaseIdentity)).ReadObject(input) as ReleaseIdentity; }
        catch (SerializationException error) { throw new InvalidDataException("发行身份文件格式无效", error); }
        if (identity is null || identity.SchemaVersion != 1 || identity.Product != "Moongate" || identity.Version.Length is 0 or > 64 ||
            identity.Channel is not ("stable" or "preview") ||
            identity.DisplayVersion != (identity.Channel == "preview" ? identity.Version + "-preview" : identity.Version) ||
            identity.Entry is null || !IsHash(identity.Entry.Sha256) || identity.Components is not { Length: > 0 } ||
            identity.Components.Any(item => item is null || !IsComponentId(item.Id) || !IsHash(item.TreeSha256) || item.Files < 1) ||
            identity.Components.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != identity.Components.Length)
            throw new InvalidDataException("发行身份内容无效");
        return identity;
    }

    /// <summary>
    /// 核对包根内容与本身份一致：入口哈希、<c>components/</c> 下的组件集合与各组件树哈希。不一致时返回原因，一致时返回空。
    /// </summary>
    internal string? Verify(string packageRoot)
    {
        var entry = Path.Combine(packageRoot, EntryName);
        if (!File.Exists(entry) || !string.Equals(FileSha256(entry), Entry!.Sha256, StringComparison.OrdinalIgnoreCase))
            return EntryName;
        var components = Path.Combine(packageRoot, "components");
        RejectReparsePoint(components);
        var actual = Directory.GetDirectories(components).Select(Path.GetFileName).ToArray();
        var expected = Components!.Select(item => item.Id).ToArray();
        if (actual.Length != expected.Length || actual.Any(name => !expected.Contains(name, StringComparer.OrdinalIgnoreCase)))
            return "components";
        foreach (var component in Components!)
        {
            var tree = TreeSha256(Path.Combine(components, component.Id), out var files);
            if (files != component.Files || !string.Equals(tree, component.TreeSha256, StringComparison.OrdinalIgnoreCase))
                return "components/" + component.Id;
        }
        return null;
    }

    /// <summary>
    /// 组件树哈希：目录内全部文件按相对路径（<c>/</c> 分隔）的序号顺序排列，对每个文件的
    /// <c>相对路径 + 制表符 + 小写 SHA-256 + 换行</c> 依次求 SHA-256。目录内出现链接时拒绝。
    /// </summary>
    internal static string TreeSha256(string directory, out int files)
    {
        var entries = new List<KeyValuePair<string, string>>();
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            RejectReparsePoint(current);
            foreach (var child in Directory.GetDirectories(current)) pending.Push(child);
            foreach (var file in Directory.GetFiles(current))
            {
                RejectReparsePoint(file);
                if (entries.Count >= MaximumFiles) throw new InvalidDataException("组件文件数超出上限");
                entries.Add(new KeyValuePair<string, string>(file.Substring(root.Length + 1).Replace('\\', '/'), file));
            }
        }
        entries.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
        var text = new StringBuilder();
        foreach (var entry in entries) text.Append(entry.Key).Append('\t').Append(FileSha256(entry.Value)).Append('\n');
        files = entries.Count;
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
    }

    internal static string FileSha256(string path)
    {
        using var sha = SHA256.Create();
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Hex(sha.ComputeHash(input));
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("程序目录包含链接：" + path);
    }

    private static string Hex(byte[] bytes)
    {
        var text = new StringBuilder(bytes.Length * 2);
        foreach (var value in bytes) text.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return text.ToString();
    }

    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsComponentId(string? value) => value is { Length: > 0 and <= 64 } &&
        value.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}

[DataContract]
internal sealed class ReleaseFile
{
    [DataMember(Name = "path")] public string Path { get; set; } = "";
    [DataMember(Name = "sha256")] public string Sha256 { get; set; } = "";
}

[DataContract]
internal sealed class ReleaseComponent
{
    [DataMember(Name = "id")] public string Id { get; set; } = "";
    [DataMember(Name = "files")] public int Files { get; set; }
    [DataMember(Name = "treeSha256")] public string TreeSha256 { get; set; } = "";
}

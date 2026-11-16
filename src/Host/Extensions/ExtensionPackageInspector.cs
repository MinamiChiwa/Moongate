using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Extensions;

public sealed record ExtensionHostApiRange(Version Minimum, Version MaximumExclusive);
public sealed record ExtensionDependency(string Id, Version MinimumVersion);
/// <summary>清单里留给以后统一验证器的签名；本期只核对格式，不验签。</summary>
public sealed record ExtensionSignature(string Algorithm, string KeyId, string Value);
/// <summary><see cref="AssetPack"/> 是包内素材包清单（桌面 pack.json 格式）的相对路径。</summary>
public sealed record ExtensionManifest(
    string Id, Version Version, ExtensionHostApiRange HostApi,
    IReadOnlyDictionary<string, string> DisplayNames, IReadOnlyDictionary<string, string> Descriptions,
    IReadOnlyList<string> Provides, IReadOnlyList<string> Permissions,
    IReadOnlyList<ExtensionDependency> Dependencies, IReadOnlyList<ModuleRiskBehavior> HighRiskBehaviors,
    string? AssetPack = null, ExtensionSignature? Signature = null)
{
    public string Publisher => Id[..Id.IndexOf('.')];
}
public sealed record ExtensionPackageFile(string Path, long Size, string Sha256);
public sealed record ExtensionPackageInspection(
    ExtensionManifest Manifest, IReadOnlyList<ExtensionPackageFile> Files, string TreeSha256, bool ContainsExecutableCode);

public sealed class ExtensionPackageException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// 只读检查本地扩展模块包：在流上核对 ZIP 结构、路径与清单，不解出文件、不执行包内内容。
/// 检查通过不代表可以安装或运行；是否允许安装由 <see cref="ExtensionInstaller"/> 按纯数据规则判定。
/// </summary>
public static class ExtensionPackageInspector
{
    public const long MaximumPackageBytes = 32L * 1024 * 1024;
    public const int MaximumExpandedBytes = 64 * 1024 * 1024, MaximumEntries = 1000, MaximumManifestBytes = 64 * 1024;
    public const string ManifestName = "extension.json";

    /// <summary>已登记的扩展权限；与模块清单使用同一组名称。</summary>
    public static IReadOnlySet<string> KnownPermissions { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "capture.selected-window", "input.selected-window", "process.read.selected-game" };

    private static readonly Regex Segment = new("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex ModuleId = new("^[a-z][a-z0-9-]{0,63}(\\.[a-z][a-z0-9-]{0,31})?$", RegexOptions.CultureInvariant);
    private static readonly Regex CapabilitySuffix = new("^[a-z][a-z0-9-]{0,31}(\\.[a-z][a-z0-9-]{0,31}){0,3}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ScriptSuffixes = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".sys", ".com", ".scr", ".msi", ".ps1", ".psm1", ".bat", ".cmd", ".vbs", ".js", ".py" };
    private static readonly HashSet<string> Languages = new(StringComparer.Ordinal) { "zh-Hans", "zh-Hant" };

    public static async Task<ExtensionPackageInspection> InspectAsync(Stream package, Version hostApi, CancellationToken ct)
    {
        if (!package.CanSeek) throw Invalid("扩展包必须是可定位的本地文件");
        if (package.Length > MaximumPackageBytes) throw new ExtensionPackageException("extension.limitExceeded", "扩展包超过 32 MiB");
        IReadOnlyList<ImportSourceFile> entries;
        try { entries = await FilePackageReader.ReadAsync(package, MaximumExpandedBytes, ct); }
        catch (OperationException error)
        {
            throw error.Code switch
            {
                "storage.pathRejected" => new ExtensionPackageException("extension.pathRejected", error.Message),
                "storage.archiveLimitExceeded" or "request.bodyTooLarge" => new ExtensionPackageException("extension.limitExceeded", error.Message),
                _ => Invalid(error.Message)
            };
        }
        var files = new List<(ExtensionPackageFile File, byte[] Content)>();
        foreach (var entry in entries)
        {
            var path = FileNames.ArchivePath(entry.Name);
            var content = Convert.FromBase64String(entry.ContentBase64);
            files.Add((new(path, content.Length, Convert.ToHexStringLower(SHA256.HashData(content))), content));
        }
        if (files.Count > MaximumEntries) throw new ExtensionPackageException("extension.limitExceeded", "扩展包条目超过 1,000 个");
        var manifestEntry = files.Where(x => x.File.Path == ManifestName).Select(x => x.Content).FirstOrDefault()
            ?? throw new ExtensionPackageException("extension.manifestMissing", "扩展包根目录缺少 extension.json");
        if (manifestEntry.Length > MaximumManifestBytes) throw new ExtensionPackageException("extension.limitExceeded", "扩展清单超过 64 KiB");
        var manifest = ParseManifest(manifestEntry);
        if (manifest.AssetPack is { } pack && !files.Any(x => x.File.Path == pack))
            throw Manifest("assetPack 指向的文件不在包内：" + pack);
        if (hostApi < manifest.HostApi.Minimum || hostApi >= manifest.HostApi.MaximumExclusive)
            throw new ExtensionPackageException("extension.hostApiIncompatible", "当前宿主 API 不在扩展声明的兼容范围内");
        var ordered = files.Select(x => x.File).OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
        var tree = new StringBuilder();
        foreach (var file in ordered) tree.Append(file.Path).Append('\0').Append(file.Sha256).Append('\n');
        var executable = files.Any(x => ScriptSuffixes.Contains(System.IO.Path.GetExtension(x.File.Path)) ||
            x.Content is [(byte)'M', (byte)'Z', ..]);
        return new(manifest, ordered, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tree.ToString()))), executable);
    }

    /// <summary>解析已安装扩展目录里的清单；规则与检查包时相同。</summary>
    internal static ExtensionManifest ParseManifest(byte[] bytes)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException) { throw Manifest("extension.json 不是有效 JSON"); }
        using (document)
        {
            var root = Object(document.RootElement, "清单", "schemaVersion", "id", "version", "hostApi", "displayNames", "descriptions",
                "provides", "permissions", "dependencies", "highRiskBehaviors", "assetPack", "signature");
            if (!root.TryGetValue("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1) throw Manifest("schemaVersion 必须为 1");
            var id = Text(root, "id", 65);
            var parts = id.Split('.');
            if (parts.Length != 2 || !parts.All(Segment.IsMatch)) throw Manifest("标识必须写成“发布者.名称”");
            var version = ThreePart(Text(root, "version", 32), "version");
            if (!root.TryGetValue("hostApi", out var hostElement)) throw Manifest("缺少 hostApi");
            var host = Object(hostElement, "hostApi", "minimum", "maximumExclusive");
            var range = new ExtensionHostApiRange(Api(Text(host, "minimum", 16)), Api(Text(host, "maximumExclusive", 16)));
            if (range.Minimum >= range.MaximumExclusive) throw Manifest("hostApi 范围为空");
            var names = Localized(root, "displayNames", 64, required: true);
            var descriptions = Localized(root, "descriptions", 200, required: false);
            var provides = Strings(root, "provides", 32, 128);
            foreach (var capability in provides)
                if (!capability.StartsWith(id + ".", StringComparison.Ordinal) || !CapabilitySuffix.IsMatch(capability[(id.Length + 1)..]))
                    throw Manifest("能力编号必须以本扩展标识加“.”开头：" + capability);
            var permissions = Strings(root, "permissions", 16, 64);
            foreach (var permission in permissions)
                if (!KnownPermissions.Contains(permission))
                    throw new ExtensionPackageException("extension.permissionUnknown", "未登记的权限：" + permission);
            var dependencies = new List<ExtensionDependency>();
            if (root.TryGetValue("dependencies", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 16) throw Manifest("dependencies 必须是最多 16 项的数组");
                foreach (var item in list.EnumerateArray())
                {
                    var dependency = Object(item, "依赖", "id", "minimumVersion");
                    var dependencyId = Text(dependency, "id", 97);
                    if (!ModuleId.IsMatch(dependencyId) || dependencyId == id) throw Manifest("依赖标识无效：" + dependencyId);
                    if (dependencies.Any(x => x.Id == dependencyId)) throw Manifest("依赖重复：" + dependencyId);
                    dependencies.Add(new(dependencyId, ThreePart(Text(dependency, "minimumVersion", 32), "minimumVersion")));
                }
            }
            var behaviors = new List<ModuleRiskBehavior>();
            foreach (var value in Strings(root, "highRiskBehaviors", 4, 32))
            {
                ModuleRiskBehavior? behavior = value switch
                {
                    "memoryRead" => ModuleRiskBehavior.MemoryRead, "memoryInjection" => ModuleRiskBehavior.MemoryInjection,
                    "gameFileModification" => ModuleRiskBehavior.GameFileModification, "gamePacketSend" => ModuleRiskBehavior.GamePacketSend,
                    _ => null
                };
                behaviors.Add(behavior ?? throw Manifest("未知的高风险行为：" + value));
            }
            if (permissions.Contains("process.read.selected-game") && !behaviors.Contains(ModuleRiskBehavior.MemoryRead))
                throw Manifest("声明进程读取时必须同时声明 memoryRead");
            string? assetPack = null;
            if (root.ContainsKey("assetPack"))
            {
                assetPack = Text(root, "assetPack", 260);
                try { assetPack = FileNames.ArchivePath(assetPack) == assetPack ? assetPack : null; }
                catch (OperationException) { assetPack = null; }
                if (assetPack is null || !assetPack.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    throw Manifest("assetPack 必须是包内 .json 文件的相对路径");
            }
            ExtensionSignature? signature = null;
            if (root.TryGetValue("signature", out var signatureElement))
            {
                var fields = Object(signatureElement, "signature", "algorithm", "keyId", "value");
                signature = new(Text(fields, "algorithm", 32), Text(fields, "keyId", 128), Text(fields, "value", 4096));
            }
            return new(id, version, range, names, descriptions, provides, permissions, dependencies, behaviors, assetPack, signature);
        }
    }

    private static Dictionary<string, JsonElement> Object(JsonElement element, string name, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Manifest(name + " 必须是对象");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) throw Manifest(name + " 含未知字段：" + property.Name);
            if (!result.TryAdd(property.Name, property.Value)) throw Manifest(name + " 含重复字段：" + property.Name);
        }
        return result;
    }

    private static string Text(Dictionary<string, JsonElement> owner, string name, int maximum) =>
        owner.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text &&
        text.Length <= maximum && !text.Any(char.IsControl) ? text : throw Manifest(name + " 缺失或无效");

    private static string[] Strings(Dictionary<string, JsonElement> owner, string name, int maximumCount, int maximumLength)
    {
        if (!owner.TryGetValue(name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximumCount) throw Manifest(name + " 必须是最多 " + maximumCount + " 项的数组");
        var result = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text &&
            text.Length <= maximumLength ? text : throw Manifest(name + " 含无效项")).ToArray();
        return result.Distinct(StringComparer.Ordinal).Count() == result.Length ? result : throw Manifest(name + " 含重复项");
    }

    private static IReadOnlyDictionary<string, string> Localized(Dictionary<string, JsonElement> owner, string name, int maximum, bool required)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (owner.TryGetValue(name, out var value))
            foreach (var (language, element) in Object(value, name, [.. Languages]))
                result[language] = element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text &&
                    text.Length <= maximum && !text.Any(char.IsControl) ? text : throw Manifest(name + "." + language + " 无效");
        return required && result.Count == 0 ? throw Manifest(name + " 至少需要一种语言") : result;
    }

    private static Version ThreePart(string text, string name) =>
        Regex.IsMatch(text, "^(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})$", RegexOptions.CultureInvariant)
            ? Version.Parse(text) : throw Manifest(name + " 必须是三段数字版本");

    private static Version Api(string text) =>
        Regex.IsMatch(text, "^(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})$", RegexOptions.CultureInvariant)
            ? Version.Parse(text) : throw Manifest("hostApi 必须是两段数字版本");

    private static ExtensionPackageException Manifest(string message) => new("extension.manifestInvalid", message);
    private static ExtensionPackageException Invalid(string message) => new("extension.packageInvalid", message);
}

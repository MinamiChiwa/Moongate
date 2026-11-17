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
/// <summary>
/// 运行入口。<see cref="Kind"/>：<c>executable</c> 运行包内 .exe；<c>script</c> 用包内解释器 <see cref="Interpreter"/> 运行包内脚本；
/// <c>dotnet</c> 用工具所用的 .NET 运行时运行包内 .dll。路径都是包内相对路径。
/// </summary>
public sealed record ExtensionRuntimeSpec(int Protocol, string Kind, string Entry, string? Interpreter, IReadOnlyList<string> Arguments);
public sealed record ExtensionSettingOptionSpec(string Value, IReadOnlyDictionary<string, string> Labels);
/// <summary>设置项；<see cref="Default"/> 的 JSON 类型与 <see cref="Type"/> 一致（toggle 为布尔，number 为数字，text 与 choice 为文字）。</summary>
public sealed record ExtensionSettingSpec(string Key, string Type, IReadOnlyDictionary<string, string> Labels, JsonElement Default,
    double? Minimum, double? Maximum, IReadOnlyList<ExtensionSettingOptionSpec> Options);
public sealed record ExtensionCommandSpec(string Id, IReadOnlyDictionary<string, string> Labels);

/// <summary><see cref="AssetPack"/> 是包内素材包清单（桌面 pack.json 格式）的相对路径；带代码的扩展另有 <see cref="Runtime"/>。</summary>
public sealed record ExtensionManifest(
    string Id, Version Version, ExtensionHostApiRange HostApi,
    IReadOnlyDictionary<string, string> DisplayNames, IReadOnlyDictionary<string, string> Descriptions,
    IReadOnlyList<string> Provides, IReadOnlyList<string> Permissions,
    IReadOnlyList<ExtensionDependency> Dependencies, IReadOnlyList<ModuleRiskBehavior> HighRiskBehaviors,
    string? AssetPack = null, ExtensionSignature? Signature = null, ExtensionRuntimeSpec? Runtime = null,
    IReadOnlyList<string>? NetworkHosts = null, IReadOnlyList<ExtensionSettingSpec>? Settings = null,
    IReadOnlyList<ExtensionCommandSpec>? Commands = null)
{
    public string Publisher => Id[..Id.IndexOf('.')];
    public IReadOnlyList<string> Hosts => NetworkHosts ?? [];
    public IReadOnlyList<ExtensionSettingSpec> SettingItems => Settings ?? [];
    public IReadOnlyList<ExtensionCommandSpec> CommandItems => Commands ?? [];
    /// <summary>扩展需要代码才能实现的声明：运行入口、能力、权限、高风险行为、设置项、命令或网络主机。</summary>
    public bool DeclaresCode => Runtime is not null || Provides.Count > 0 || Permissions.Count > 0 || HighRiskBehaviors.Count > 0 ||
        SettingItems.Count > 0 || CommandItems.Count > 0 || Hosts.Count > 0;
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

    /// <summary>已登记的扩展权限，见 <see cref="ExtensionPermissions"/>；输入与截图沿用模块清单的权限名称。</summary>
    public static IReadOnlySet<string> KnownPermissions => ExtensionPermissions.Known;
    private static readonly Regex SettingKey = new("^[a-z][a-zA-Z0-9_-]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex HostName = new("^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$", RegexOptions.CultureInvariant);

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
        if (manifest.Runtime is { } runtime)
            foreach (var path in new[] { runtime.Entry, runtime.Interpreter })
                if (path is not null && !files.Any(x => x.File.Path == path))
                    throw Manifest("runtime 指向的文件不在包内：" + path);
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
                "provides", "permissions", "dependencies", "highRiskBehaviors", "assetPack", "signature", "runtime", "network", "settings", "commands");
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
            var runtime = root.TryGetValue("runtime", out var runtimeElement) ? Runtime(runtimeElement) : null;
            string[] hosts = [];
            if (root.TryGetValue("network", out var networkElement))
            {
                hosts = Strings(Object(networkElement, "network", "hosts"), "hosts", 8, 253);
                if (hosts.Any(host => !HostName.IsMatch(host))) throw Manifest("network.hosts 只接受小写主机名");
            }
            if (permissions.Contains("network.fetch") != hosts.Length > 0)
                throw Manifest("network.fetch 权限与 network.hosts 必须同时声明");
            var settings = root.TryGetValue("settings", out var settingsElement) ? Settings(settingsElement) : [];
            if (settings.Count > 0 && !permissions.Contains("ui.settings")) throw Manifest("声明设置项时必须同时声明 ui.settings 权限");
            var commands = new List<ExtensionCommandSpec>();
            if (root.TryGetValue("commands", out var commandsElement))
            {
                if (commandsElement.ValueKind != JsonValueKind.Array || commandsElement.GetArrayLength() > 16) throw Manifest("commands 必须是最多 16 项的数组");
                foreach (var item in commandsElement.EnumerateArray())
                {
                    var command = Object(item, "命令", "id", "labels");
                    var commandId = Text(command, "id", 32);
                    if (!Segment.IsMatch(commandId) || commands.Any(x => x.Id == commandId)) throw Manifest("命令标识无效或重复：" + commandId);
                    commands.Add(new(commandId, Localized(command, "labels", 32, required: true)));
                }
            }
            if (commands.Count > 0 && !permissions.Contains("ui.shortcut")) throw Manifest("声明命令时必须同时声明 ui.shortcut 权限");
            return new(id, version, range, names, descriptions, provides, permissions, dependencies, behaviors, assetPack, signature,
                runtime, hosts, settings, commands);
        }
    }

    private static ExtensionRuntimeSpec Runtime(JsonElement element)
    {
        var fields = Object(element, "runtime", "protocol", "kind", "entry", "interpreter", "arguments");
        if (!fields.TryGetValue("protocol", out var protocol) || protocol.ValueKind != JsonValueKind.Number ||
            !protocol.TryGetInt32(out var version) || version != ExtensionProtocol.Version)
            throw Manifest("runtime.protocol 必须为 " + ExtensionProtocol.Version);
        var kind = Text(fields, "kind", 16);
        var entry = PackagePath(Text(fields, "entry", 260), "runtime.entry");
        string? interpreter = fields.ContainsKey("interpreter") ? PackagePath(Text(fields, "interpreter", 260), "runtime.interpreter") : null;
        switch (kind)
        {
            case "executable" when entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && interpreter is null:
            case "dotnet" when entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && interpreter is null:
            case "script" when interpreter is not null && interpreter.EndsWith(".exe", StringComparison.OrdinalIgnoreCase):
                break;
            default:
                throw Manifest("runtime.kind 为 executable（entry 为 .exe）、dotnet（entry 为 .dll）或 script（interpreter 为包内 .exe）");
        }
        var arguments = Strings(fields, "arguments", 16, 256, allowDuplicates: true);
        if (arguments.Any(value => value.Any(char.IsControl))) throw Manifest("runtime.arguments 含控制字符");
        return new(version, kind, entry, interpreter, arguments);
    }

    private static List<ExtensionSettingSpec> Settings(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 32) throw Manifest("settings 必须是最多 32 项的数组");
        var result = new List<ExtensionSettingSpec>();
        foreach (var item in element.EnumerateArray())
        {
            var fields = Object(item, "设置项", "key", "type", "labels", "default", "minimum", "maximum", "options");
            var key = Text(fields, "key", 32);
            if (!SettingKey.IsMatch(key) || result.Any(x => x.Key == key)) throw Manifest("设置项键无效或重复：" + key);
            var type = Text(fields, "type", 16);
            var labels = Localized(fields, "labels", 32, required: true);
            if (!fields.TryGetValue("default", out var value)) throw Manifest("设置项缺少 default：" + key);
            double? Number(string name) => fields.TryGetValue(name, out var number)
                ? number.ValueKind == JsonValueKind.Number && number.TryGetDouble(out var parsed) && double.IsFinite(parsed) ? parsed : throw Manifest(key + "." + name + " 无效")
                : null;
            var minimum = Number("minimum");
            var maximum = Number("maximum");
            var options = new List<ExtensionSettingOptionSpec>();
            if (fields.TryGetValue("options", out var optionList))
            {
                if (optionList.ValueKind != JsonValueKind.Array || optionList.GetArrayLength() is 0 or > 32) throw Manifest(key + ".options 必须是 1–32 项的数组");
                foreach (var option in optionList.EnumerateArray())
                {
                    var optionFields = Object(option, key + ".options", "value", "labels");
                    var optionValue = Text(optionFields, "value", 64);
                    if (options.Any(x => x.Value == optionValue)) throw Manifest(key + ".options 重复：" + optionValue);
                    options.Add(new(optionValue, Localized(optionFields, "labels", 32, required: true)));
                }
            }
            var valid = type switch
            {
                "toggle" => value.ValueKind is JsonValueKind.True or JsonValueKind.False && options.Count == 0 && minimum is null && maximum is null,
                "number" => value.ValueKind == JsonValueKind.Number && options.Count == 0 && (minimum is null || maximum is null || minimum <= maximum),
                "text" => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 256 && options.Count == 0 && minimum is null && maximum is null,
                "choice" => value.ValueKind == JsonValueKind.String && options.Any(x => x.Value == value.GetString()) && minimum is null && maximum is null,
                _ => false
            };
            if (!valid) throw Manifest("设置项类型、默认值或范围无效：" + key);
            var spec = new ExtensionSettingSpec(key, type, labels, value.Clone(), minimum, maximum, options);
            if (!SettingAccepts(spec, spec.Default)) throw Manifest("设置项默认值超出范围：" + key);
            result.Add(spec);
        }
        return result;
    }

    /// <summary>设置值是否符合设置项的类型与范围。</summary>
    public static bool SettingAccepts(ExtensionSettingSpec spec, JsonElement value) => spec.Type switch
    {
        "toggle" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) &&
            (spec.Minimum is null || number >= spec.Minimum) && (spec.Maximum is null || number <= spec.Maximum),
        "text" => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 256 && !value.GetString()!.Any(char.IsControl),
        "choice" => value.ValueKind == JsonValueKind.String && spec.Options.Any(x => x.Value == value.GetString()),
        _ => false
    };

    private static string PackagePath(string value, string name)
    {
        try { return FileNames.ArchivePath(value) == value ? value : throw Manifest(name + " 必须是包内相对路径"); }
        catch (OperationException) { throw Manifest(name + " 必须是包内相对路径"); }
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

    private static string[] Strings(Dictionary<string, JsonElement> owner, string name, int maximumCount, int maximumLength, bool allowDuplicates = false)
    {
        if (!owner.TryGetValue(name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximumCount) throw Manifest(name + " 必须是最多 " + maximumCount + " 项的数组");
        var result = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text &&
            text.Length <= maximumLength ? text : throw Manifest(name + " 含无效项")).ToArray();
        return allowDuplicates || result.Distinct(StringComparer.Ordinal).Count() == result.Length ? result : throw Manifest(name + " 含重复项");
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

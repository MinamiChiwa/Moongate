using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Providers.TwObservation.ItemCatalog;

/// <summary>
/// 一个图标键的来源：先按 ParameterTypes 顺序在 ParameterInfo 表中找 IconPath 与 MainColor，没有可读的行时用 FixedPath。
/// NameParameter 为显示名称取自的 ParameterInfo 行（该行 DisplayName 与此属性同义时才登记），没有时为空。
/// </summary>
public sealed record ClientIconDefinition(string Key, IReadOnlyList<string> ParameterTypes, string FixedPath, string? NameParameter = null);

/// <summary>ParameterInfo 表的一行：DisplayName 原文、IconPath（相对 Assets/PatchableAssets/ 的资源路径）与 MainColor（#RRGGBB，没有颜色时为空）。</summary>
public sealed record ClientParameterIcon(string? DisplayName, string? IconPath, string? Tint);

/// <summary>
/// 一个图标键的读取结果：Png 不为空时可用；否则 Reason 为原因码。SourcePath 为相对 Assets/PatchableAssets/ 的资源路径。
/// NameKey 为显示名称所在的 ParameterInfo 行（ParameterType），DisplayName 为该行 DisplayName 原文；没有对应行或表不可读时均为空。
/// </summary>
public sealed record ClientIconEntry(string Key, string? Reason, string? Tint, ClientIconSource Source, string SourcePath,
    int? Width, int? Height, byte[]? Png, string? Sha256, string? NameKey = null, string? DisplayName = null)
{
    public bool Available => Png is not null;
}

/// <summary>当前客户端的全部图标。ClientId 为 AssetPackages 与 ResourcesPackage/Starter 两份资源索引的指纹；Reasons 为附加说明。</summary>
public sealed record ClientIconSet(string ClientId, IReadOnlyList<ClientIconEntry> Icons, IReadOnlyList<string> Reasons)
{
    public long Bytes => Icons.Sum(icon => 256L + (icon.Png?.Length ?? 0));
}

/// <summary>
/// 战斗属性图标：从本机客户端 AssetPackages 按资源路径名称定位单精灵 UnityFS 包并解码为 PNG。属性类的路径与着色取客户端
/// ParameterInfo 表（列顺序以 global-metadata 的 ParameterInfoSheetData 字段声明核对），元素类用已核对的固定路径。
/// 某个键找不到资源或格式不符时只有该键不可用；读取期间客户端文件变化时整体失败、不产生结果。
/// </summary>
public static partial class ClientAttributeIcons
{
    public const string ResourcePrefix = "Assets/PatchableAssets/";
    public const string ParameterSheet = "ParameterInfo";
    public const int MaximumPngBytes = 1024 * 1024;
    public const int MaximumParameterRows = 4096;
    private const long MaximumIndexBytes = 64L * 1024 * 1024;
    private const string MetaPath = "Sheetdata_Client/client.sheetmeta.bin";
    private const string TextPath = "Sheetdata_Client/client.sheettext.bin";
    private const string DataPath = "Sheetdata_Client/client.sheetdata.bin";
    private const string Icon64 = "UI/Sprites/Common/Icon64/";
    private const string ElementIcon = "UI/Sprites/BuffIcon/DamageFloater_ElementalDoT_";
    /// <summary>当前客户端 ParameterInfoSheetData 的字段（tw-20261110 之后的构建离线核对）。</summary>
    public static readonly IReadOnlyList<string> ParameterColumns = ["ParameterType", "DisplayName", "PrimaryParameterType", "MainColor", "IconPath"];

    public static IReadOnlyList<ClientIconDefinition> Definitions { get; } = Array.AsReadOnly<ClientIconDefinition>([
        new(ClientIconKeys.Critical, ["Critical"], Icon64 + "Icon64_SubStat_Critical.png", "Critical"),
        new(ClientIconKeys.PowerfulHit, ["PowerfulHitPowerUp", "PowerfulHitDamageAmplification"], Icon64 + "ICON64_Smite_UP.png"),
        new(ClientIconKeys.RapidHit, ["RapidHitPowerUp", "RapidHitDamageAmplification"], Icon64 + "ICON64_Blow_After_Blow_UP.png"),
        new(ClientIconKeys.DoubleHit, ["DoubleHit"], Icon64 + "ICON64_An_Additional_Hit.png", "DoubleHit"),
        new(ClientIconKeys.BreakedHit, ["DamageAmplificationOnBreak"], Icon64 + "Icon64_Stat_Brake.png"),
        new(ClientIconKeys.BreakOccurred, ["BreakDamage"], Icon64 + "Icon64_Stat_Brake.png", "BreakDamage"),
        new(ClientIconKeys.MultiHit, ["MultiHitPowerUp"], Icon64 + "ICON64_Wide_Area_Reinforcement.png"),
        .. new[] { "Bleed", "Dark", "Fire", "Holy", "Ice", "Lightning", "Poison", "Soul" }.Select(element =>
            new ClientIconDefinition("element." + element.ToLowerInvariant(), [], ElementIcon + element + ".png"))
    ]);

    /// <summary>读取两份资源索引并计算客户端指纹（SHA-256(SHA-256(AssetPackages/index) ‖ SHA-256(Starter/index))，小写十六进制）。</summary>
    public static string ClientId(string appdataDirectory, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(appdataDirectory);
        return Fingerprint(ReadIndex(Path.Combine(root, "AssetPackages", "index"), cancellationToken),
            ReadIndex(Path.Combine(root, "ResourcesPackage", "Starter", "index"), cancellationToken));
    }

    public static ClientIconSet Extract(string appdataDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appdataDirectory);
        var root = Path.GetFullPath(appdataDirectory);
        var assets = ClientResourcePackage.Open(Path.Combine(root, "AssetPackages"), cancellationToken);
        var starter = ClientResourcePackage.Open(Path.Combine(root, "ResourcesPackage", "Starter"), cancellationToken);
        var reasons = new List<string>();
        IReadOnlyDictionary<string, ClientParameterIcon> parameters;
        try { parameters = ReadParameters(root, starter, cancellationToken); }
        catch (ClientIconFormatException) { parameters = new Dictionary<string, ClientParameterIcon>(); reasons.Add(ClientIconReasons.ParameterTableUnavailable); }

        var planned = Definitions.Select(definition => Plan(definition, parameters)).ToArray();
        var wanted = planned.Select(plan => ResourcePrefix + plan.Path).ToHashSet(StringComparer.Ordinal);
        var located = new Dictionary<string, List<ClientResourcePackage.PackageEntry>>(StringComparer.Ordinal);
        foreach (var entry in assets.Entries)
            if (wanted.Contains(entry.Path))
            {
                if (!located.TryGetValue(entry.Path, out var list)) located[entry.Path] = list = [];
                list.Add(entry);
            }
        var stamps = located.Values.SelectMany(list => list).Select(entry => entry.BlobPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, StableFileStamp.Capture, StringComparer.OrdinalIgnoreCase);
        var decoded = new Dictionary<string, (byte[]? Png, int Width, int Height, string? Reason)>(StringComparer.Ordinal);
        var icons = new List<ClientIconEntry>(planned.Length);
        foreach (var plan in planned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!decoded.TryGetValue(plan.Path, out var image))
                decoded[plan.Path] = image = Decode(assets, located.GetValueOrDefault(ResourcePrefix + plan.Path), stamps, cancellationToken);
            icons.Add(image.Png is { } png
                ? new(plan.Key, null, plan.Tint, plan.Source, plan.Path, image.Width, image.Height, png, Convert.ToHexStringLower(SHA256.HashData(png)), plan.NameKey, plan.DisplayName)
                : new(plan.Key, image.Reason, plan.Tint, plan.Source, plan.Path, null, null, null, null, plan.NameKey, plan.DisplayName));
        }
        assets.EnsureUnchanged();
        starter.EnsureUnchanged();
        foreach (var pair in stamps) pair.Value.EnsureUnchanged(pair.Key);
        return new ClientIconSet(Fingerprint(assets.IndexBytes, starter.IndexBytes), icons, reasons);
    }

    /// <summary>
    /// 解析 ParameterInfo 表：键列为 ParameterType 名称的文本偏移；行内依次为 DisplayName、PrimaryParameterType（文本偏移）、
    /// MainColor（标记 0xB0 后接 3 字节 RGB，标记 0x9F 表示没有颜色）、IconPath（文本偏移），整行必须恰好读完。
    /// 单行编码不符时只跳过该行；表缺失或键列损坏时整表不可用。
    /// </summary>
    public static IReadOnlyDictionary<string, ClientParameterIcon> DecodeParameters(byte[] meta, byte[] text, byte[] data,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tables = ClientNameTable.ReadTables(meta, text, cancellationToken);
            if (!tables.TryGetValue(ParameterSheet, out var table) || table.Rows > MaximumParameterRows)
                throw new ClientIconFormatException(ClientIconReasons.ParameterTableUnavailable, "客户端缺少 ParameterInfo 表或行数超过预算");
            var keys = new ClientDataReader(table.Block);
            var result = new Dictionary<string, ClientParameterIcon>(StringComparer.Ordinal);
            for (var row = 0; row < table.Rows; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = ClientNameTable.ReadString(text, checked((int)keys.Var()));
                var start = table.DataOffsets[row];
                var end = row + 1 < table.Rows ? table.DataOffsets[row + 1] : table.DataEnd;
                if (start > end || end > (ulong)data.Length) throw new InvalidDataException("ParameterInfo 行越界");
                try
                {
                    var reader = new ClientDataReader(data, checked((int)start), checked((int)end));
                    var name = ClientNameTable.ReadString(text, checked((int)reader.Var()));
                    reader.Var();
                    var tint = reader.Take(1)[0] switch
                    {
                        0xb0 => "#" + Convert.ToHexString(reader.Take(3)),
                        0x9f => null,
                        _ => throw new InvalidDataException("ParameterInfo 颜色编码未知")
                    };
                    var icon = ClientNameTable.ReadString(text, checked((int)reader.Var()));
                    reader.EnsureEnd();
                    result.TryAdd(key, new ClientParameterIcon(CleanName(name), ResourcePath().IsMatch(icon) ? icon : null, tint));
                }
                catch (Exception error) when (error is InvalidDataException or OverflowException) { }
            }
            keys.EnsureEnd();
            return result;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            throw new ClientIconFormatException(ClientIconReasons.ParameterTableUnavailable, "ParameterInfo 表无法解析：" + error.Message);
        }
    }

    private static IReadOnlyDictionary<string, ClientParameterIcon> ReadParameters(string root, ClientResourcePackage starter, CancellationToken cancellationToken)
    {
        var wanted = new[] { MetaPath, TextPath, DataPath };
        var records = starter.Entries.Where(entry => wanted.Contains(entry.Path, StringComparer.Ordinal)).ToArray();
        if (records.Length != wanted.Length || records.Select(entry => entry.Path).Distinct(StringComparer.Ordinal).Count() != wanted.Length)
            throw new ClientIconFormatException(ClientIconReasons.ParameterTableUnavailable, "客户端缺少表资源");
        var stamps = records.Select(entry => entry.BlobPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, StableFileStamp.Capture, StringComparer.OrdinalIgnoreCase);
        var resources = records.ToDictionary(entry => entry.Path, entry => starter.Read(entry, stamps[entry.BlobPath], cancellationToken).Decoded, StringComparer.Ordinal);
        foreach (var pair in stamps) pair.Value.EnsureUnchanged(pair.Key);
        var metadataPath = Path.Combine(root, "MabinogiMobile_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        var metadataStamp = StableFileStamp.Capture(metadataPath);
        if (metadataStamp.Length > 256L * 1024 * 1024) throw new InvalidDataException("客户端 metadata 超过读取预算");
        var metadata = File.ReadAllBytes(metadataPath);
        metadataStamp.EnsureUnchanged(metadataPath);
        IReadOnlyDictionary<string, IReadOnlyList<string>> fields;
        try { fields = ClientItemCatalogExtractor.ReadSheetFields(metadata, [ParameterSheet], cancellationToken); }
        catch (InvalidDataException error)
        { throw new ClientIconFormatException(ClientIconReasons.ParameterTableUnavailable, "客户端 metadata 无法核对 ParameterInfo 列：" + error.Message); }
        if (!fields.TryGetValue(ParameterSheet, out var declared) || !declared.SequenceEqual(ParameterColumns, StringComparer.Ordinal))
            throw new ClientIconFormatException(ClientIconReasons.ParameterTableUnavailable, "ParameterInfo 列顺序与已核对的不符");
        return DecodeParameters(resources[MetaPath], resources[TextPath], resources[DataPath], cancellationToken);
    }

    private static (string Key, string Path, string? Tint, ClientIconSource Source, string? NameKey, string? DisplayName) Plan(
        ClientIconDefinition definition, IReadOnlyDictionary<string, ClientParameterIcon> parameters)
    {
        var rows = definition.ParameterTypes.Select(type => parameters.GetValueOrDefault(type)).OfType<ClientParameterIcon>().ToArray();
        var tint = rows.Select(row => row.Tint).FirstOrDefault(value => value is not null);
        var name = definition.NameParameter is { } nameKey && parameters.GetValueOrDefault(nameKey)?.DisplayName is { } text ? (nameKey, text) : ((string?)null, (string?)null);
        return rows.Select(row => row.IconPath).FirstOrDefault(value => value is not null) is { } path
            ? (definition.Key, path, tint, ClientIconSource.ParameterInfo, name.Item1, name.Item2)
            : (definition.Key, definition.FixedPath, tint, ClientIconSource.FixedPath, name.Item1, name.Item2);
    }

    /// <summary>去掉颜色标记与首尾空白；空、含控制字符或超过 40 字的名称视为没有。</summary>
    private static string? CleanName(string raw)
    {
        var value = ColorTag().Replace(raw, string.Empty).Trim();
        return value.Length is 0 or > 40 || value.Any(char.IsControl) ? null : value;
    }

    private static (byte[]? Png, int Width, int Height, string? Reason) Decode(ClientResourcePackage assets,
        List<ClientResourcePackage.PackageEntry>? entries, IReadOnlyDictionary<string, StableFileStamp> stamps, CancellationToken cancellationToken)
    {
        if (entries is null) return (null, 0, 0, ClientIconReasons.ResourceMissing);
        if (entries.Count != 1) return (null, 0, 0, ClientIconReasons.ResourceInvalid);
        try
        {
            var raw = assets.ReadRaw(entries[0], stamps[entries[0].BlobPath], UnityBundle.MaximumBundleBytes, cancellationToken);
            var bitmap = ClientIconImage.DecodeSpriteBundle(raw, Path.GetFileNameWithoutExtension(entries[0].Path), cancellationToken);
            var png = PngEncoder.Encode(bitmap);
            return png.Length > MaximumPngBytes ? (null, 0, 0, ClientIconReasons.ResourceInvalid) : (png, bitmap.Width, bitmap.Height, null);
        }
        catch (ClientIconFormatException error) { return (null, 0, 0, error.Code); }
        catch (Exception error) when (error is InvalidDataException or OverflowException) { return (null, 0, 0, ClientIconReasons.ResourceInvalid); }
    }

    private static byte[] ReadIndex(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stamp = StableFileStamp.Capture(path);
        if (stamp.Length > MaximumIndexBytes) throw new InvalidDataException("客户端资源索引超过大小预算");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        if (stream.Length != stamp.Length) throw new InvalidDataException("客户端资源文件在提取期间发生变化");
        var bytes = new byte[stamp.Length];
        stream.ReadExactly(bytes);
        stamp.EnsureUnchanged(path);
        return bytes;
    }

    private static string Fingerprint(byte[] assetIndex, byte[] starterIndex) =>
        Convert.ToHexStringLower(SHA256.HashData([.. SHA256.HashData(assetIndex), .. SHA256.HashData(starterIndex)]));

    /// <summary>ParameterInfo.IconPath 只接受相对的 .png 资源路径。</summary>
    [GeneratedRegex(@"\A(?!.*\.\.)[A-Za-z0-9_\-]+(/[A-Za-z0-9_\-. ]+)*\.png\z", RegexOptions.CultureInvariant)]
    private static partial Regex ResourcePath();
    [GeneratedRegex(@"</?color(?:=#[0-9a-fA-F]{6,8})?>", RegexOptions.CultureInvariant)] private static partial Regex ColorTag();
}

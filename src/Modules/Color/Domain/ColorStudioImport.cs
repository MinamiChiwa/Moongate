using System.Globalization;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using static MabiTool.Infrastructure.LegacyJsonFiles;

namespace MabiTool.Modules.Color.Domain;

public sealed record ColorStudioImportPlan(ColorStudioPreferences Preferences, ColorProfile? Draft,
    IReadOnlyList<ColorProfile> Profiles, IReadOnlyList<ImportedColorHistoryItem> History,
    IReadOnlyList<ImportIdMapping> Mappings, IReadOnlyList<ImportIssue> Issues, bool Conflict);

public static class ColorStudioImport
{
    public static IReadOnlySet<string> Files { get; } = new HashSet<string>(StringComparer.Ordinal)
        { "settings.json", "profile.json", "presets.json", "history.json" };

    public static ColorStudioImportPlan Plan(LegacyJsonSource source, SettingsImportRequest request,
        ColorLibrary current, ColorStudioPreferences previous, DateTimeOffset now, CancellationToken ct)
    {
        if (request.BindingOverrides is { Count: > 0 }) throw Invalid("", "Color Studio 迁移不接受模式绑定覆盖");
        var issues = new List<ImportIssue>(); var mappings = new List<ImportIdMapping>();
        var profiles = new List<ColorProfile>(); var history = new List<ImportedColorHistoryItem>();
        var conflict = false;
        var draftStrategy = previous.DraftStrategy;
        var names = current.Profiles.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        void Issue(string code, string file, string oldId, string message) => issues.Add(new(code, file, oldId, message));
        string Id() => Guid.NewGuid().ToString("N");
        ColorStrategy Strategy(JsonElement profile, JsonElement container, string file, string oldId, ColorStrategy fallback)
        {
            var value = Field(profile, "search_strategy");
            var revision = Field(container, "screenshot_default_revision");
            if (revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt32(out var number) || number != 1)
            {
                if (value.ValueKind != JsonValueKind.Undefined && (value.ValueKind != JsonValueKind.String || value.GetString() != "screenshot"))
                    Issue("import.strategyAdjusted", file, oldId, "沿用原一次性默认迁移选择截图策略");
                return ColorStrategy.Screenshot;
            }
            return value.ValueKind == JsonValueKind.Undefined ? fallback : Text(value, file, 32) switch
                { "screenshot" or "atlas" => ColorStrategy.Screenshot, "native" => ColorStrategy.Precise, _ => throw Invalid(file, "寻色策略无效") };
        }
        ColorProfile? Profile(JsonElement value, JsonElement container, string file, string oldId, string name, string id, bool named, ColorStrategy fallback)
        {
            ct.ThrowIfCancellationRequested();
            var rows = value.ValueKind == JsonValueKind.Array ? value : Field(value, "regions");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != 3) throw Invalid(file, "方案须包含三个区域");
            var orderValue = Field(value, "priority_order");
            int[] order = orderValue.ValueKind == JsonValueKind.Undefined ? [0, 1, 2] : ReadOrder(orderValue, file);
            var strategy = Strategy(value, container, file, oldId, fallback);
            if (!named) draftStrategy = strategy;
            var regions = new List<ColorRegion>(); var usable = true;
            for (var index = 0; index < 3; index++)
            {
                var row = rows[index];
                if (row.ValueKind != JsonValueKind.Object) throw Invalid(file, "区域设置须为对象");
                var enabled = RequiredBoolean(Field(row, "enabled"), file);
                var target = RawText(Field(row, "target"), file, 64);
                var alt = Field(row, "alt").ValueKind == JsonValueKind.Undefined ? "" : RawText(Field(row, "alt"), file, 2048);
                var mode = Text(Field(row, "mode"), file, 32) switch
                    { "精准 HEX" => ColorMatchMode.ExactHex, "相似颜色" => ColorMatchMode.Similar, _ => throw Invalid(file, "颜色匹配模式无效") };
                var tolerance = RequiredNumber(Field(row, "tolerance"), file, 1, 35);
                var colors = alt.Replace('，', ',').Split(',').Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                if (colors.Length > 63) throw Invalid(file, "替代色最多为 63 个");
                var validColors = Normalize(target) is not null && colors.All(x => Normalize(x) is not null);
                if (!validColors)
                {
                    if (named && enabled) throw Invalid(file, "启用区域需要六位 HEX 目标及替代色");
                    usable = false;
                    Issue("import.draftNeedsReview", file, oldId + ".region-" + (index + 1), "原颜色输入已保留，修正后可载入目标草稿");
                }
                regions.Add(new("region-" + (index + 1), Normalize(target) ?? "#202020", colors.Select(x => Normalize(x) ?? "#202020").ToArray(),
                    mode, tolerance, Array.IndexOf(order, index) + 1, enabled));
            }
            if (named && !regions.Any(x => x.Enabled)) throw Invalid(file, "命名方案至少启用一个区域");
            if (!usable && named)
            {
                Issue("import.profileNeedsReview", file, oldId, "禁用区域含未完成色码，请修正原方案后重新导入"); conflict = true;
            }
            if (!usable) return null;
            var result = new ColorProfile(id, 1, name, strategy, regions, new(0));
            ColorProfiles.Validate(result, named); return result;
        }

        var settings = source.Documents.GetValueOrDefault("settings.json");
        if (settings.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined)) throw Invalid("settings.json", "设置须为对象");
        var defaultStrategy = settings.ValueKind == JsonValueKind.Undefined ? previous.DefaultStrategy.KnownValue!.Value
            : Strategy(settings, settings, "settings.json", "search_strategy", ColorStrategy.Screenshot);
        var server = Field(settings, "server").ValueKind == JsonValueKind.Undefined ? previous.Server : Text(Field(settings, "server"), "settings.json", 16);
        if (server is not ("tw" or "kr" or "jp")) throw Invalid("settings.json", "服务器偏好无效");
        var language = Field(settings, "language").ValueKind == JsonValueKind.Undefined ? previous.Language : Text(Field(settings, "language"), "settings.json", 32);
        if (language is not ("简体中文" or "繁體中文" or "English" or "日本語" or "한국어"))
        { Issue("import.settingAdjusted", "settings.json", "language", "沿用原加载规则恢复简体中文"); language = "简体中文"; }
        var presentation = previous.PresentationPreferences.Deserialize<Dictionary<string, JsonElement>>() ?? [];
        foreach (var key in new[] { "overlay_position", "overlay_alpha" })
        {
            var value = Field(settings, key);
            if (value.ValueKind == JsonValueKind.Undefined) continue;
            if (key == "overlay_position")
            {
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2 ||
                    value.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out _))) throw Invalid("settings.json", "浮窗位置须为两个整数");
            }
            else
            {
                double alpha = .92;
                var parsed = value.ValueKind == JsonValueKind.Number ? value.TryGetDouble(out alpha) :
                    value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out alpha);
                var adjusted = parsed && double.IsFinite(alpha) ? Math.Clamp(alpha, .65, 1) : .92;
                if (!parsed || !double.IsFinite(alpha) || alpha != adjusted) Issue("import.settingAdjusted", "settings.json", key, "沿用原浮窗透明度规则恢复或裁剪");
                value = JsonSerializer.SerializeToElement(adjusted);
            }
            presentation[key] = value;
        }
        ColorProfile? draft = null;
        var originalDraft = previous.OriginalDraft;
        if (source.Documents.TryGetValue("profile.json", out var draftValue))
        {
            originalDraft = draftValue;
            draft = Profile(draftValue, draftValue, "profile.json", "draft", "当前草稿", "draft", false, defaultStrategy);
            mappings.Add(new("color-draft", "draft", draft?.Id, null, draft is null ? "retained-input" : "mapped", []));
        }
        if (source.Documents.TryGetValue("presets.json", out var presets))
        {
            if (presets.ValueKind != JsonValueKind.Object || Field(presets, "schema").ValueKind != JsonValueKind.Number ||
                !Field(presets, "schema").TryGetInt32(out var schema) || schema != 1 || Field(presets, "presets").ValueKind != JsonValueKind.Array)
                throw Invalid("presets.json", "方案库须为 schema=1 的对象");
            var rows = Field(presets, "presets");
            if (rows.GetArrayLength() + current.Profiles.Count > 100) throw new OperationException("resource.capacityExceeded", "导入后方案超过 100 个");
            var oldIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                var oldId = Text(Field(row, "id"), "presets.json", 128);
                if (oldId.Length == 0 || !oldIds.Add(oldId)) throw Invalid("presets.json", "旧方案 ID 为空或重复");
                var rawName = Text(Field(row, "name"), "presets.json", 60);
                var name = request.ProfileNames?.GetValueOrDefault(oldId, rawName) ?? rawName;
                if (name is null || name.Trim().Length is < 1 or > 60 || name.Any(char.IsControl) || name.Any(char.IsSurrogate)) throw Invalid("presets.json", "方案名称无效");
                name = name.Trim();
                if (!names.Add(name)) { Issue("import.profileNameConflict", "presets.json", oldId, "方案名称与已有方案冲突，请明确提供新名称"); conflict = true; }
                var id = Id();
                var profile = Profile(Field(row, "profile"), presets, "presets.json", oldId, name, id, true, ColorStrategy.Screenshot);
                if (profile is not null) profiles.Add(profile);
                mappings.Add(new("color-profile", oldId, id, rawName, name == rawName ? "new-id" : "explicit-name", []));
            }
            if (request.ProfileNames is not null && request.ProfileNames.Keys.Any(x => !oldIds.Contains(x))) throw Invalid("presets.json", "名称映射含未知方案 ID");
        }
        else if (request.ProfileNames is { Count: > 0 }) throw Invalid("presets.json", "名称映射需要旧方案文件");
        if (source.Documents.TryGetValue("history.json", out var entries))
        {
            if (entries.ValueKind != JsonValueKind.Array) throw Invalid("history.json", "历史须为数组");
            if (entries.GetArrayLength() > 50) throw new OperationException("resource.capacityExceeded", "原历史最多 50 条");
            var index = 0;
            foreach (var row in entries.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                if (row.ValueKind != JsonValueKind.Object) throw Invalid("history.json", "历史条目须为对象");
                var oldId = (index++).ToString(CultureInfo.InvariantCulture); var id = Id();
                var time = OptionalText(Field(row, "time"), "history.json", 32);
                if (time is not null && !DateTime.TryParseExact(time, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw Invalid("history.json", "原本地时间格式无效");
                var item = new ImportedColorHistoryItem(id, source.SourceId, source.SourceHash, oldId, time,
                    OptionalText(Field(row, "outcome"), "history.json", 64), OptionalBoolean(Field(row, "restored"), "history.json"),
                    Result(row), Field(row, "best").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : Result(Field(row, "best")),
                    ObservationQuality.Estimated, now);
                history.Add(item); mappings.Add(new("color-history", oldId, id, null, "imported-estimate", []));
            }
        }
        return new(new(previous.Revision, server, language, defaultStrategy, JsonSerializer.SerializeToElement(presentation), originalDraft, draftStrategy),
            draft, profiles, history, mappings, issues, conflict);
    }

    private static int[] ReadOrder(JsonElement value, string file)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3 ||
            value.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out _))) throw Invalid(file, "优先级顺序无效");
        var order = value.EnumerateArray().Select(x => x.GetInt32()).ToArray();
        if (!order.Order().SequenceEqual(new[] { 0, 1, 2 })) throw Invalid(file, "优先级须为三个不同区域");
        return order;
    }
    private static string RawText(JsonElement value, string file, int maximum) => value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length <= maximum
        ? text : throw Invalid(file, "颜色输入无效");
    private static string? Normalize(string value)
    {
        var hex = "#" + value.Trim().TrimStart('#').ToUpperInvariant();
        return ColorProfiles.IsHex(hex) ? hex : null;
    }
    private static bool RequiredBoolean(JsonElement value, string file) => value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean() : throw Invalid(file, "区域布尔属性无效");
    private static bool? OptionalBoolean(JsonElement value, string file) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : RequiredBoolean(value, file);
    private static string? OptionalText(JsonElement value, string file, int maximum) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : Text(value, file, maximum);
    private static double RequiredNumber(JsonElement value, string file, double minimum, double maximum) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= minimum && number <= maximum
            ? number : throw Invalid(file, "数值或范围无效");
    private static double? OptionalNumber(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : RequiredNumber(value, "history.json", 0, double.MaxValue);
    private static ImportedColorResult Result(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("history.json", "历史摘要须为对象");
        var rows = Field(value, "regions"); var regions = new List<ImportedColorRegion>();
        if (rows.ValueKind != JsonValueKind.Undefined)
        {
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 3) throw Invalid("history.json", "历史区域最多三个");
            foreach (var row in rows.EnumerateArray())
            {
                var region = (int)RequiredNumber(Field(row, "region"), "history.json", 1, 3);
                if (!Field(row, "region").TryGetInt32(out _) || regions.Any(x => x.Region == region)) throw Invalid("history.json", "历史区域编号无效或重复");
                var targets = Field(row, "targets"); IReadOnlyList<string>? colors = null;
                if (targets.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                {
                    if (targets.ValueKind != JsonValueKind.Array || targets.GetArrayLength() > 64) throw Invalid("history.json", "历史目标颜色过多");
                    colors = targets.EnumerateArray().Select(x => Text(x, "history.json", 64)).ToArray();
                    if (colors.Any(x => Normalize(x) is null)) throw Invalid("history.json", "历史目标色码无效");
                }
                var color = OptionalText(Field(row, "color"), "history.json", 64);
                if (color is not null && Normalize(color) is null) throw Invalid("history.json", "历史实测色码无效");
                regions.Add(new(region, color, OptionalBoolean(Field(row, "enabled"), "history.json"), OptionalBoolean(Field(row, "available"), "history.json"),
                    colors, OptionalNumber(Field(row, "delta"))));
            }
        }
        var count = Field(value, "region_count");
        int? regionCount = count.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null :
            count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var number) && number is >= 0 and <= 3 ? number : throw Invalid("history.json", "历史区域数量无效");
        return new(regions, regionCount, OptionalNumber(Field(value, "maximum")), OptionalNumber(Field(value, "average")));
    }
}

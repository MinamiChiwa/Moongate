using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Modules.Color.Domain;

public static class ColorProfiles
{
    public static ColorProfile Default => new("draft", 1, "当前草稿", ColorStrategy.Screenshot,
        Enumerable.Range(1, 3).Select(i => new ColorRegion("region-" + i, "#202020", [],
            ColorMatchMode.ExactHex, 8, i, true)).ToArray(), new(0));

    public static bool IsId(string? id) => id is { Length: >= 1 and <= 128 } &&
        Regex.IsMatch(id, @"\A[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant);
    public static bool IsHex(string? hex) => hex is not null && Regex.IsMatch(hex, @"\A#[0-9A-F]{6}\z", RegexOptions.CultureInvariant);

    public static void Validate(ColorProfile? p, bool requireEnabled = true)
    {
        if (p is null || !IsId(p.Id) || p.SchemaVersion != 1 ||
            p.Name is not { Length: >= 1 and <= 60 } || p.Name.Trim().Length == 0 || p.Name.Any(char.IsControl) ||
            p.Name.Any(char.IsSurrogate) || p.Strategy.KnownValue is null ||
            p.Regions is not { Count: 3 } || p.Regions.Any(r => r is null) ||
            p.Regions.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != 3 ||
            requireEnabled && !p.Regions.Any(r => r.Enabled) ||
            !p.Regions.Select(r => r.Priority).Order().SequenceEqual(new[] { 1, 2, 3 }) ||
            p.Regions.Any(r => !IsId(r.Id) || !IsHex(r.TargetHex) || r.MatchMode.KnownValue is null ||
                !double.IsFinite(r.Tolerance) || r.Tolerance is < 1 or > 35 ||
                r.AlternativeHex is null || r.AlternativeHex.Count > 63 || r.AlternativeHex.Any(h => !IsHex(h))))
            throw new OperationException("request.invalid", "需要三个有效区域、大写 #RRGGBB、1–3 优先级及 1–35 色差；命名方案至少启用一个区域");
    }

    public static ColorProfile Copy(ColorProfile profile) => JsonSerializer.Deserialize<ColorProfile>(
        JsonSerializer.Serialize(profile, ContractJson.Options), ContractJson.Options)!;

    public static object EngineProfile(ColorProfile profile) => new
    {
        schema = 1, search_strategy = profile.Strategy.KnownValue == ColorStrategy.Precise ? "native" : "screenshot",
        priority_order = profile.Regions.Select((row, index) => (row.Priority, index)).OrderBy(x => x.Priority).Select(x => x.index),
        regions = profile.Regions.Select(row => new { enabled = row.Enabled, target = row.TargetHex,
            alt = string.Join(", ", row.AlternativeHex), mode = row.MatchMode.KnownValue == ColorMatchMode.ExactHex ? "精准 HEX" : "相似颜色", tolerance = row.Tolerance })
    };
}

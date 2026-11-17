using System.Globalization;

namespace MabiTool.Providers.GitHubReleases;

/// <summary>
/// 发行版本，按语义化版本 2.0 比较：<c>主.次.修订[-预发布]</c>。数字段按数值比较；预发布版本低于同号正式版本，
/// 预发布标识逐段比较（数字低于字母数字，前缀相同时段少者为低）。例：<c>0.2.0-preview &lt; 0.2.0 &lt; 0.2.1-preview</c>。
/// </summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private ReleaseVersion(int major, int minor, int patch, string[] prerelease)
    {
        Major = major; Minor = minor; Patch = patch; Prerelease = prerelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> Prerelease { get; }
    public bool IsPrerelease => Prerelease.Count > 0;

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = null!;
        if (text is not { Length: > 0 and <= 64 }) return false;
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        var core = (dash < 0 ? text : text[..dash]).Split('.');
        var pre = dash < 0 ? [] : text[(dash + 1)..].Split('.');
        if (core.Length != 3 || !core.All(Numeric) || dash >= 0 && pre.Any(part => part.Length == 0 ||
                !part.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-') || part.All(char.IsAsciiDigit) && !Numeric(part)))
            return false;
        if (!int.TryParse(core[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(core[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(core[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch)) return false;
        version = new(major, minor, patch, pre);
        return true;
    }

    public static ReleaseVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException("版本号不是 主.次.修订[-预发布] 格式：" + text);

    // 数字段不能有前导零（单个 0 除外）。
    private static bool Numeric(string part) => part.Length is > 0 and <= 9 && part.All(char.IsAsciiDigit) && (part.Length == 1 || part[0] != '0');

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0) return core;
        if (IsPrerelease != other.IsPrerelease) return IsPrerelease ? -1 : 1;
        if (!IsPrerelease) return 0;
        for (var i = 0; i < Math.Min(Prerelease.Count, other.Prerelease.Count); i++)
        {
            var left = Prerelease[i];
            var right = other.Prerelease[i];
            var leftNumeric = left.All(char.IsAsciiDigit);
            var rightNumeric = right.All(char.IsAsciiDigit);
            var result = leftNumeric && rightNumeric ? long.Parse(left, CultureInfo.InvariantCulture).CompareTo(long.Parse(right, CultureInfo.InvariantCulture))
                : leftNumeric ? -1 : rightNumeric ? 1 : string.CompareOrdinal(left, right);
            if (result != 0) return Math.Sign(result);
        }
        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    public bool Equals(ReleaseVersion? other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is ReleaseVersion other && Equals(other);
    public override int GetHashCode() => ToString().GetHashCode(StringComparison.Ordinal);
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (IsPrerelease ? "-" + string.Join('.', Prerelease) : "");
}

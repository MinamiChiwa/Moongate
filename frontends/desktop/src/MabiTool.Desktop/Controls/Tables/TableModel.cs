using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MabiTool.Desktop.Controls.Tables;

public sealed record TableSort(string ColumnId, bool Descending);

/// <summary>一张表保存在前端偏好里的设置：列顺序、列宽、隐藏的列、排序与改过的列名。缺省项取列定义的默认值。</summary>
public sealed record TableSettings(IReadOnlyList<string>? Order = null, IReadOnlyDictionary<string, double>? Widths = null,
    IReadOnlyList<string>? Hidden = null, IReadOnlyList<TableSort>? Sort = null, IReadOnlyDictionary<string, string>? Titles = null,
    IReadOnlyList<string>? Shown = null)
{
    public static TableSettings Empty { get; } = new();
}

/// <summary>
/// 各表的设置，按表标识保存在前端偏好文件里；同一标识的表格（例如背包页与背包窗口）共用一份，任一处修改另一处随 <see cref="Changed"/> 刷新。
/// </summary>
public static class TableSettingsStore
{
    private static Dictionary<string, TableSettings> tables = new(StringComparer.Ordinal);

    public static event EventHandler<string>? Changed;

    public static IReadOnlyDictionary<string, TableSettings> All => tables;

    public static TableSettings Get(string? id) => id is not null && tables.TryGetValue(id, out var settings) ? settings : TableSettings.Empty;

    public static void Set(string? id, TableSettings settings)
    {
        if (id is null) return;
        tables[id] = settings;
        Changed?.Invoke(null, id);
    }

    /// <summary>读取偏好时恢复，不触发保存。</summary>
    internal static void Restore(IReadOnlyDictionary<string, TableSettings>? saved) =>
        tables = saved is null ? new(StringComparer.Ordinal) : new Dictionary<string, TableSettings>(saved, StringComparer.Ordinal);
}

/// <summary>一列的筛选条件：文字列为“包含”，数字列为范围（两端可空）。</summary>
public sealed record ColumnFilter(string? Contains = null, double? Min = null, double? Max = null)
{
    public bool IsActive => !string.IsNullOrWhiteSpace(Contains) || Min is not null || Max is not null;
}

/// <summary>表格的排列、筛选、检索与文字导出规则；不依赖界面，单独可测。</summary>
public static partial class TableQuery
{
    private static readonly CompareInfo Collation = CultureInfo.GetCultureInfo("zh-Hans").CompareInfo;

    [GeneratedRegex(@"^\s*([-+]?\d[\d,]*(?:\.\d+)?)")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"^\s*(\d+):(\d{2})(?::(\d{2}))?\s*$")]
    private static partial Regex Clock();

    /// <summary>
    /// 单元格文字的数值：开头的数字（可带千分位与小数，后面可跟 %、单位等），或 m:ss / h:mm:ss 时长（换算成秒）。
    /// “未读取”“不适用”“未知”等没有数字开头的文字返回空，排序时排在最后。
    /// </summary>
    public static double? Number(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (Clock().Match(text) is { Success: true } clock)
        {
            var a = int.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture);
            var b = int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture);
            return clock.Groups[3].Success ? a * 3600 + b * 60 + int.Parse(clock.Groups[3].Value, CultureInfo.InvariantCulture) : a * 60 + b;
        }
        var match = LeadingNumber().Match(text);
        return match.Success && double.TryParse(match.Groups[1].Value.Replace(",", string.Empty, StringComparison.Ordinal),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>
    /// 列顺序：保存过的顺序里仍存在的列按保存的先后，之后出现的新列（例如新读到的角色）插在默认顺序中它前一列之后，
    /// 默认顺序里它前面没有列时排在最前。
    /// </summary>
    public static IReadOnlyList<string> ResolveOrder(IReadOnlyList<string> defaults, IReadOnlyList<string>? saved)
    {
        if (saved is null || saved.Count == 0) return defaults;
        var known = defaults.ToHashSet(StringComparer.Ordinal);
        var result = saved.Where(known.Contains).Distinct(StringComparer.Ordinal).ToList();
        var placed = result.ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < defaults.Count; i++)
        {
            var id = defaults[i];
            if (placed.Contains(id)) continue;
            var anchor = -1;
            for (var j = i - 1; j >= 0 && anchor < 0; j--) anchor = result.IndexOf(defaults[j]);
            result.Insert(anchor + 1, id);
            placed.Add(id);
        }
        return result;
    }

    /// <summary>该列的筛选是否接受这段文字：文字列不区分大小写地包含；数字列取数值落在范围内，没有数值的不接受。</summary>
    public static bool Accepts(ColumnKind kind, ColumnFilter filter, string text)
    {
        if (!filter.IsActive) return true;
        if (!string.IsNullOrWhiteSpace(filter.Contains) && !text.Contains(filter.Contains.Trim(), StringComparison.CurrentCultureIgnoreCase))
            return false;
        if (filter.Min is null && filter.Max is null) return true;
        if (Number(text) is not { } value) return false;
        return (filter.Min is not { } min || value >= min) && (filter.Max is not { } max || value <= max);
    }

    /// <summary>按筛选留下行，再按排序键依次比较；数字列的非数值、文字列的空白在升序与降序下都排在最后，比较相同时保持原顺序。</summary>
    public static List<object> Apply(IEnumerable<object> rows, IReadOnlyList<TableColumn> columns,
        IReadOnlyDictionary<string, ColumnFilter> filters, IReadOnlyList<TableSort> sort)
    {
        var byId = columns.ToDictionary(column => column.Id, StringComparer.Ordinal);
        var active = filters.Where(pair => pair.Value.IsActive && byId.ContainsKey(pair.Key))
            .Select(pair => (Column: byId[pair.Key], Filter: pair.Value)).ToList();
        var kept = rows.Where(row => active.All(item => Accepts(item.Column.Kind, item.Filter, item.Column.TextOf(row)))).ToList();
        var keys = sort.Where(key => byId.TryGetValue(key.ColumnId, out var column) && column.Sortable)
            .Select(key => (Column: byId[key.ColumnId], key.Descending)).ToList();
        if (keys.Count == 0) return kept;
        var indexed = kept.Select((row, index) => (row, index)).ToList();
        indexed.Sort((a, b) =>
        {
            foreach (var (column, descending) in keys)
            {
                var result = Compare(column, column.TextOf(a.row), column.TextOf(b.row), descending);
                if (result != 0) return result;
            }
            return a.index.CompareTo(b.index);
        });
        return indexed.Select(item => item.row).ToList();
    }

    private static int Compare(TableColumn column, string a, string b, bool descending)
    {
        if (column.Kind == ColumnKind.Number)
        {
            var x = Number(a);
            var y = Number(b);
            if (x is null || y is null)
            {
                if (x is null && y is null) return 0;
                return x is null ? 1 : -1;
            }
            return Ordered(x.Value.CompareTo(y.Value), descending);
        }
        var emptyA = string.IsNullOrWhiteSpace(a);
        var emptyB = string.IsNullOrWhiteSpace(b);
        if (emptyA || emptyB) return emptyA == emptyB ? 0 : emptyA ? 1 : -1;
        return Ordered(Collation.Compare(a, b, CompareOptions.IgnoreCase), descending);
    }

    private static int Ordered(int result, bool descending) => descending ? -result : result;

    /// <summary>检索：任一可见列的文字不区分大小写地包含检索词（去掉首尾空白）；检索词为空时没有匹配。</summary>
    public static bool Matches(object row, IReadOnlyList<TableColumn> visible, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return false;
        var term = search.Trim();
        return visible.Any(column => column.TextOf(row).Contains(term, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>
    /// 复制用的文字：首行是表头，之后每行一条，列之间用制表符分隔，行之间用 CRLF，可直接贴进 Excel；
    /// 单元格里的制表符与换行换成空格。
    /// </summary>
    public static string Tsv(IEnumerable<object> rows, IReadOnlyList<TableColumn> visible, Func<TableColumn, string> header)
    {
        static string Clean(string text) => text.Replace('\t', ' ').Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
        var builder = new StringBuilder();
        builder.Append(string.Join('\t', visible.Select(column => Clean(header(column)))));
        foreach (var row in rows)
        {
            builder.Append("\r\n");
            builder.Append(string.Join('\t', visible.Select(column => Clean(column.TextOf(row)))));
        }
        return builder.ToString();
    }

    /// <summary>RFC 4180：含逗号、双引号、换行或首尾空白的字段加双引号，字段内的双引号写成两个；行之间用 CRLF。</summary>
    public static string Csv(IEnumerable<object> rows, IReadOnlyList<TableColumn> visible, Func<TableColumn, string> header)
    {
        static string Field(string text) =>
            text.IndexOfAny([',', '"', '\r', '\n']) >= 0 || text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]))
                ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : text;
        var builder = new StringBuilder();
        builder.Append(string.Join(',', visible.Select(column => Field(header(column)))));
        foreach (var row in rows)
        {
            builder.Append("\r\n");
            builder.Append(string.Join(',', visible.Select(column => Field(column.TextOf(row)))));
        }
        builder.Append("\r\n");
        return builder.ToString();
    }

    /// <summary>导出的 CSV 文件内容：UTF-8 带 BOM，Excel 直接打开时中文不乱码。</summary>
    public static byte[] CsvBytes(IEnumerable<object> rows, IReadOnlyList<TableColumn> visible, Func<TableColumn, string> header) =>
        [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(Csv(rows, visible, header))];

    /// <summary>默认文件名里不能出现的字符换成下划线。</summary>
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

/// <summary>自由文本不进入默认记录；各字段分别按实际契约校验。</summary>
public static class DiagnosticSanitizer
{
    public const int MaximumFields = 12;
    /// <summary>单条文字详情的字符上限。</summary>
    public const int MaximumDetailChars = 8192;
    private static readonly HashSet<string> Exceptions = new(StringComparer.Ordinal)
    { "Exception", "OperationException", "StorageException", "ModuleRuntimeException", "ActivityBarrierException",
        "WorkerException", "IOException", "TimeoutException", "UnauthorizedAccessException", "InvalidOperationException",
        "JsonException", "SqliteException", "OperationCanceledException", "ArgumentException", "GameInputException", "ResourceLeaseException" };
    public static readonly IReadOnlyList<string> Levels = ["trace", "debug", "info", "warning", "error"];

    private static readonly HashSet<string> HttpMethods = new(StringComparer.Ordinal) { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

    /// <param name="allowDetail">只有调试采集记录允许携带已清洗的文字详情。</param>
    /// <param name="httpMethod">HTTP 问题响应的请求方法；不在固定集合内时丢弃。</param>
    /// <param name="route">HTTP 问题响应匹配到的路由模板；不符合 <see cref="IsRouteTemplate"/> 时丢弃。</param>
    public static DiagnosticRecord Create(DateTimeOffset at, string level, string module, string? code = null,
        string? traceId = null, string? operationId = null, string? providerVersion = null, string? exceptionType = null,
        string? component = null, string? @event = null, IReadOnlyDictionary<string, string>? fields = null,
        UInt64String? sequence = null, string? detail = null, bool allowDetail = false, string? httpMethod = null, string? route = null)
    {
        var definition = @event is null ? null : DiagnosticEvents.Find(@event);
        return new(at, Levels.Contains(level) ? level : "error",
            Regex.IsMatch(module, @"\A[a-z][a-z0-9-]{0,39}\z") ? module : "core",
            code is not null && ApiErrors.Definitions.Any(x => x.Code == code) ? code : null,
            IsTraceId(traceId) ? traceId : null,
            IsOperationId(operationId) ? operationId : null,
            providerVersion is { Length: <= 32 } && Version.TryParse(providerVersion, out _) ? providerVersion : null,
            exceptionType is not null && Exceptions.Contains(exceptionType) ? exceptionType : null,
            IsProbeId(component) ? component : null, definition?.Code, Fields(fields, definition), sequence,
            allowDetail ? Detail(detail) : null,
            httpMethod is not null && HttpMethods.Contains(httpMethod) ? httpMethod : null,
            IsRouteTemplate(route) ? route : null);
    }

    /// <summary>
    /// 关联 ID 的合法形态：W3C trace-id（32 位小写十六进制）、ASP.NET Core 的 <c>HttpContext.TraceIdentifier</c>
    /// （Kestrel 连接 ID 为 13 位 0–9/A–V 的 base32，加冒号与 8 位大写十六进制请求序号，如 <c>0HNQ1JQLS16H4:0000000A</c>），
    /// 以及以 Operation ID 作关联的 <c>op-</c> 加 32 位小写十六进制。
    /// </summary>
    public static bool IsTraceId(string? value) => value is not null &&
        Regex.IsMatch(value, @"\A(?:[0-9a-f]{32}|[0-9A-V]{13}:[0-9A-F]{8}|op-[0-9a-f]{32})\z");
    public static bool IsOperationId(string? value) => value is not null && Regex.IsMatch(value, @"\Aop-[0-9a-f]{32}\z");

    /// <summary>
    /// 路由模板：以 / 开头、至多 200 字符、至多 12 段；每段是字面量（小写字母开头的小写字母、数字、点与连字符，至多 40 字符，
    /// 不含 4 位以上连续数字或 16 位以上十六进制串）或参数（<c>{name}</c>，可带 <c>:约束</c>）。实际 ID、令牌和查询串因此无法进入记录。
    /// </summary>
    public static bool IsRouteTemplate(string? value)
    {
        if (value is not { Length: > 0 and <= 200 } || value[0] != '/' || value.Contains("//", StringComparison.Ordinal)) return false;
        var segments = value[1..].Split('/');
        if (segments.Length > 12) return false;
        foreach (var segment in segments)
        {
            if (segment.Length == 0 && segments.Length == 1) continue;
            if (Regex.IsMatch(segment, @"\A\{[A-Za-z][A-Za-z0-9]{0,31}(?::[a-z][a-z0-9()]{0,31})?\??\}\z")) continue;
            if (!Regex.IsMatch(segment, @"\A[a-z][a-z0-9.-]{0,39}\z") || Regex.IsMatch(segment, "[0-9]{4}") ||
                Regex.IsMatch(segment, "[0-9a-f]{16}")) return false;
        }
        return true;
    }

    private static IReadOnlyDictionary<string, string>? Fields(IReadOnlyDictionary<string, string>? fields, DiagnosticEventDefinition? definition)
    {
        if (fields is null || definition is null) return null;
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            if (result.Count >= MaximumFields) break;
            if (definition.Fields.Contains(key) && IsProbeKey(key) && IsProbeValue(value)) result[key] = value;
        }
        return result.Count == 0 ? null : result;
    }

    private static string? Detail(string? detail)
    {
        if (detail is null) return null;
        var cleaned = DiagnosticDetailSanitizer.StripSecrets(detail);
        if (cleaned.Length > MaximumDetailChars) cleaned = cleaned[..MaximumDetailChars];
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>落盘记录复验：重新按白名单生成后序列化结果必须一致。</summary>
    public static bool IsValid(DiagnosticRecord record, bool allowDetail = false) =>
        JsonSerializer.Serialize(record, ContractJson.Options) == JsonSerializer.Serialize(Create(record.OccurredAt, record.Level, record.Module,
            record.Code, record.TraceId, record.OperationId, record.ProviderVersion, record.ExceptionType, record.Component, record.Event,
            record.Fields, record.Sequence, record.Detail, allowDetail, record.HttpMethod, record.Route), ContractJson.Options);

    // 运行状态探针：标识、状态与键只允许固定短码；值允许错误码、版本号和 profile 等受限短串，
    // 拒绝空白、路径分隔符、@ 和长十六进制串，使会话 ID、令牌、哈希与自由文本无法进入总览。
    public static bool IsProbeId(string? value) => value is not null &&
        Regex.IsMatch(value, @"\A[a-z][a-z0-9-]{0,31}(?:\.[a-z][a-z0-9-]{0,31}){0,3}\z");
    public static bool IsProbeModule(string? value) => value is not null && Regex.IsMatch(value, @"\A[a-z][a-z0-9-]{0,39}\z");
    public static bool IsProbeState(string? value) => value is not null && Regex.IsMatch(value, @"\A[a-z][a-z0-9-]{0,31}\z");
    public static bool IsProbeKey(string? value) => value is { Length: <= 96 } &&
        Regex.IsMatch(value, @"\A[a-z][A-Za-z0-9-]{0,47}(?:\.[a-z][A-Za-z0-9-]{0,47}){0,5}\z");
    public static bool IsProbeValue(string? value) => value is { Length: <= 64 } &&
        Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._:+-]{0,63}\z") && !Regex.IsMatch(value, "[0-9A-Fa-f]{16}");
    public static string? ProbeReason(string? code) => code is not null && ApiErrors.Definitions.Any(x => x.Code == code) ? code : null;
}

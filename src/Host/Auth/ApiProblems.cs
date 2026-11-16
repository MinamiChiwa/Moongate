using MabiTool.Contracts;
using MabiTool.Host.Diagnostics;
using MabiTool.Infrastructure;

namespace MabiTool.Host;

public static class ApiProblems
{
    public static Task Write(HttpContext context, string code, string title, IReadOnlyDictionary<string, System.Text.Json.JsonElement>? details = null)
    {
        var problem = ApiErrors.Create(code, title, context.TraceIdentifier, details);
        if (problem.Code != "resource.busy")
            context.RequestServices.GetService<DiagnosticRecorder>()?.Record(DiagnosticSanitizer.Create(DateTimeOffset.UtcNow,
                problem.Status >= 500 ? "error" : "warning", "core", problem.Code, problem.TraceId,
                httpMethod: context.Request.Method, route: RouteTemplate(context)));
        return Results.Json(problem, ContractJson.Options, "application/problem+json", problem.Status).ExecuteAsync(context);
    }

    /// <summary>请求匹配到的路由模板（如 <c>/api/v1/operations/{id}</c>），不含实际参数值与查询串；未匹配路由时为空。</summary>
    internal static string? RouteTemplate(HttpContext context)
    {
        if (context.GetEndpoint() is not RouteEndpoint endpoint || endpoint.RoutePattern.RawText is not { Length: > 0 } raw) return null;
        return raw[0] == '/' ? raw : "/" + raw;
    }
}

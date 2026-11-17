using System.Net;
using System.Security.Claims;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Infrastructure.Updates;
using MabiTool.Providers.GitHubReleases;
using Microsoft.AspNetCore.Http.Features;

namespace MabiTool.Host;

public sealed class LocalApiMiddleware(RequestDelegate next, LocalApiOptions options)
{
    private readonly HashSet<string> origins = options.AllowedOrigins.ToHashSet(StringComparer.Ordinal);
    private static readonly string[] Methods = ["GET", "POST", "DELETE"];
    private static readonly string[] Headers = ["authorization", "content-type", "idempotency-key", "if-match", "x-event-topics", "x-event-resources", "last-event-id"];
    public static AuthSession Session(HttpContext context) => (AuthSession)context.Items[typeof(AuthSession)]!;

    public async Task InvokeAsync(HttpContext context, LocalAuthService auth, ILogger<LocalApiMiddleware> logger)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        try
        {
            var host = context.Request.Host;
            if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
                (host.Host != "127.0.0.1" && host.Host != "localhost") || host.Port != context.Connection.LocalPort)
                throw new LocalApiException(ApiErrors.HostRejected, "本机 API 的 Host 或连接来源不受允许");
            var rawOrigin = context.Request.Headers.Origin;
            var origin = rawOrigin.ToString();
            if (rawOrigin.Count > 1 || (origin.Length > 0 && !origins.Contains(origin)))
                throw new LocalApiException(ApiErrors.OriginRejected, "请求 Origin 未获允许");
            if (context.Request.QueryString.HasValue)
                throw new LocalApiException(ApiErrors.QueryNotSupported, "当前接口不接受 URL 参数");
            if (origin.Length > 0)
            {
                context.Response.Headers.AccessControlAllowOrigin = origin;
                context.Response.Headers.Vary = "Origin";
                context.Response.Headers.AccessControlExposeHeaders = "ETag, Location, Content-Length, Content-Disposition, X-Content-Sha256";
            }
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                var requestedMethod = context.Request.Headers.AccessControlRequestMethod;
                var requestedHeaders = context.Request.Headers.AccessControlRequestHeaders.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (origin.Length == 0 || requestedMethod.Count != 1 || !Methods.Contains(requestedMethod.ToString(), StringComparer.Ordinal) ||
                    requestedHeaders.Any(h => !Headers.Contains(h, StringComparer.OrdinalIgnoreCase)))
                    throw new LocalApiException("request.invalid", "预检方法或请求头不受支持");
                context.Response.Headers.AccessControlAllowMethods = string.Join(", ", Methods);
                context.Response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type, Idempotency-Key, If-Match, X-Event-Topics, X-Event-Resources, Last-Event-ID";
                context.Response.StatusCode = 204;
                return;
            }
            var endpoint = context.GetEndpoint()?.Metadata.GetMetadata<EndpointDefinition>();
            if (HttpMethods.IsPost(context.Request.Method) &&
                (endpoint?.BinaryRequest == true ? context.Request.ContentType != "application/octet-stream" : !context.Request.HasJsonContentType()))
                throw new LocalApiException("request.unsupportedMediaType", endpoint?.BinaryRequest == true
                    ? "文件内容需要 application/octet-stream" : "写入请求需要 application/json");
            if (endpoint?.Anonymous != true)
            {
                var authorization = context.Request.Headers.Authorization;
                var bearer = authorization.ToString();
                var supplied = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? bearer[7..] : "";
                var session = authorization.Count == 1 ? auth.Authenticate(supplied, origin.Length == 0 ? null : origin) : null;
                if (session is null)
                {
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    throw new LocalApiException(ApiErrors.AuthRequired, "需要有效的本机访问凭据");
                }
                if (endpoint is null && !HttpMethods.IsGet(context.Request.Method))
                    throw new LocalApiException(ApiErrors.ResourceNotFound, "资源不存在");
                if (!session.Permissions.Contains(endpoint?.Permission ?? ApiPermissions.SystemRead, StringComparer.Ordinal))
                    throw new LocalApiException("permission.denied", "当前客户端未获得所需权限");
                context.Items[typeof(AuthSession)] = session;
                var claims = session.Permissions.Select(p => new Claim("permission", p)).Append(new Claim(ClaimTypes.NameIdentifier, session.ClientId));
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "LocalBearer"));
            }
            if (endpoint is not null && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } budget)
                budget.MaxRequestBodySize = endpoint.MaximumRequestBytes;
            if (context.Request.ContentLength > (endpoint?.MaximumRequestBytes ?? 64 * 1024))
                throw new LocalApiException("request.bodyTooLarge", "请求正文超过接口预算");
            await next(context);
            if (context.Response.StatusCode == 405 && !context.Response.HasStarted)
                await ApiProblems.Write(context, "request.methodNotAllowed", "该资源不支持此请求方法");
        }
        catch (ModuleRuntimeException error) when (!context.Response.HasStarted)
        {
            await ApiProblems.Write(context, error.Code, error.Message);
        }
        catch (OperationException error) when (!context.Response.HasStarted)
        {
            await ApiProblems.Write(context, error.Code, error.Message, error.Details);
        }
        catch (Exception error) when (!context.Response.HasStarted && error is UpdateSourceException or UpdatePackageException or AssetDownloadException)
        {
            var code = error switch
            {
                UpdateSourceException e => e.Code, UpdatePackageException e => e.Code,
                AssetDownloadException e => e.Code, _ => "update.storageUnavailable"
            };
            var details = error is UpdateSourceException sources ? new Dictionary<string, System.Text.Json.JsonElement>
                { ["sources"] = System.Text.Json.JsonSerializer.SerializeToElement(sources.Failures, ContractJson.Options) } : null;
            await ApiProblems.Write(context, code, "更新请求未完成", details);
        }
        catch (ActivityBarrierException error) when (!context.Response.HasStarted)
        {
            await ApiProblems.Write(context, error.Code, "更新维护期间暂停接收新活动，请在维护完成后重试");
        }
        catch (StorageException error) when (!context.Response.HasStarted)
        {
            var registered = ApiErrors.Definitions.Any(x => x.Code == error.Code);
            // 未登记的内部存储错误对外折叠为 storage.unavailable，原始错误码保留在宿主日志中。
            if (!registered) logger.LogWarning("存储错误 {storageCode} 以 storage.unavailable 返回：{method} {path}",
                error.Code, context.Request.Method, context.Request.Path.Value);
            await ApiProblems.Write(context, registered ? error.Code : "storage.unavailable", "文件或持久存储无法完成当前请求");
        }
        catch (LocalApiException error) when (!context.Response.HasStarted)
        {
            await ApiProblems.Write(context, error.Code, error.Message);
        }
        catch (BadHttpRequestException error) when (!context.Response.HasStarted)
        {
            var code = error.StatusCode switch { 413 => "request.bodyTooLarge", 415 => "request.unsupportedMediaType", _ => "request.invalid" };
            await ApiProblems.Write(context, code, "请求格式、大小或媒体类型无效");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                await ApiProblems.Write(context, ApiErrors.UnexpectedError, "请求执行失败");
            }
            else context.Abort();
        }
    }
}

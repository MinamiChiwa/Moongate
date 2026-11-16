using System.Text.Json.Nodes;

namespace MabiTool.Contracts.Generation;

public static class OpenApiDocument
{
    public static JsonObject Create()
    {
        var schemas = new JsonObject();
        foreach (var type in ContractSchemas.ReachableTypes())
        {
            var schema = ContractSchemas.Export(type);
            RebaseReferences(schema, "#/components/schemas/" + ContractSchemas.Name(type));
            if (ApiContract.DomainTypes.Contains(type) && type != typeof(ApiProblem) && type != typeof(StreamControl) && type != typeof(EventStreamOptions) && !ApiContract.Endpoints.Any(e => e.ResponseType == type))
                schema["x-runtime-support"] = "schema-only";
            schemas[ContractSchemas.Name(type)] = schema;
        }
        var paths = new JsonObject();
        foreach (var endpoint in ApiContract.Endpoints)
        {
            var parameters = new JsonArray();
            if (endpoint.Path.Contains("{id}", StringComparison.Ordinal))
                parameters.Add(new JsonObject
                {
                    ["name"] = "id", ["in"] = "path", ["required"] = true,
                    ["schema"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 }
                });
            foreach (var header in new[] { ("Idempotency-Key", endpoint.Idempotent), ("If-Match", endpoint.Conditional) }.Where(h => h.Item2))
                parameters.Add(new JsonObject { ["name"] = header.Item1, ["in"] = "header", ["required"] = true,
                    ["schema"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 } });
            if (endpoint.EventFiltered)
                foreach (var header in new[] { "X-Event-Topics", "X-Event-Resources" }.Concat(endpoint.Streaming ? ["Last-Event-ID"] : Array.Empty<string>()))
                    parameters.Add(new JsonObject { ["name"] = header, ["in"] = "header", ["required"] = false,
                        ["description"] = header == "Last-Event-ID" ? "不透明续传游标；绑定实例、客户端及规范化过滤" : "逗号分隔的精确匹配过滤；省略时匹配全部已授权资源",
                        ["schema"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 8192 } });
            var responses = new JsonObject
            {
                [endpoint.SuccessStatus.ToString()] = new JsonObject
                {
                    ["description"] = "请求成功",
                    ["content"] = new JsonObject { [endpoint.MediaType] = new JsonObject
                        { ["schema"] = endpoint.BinaryResponse ? new JsonObject { ["type"] = "string", ["format"] = "binary" } :
                            endpoint.Streaming ? new JsonObject { ["type"] = "string", ["description"] = "SSE 帧流；JSON data 为 snapshot、control 或版本化 EventEnvelope；id 为不透明续传游标" } : ResponseSchema(endpoint.ResponseType) } }
                }
            };
            var responseHeaders = new JsonObject();
            if (endpoint.ETag) responseHeaders["ETag"] = new JsonObject { ["description"] = "强资源修订", ["schema"] = new JsonObject { ["type"] = "string" } };
            if (endpoint.BinaryResponse)
                foreach (var name in new[] { "Content-Length", "Content-Disposition", "X-Content-Sha256" })
                    responseHeaders[name] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "string" } };
            if (endpoint.SuccessStatus == 202) responseHeaders["Location"] = new JsonObject { ["description"] = "操作查询路径", ["schema"] = new JsonObject { ["type"] = "string" } };
            if (responseHeaders.Count > 0) responses[endpoint.SuccessStatus.ToString()]!["headers"] = responseHeaders;
            foreach (var status in ApiErrors.Definitions.Select(e => e.Status).Distinct().Order())
                responses[status.ToString()] = new JsonObject
                {
                    ["description"] = "结构化错误",
                    ["content"] = new JsonObject { ["application/problem+json"] = new JsonObject
                        { ["schema"] = Reference(typeof(ApiProblem)) } }
                };
            var operation = new JsonObject
            {
                ["operationId"] = endpoint.OperationId, ["parameters"] = parameters,
                ["responses"] = responses, ["x-required-permission"] = endpoint.Anonymous ? null : endpoint.Permission
            };
            if (endpoint.Method == "POST") operation["x-max-request-bytes"] = endpoint.MaximumRequestBytes;
            if (endpoint.MaximumResponseBytes != 4 * 1024 * 1024) operation["x-max-response-bytes"] = endpoint.MaximumResponseBytes;
            if (endpoint.Streaming)
            {
                operation["x-sse-message"] = Reference(typeof(StreamMessage));
                operation["x-sse-snapshot"] = Reference(typeof(ProjectionSnapshot));
                operation["x-sse-control"] = Reference(typeof(StreamControl));
            }
            if (endpoint.RequestType is not null || endpoint.BinaryRequest)
                operation["requestBody"] = new JsonObject
                {
                    ["required"] = true,
                    ["content"] = new JsonObject { [endpoint.BinaryRequest ? "application/octet-stream" : "application/json"] =
                        new JsonObject { ["schema"] = endpoint.BinaryRequest ? new JsonObject { ["type"] = "string", ["format"] = "binary" } : Reference(endpoint.RequestType!) } }
                };
            if (endpoint.Anonymous) operation["security"] = new JsonArray();
            if (paths[endpoint.Path] is not JsonObject path) { path = new JsonObject(); paths[endpoint.Path] = path; }
            path[endpoint.Method.ToLowerInvariant()] = operation;
        }
        return new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["jsonSchemaDialect"] = "https://json-schema.org/draft/2020-12/schema",
            ["info"] = new JsonObject { ["title"] = "Moongate Local API", ["version"] = ApiContract.Version },
            ["security"] = new JsonArray(new JsonObject { ["localBearer"] = new JsonArray() }),
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject
                {
                    ["localBearer"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer" }
                },
                ["schemas"] = schemas
            },
            ["x-error-codes"] = System.Text.Json.JsonSerializer.SerializeToNode(ApiErrors.Definitions, ContractJson.Options),
            ["x-event-types"] = new JsonArray(ContractEvents.Definitions.Select(x => (JsonNode?)JsonValue.Create(x.Type)).ToArray())
        };
    }

    private static JsonNode ResponseSchema(Type type) => type == typeof(System.Text.Json.JsonElement)
        ? new JsonObject() : Reference(type);

    private static JsonObject Reference(Type type) => new() { ["$ref"] = "#/components/schemas/" + ContractSchemas.Name(type) };

    private static void RebaseReferences(JsonNode? node, string prefix)
    {
        if (node is JsonObject obj)
        {
            if (obj["$ref"] is JsonValue value && value.TryGetValue<string>(out var reference) && reference.StartsWith('#'))
                obj["$ref"] = prefix + reference[1..];
            foreach (var property in obj.ToArray()) RebaseReferences(property.Value, prefix);
        }
        else if (node is JsonArray array)
            foreach (var item in array) RebaseReferences(item, prefix);
    }
}

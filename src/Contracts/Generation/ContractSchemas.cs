using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace MabiTool.Contracts.Generation;

public static class ContractSchemas
{
    public static string Name(Type type) => type.IsGenericType
        ? type.Name[..type.Name.IndexOf('`')] + "Of" + string.Join("And", type.GetGenericArguments().Select(Name))
        : type.Name;

    public static JsonNode Export(Type type)
    {
        return ContractJson.Options.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, schema) =>
            {
                var underlying = Nullable.GetUnderlyingType(context.TypeInfo.Type);
                var actualType = underlying ?? context.TypeInfo.Type;
                if (actualType.IsGenericType && actualType.GetGenericTypeDefinition() == typeof(OpenEnum<>))
                {
                    var enumType = actualType.GenericTypeArguments[0];
                    return new JsonObject
                    {
                        ["type"] = underlying is null ? JsonValue.Create("string") : new JsonArray("string", "null"),
                        ["minLength"] = 1, ["maxLength"] = 128, ["pattern"] = "^[^\\u0000-\\u001f]+(?![\\s\\S])",
                        ["x-known-values"] = new JsonArray(Enum.GetNames(enumType).Select(name =>
                            (JsonNode?)JsonValue.Create(JsonNamingPolicy.KebabCaseLower.ConvertName(name))).ToArray())
                    };
                }
                if (actualType == typeof(DamageAmountString))
                    return new JsonObject
                    {
                        ["type"] = underlying is null ? JsonValue.Create("string") : new JsonArray("string", "null"),
                        ["pattern"] = "^(0|[1-9][0-9]{0,127})(?![\\s\\S])", ["maxLength"] = 128,
                        ["description"] = "累计伤害的规范非负整数十进制字符串；保留超过 UInt64 的总量。"
                    };
                if (actualType == typeof(UInt64String))
                {
                    return new JsonObject
                    {
                        ["type"] = underlying is null ? JsonValue.Create("string") : new JsonArray("string", "null"),
                        ["pattern"] = UInt64Pattern(),
                        ["maxLength"] = 20,
                        ["description"] = "规范十进制 UInt64 字符串，范围 0–18446744073709551615；禁止转换为 JavaScript number。"
                    };
                }
                // 根层类型不允许为空，属性层的可空性由对应 DTO 声明决定。
                if (context.Path.Length == 0 && schema is JsonObject obj && obj["type"] is JsonArray types)
                {
                    var nonNull = types.Where(t => t?.GetValue<string>() != "null").Select(t => t!.DeepClone()).ToArray();
                    obj["type"] = nonNull.Length == 1 ? nonNull[0] : new JsonArray(nonNull);
                }
                if (schema is JsonObject parent && parent["properties"] is JsonObject properties)
                    foreach (var property in context.TypeInfo.Properties)
                    {
                        if (properties[property.Name] is not JsonObject previous) continue;
                        // 元素类型带自定义转换器（OpenEnum、UInt64String、DamageAmountString）的数组，导出器不生成 items，这里补齐。
                        if (!previous.ContainsKey("items") && ElementType(property.PropertyType) is { } elementType &&
                            ScalarSchema(elementType) is { } items)
                        { previous["items"] = items; continue; }
                        var propertyType = Nullable.GetUnderlyingType(property.PropertyType);
                        JsonObject scalar;
                        if (propertyType == typeof(UInt64String))
                            scalar = new()
                            {
                                ["type"] = new JsonArray("string", "null"), ["pattern"] = UInt64Pattern(), ["maxLength"] = 20,
                                ["description"] = "规范十进制 UInt64 字符串，范围 0–18446744073709551615；禁止转换为 JavaScript number。"
                            };
                        else if (propertyType is { IsGenericType: true } && propertyType.GetGenericTypeDefinition() == typeof(OpenEnum<>))
                            scalar = new()
                            {
                                ["type"] = new JsonArray("string", "null"), ["minLength"] = 1, ["maxLength"] = 128,
                                ["pattern"] = "^[^\\u0000-\\u001f]+(?![\\s\\S])",
                                ["x-known-values"] = new JsonArray(Enum.GetNames(propertyType.GenericTypeArguments[0]).Select(name =>
                                    (JsonNode?)JsonValue.Create(JsonNamingPolicy.KebabCaseLower.ConvertName(name))).ToArray())
                            };
                        else continue;
                        if (previous.ContainsKey("default")) scalar["default"] = previous["default"]?.DeepClone();
                        properties[property.Name] = scalar;
                    }
                if (actualType == typeof(CombatBreakGauge) &&
                    schema is JsonObject detail && detail["properties"] is JsonObject detailProperties)
                {
                    if (detailProperties["current"] is JsonObject number) number["minimum"] = 0;
                    if (detailProperties["reason"] is JsonObject reason)
                    {
                        reason["maxLength"] = 128;
                        reason["pattern"] = "^[^\\u0000-\\u001f]*(?![\\s\\S])";
                    }
                }
                return schema;
            }
        });
    }

    private static Type? ElementType(Type type)
    {
        if (type == typeof(string)) return null;
        if (type.IsArray) return type.GetElementType();
        var sequence = type.GetInterfaces().Append(type)
            .FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return sequence?.GenericTypeArguments[0];
    }

    /// <summary>自定义转换器标量的元素 schema，与同类型的单个属性保持一致；可空元素允许 null。</summary>
    private static JsonObject? ScalarSchema(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        var actual = underlying ?? type;
        JsonNode kind = underlying is null ? JsonValue.Create("string") : new JsonArray("string", "null");
        if (actual == typeof(UInt64String))
            return new()
            {
                ["type"] = kind, ["pattern"] = UInt64Pattern(), ["maxLength"] = 20,
                ["description"] = "规范十进制 UInt64 字符串，范围 0–18446744073709551615；禁止转换为 JavaScript number。"
            };
        if (actual == typeof(DamageAmountString))
            return new()
            {
                ["type"] = kind, ["pattern"] = "^(0|[1-9][0-9]{0,127})(?![\\s\\S])", ["maxLength"] = 128,
                ["description"] = "累计伤害的规范非负整数十进制字符串；保留超过 UInt64 的总量。"
            };
        if (actual is { IsGenericType: true } && actual.GetGenericTypeDefinition() == typeof(OpenEnum<>))
            return new()
            {
                ["type"] = kind, ["minLength"] = 1, ["maxLength"] = 128, ["pattern"] = "^[^\\u0000-\\u001f]+(?![\\s\\S])",
                ["x-known-values"] = new JsonArray(Enum.GetNames(actual.GenericTypeArguments[0]).Select(name =>
                    (JsonNode?)JsonValue.Create(JsonNamingPolicy.KebabCaseLower.ConvertName(name))).ToArray())
            };
        return null;
    }

    public static JsonNode EventSchema(EventDefinition definition)
    {
        var schema = Export(definition.EnvelopeType);
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["title"] = definition.Type + ".v1";
        schema["x-runtime-support"] = definition.Type is "shortcut.triggered" or "diagnostics.record" or "operation.changed" or "module.configuration-changed" or "replay.observation-changed" or "music.catalog.changed" or "music.library.changed" or "music.playback-history.changed" or "music.import.committed" or "room.import.committed" or "color.import.committed" or "room.preferences.changed" or "color.preferences.changed" or "color.run.changed" or "color.library.changed" or "game.observation.changed" or "room.session.changed" or "room.server.changed" or "desktop.surface.changed" or "combat.summary.changed" or "combat.effects.changed" or "combat.encounter.closed" or "combat.observation.degraded" or "combat.measurement.changed" or "storage.changed" or "storage.backup.changed" or "storage.maintenance.changed" or "storage.module-data.changed" or "update.policy-changed" or "update.progress" ? "implemented" : "schema-only";
        var properties = schema["properties"]!;
        if (definition.Type == "room.score-draft.changed") schema["x-runtime-support"] = "implemented";
        if (definition.Module == "inventory") schema["x-runtime-support"] = "implemented";
        properties["type"] = new JsonObject { ["const"] = definition.Type };
        properties["module"] = new JsonObject { ["const"] = definition.Module };
        properties["schemaVersion"] = new JsonObject { ["const"] = 1 };
        return schema;
    }

    public static IReadOnlyList<Type> ReachableTypes()
    {
        var visited = new HashSet<Type>();
        void Visit(Type type)
        {
            if (Nullable.GetUnderlyingType(type) is { } underlying) { Visit(underlying); return; }
            if (!visited.Add(type)) return;
            if (type.IsGenericType)
                foreach (var argument in type.GetGenericArguments()) Visit(argument);
            if ((type.IsGenericType && type.GetGenericTypeDefinition() == typeof(OpenEnum<>)) ||
                type == typeof(UInt64String) || type == typeof(DamageAmountString) || type == typeof(JsonElement) || type.IsEnum ||
                type.Namespace != "MabiTool.Contracts") return;
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) Visit(property.PropertyType);
        }
        foreach (var type in ApiContract.Endpoints.Select(x => x.ResponseType).Concat(ApiContract.DomainTypes)) Visit(type);
        foreach (var type in ApiContract.Endpoints.Select(x => x.RequestType).OfType<Type>()) Visit(type);
        Visit(typeof(LocalDiscoveryInfo));
        Visit(typeof(BootstrapRequest));
        Visit(typeof(BootstrapResponse));
        foreach (var definition in ContractEvents.Definitions) Visit(definition.EnvelopeType);
        return visited.Where(t => t.Namespace == "MabiTool.Contracts").OrderBy(Name, StringComparer.Ordinal).ToArray();
    }

    private static string UInt64Pattern()
    {
        const string maximum = "18446744073709551615";
        List<string> alternatives = ["0", "[1-9][0-9]{0,18}"];
        for (var i = 0; i < maximum.Length; i++)
        {
            var minimum = i == 0 ? 1 : 0;
            var upper = maximum[i] - '0' - 1;
            if (upper < minimum) continue;
            var digit = minimum == upper ? minimum.ToString() : $"[{minimum}-{upper}]";
            var remaining = maximum.Length - i - 1;
            alternatives.Add(maximum[..i] + digit + (remaining > 0 ? $"[0-9]{{{remaining}}}" : ""));
        }
        alternatives.Add(maximum);
        return "^(?:" + string.Join("|", alternatives) + ")(?![\\s\\S])";
    }
}

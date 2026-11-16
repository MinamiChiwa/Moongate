using System.Text.Json.Nodes;

namespace MabiTool.Contracts.Generation;

public static class RoomScoreDraftProtocolSchema
{
    public static JsonObject Create()
    {
        static JsonObject Ref(string type) => new() { ["$ref"] = "urn:mabitool:openapi#/components/schemas/" + type };
        static JsonObject Text(int maximum = 128) => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = maximum, ["pattern"] = "^[^\\u0000-\\u001f]+(?![\\s\\S])" };
        static JsonObject Message(string type, JsonObject fields, params string[] required)
        {
            fields["type"] = new JsonObject { ["const"] = type };
            fields["requestId"] = Text(64);
            return new() { ["type"] = "object", ["properties"] = fields, ["required"] = new JsonArray(new[] { "type" }.Concat(required).Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()), ["additionalProperties"] = false };
        }
        var actionFields = new JsonObject
        {
            ["id"] = Text(), ["sessionId"] = Text(), ["kind"] = new JsonObject { ["enum"] = new JsonArray("start", "stop") },
            ["targetServerMs"] = new JsonObject { ["type"] = "number", ["minimum"] = 0 },
            ["serverEpoch"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
            ["scoreDraftId"] = Text(), ["scoreDraftRevision"] = Ref("UInt64String")
        };
        var action = new JsonObject { ["type"] = "object", ["properties"] = actionFields,
            ["required"] = new JsonArray(actionFields.Select(field => (JsonNode?)JsonValue.Create(field.Key)).ToArray()), ["additionalProperties"] = false };
        var musicMode = new JsonObject { ["enum"] = new JsonArray("independent", "ensemble") };
        return new()
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema", ["$id"] = "urn:mabitool:room:score-draft:v1",
            ["title"] = "roundScoreDraft v1", ["description"] = "房间协议 v1 的 roundScoreDraft:1 扩展；类型引用同包 OpenAPI 组件。",
            ["oneOf"] = new JsonArray(
                Message("publish-score-draft", new() { ["expectedRevision"] = Ref("UInt64String"), ["source"] = Ref("MusicScoreSelection"),
                    ["score"] = Ref("MusicResolvedScore"), ["assignments"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 24, ["items"] = Ref("RoomScoreAssignment") } },
                    "expectedRevision", "source", "score", "assignments"),
                Message("map-score-draft", new() { ["draftId"] = Text(), ["draftRevision"] = Ref("UInt64String"),
                    ["mapping"] = new JsonObject { ["allOf"] = new JsonArray(Ref("RoomScoreMemberMapping"), new JsonObject
                    { ["properties"] = new JsonObject { ["state"] = new JsonObject { ["enum"] = new JsonArray("mapped", "pending") } } }) } }, "draftId", "draftRevision", "mapping"),
                Message("clear-score-draft", new() { ["draftId"] = Text(), ["draftRevision"] = Ref("UInt64String") }, "draftId", "draftRevision"),
                Message("prepare-action", new() { ["action"] = action, ["mode"] = musicMode.DeepClone() }, "action", "mode"),
                Message("prepare-session", new() { ["sessionId"] = Text(), ["serverEpoch"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
                    ["mode"] = musicMode, ["scoreDraftId"] = Text(), ["scoreDraftRevision"] = Ref("UInt64String") }, "sessionId", "serverEpoch", "mode", "scoreDraftId", "scoreDraftRevision"))
        };
    }
}

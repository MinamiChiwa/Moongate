using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using MabiTool.LifeVision.Worker;
using Xunit;

namespace MabiTool.LifeVision.Tests;

/// <summary>线格式 DTO 与 contracts/worker/life-vision.v1.schema.json 逐层属性一致，Worker 实际输出的枚举值都在 schema 里。</summary>
public sealed class ContractTests
{
    private static readonly JsonNode Schema = JsonNode.Parse(File.ReadAllText(Path.Combine(TestAssets.RepositoryRoot(), "contracts", "worker", "life-vision.v1.schema.json")))!;

    private static JsonObject Definition(string name) => Schema["$defs"]![name]!.AsObject();

    /// <summary>解开 $ref 与 oneOf（null 之外的分支），返回对象定义。</summary>
    private static JsonObject Resolve(JsonNode node)
    {
        var value = node.AsObject();
        if (value["$ref"] is { } reference) return Definition(reference.GetValue<string>().Split('/')[^1]);
        if (value["oneOf"] is JsonArray branches)
            return Resolve(branches.Single(branch => branch!["type"]?.GetValue<string>() != "null")!);
        return value;
    }

    private static void AssertSameShape(JsonObject schema, JsonElement instance, string path)
    {
        var properties = schema["properties"]!.AsObject();
        var names = instance.EnumerateObject().Select(property => property.Name).Order().ToArray();
        Assert.Equal(properties.Select(property => property.Key).Order(), names);
        // DTO 的每个属性都会序列化（可空的也写 null），所以 required 等于全部属性。
        Assert.Equal(schema["required"]!.AsArray().Select(item => item!.GetValue<string>()).Order(), names);
        foreach (var property in instance.EnumerateObject().Where(property => property.Value.ValueKind == JsonValueKind.Object))
            AssertSameShape(Resolve(properties[property.Name]!), property.Value, path + "." + property.Name);
    }

    [Fact]
    public void ObservationHasTheSchemaProperties()
    {
        var observation = new FishingObservation(new UInt64String(7), 12.5, true, false, true,
            new FishingHookObservation(new VisionPoint(630, 371), .98), new FishingGaugeObservation(.4, 38, 96, "hook-track"),
            new FishingDialogObservation("focused", new VisionPoint(752, 864), new VisionPoint(530, 864)), 8, 3);
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new LifeVisionEvent(observation), ContractJson.Options));
        Assert.Equal(["observation"], document.RootElement.EnumerateObject().Select(property => property.Name));
        AssertSameShape(Definition("watchEvent"), document.RootElement, "event");
        // 可空的嵌套对象为空时仍写出键（schema 的 required 包含它们）。
        var empty = observation with { Hook = null, Gauge = null, Dialog = null };
        using var emptyDocument = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new LifeVisionEvent(empty), ContractJson.Options));
        var names = emptyDocument.RootElement.GetProperty("observation").EnumerateObject().Select(property => property.Name).Order();
        Assert.Equal(Definition("observation")["required"]!.AsArray().Select(item => item!.GetValue<string>()).Order(), names);
        Assert.Equal(JsonValueKind.String, emptyDocument.RootElement.GetProperty("observation").GetProperty("frameId").ValueKind);
    }

    [Fact]
    public void WatchArgumentsHaveTheSchemaProperties()
    {
        var arguments = new LifeVisionWatchArguments(new UInt64String(66082), 1234, DateTimeOffset.UnixEpoch.AddDays(1), new PhysicalWindowRect(10, 20, 1280, 960),
            96, 100, LifeVisionProfiles.TwFishing1280x960);
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(arguments, ContractJson.Options));
        AssertSameShape(Definition("watchArguments"), document.RootElement, "arguments");
        Assert.Equal(LifeVisionProfiles.WatchMethod, Schema["$defs"]!["watchRequest"]!["properties"]!["method"]!["const"]!.GetValue<string>());
        Assert.Equal(LifeVisionProfiles.TwFishing1280x960, Definition("watchArguments")["properties"]!["profile"]!["const"]!.GetValue<string>());
        var client = Definition("rect")["properties"]!;
        Assert.Equal(LifeVisionProfiles.ClientWidth, client["width"]!["const"]!.GetValue<int>());
        Assert.Equal(LifeVisionProfiles.ClientHeight, client["height"]!["const"]!.GetValue<int>());
    }

    [Fact]
    public void WorkerFailureCodesAreExactlyTheSchemaEnum()
    {
        var codes = Definition("failure")["properties"]!["code"]!["enum"]!.AsArray().Select(item => item!.GetValue<string>());
        Assert.Equal(codes.Order(), LifeVisionApplication.FailureCodes.Order());
    }

    [Fact]
    public void RecognizedSourcesAndDialogKindsAreSchemaValues()
    {
        var observation = Definition("observation")["properties"]!;
        var sources = Resolve(observation["gauge"]!)["properties"]!["source"]!["enum"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        var kinds = Resolve(observation["dialog"]!)["properties"]!["kind"]!["enum"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        var seen = new HashSet<string>();
        var recognizer = new FishingRecognizer(TestAssets.Catalog);
        seen.Add(recognizer.Recognize(TestAssets.HookFrame(true, 38), 0).Gauge!.Source);
        seen.Add(recognizer.Recognize(TestAssets.HookFrame(false, 38), 100).Gauge!.Source);
        seen.Add(new FishingRecognizer(TestAssets.Catalog).Recognize(TestAssets.HookFrame(false, 38), 0).Gauge!.Source);
        Assert.Equal(sources.Order(), seen.Order());
        var dialogs = new[] { FishingProfile.FocusedDialog, FishingProfile.LeisureDialog }.Select(spec =>
        {
            var frame = TestAssets.Black();
            TestAssets.PasteTemplate(frame, spec);
            return new FishingRecognizer(TestAssets.Catalog).Recognize(frame, 0).Dialog!.Kind;
        });
        Assert.Equal(kinds.Order(), dialogs.Order());
    }
}

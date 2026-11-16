using MabiTool.Contracts;

namespace MabiTool.ModuleSdk;

public sealed record ModuleDefinition(
    string Id, Version Version, Version MinimumHostApi, Version MaximumHostApiExclusive,
    IReadOnlyList<ModuleDependency> Dependencies, bool Implemented = true, bool EnabledByDefault = true,
    int MaximumConcurrentDemands = 8, int IdleRetentionSeconds = 30,
    string SettingsSchema = """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false}""",
    IReadOnlyList<ModuleRiskBehavior>? HighRiskBehaviors = null,
    IReadOnlyDictionary<string, string>? DisplayNames = null, IReadOnlyDictionary<string, string>? Descriptions = null,
    bool Essential = false)
{
    public ModuleRisk Risk => new(HighRiskBehaviors is { Count: > 0 } ? ModuleRiskLevel.High : ModuleRiskLevel.Low,
        (HighRiskBehaviors ?? []).Select(value => (OpenEnum<ModuleRiskBehavior>)value).ToArray());
}

/// <summary>工厂仅在首次有效需求时调用；实例销毁负责释放其全部资源。</summary>
public sealed record ModuleRegistration(ModuleDefinition Definition, Func<CancellationToken, ValueTask<IAsyncDisposable>> Factory);

public sealed class ModuleRuntimeException(string code, string message, IReadOnlyList<string>? operationIds = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public IReadOnlyList<string> OperationIds { get; } = operationIds ?? [];
}

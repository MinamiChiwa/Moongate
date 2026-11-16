using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.ModuleSdk;

public sealed class ModuleCatalog
{
    public Version HostApi { get; }
    public IReadOnlyDictionary<string, ModuleRegistration> Registrations { get; }

    public ModuleCatalog(Version hostApi, IEnumerable<ModuleRegistration> registrations)
    {
        HostApi = hostApi;
        var entries = new Dictionary<string, ModuleRegistration>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            var d = registration.Definition;
            if (!Regex.IsMatch(d.Id, "^[a-z][a-z0-9-]{0,63}$") ||
                d.MaximumConcurrentDemands < 1 || d.IdleRetentionSeconds is < 0 or > 86400 ||
                d.MinimumHostApi >= d.MaximumHostApiExclusive)
                throw new ArgumentException("模块清单无效：" + d.Id);
            if (d.Dependencies.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != d.Dependencies.Count ||
                d.Dependencies.Any(x => !Version.TryParse(x.MinimumVersion, out _)))
                throw new ArgumentException("模块依赖无效：" + d.Id);
            if (d.HighRiskBehaviors?.Any(value => !Enum.IsDefined(value)) == true)
                throw new ArgumentException("模块风险行为无效：" + d.Id);
            using var schema = System.Text.Json.JsonDocument.Parse(d.SettingsSchema);
            if (schema.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new ArgumentException("模块设置 Schema 必须为对象：" + d.Id);
            var immutable = registration with
            {
                Definition = d with
                {
                    Dependencies = Array.AsReadOnly(d.Dependencies.ToArray()),
                    HighRiskBehaviors = Array.AsReadOnly((d.HighRiskBehaviors ?? []).Distinct().Order().ToArray())
                }
            };
            if (!entries.TryAdd(d.Id, immutable))
                throw new ArgumentException("模块 ID 重复：" + d.Id);
        }
        Registrations = new ReadOnlyDictionary<string, ModuleRegistration>(entries);
    }

    public IReadOnlyList<ModuleProblem> GetProblems(string id) => Visit(id, new HashSet<string>(StringComparer.Ordinal));

    private List<ModuleProblem> Visit(string id, HashSet<string> path)
    {
        var problems = new List<ModuleProblem>();
        if (!Registrations.TryGetValue(id, out var registration))
            return [new("module.dependencyMissing", "缺少依赖模块：" + id, id)];
        if (!path.Add(id))
            return [new("module.dependencyCycle", "模块依赖形成循环：" + id, id)];
        var d = registration.Definition;
        if (!d.Implemented) problems.Add(new("module.notImplemented", "模块等待实现"));
        if (HostApi < d.MinimumHostApi || HostApi >= d.MaximumHostApiExclusive)
            problems.Add(new("module.hostApiIncompatible", "宿主 API 版本超出兼容范围"));
        foreach (var dependency in d.Dependencies)
        {
            if (Registrations.TryGetValue(dependency.Id, out var target) &&
                target.Definition.Version < Version.Parse(dependency.MinimumVersion))
                problems.Add(new("module.dependencyVersionIncompatible", "依赖版本低于要求", dependency.Id));
            problems.AddRange(Visit(dependency.Id, path));
        }
        path.Remove(id);
        return problems.Distinct().ToList();
    }
}

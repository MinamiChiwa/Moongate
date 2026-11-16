using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Runtime.Tests;

public sealed class ModuleRiskTests
{
    [Theory]
    [InlineData(ModuleRiskBehavior.MemoryRead)]
    [InlineData(ModuleRiskBehavior.MemoryInjection)]
    [InlineData(ModuleRiskBehavior.GameFileModification)]
    [InlineData(ModuleRiskBehavior.GamePacketSend)]
    public async Task DeclaredHighRiskBehaviorIsReportedWithoutStartingTheModule(ModuleRiskBehavior behavior)
    {
        var starts = 0;
        var registration = Register([behavior], () => starts++);
        await using var runtime = new ModuleRuntime(new(new(1, 0), [registration]));
        var risk = Assert.Single((await runtime.SnapshotAsync()).Items).Risk!;
        Assert.Equal(ModuleRiskLevel.High, risk.Level.KnownValue);
        Assert.Equal(behavior, Assert.Single(risk.HighRiskBehaviors).KnownValue);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task ModuleWithoutHighRiskBehaviorIsLowRisk()
    {
        await using var runtime = new ModuleRuntime(new(new(1, 0), [Register([])]));
        var risk = Assert.Single((await runtime.SnapshotAsync()).Items).Risk!;
        Assert.Equal(ModuleRiskLevel.Low, risk.Level.KnownValue);
        Assert.Empty(risk.HighRiskBehaviors);
    }

    [Fact]
    public async Task CatalogFreezesRiskDeclarationsAndRemovesDuplicateReasons()
    {
        var behaviors = new List<ModuleRiskBehavior> { ModuleRiskBehavior.MemoryRead, ModuleRiskBehavior.MemoryRead };
        var catalog = new ModuleCatalog(new(1, 0), [Register(behaviors)]);
        behaviors.Clear();
        await using var runtime = new ModuleRuntime(catalog);
        var risk = Assert.Single((await runtime.SnapshotAsync()).Items).Risk!;
        Assert.Equal(ModuleRiskLevel.High, risk.Level.KnownValue);
        Assert.Single(risk.HighRiskBehaviors);
    }

    [Fact]
    public void InvalidRiskDeclarationIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new ModuleCatalog(new(1, 0), [Register([(ModuleRiskBehavior)999])]));
    }

    private static ModuleRegistration Register(IReadOnlyList<ModuleRiskBehavior> behaviors, Action? started = null) => new(
        new("sample", new(0, 1, 0), new(1, 0), new(2, 0), [], HighRiskBehaviors: behaviors),
        _ => { started?.Invoke(); return ValueTask.FromResult<IAsyncDisposable>(new Resource()); });

    private sealed class Resource : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

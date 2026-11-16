using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

internal static class DataMaintenanceProbe
{
    private sealed record Configuration(IReadOnlyList<DatabaseMigration> Migrations, IReadOnlyList<StorageDataDefinition> Data);
    public static async Task<int> RunAsync(string bootstrap, string originalRoot, string marker, string point)
    {
        var configuration = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(Path.Combine(bootstrap, "data-maintenance-probe.json")), ContractJson.Options)!;
        await StorageMaintenanceCoordinator.RecoverCoreAsync(new(bootstrap, false, DataRoot: originalRoot), configuration.Migrations,
            configuration.Data, TimeProvider.System, VolumeSpaceReservations.Shared, default, async at =>
            {
                if (at != point) return;
                File.WriteAllText(marker, at);
                await Task.Delay(Timeout.InfiniteTimeSpan);
            });
        return 0;
    }
}

using MabiTool.Host;
using MabiTool.Infrastructure;
using MabiTool.Host.Updates;
using MabiTool.Host.Color;

// 常规宿主模式下记录监听端口与是否已开始监听：启动失败按端口、数据目录与未知错误分别给出原因与退出码（HostStartupFailure）。
var hostMode = false;
var started = false;
var port = 0;
try
{
    if (args.Length > 0 && args[0] == "--initial-install-health")
    {
        Environment.ExitCode = await InitialInstallProgram.RunAsync(args); return;
    }
    if (args.Length > 0 && args[0] is "--update-preflight" or "--update-candidate" or "--update-rollback-health")
    {
        try { await UpdateCandidateProgram.RunAsync(args); }
        catch (Exception error)
        {
            if (args.Length == 6)
                await MabiTool.Infrastructure.Updates.UpdateFailureLog.RecordAsync(
                    await MabiTool.Infrastructure.Updates.UpdateInstallIntent.ReadAsync(args[1], args[3]), error, args[0],
                    error is UpdateHostHealthException health ? health.Code : null);
            throw;
        }
        return;
    }
    if (args.Length == 2 && args[0] == "--print-install-target")
    {
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(BackendApplication.InstallationTarget(args[1]), MabiTool.Contracts.ContractJson.Options));
        return;
    }
    hostMode = true;
    var mock = args.Contains("--mock", StringComparer.Ordinal);
    if (args.Count(x => x == "--mock") > 1) throw new ArgumentException("--mock 不能重复");
    var requested = StorageOptions.FromArguments(args.Where(x => x != "--mock").ToArray());
    // 工具根布局的程序文件夹被搬移、改名、复制或换用户后，先接管当前位置（权限与登记的路径），再打开数据根。
    var adopted = requested.LocatedByToolRoot && !mock ? DataRootLease.AdoptToolRootLayout(requested) : null;
    if (adopted?.PreviousDataRoot is { } previousRoot) Console.WriteLine($"程序文件夹位置已变化：数据从 {previousRoot} 改为在当前位置打开");
    if (adopted?.AccessRepaired == true) Console.WriteLine("程序文件夹里的数据目录已改为当前 Windows 用户独占");
    // 全部运行时文件位于工具根；只有工具根尚未初始化时才检查旧默认位置并一次性迁入。
    var imported = requested.UsesToolRootDefaults && !mock ? DataRootLease.ImportLegacyLayout(requested, DataRootLease.LegacyBootstrapDirectory) : null;
    var storage = StorageMaintenanceCoordinator.StartupOptions(requested);
    InitialInstallProgram.RequireCompleted(storage);
    var updateGate = MabiTool.Infrastructure.Updates.UpdateInstallGate.Find(storage);
    if (updateGate is not null) await MabiTool.Infrastructure.Updates.UpdateLaunchSupport.RecoverAsync(updateGate.InstallationRoot);
    var options = LocalApiOptions.FromEnvironment(storage.PortableRoot);
    port = options.Port;
    if (mock && storage.PortableRoot is not null)
        options = options with { RuntimeDirectory = Path.Combine(storage.PortableRoot, "runtime") };
    do
    {
        started = false;
        await StorageMaintenanceCoordinator.RecoverAsync(storage, BackendApplication.DatabaseMigrations, BackendApplication.StorageData);
        await using (var application = BackendApplication.Build(options, storage: storage, mock: mock,
            updateInstallation: UpdateInstallationOptions.FromEnvironment(), colorPreviewOptions: mock ? null : ColorPreviewService.FromEnvironment,
            observationOptions: mock ? null : paths => paths is null ? null : MabiTool.Host.Game.GameObservationService.FromEnvironment(paths)(),
            gameAutomation: !mock))
        {
            await application.StartAsync();
            started = true;
            if (imported is not null)
            {
                application.Services.GetRequiredService<DiagnosticTrace>().Emit(DiagnosticEvents.LegacyDataImported,
                    new DiagnosticFields().Count("files", imported.Files).Count("bytes", imported.Bytes));
                Console.WriteLine($"已从旧位置迁入 {imported.Files} 个文件；旧目录 {imported.SourceBootstrap} 与 {imported.SourceDataRoot} 保持原样，确认无误后可手动清理");
                imported = null;
            }
            await application.WaitForShutdownAsync();
        }
    } while (StorageMaintenanceCoordinator.NeedsRecovery(storage));
}
catch (StorageException error)
{
    Console.Error.WriteLine($"{error.Code}: {error.Message}");
    Environment.ExitCode = 3;
}
catch (ArgumentException error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 2;
}
catch (MabiTool.Infrastructure.Updates.UpdatePlanException error)
{
    Console.Error.WriteLine(error.Code);
    Environment.ExitCode = 4;
}
catch (UpdateHostHealthException error)
{
    Console.Error.WriteLine(error.Code);
    Environment.ExitCode = 4;
}
catch (Exception error) when (!hostMode && error is IOException or UnauthorizedAccessException)
{
    // 安装与更新检查模式：文件访问与权限失败按数据目录不可用结束。
    Console.Error.WriteLine($"storage.unavailable: 程序文件夹里的数据无法访问：{error.Message}");
    Environment.ExitCode = HostStartupFailure.StorageExitCode;
}
catch (Exception error) when (hostMode)
{
    // 启动阶段：端口被占用或不可用、文件访问与权限失败各有原因码与退出码；分不清的写出原始异常类型与消息。
    var failure = await HostStartupFailure.DescribeAsync(error, port, started);
    Console.Error.WriteLine($"{failure.Code}: {failure.Message}");
    if (failure.ExitCode == HostStartupFailure.UnknownExitCode) Console.Error.WriteLine(error);
    Environment.ExitCode = failure.ExitCode;
}

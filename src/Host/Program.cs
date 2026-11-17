using MabiTool.Host;
using MabiTool.Infrastructure;
using MabiTool.Host.Updates;
using MabiTool.Host.Color;

// 游戏窗口绑定、截图与鼠标坐标统一用物理像素：在任何窗口与线程创建前声明每显示器 DPI 感知 v2（WindowsDpiAwareness）。
if (OperatingSystem.IsWindows()) MabiTool.Platform.Windows.WindowsDpiAwareness.EnablePerMonitorV2();
// 记录监听端口与是否已开始监听：启动失败按端口、数据目录与未知错误分别给出原因与退出码（HostStartupFailure）。
var started = false;
var port = 0;
try
{
    // 目录包前端拉起宿主时传入自己的进程号：前端结束后宿主随之按正常停止流程退出。
    var (hostArgs, ownerRequested, owner) = OwnerProcess.Extract(args);
    var mock = hostArgs.Contains("--mock", StringComparer.Ordinal);
    if (hostArgs.Count(x => x == "--mock") > 1) throw new ArgumentException("--mock 不能重复");
    var requested = StorageOptions.FromArguments(hostArgs.Where(x => x != "--mock").ToArray());
    // 工具根布局的程序文件夹被搬移、改名、复制或换用户后，先接管当前位置（权限与登记的路径），再打开数据根。
    var adopted = requested.LocatedByToolRoot && !mock ? DataRootLease.AdoptToolRootLayout(requested) : null;
    if (adopted?.PreviousDataRoot is { } previousRoot) Console.WriteLine($"程序文件夹位置已变化：数据从 {previousRoot} 改为在当前位置打开");
    if (adopted?.AccessRepaired == true) Console.WriteLine("程序文件夹里的数据目录已改为当前 Windows 用户独占");
    // 全部运行时文件位于工具根；只有工具根尚未初始化时才检查旧默认位置并一次性迁入。
    var imported = requested.UsesToolRootDefaults && !mock ? DataRootLease.ImportLegacyLayout(requested, DataRootLease.LegacyBootstrapDirectory) : null;
    var storage = StorageMaintenanceCoordinator.StartupOptions(requested);
    // 解压版（目录包且带发行身份、位置由工具根推出）才启用更新；更新回退时先恢复安装前的数据库。
    var portable = requested.UsesToolRootDefaults && !mock ? PortableInstallation.Detect(AppContext.BaseDirectory) : null;
    await PortableUpdateStartup.RestoreDatabaseIfRequestedAsync(portable, storage);
    var options = LocalApiOptions.FromEnvironment(storage.PortableRoot);
    port = options.Port;
    if (mock && storage.PortableRoot is not null)
        options = options with { RuntimeDirectory = Path.Combine(storage.PortableRoot, "runtime") };
    // 未指定端口时默认端口被其他程序占用或被系统拒绝，改由系统分配空闲端口；占用者是 Moongate 宿主时按端口冲突结束。
    var (resolved, portFallback) = await HostStartupFailure.ResolveDefaultPortAsync(options);
    options = resolved;
    do
    {
        started = false;
        await StorageMaintenanceCoordinator.RecoverAsync(storage, BackendApplication.DatabaseMigrations, BackendApplication.StorageData);
        await using (var application = BackendApplication.Build(options, storage: storage, mock: mock,
            portableUpdate: portable, ownerProcess: owner, colorPreviewOptions: mock ? null : ColorPreviewService.FromEnvironment,
            observationOptions: mock ? null : paths => paths is null ? null : MabiTool.Host.Game.GameObservationService.FromEnvironment(paths)(),
            gameAutomation: !mock))
        {
            await application.StartAsync();
            started = true;
            if (ownerRequested) _ = OwnerProcess.WatchAsync(owner, application.Lifetime);
            PortableUpdateStartup.ReportReady(portable);
            if (portFallback is not null)
            {
                var actual = new Uri(application.Urls.Single()).Port;
                application.Services.GetRequiredService<DiagnosticTrace>().Emit(DiagnosticEvents.HostPortFallback,
                    new DiagnosticFields().Value("reason", portFallback.Reason).Count("requestedPort", portFallback.RequestedPort).Count("port", actual));
                Console.WriteLine($"默认端口 {portFallback.RequestedPort} 不可用（{(portFallback.Reason == "occupied" ? "被其他程序占用" : "系统拒绝监听")}），已改用本机端口 {actual}");
            }
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
catch (MabiTool.Infrastructure.Updates.UpdatePackageException error)
{
    // 更新回退所需的数据库备份描述缺失或无效：保留事务日志，由用户按手动升级步骤处理。
    Console.Error.WriteLine($"{error.Code}: {error.Message}");
    Environment.ExitCode = 4;
}
catch (Exception error)
{
    // 启动阶段：端口被占用或不可用、文件访问与权限失败各有原因码与退出码；分不清的写出原始异常类型与消息。
    var failure = await HostStartupFailure.DescribeAsync(error, port, started);
    Console.Error.WriteLine($"{failure.Code}: {failure.Message}");
    if (failure.ExitCode == HostStartupFailure.UnknownExitCode) Console.Error.WriteLine(error);
    Environment.ExitCode = failure.ExitCode;
}

using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;

try
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    if (args.Length == 5 && args[0] == "--continue-updater")
    {
        var predecessor = System.Text.Json.JsonSerializer.Deserialize<UpdatePeerIdentity>(args[4], UpdateHandoffProtocol.Json) ?? throw UpdateHandoffProtocol.Invalid();
        await UpdaterBootstrapExchange.RunLauncherAsync(typeof(Program).Assembly.Location, args[1], args[2], args[3], predecessor);
        Console.WriteLine("updaterCompleted"); return;
    }
    if (args.Length is < 2 or > 3 || args[0] is not ("--launch" or "--recover") || args.Length == 3 && args[2] != "--initialize-data")
        throw new ArgumentException("支持 --launch <安装根> [--initialize-data] 或 --recover <安装根>");
    var root = StoragePaths.ValidateLocal(args[1]);
    var storage = UpdateLaunchSupport.StorageFromConfiguration(root, args.Length == 3);
    await UpdateLaunchSupport.RecoverAsync(root);
    if (args[0] == "--launch") using (await UpdateLaunchSupport.StartHostAsync(root, storage)) { }
    Console.WriteLine(args[0] == "--launch" ? "started" : "recovered");
}
catch (Exception error) when (error is UpdatePlanException or StorageException or FileTreeException or TrustValidationException or
    IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or OperationCanceledException or TimeoutException)
{
    if (args.Length == 5 && args[0] == "--continue-updater")
    {
        try { await UpdateFailureLog.RecordAsync(await UpdateInstallIntent.ReadAsync(args[1], args[2]), error, "launcherStart"); }
        catch (Exception failure) when (failure is UpdatePlanException or StorageException or IOException or System.Text.Json.JsonException) { }
    }
    Console.Error.WriteLine(error switch { UpdatePlanException e => e.Code, StorageException e => e.Code, FileTreeException e => e.Code,
        TrustValidationException e => e.Code, ArgumentException => "request.invalid", _ => "update.recoveryRequired" });
    Environment.ExitCode = 4;
}

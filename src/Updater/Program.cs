using System.Diagnostics;
using System.Text.Json;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;

try
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    var version = typeof(Program).Assembly.GetName().Version!;
    if (args is ["--version"]) { Console.WriteLine(version.ToString(3)); return; }
    if (args.Length == 2 && args[0] == "--initial-install")
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var initialCompleted = await new InitialInstallEngine(new Version(version.ToString(3)), typeof(Program).Assembly.Location).RunAsync(args[1], cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, phase = initialCompleted.Phase,
                installationRoot = initialCompleted.Request.InstallationRoot, releaseId = initialCompleted.Version!.ReleaseId,
                launcher = UpdatePaths.Local(initialCompleted.StableRoot, "components/launcher/MabiTool.Launcher.dll"),
                runtime = UpdatePaths.Local(initialCompleted.VersionRoot, "components/dotnet-runtime/dotnet.exe"),
                launchArguments = new[] { "--launch", initialCompleted.Request.InstallationRoot },
                dataRoot = initialCompleted.Request.DataRoot }, UpdateHandoffProtocol.Json));
        }
        finally { Console.CancelKeyPress -= cancel; }
        return;
    }
    UpdateInstallIntent intent; string hash; var launch = true; string? resumedRoot = null;
    if (args.Length == 6 && args[0] == "--handoff")
    {
        intent = await UpdateInstallIntent.ReadAsync(args[1], args[3]); hash = args[3];
        if (intent.TransactionId != args[2]) throw UpdateHandoffProtocol.Invalid();
        var parent = JsonSerializer.Deserialize<UpdatePeerIdentity>(args[5], UpdateHandoffProtocol.Json) ?? throw UpdateHandoffProtocol.Invalid();
        if (parent != intent.Parent) throw UpdateHandoffProtocol.Invalid();
        using var pinned = new PinnedUpdatePeer(parent);
        using var parentProcess = Process.GetProcessById(parent.ProcessId);
        _ = parentProcess.Handle;
        using (var connection = await UpdateHandoffConnection.ConnectAsync(args[4], parent, intent.TransactionId))
        {
            var transfer = new UpdateIntentTransfer(intent.TransactionId, hash);
            if (await connection.ReceiveAsync<UpdateIntentTransfer>("intent") != transfer) throw UpdateHandoffProtocol.Invalid();
            await connection.SendAsync("accepted", transfer);
            if (await connection.ReceiveAsync<UpdateIntentTransfer>("release") != transfer) throw UpdateHandoffProtocol.Invalid();
            await connection.SendAsync("released", transfer);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await parentProcess.WaitForExitAsync(deadline.Token);
    }
    else if (args.Length == 5 && args[0] == "--resume")
    {
        intent = await UpdateInstallIntent.ReadAsync(args[1], args[2]); hash = args[2];
        var launcher = JsonSerializer.Deserialize<UpdatePeerIdentity>(args[4], UpdateHandoffProtocol.Json) ?? throw UpdateHandoffProtocol.Invalid();
        var record = await UpdaterBootstrapExchange.AcceptResumeAsync(typeof(Program).Assembly.Location, version, intent, hash, args[3], launcher);
        launch = record.LaunchHost; resumedRoot = UpdaterBootstrap.VersionRoot(intent, record);
    }
    else if (args.Length is 2 or 3 && args[0] == "--recover" && (args.Length == 2 || args[2] == "--no-launch"))
    {
        intent = await UpdateInstallIntent.ReadAsync(args[1]);
        var gate = UpdateInstallGate.Find(intent.Storage);
        hash = gate?.IntentSha256 ?? UpdateInstallIntent.Hash(JsonSerializer.SerializeToUtf8Bytes(intent, UpdateHandoffProtocol.Json));
        _ = await UpdateInstallIntent.ReadAsync(args[1], hash);
        await UpdateLaunchSupport.WaitForParentExitAsync(intent.Parent);
        launch = args.Length == 2;
    }
    else throw new ArgumentException("支持 --version、--initial-install <请求文件>、--handoff、--resume 或 --recover <安装根> [--no-launch]");
    var updaterRoot = resumedRoot ?? (args[0] == "--recover" ? await UpdateLaunchSupport.RecoveryUpdaterRootAsync(intent) : intent.PreviousRoot);
    if (args[0] == "--handoff") await UpdateLaunchSupport.VerifyUpdaterAsync(updaterRoot, intent.Previous.Files);
    var expectedAssembly = UpdatePaths.Local(updaterRoot, "components/updater/MabiTool.Updater.dll");
    if (!UpdatePaths.SameLocalFile(typeof(Program).Assembly.Location, expectedAssembly))
        throw new UpdatePlanException("update.updaterUnavailable");
    if (intent.Plan.RequiredUpdaterVersion is { } required && version < required &&
        !File.Exists(UpdatePaths.Local(intent.StateRoot, "completed-installs/" + intent.TransactionId + ".json")))
    {
        try { await UpdaterBootstrapExchange.HandOffAsync(intent, hash, launch); Console.WriteLine("updaterHandedOff"); }
        catch (Exception error) when (error is UpdatePlanException or FileTreeException or TrustValidationException or IOException or OperationCanceledException)
        {
            var failure = error is UpdatePlanException e ? e.Code : error is TrustValidationException t ? t.Code :
                error is FileTreeException f ? f.Code : "update.updaterUnavailable";
            _ = await new UpdateInstallEngine(intent, hash, version).RunAsync(true, initialFailure: failure);
            throw;
        }
        return;
    }
    var completed = await new UpdateInstallEngine(intent, hash, version).RunAsync(args[0] == "--recover");
    Console.WriteLine(completed.Outcome);
    if (launch) using (await UpdateLaunchSupport.StartHostAsync(intent.InstallationRoot, intent.Storage)) { }
}
catch (Exception error) when (error is UpdatePlanException or StorageException or OperationException or TrustValidationException or FileTreeException or
    ActivityBarrierException or IOException or UnauthorizedAccessException or ArgumentException or JsonException or OperationCanceledException or TimeoutException)
{
    Console.Error.WriteLine(error switch { UpdatePlanException e => e.Code, StorageException e => e.Code, OperationException e => e.Code,
        TrustValidationException e => e.Code, FileTreeException e => e.Code, ActivityBarrierException e => e.Code,
        ArgumentException => "request.invalid", _ => "update.recoveryRequired" });
    if (error.Data["updatePhase"] is string phase) Console.Error.WriteLine("phase=" + phase);
    if (error.Data["initialInstallPhase"] is string initialPhase) Console.Error.WriteLine("phase=" + initialPhase);
    if (error.Data["peerCheck"] is string peerCheck) Console.Error.WriteLine("peerCheck=" + peerCheck);
    if (error.Data["peerOpenError"] is int peerOpenError) Console.Error.WriteLine("peerOpenError=" + peerOpenError);
    if (error.Data["transactionCallback"] is string callback) Console.Error.WriteLine("transactionCallback=" + callback);
    if (error.Data["transactionElapsedMs"] is long elapsed) Console.Error.WriteLine("transactionElapsedMs=" + elapsed);
    Environment.ExitCode = 4;
}

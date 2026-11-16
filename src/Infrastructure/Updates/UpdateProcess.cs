using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MabiTool.Infrastructure.Updates;

[SupportedOSPlatform("windows")]
internal static class UpdateProcess
{
    internal static Process Start(string versionRoot, string component, string assembly, string runtimeMode,
        IEnumerable<string> arguments, bool captureOutput = false, IReadOnlyDictionary<string, string>? environment = null)
    {
        var entry = UpdatePaths.Local(versionRoot, $"components/{component}/{assembly}." + (runtimeMode == "self-contained" ? "exe" : "dll"));
        if (runtimeMode is not ("self-contained" or "framework-dependent") || !File.Exists(entry))
            throw new UpdatePlanException(component == "updater" ? "update.updaterUnavailable" : "update.installTargetInvalid");
        var bundledRuntime = UpdatePaths.Local(versionRoot, "components/dotnet-runtime");
        var executable = runtimeMode == "self-contained" ? entry :
            Directory.Exists(bundledRuntime) ? BundledDotnet(bundledRuntime) : Dotnet();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(entry)!,
            RedirectStandardOutput = captureOutput, RedirectStandardError = captureOutput
        };
        if (runtimeMode == "framework-dependent") start.ArgumentList.Add(entry);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null) foreach (var item in environment) start.Environment[item.Key] = item.Value;
        if (runtimeMode == "framework-dependent" && Directory.Exists(bundledRuntime))
        {
            start.Environment["DOTNET_ROOT"] = bundledRuntime;
            start.Environment["DOTNET_ROOT_X64"] = bundledRuntime;
            start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        }
        return Process.Start(start) ?? throw new UpdatePlanException("update.installationFailed");
    }

    private static string BundledDotnet(string runtimeRoot)
    {
        var executable = StoragePaths.ValidateLocal(Path.Combine(runtimeRoot, "dotnet.exe"));
        if (!File.Exists(executable)) throw new UpdatePlanException("update.updaterUnavailable");
        return executable;
    }

    internal static string Dotnet()
    {
        var path = Environment.ProcessPath;
        if (path is not null && Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return StoragePaths.ValidateLocal(path);
        var host = Path.GetFullPath("../../../dotnet.exe", RuntimeEnvironment.GetRuntimeDirectory());
        if (!File.Exists(host)) throw new UpdatePlanException("update.updaterUnavailable");
        return StoragePaths.ValidateLocal(host);
    }

    internal static async Task<string> ReadBoundedAsync(StreamReader reader, int limit = 65536)
    {
        var text = new System.Text.StringBuilder(); var buffer = new char[2048];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            if (text.Length < limit) text.Append(buffer, 0, Math.Min(read, limit - text.Length));
        return text.ToString();
    }

    internal static void Terminate(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
    }
}

internal sealed record UpdateIntentTransfer(string TransactionId, string IntentSha256);
internal sealed record UpdatePreflightRequest(string SnapshotId, long Revision);
internal sealed record UpdateRollbackHealthRequest(string Challenge, InstallHealthTarget Target);

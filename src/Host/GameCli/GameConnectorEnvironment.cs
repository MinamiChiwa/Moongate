using MabiTool.Providers.TwCli;
using MabiTool.Platform.Windows;

namespace MabiTool.Host.GameCli;

/// <summary>连接器依赖的本机环境：设置/备份目录（数据根内）、台服偏好目录、默认游戏目录、进程探测与命令行来源。测试注入替身，真实宿主用 <see cref="ForWindows"/>。</summary>
public sealed record GameConnectorEnvironment(
    string? SettingsDirectory,
    string PreferencesDirectory,
    IReadOnlyList<string> DefaultGameDirectories,
    IGameProcessProbe Probe,
    Func<int, string?> ProcessImagePath,
    ITwCliRunner Runner,
    Func<string?> PackageCliPath,
    Func<IReadOnlyList<string>> KoreanCliCandidates,
    string? UnavailableReason = null,
    string? ExpectedCliSha256 = null)
{
    public const string PackagePathVariable = "MABITOOL_GAME_CLI_EXECUTABLE";

    /// <summary>不使用连接器的宿主（模拟宿主、测试宿主、非 Windows）：状态为不可用并给出原因。</summary>
    public static GameConnectorEnvironment Unsupported(string reason) => new(null, string.Empty, [], new NoGameProbe(),
        _ => null, new TwCliProcessRunner(), () => null, () => [], reason);

    public static GameConnectorEnvironment ForWindows(string? settingsDirectory)
    {
        if (!OperatingSystem.IsWindows()) return Unsupported("platform.unsupported");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fixedDrives = DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady).Select(x => x.RootDirectory.FullName).ToArray();
        return new(settingsDirectory, Path.Combine(profile, "AppData", "LocalLow", "devCAT", "瑪奇 Mobile"),
            [.. fixedDrives.Select(root => Path.Combine(root, "Nexon", "Library", "MabinogiMobile_TW", "appdata"))],
            new WindowsGameProcessProbe(), WindowsProcessAccess.ImagePath, new TwCliProcessRunner(),
            PackagePath, () => [.. fixedDrives.Select(root => Path.Combine(root, "Nexon", "MabinogiMobile", TwCliFile.FileName))]);
    }

    private static string? PackagePath() => Environment.GetEnvironmentVariable(PackagePathVariable) is { Length: > 0 } value ? value : null;

    private sealed class NoGameProbe : IGameProcessProbe
    {
        public GameProcessInfo Probe(string gameDirectory) => new(GameProcessState.NotRunning);
    }
}

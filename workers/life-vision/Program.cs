using MabiTool.Contracts;
using MabiTool.Infrastructure.Workers;
using MabiTool.LifeVision.Worker;

if (!OperatingSystem.IsWindows()) { Environment.ExitCode = 2; return; }
try
{
    using var source = new PrintWindowFrameSource();
    await using var peer = await WorkerPeerConnection.ConnectFromEnvironmentAsync(LifeVisionProfiles.WorkerId);
    await LifeVisionApplication.RunAsync(source, peer, SystemVisionClock.Instance);
}
catch { Environment.ExitCode = 3; }

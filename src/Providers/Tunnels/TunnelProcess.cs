using MabiTool.Platform.Windows;

namespace MabiTool.Providers.Tunnels;

public interface ITunnelProcess : IAsyncDisposable
{
    TextReader StandardOutput { get; }
    TextReader StandardError { get; }
    Task<int> WaitForExitAsync(CancellationToken ct = default);
    void Terminate();
}

public interface ITunnelProcessFactory
{
    ITunnelProcess Start(string executable, IReadOnlyList<string> arguments);
}

public sealed class WindowsTunnelProcessFactory : ITunnelProcessFactory
{
    public ITunnelProcess Start(string executable, IReadOnlyList<string> arguments) =>
        new Child(WindowsOwnedChildProcess.Start(executable, arguments));
    private sealed class Child(WindowsOwnedChildProcess process) : ITunnelProcess
    {
        public TextReader StandardOutput => process.StandardOutput;
        public TextReader StandardError => process.StandardError;
        public Task<int> WaitForExitAsync(CancellationToken ct = default) => process.WaitForExitAsync(ct);
        public void Terminate() => process.Terminate();
        public ValueTask DisposeAsync() => process.DisposeAsync();
    }
}

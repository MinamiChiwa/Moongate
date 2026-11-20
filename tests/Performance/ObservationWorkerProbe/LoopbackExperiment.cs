using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Observation.Worker;

namespace MabiTool.ObservationWorkerProbe;

/// <summary>
/// 读取方式的本机回环实测：本进程内自建 127.0.0.1 收发连接，Npcap 回环适配器只抓实验端口（tcp and dst port 实验端口），
/// 不涉及游戏与其他流量。按读取方式与负载分别测捕获线程 CPU、每秒唤醒次数、抓包时刻到读出的延迟与丢包。
/// </summary>
internal static class LoopbackExperiment
{
    private sealed record Load(string Name, int PacketsPerSecond, int PayloadBytes);

    public static JsonNode Run(int seconds)
    {
        var device = CaptureChannels.Devices().FirstOrDefault(value => value.Kind == "loopback")
            ?? throw new InvalidOperationException("没有 Npcap 回环适配器");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient { NoDelay = true }; client.Connect(IPAddress.Loopback, port);
        using var server = listener.AcceptTcpClient();
        var drain = new Thread(() =>
        {
            var buffer = new byte[1 << 16]; var stream = server.GetStream();
            try { while (stream.Read(buffer) > 0) { } } catch (IOException) { } catch (ObjectDisposedException) { }
        }) { IsBackground = true };
        drain.Start();
        var filter = "tcp and dst port " + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var buffer = NativeCaptureBoundary.DeviceBufferBytes;
        CaptureReadConfig[] configs = [new(CaptureReadMode.Poll, buffer, 0, 10), new(CaptureReadMode.Immediate, buffer),
            new(CaptureReadMode.Immediate, buffer, CoalesceMs: 10), new(CaptureReadMode.MinToCopy, buffer, 8 * 1024, 20)];
        Load[] loads = [new("idle", 0, 0), new("light", 100, 200), new("heavy", 1300, 1230)];
        var results = new JsonArray();
        foreach (var load in loads)
            foreach (var config in configs)
                results.Add(Measure(device.Name, config, filter, client.GetStream(), load, seconds));
        client.Close();
        return new JsonObject
        {
            ["device"] = "Npcap 回环适配器（只抓实验端口）",
            ["secondsPerCase"] = seconds,
            ["cases"] = results
        };
    }

    private static JsonNode Measure(string device, CaptureReadConfig config, string filter, NetworkStream stream, Load load, int seconds)
    {
        var error = new StringBuilder(256);
        var (handle, readEvent, issue) = CaptureChannels.OpenReadHandle(device, config, filter, error, out _);
        if (issue is not null) throw new InvalidOperationException(issue);
        var mode = config.Mode != CaptureReadMode.Poll && readEvent == 0 ? "poll(fallback)"
            : config.CoalesceMs > 0 ? config.Mode + "+coalesce" + config.CoalesceMs.ToString(System.Globalization.CultureInfo.InvariantCulture) : config.Mode.ToString();
        using var stop = new CancellationTokenSource();
        long sent = 0;
        var sender = new Thread(() =>
        {
            if (load.PacketsPerSecond == 0) return;
            var payload = new byte[load.PayloadBytes]; var pace = Stopwatch.StartNew();
            while (!stop.IsCancellationRequested)
            {
                var due = (long)(pace.Elapsed.TotalSeconds * load.PacketsPerSecond);
                while (sent < due) { stream.Write(payload); sent++; }
                Thread.Sleep(1);
            }
        }) { IsBackground = true };
        var latencies = new List<double>(); long packets = 0, wakes = 0;
        var events = new[] { readEvent };
        var cycles = Cycles();
        var clock = Stopwatch.StartNew();
        sender.Start();
        var limit = seconds * 1000d; var lastWake = double.NegativeInfinity;
        while (clock.Elapsed.TotalMilliseconds < limit + 200)
        {
            if (clock.Elapsed.TotalMilliseconds >= limit && !stop.IsCancellationRequested) stop.Cancel();
            wakes++;
            var wake = clock.Elapsed.TotalMilliseconds; var before = packets;
            while (CaptureChannels.ReadNext(handle, out var header, out _) == 1)
            {
                packets++;
                var micros = Marshal.ReadInt32(header) * 1_000_000L + Marshal.ReadInt32(header + 4);
                latencies.Add(((DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10 - micros) / 1000d);
            }
            if (config.CoalesceMs > 0 && packets > before && wake - lastWake < config.CoalesceMs) Thread.Sleep(config.CoalesceMs);
            lastWake = wake;
            if (mode.StartsWith("poll", StringComparison.Ordinal) || readEvent == 0) Thread.Sleep(config.TimeoutMs);
            else CaptureChannels.WaitAny(events, 1, config.Mode == CaptureReadMode.MinToCopy ? config.TimeoutMs : 500);
        }
        sender.Join();
        var elapsed = clock.Elapsed.TotalSeconds;
        var spent = Cycles() - cycles;
        var drops = CaptureChannels.Drops(handle);
        CaptureChannels.CloseHandle(handle);
        var sorted = latencies.Order().ToArray();
        double At(double q) => sorted.Length == 0 ? 0 : Math.Round(sorted[Math.Min(sorted.Length - 1, (int)(q * (sorted.Length - 1)))], 2);
        return JsonSerializer.SerializeToNode(new
        {
            load = load.Name, packetsPerSecond = load.PacketsPerSecond, payloadBytes = load.PayloadBytes, mode,
            minToCopyBytes = config.Mode == CaptureReadMode.MinToCopy ? config.MinToCopyBytes : (int?)null,
            timeoutMs = config.Mode == CaptureReadMode.Immediate ? (int?)null : config.TimeoutMs,
            seconds = Math.Round(elapsed, 2), sent, captured = packets, driverDrops = drops,
            wakesPerSecond = Math.Round(wakes / elapsed, 1),
            captureThreadMegacyclesPerSecond = Math.Round(spent / 1e6 / elapsed, 3),
            captureThreadCpuMsPerSecond = Math.Round(spent / (Megahertz * 1e3) / elapsed, 3),
            latencyMs = new { p50 = At(0.5), p95 = At(0.95), p99 = At(0.99), max = sorted.Length == 0 ? 0 : Math.Round(sorted[^1], 2) }
        })!;
    }

    /// <summary>捕获线程的 CPU 周期（QueryThreadCycleTime，含内核态）；按处理器标称频率换算为毫秒。</summary>
    private static ulong Cycles() => QueryThreadCycleTime(GetCurrentThread(), out var value) ? value : 0;
    private static readonly double Megahertz = Convert.ToDouble(Microsoft.Win32.Registry.GetValue(
        @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz", 2900) ?? 2900, System.Globalization.CultureInfo.InvariantCulture);
    [DllImport("kernel32")] private static extern nint GetCurrentThread();
    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(nint thread, out ulong cycles);
}

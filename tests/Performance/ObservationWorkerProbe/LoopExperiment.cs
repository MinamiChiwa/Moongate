using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using MabiTool.Observation.Worker;
using MabiTool.Providers.TwObservation.Combat;

namespace MabiTool.ObservationWorkerProbe;

/// <summary>
/// 生产读取循环（CaptureChannels.ReadLoop 与 ReadTap、CapturePipeline）的本机回环实测：本进程内自建 127.0.0.1 连接，Npcap 回环适配器只抓实验端口。
/// 空闲段不发送；回放段把实采样本中负载最多的一条游戏连接的字节流按原时刻经该连接发出，管线按帧头确认、解析（战斗兴趣开启）。
/// 分别报告读取线程的 CPU 周期、读取调用次数与读到的包数。
/// </summary>
internal static class LoopExperiment
{
    public static JsonNode Run(Sample sample, int seconds)
    {
        var device = CaptureChannels.Devices().First(value => value.Kind == "loopback");
        var stream = sample.Packets.GroupBy(packet => packet.Connection).OrderByDescending(group => group.Sum(packet => (long)packet.Payload.Length)).First()
            .Where(packet => packet.Payload.Length > 0).ToArray();
        var results = new JsonArray();
        foreach (var config in new[] { CaptureReadConfig.Resident.Fallback, CaptureReadConfig.Resident })
            foreach (var replay in new[] { false, true })
                results.Add(Measure(device, config, replay ? stream : [], seconds));
        return new JsonObject { ["sample"] = sample.Name, ["secondsPerCase"] = seconds, ["cases"] = results };
    }

    private static JsonNode Measure(CaptureDevice device, CaptureReadConfig config, IReadOnlyList<SamplePacket> stream, int seconds)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient { NoDelay = true }; client.Connect(IPAddress.Loopback, port);
        using var server = listener.AcceptTcpClient();
        var drain = new Thread(() =>
        {
            var buffer = new byte[1 << 16]; var input = server.GetStream();
            try { while (input.Read(buffer) > 0) { } } catch (IOException) { } catch (ObjectDisposedException) { }
        }) { IsBackground = true };
        drain.Start();
        var context = new ObservationCaptureContext([Tw20261110CombatProtocol.CombatProfile], _ => { }, _ => { });
        context.Configure(new(true, new(new string('c', 32), Tw20261110CombatProtocol.Profile, true, true, true)));
        var signature = Tw20261110CombatProtocol.StreamProfile.KnownProtocols.Where(code => code != 0).ToHashSet();
        using var pipeline = new CapturePipeline(1, signature, "loop", context, CombatProtocolLayout.Tw20261110, null, new LocalIdentityMemory(), null);
        var tap = CaptureChannels.OpenTap(device, context, config);
        if (!tap.Open || !CaptureChannels.ApplyFilter(tap, "tcp and dst port " + port.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            throw new InvalidOperationException(tap.Issue ?? "filter");
        var mode = config.Mode != CaptureReadMode.Poll && tap.Event == 0 ? "poll(fallback)" : config.Mode.ToString() + (config.CoalesceMs > 0 ? "+coalesce" + config.CoalesceMs : "");
        var table = new List<(CaptureConnection, int, int)> { (new("127.0.0.1", port, "127.0.0.1", (ushort)((IPEndPoint)client.Client.LocalEndPoint!).Port), 4242, 5) };
        long reads = 0, packets = 0; ulong cycles = 0;
        using var stop = new CancellationTokenSource();
        var loop = new Thread(() =>
        {
            var before = Cycles();
            try
            {
                CaptureChannels.ReadLoop([tap], config, Stopwatch.StartNew(), stop.Token, _ => pipeline.Refresh(table, 4242, [tap.Prearm]),
                    (index, value) => { reads++; var count = CaptureChannels.ReadTap(index, value, context, pipeline); packets += count; return count; }, pipeline);
            }
            catch (OperationCanceledException) { }
            cycles = Cycles() - before;
        }) { IsBackground = true, Name = "read-loop" };
        var clock = Stopwatch.StartNew();
        loop.Start();
        long sent = 0;
        var output = client.GetStream();
        if (stream.Count > 0)
        {
            var origin = stream[0].Ms;
            foreach (var packet in stream)
            {
                var due = packet.Ms - origin;
                if (due > seconds * 1000d) break;
                while (clock.Elapsed.TotalMilliseconds < due) Thread.Sleep(1);
                output.Write(packet.Payload); sent += packet.Payload.Length;
            }
        }
        while (clock.Elapsed.TotalMilliseconds < seconds * 1000d) Thread.Sleep(20);
        stop.Cancel(); loop.Join();
        var elapsed = clock.Elapsed.TotalSeconds;
        tap.Dispose();
        var frames = context.Diagnostics.Stream.Snapshot();
        return JsonSerializer.SerializeToNode(new
        {
            phase = stream.Count > 0 ? "replay" : "idle", mode, seconds = Math.Round(elapsed, 2), sentBytes = sent, packets, frames = frames.Frames,
            confirmedStreams = pipeline.Router.ConfirmedCount, readCallsPerSecond = Math.Round(reads / elapsed, 1),
            readThreadMegacyclesPerSecond = Math.Round(cycles / 1e6 / elapsed, 3)
        })!;
    }
    private static ulong Cycles() => QueryThreadCycleTime(GetCurrentThread(), out var value) ? value : 0;
    [DllImport("kernel32")] private static extern nint GetCurrentThread();
    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(nint thread, out ulong cycles);
}

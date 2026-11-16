using System.Net;

namespace MabiTool.RoomServer;

public sealed record RoomServerOptions(string Host = "127.0.0.1", int Port = 3132, int MaximumRooms = 1000,
    int MaximumConnections = 2048, int MaximumFrameBytes = 65536, int MaximumQueuedFrames = 128,
    int MaximumConnectionQueueBytes = 1_048_576, long MaximumQueuedBytes = 67_108_864)
{
    public void Validate()
    {
        if (!IPAddress.TryParse(Host, out _) || Port is < 0 or > 65535 || MaximumRooms is < 1 or > 1000 ||
            MaximumConnections is < 1 or > 2048 || MaximumFrameBytes is < 1024 or > 65536 ||
            MaximumQueuedFrames is < 1 or > 128 || MaximumConnectionQueueBytes < MaximumFrameBytes || MaximumConnectionQueueBytes > 1_048_576 ||
            MaximumQueuedBytes < MaximumConnectionQueueBytes || MaximumQueuedBytes > 67_108_864)
            throw new ArgumentException("房间服务监听地址或资源预算无效");
    }
    public static RoomServerOptions FromArguments(string[] arguments)
    {
        var result = new RoomServerOptions();
        for (var at = 0; at < arguments.Length; at += 2)
        {
            if (at + 1 >= arguments.Length) throw new ArgumentException("房间服务参数缺少值");
            result = arguments[at] switch
            {
                "--host" => result with { Host = arguments[at + 1] },
                "--port" when int.TryParse(arguments[at + 1], out var port) => result with { Port = port },
                _ => throw new ArgumentException("房间服务支持 --host <IP> 和 --port <端口>")
            };
        }
        result.Validate(); return result;
    }
}

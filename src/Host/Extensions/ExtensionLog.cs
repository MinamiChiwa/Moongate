using System.Text;
using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Host.Extensions;

/// <summary>
/// 一个扩展的有界日志：写入数据目录旁的 logs/extension.log，超过 1 MiB 时轮换为 extension.1.log（只保留一份），
/// 内存里另保留最后 500 行供模块页与诊断页读取。单行最多 2,000 字；每秒超过 100 行的部分丢弃并计数。
/// </summary>
public sealed class ExtensionLog
{
    public const int MaximumFileBytes = 1024 * 1024, MaximumLines = 500, MaximumMessageLength = 2000, MaximumLinesPerSecond = 100;
    private readonly string directory;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly LinkedList<ExtensionLogLine> recent = new();
    private long windowStart;
    private int windowCount;
    private long dropped;

    public ExtensionLog(string directory, TimeProvider time)
    {
        this.directory = directory;
        this.time = time;
        windowStart = time.GetTimestamp();
    }

    public long Dropped { get { lock (gate) return dropped; } }

    public void Write(string level, string source, string message)
    {
        if (message.Length > MaximumMessageLength) message = message[..MaximumMessageLength] + "…";
        message = new string(message.Select(c => char.IsControl(c) && c is not '\t' ? ' ' : c).ToArray());
        lock (gate)
        {
            if (time.GetElapsedTime(windowStart) >= TimeSpan.FromSeconds(1)) { windowStart = time.GetTimestamp(); windowCount = 0; }
            if (++windowCount > MaximumLinesPerSecond && source != "host") { dropped++; return; }
            var line = new ExtensionLogLine(time.GetUtcNow(), level, source, message);
            recent.AddLast(line);
            while (recent.Count > MaximumLines) recent.RemoveFirst();
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "extension.log");
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, ContractJson.Options) + "\n");
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > MaximumFileBytes)
                    File.Move(path, Path.Combine(directory, "extension.1.log"), overwrite: true);
                using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                file.Write(bytes);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { dropped++; }
        }
    }

    /// <summary>最后 <paramref name="count"/> 行；内存里没有时（宿主重启后）从日志文件末尾读取。</summary>
    public ExtensionLogPage Read(string id, int count = 200)
    {
        lock (gate)
        {
            IEnumerable<ExtensionLogLine> lines = recent;
            if (recent.Count == 0) lines = FromFile();
            var all = lines.ToArray();
            return new(id, all.TakeLast(count).ToArray(), all.Length > count);
        }
    }

    private IEnumerable<ExtensionLogLine> FromFile()
    {
        var result = new List<ExtensionLogLine>();
        try
        {
            var path = Path.Combine(directory, "extension.log");
            if (!File.Exists(path)) return result;
            foreach (var text in File.ReadLines(path).TakeLast(MaximumLines))
                try { if (JsonSerializer.Deserialize<ExtensionLogLine>(text, ContractJson.Options) is { } line) result.Add(line); }
                catch (JsonException) { }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return result;
    }
}

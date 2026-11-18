using System.Buffers.Binary;

namespace MabiTool.Providers.TwObservation;

/// <summary>
/// 按游戏帧头识别一段下行 TCP 负载是否是游戏协议，用于在多张网卡与回环之间判断哪一张能分帧。帧头与 <see cref="GameTcpStream"/> 相同：
/// 小端 int32 协议号（属于已知协议号集合）、小端 int32 正文长度（不超过 <see cref="TwGameProtocol.MaximumBody"/>）、1 字节编码标志（0 或 1）。
/// 负载开头即帧头且下一帧头（若落在给出的字节内）同样有效，或负载内任意位置出现两个首尾相接的有效帧头时判定为游戏协议。
/// 给出的字节可以是截取的前缀。
/// </summary>
public static class GameFrameSignature
{
    private const int HeaderBytes = 9;

    /// <summary>当前登记构建的音乐与战斗协议号合集。</summary>
    public static IReadOnlySet<int> RegisteredProtocols { get; } =
        TwGameProtocol.KnownProtocols.Concat(Combat.Tw20261110CombatProtocol.StreamProfile.KnownProtocols).Where(code => code != 0).ToHashSet();

    public static bool Matches(ReadOnlySpan<byte> payload, IReadOnlySet<int> known)
    {
        if (Length(payload, 0, known) is { } first)
        {
            var next = HeaderBytes + (long)first;
            if (next + HeaderBytes > payload.Length) return true;
            if (Length(payload, (int)next, known) is not null) return true;
        }
        for (var at = 1; at + HeaderBytes <= payload.Length; at++)
        {
            if (Length(payload, at, known) is not { } length) continue;
            var next = at + HeaderBytes + (long)length;
            if (next + HeaderBytes <= payload.Length && Length(payload, (int)next, known) is not null) return true;
        }
        return false;
    }

    private static int? Length(ReadOnlySpan<byte> data, int offset, IReadOnlySet<int> known)
    {
        if (offset < 0 || data.Length - offset < HeaderBytes) return null;
        var code = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        var length = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 4)..]);
        return code != 0 && known.Contains(code) && length >= 0 && length <= TwGameProtocol.MaximumBody && data[offset + 8] <= 1 ? length : null;
    }
}

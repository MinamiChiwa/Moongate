using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MabiTool.Providers.TwObservation.Combat;

/// <summary>
/// 实体 HP 的 AES-128-CBC 解密（PKCS7 填充）。SetHealth 与出现帧初值的密钥为该实体 ChannelObjectId 的 UInt64 小端 8 字节重复两遍，
/// IV 为出现帧 ActorStatus.IV；明文为 8 字节 Int64。UpdateHealthMax 的密钥与 IV 来自本连接的 SendBroadcastPacketAES，
/// 明文为实体 UInt64 与最大值 Int64。填充不合法或明文长度不符时返回假，不抛出。密钥只在调用期间存在于栈上。
/// </summary>
internal sealed class TwHealthCipher : IDisposable
{
    private const int MaximumCipherBytes = 64;
    private readonly Aes aes = Aes.Create();

    /// <summary>解出 SetHealth 或出现帧的 HP 明文；不合法时返回假。</summary>
    public bool TryHealth(ulong keyId, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> cipher, out long value)
    {
        value = 0;
        Span<byte> key = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(key, keyId);
        BinaryPrimitives.WriteUInt64LittleEndian(key[8..], keyId);
        Span<byte> plain = stackalloc byte[MaximumCipherBytes];
        if (!TryDecrypt(key, iv, cipher, plain, out var length) || length != 8) return false;
        value = BinaryPrimitives.ReadInt64LittleEndian(plain);
        return true;
    }

    /// <summary>解出 UpdateHealthMax 的实体与最大值；不合法时返回假。</summary>
    public bool TryHealthMax(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> cipher, out ulong entity, out long value)
    {
        entity = 0; value = 0;
        Span<byte> plain = stackalloc byte[MaximumCipherBytes];
        if (!TryDecrypt(key, iv, cipher, plain, out var length) || length < 16) return false;
        entity = BinaryPrimitives.ReadUInt64LittleEndian(plain);
        value = BinaryPrimitives.ReadInt64LittleEndian(plain[8..]);
        return true;
    }

    private bool TryDecrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> cipher, Span<byte> plain, out int length)
    {
        length = 0;
        if (key.Length != 16 || iv.Length != 16 || cipher.Length == 0 || cipher.Length % 16 != 0 || cipher.Length > MaximumCipherBytes) return false;
        aes.SetKey(key);
        var written = aes.DecryptCbc(cipher, iv, plain, PaddingMode.None);
        var pad = plain[written - 1];
        if (pad is 0 or > 16) return false;
        for (var index = written - pad; index < written; index++) if (plain[index] != pad) return false;
        length = written - pad;
        return true;
    }

    public void Dispose() => aes.Dispose();
}

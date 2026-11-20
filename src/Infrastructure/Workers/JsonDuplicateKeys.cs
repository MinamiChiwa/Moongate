using System.Text.Json;
using System.Text.Unicode;

namespace MabiTool.Infrastructure.Workers;

/// <summary>
/// Worker 帧的重复属性名检查：在 UTF-8 JSON 原始字节上一次顺序扫描，任一对象内出现两个反转义后相同的属性名即为重复。
/// 属性名按反转义后的 UTF-8 字节比较（与按字符串比较等价）；名称不是合法 UTF-8 或转义无效时按无效 JSON 处理。
/// 每层对象的名称只记位置与长度，不为每个对象分配集合；单个对象超过 <see cref="LinearLimit"/> 个属性时改用该对象自己的哈希集合。
/// </summary>
internal static class JsonDuplicateKeys
{
    internal const int UnboundedDepth = 4096;
    internal const int LinearLimit = 32;
    [ThreadStatic] private static Scratch? cached;

    /// <summary>
    /// JSON 中是否有对象含重复属性名；JSON 无效、超过 <paramref name="maxDepth"/> 层，或 <paramref name="requireObject"/> 为真而顶层不是对象时
    /// 抛出 <see cref="JsonException"/>。
    /// </summary>
    internal static bool Contains(ReadOnlySpan<byte> json, int maxDepth, bool requireObject = false)
    {
        var scratch = cached ?? new Scratch();
        cached = null;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = maxDepth });
            if (requireObject && (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)) throw new JsonException("顶层不是 JSON 对象");
            if (requireObject) scratch.Push();
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject: scratch.Push(); break;
                    case JsonTokenType.EndObject: scratch.Pop(); break;
                    case JsonTokenType.PropertyName:
                        if (!scratch.Add(ref reader, json)) return true;
                        break;
                }
            }
            return false;
        }
        catch (InvalidOperationException error) { throw new JsonException("属性名不是有效的 UTF-8 或转义无效", error); }
        finally
        {
            scratch.Reset();
            cached = scratch;
        }
    }

    private sealed class Scratch
    {
        private record struct Entry(int Offset, int Length, bool Unescaped);
        private Entry[] entries = new Entry[64];
        private int count;
        private int[] frames = new int[16];
        private HashSet<string>?[] sets = new HashSet<string>?[16];
        private int depth;
        private byte[] arena = new byte[256];
        private int arenaLength;

        public void Push()
        {
            if (depth == frames.Length) { Array.Resize(ref frames, depth * 2); Array.Resize(ref sets, depth * 2); }
            frames[depth] = count; sets[depth] = null; depth++;
        }

        public void Pop()
        {
            depth--;
            count = frames[depth]; sets[depth] = null;
            if (depth == 0) arenaLength = 0;
        }

        /// <returns>该名称在当前对象中首次出现时为真。</returns>
        public bool Add(ref Utf8JsonReader reader, ReadOnlySpan<byte> json)
        {
            ReadOnlySpan<byte> name;
            Entry entry;
            if (reader.ValueIsEscaped)
            {
                var room = reader.ValueSpan.Length;
                if (arena.Length - arenaLength < room) Array.Resize(ref arena, Math.Max(arena.Length * 2, arenaLength + room));
                var written = reader.CopyString(arena.AsSpan(arenaLength));
                name = arena.AsSpan(arenaLength, written);
                entry = new(arenaLength, written, true);
                arenaLength += written;
            }
            else
            {
                name = reader.ValueSpan;
                // 未转义的名称直接引用输入：记下它在输入中的偏移。
                var offset = (int)System.Runtime.CompilerServices.Unsafe.ByteOffset(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(json),
                    ref System.Runtime.InteropServices.MemoryMarshal.GetReference(name));
                entry = new(offset, name.Length, false);
            }
            if (!Utf8.IsValid(name)) throw new InvalidOperationException();
            var start = frames[depth - 1];
            if (sets[depth - 1] is { } set) return set.Add(System.Text.Encoding.UTF8.GetString(name));
            for (var i = start; i < count; i++)
            {
                var other = entries[i];
                if (other.Length == name.Length &&
                    (other.Unescaped ? arena.AsSpan(other.Offset, other.Length) : json.Slice(other.Offset, other.Length)).SequenceEqual(name))
                    return false;
            }
            if (count - start >= LinearLimit)
            {
                // 大对象：改用按字符串的集合，之前的名称一并放入。
                set = new HashSet<string>(StringComparer.Ordinal);
                for (var i = start; i < count; i++)
                {
                    var other = entries[i];
                    set.Add(System.Text.Encoding.UTF8.GetString(other.Unescaped ? arena.AsSpan(other.Offset, other.Length) : json.Slice(other.Offset, other.Length)));
                }
                sets[depth - 1] = set;
                count = start;
                return set.Add(System.Text.Encoding.UTF8.GetString(name));
            }
            if (count == entries.Length) Array.Resize(ref entries, count * 2);
            entries[count++] = entry;
            return true;
        }

        public void Reset()
        {
            count = 0; depth = 0; arenaLength = 0;
            Array.Clear(sets);
            if (entries.Length > 4096) entries = new Entry[64];
            if (arena.Length > 64 * 1024) arena = new byte[256];
        }

    }
}

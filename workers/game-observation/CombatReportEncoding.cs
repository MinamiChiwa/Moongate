using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Observation.Worker;

/// <summary>
/// 一份战斗报告的编码，入队时生成一次：外壳（帧内伤害、效果、效果校准、缺口、治疗、战斗状态六个合并数组与实体快照置空的整份报告）
/// 与各数组的元素内容（不含方括号）分开保存。同一批的若干报告按 <see cref="Write"/> 拼成的字节与把合并后的报告直接编码逐字节相同：
/// 外壳与实体快照取最后一份，六个合并数组依次连接，治疗与战斗状态全部为空时省略该属性（与 WhenWritingNull 相同）。
/// 实体快照逐个按对象缓存编码：实体是不可变记录，观察器只在实体变化时换新对象，未变化的实体在后续帧中不再重新编码。
/// </summary>
internal sealed class CombatReportEncoding
{
    /// <summary>按批合并的数组个数（序号 0–5），实体快照为序号 6。</summary>
    internal const int Arrays = 6, Healing = 4, States = 5, Entities = 6;
    private const int Slots = 7;
    private static readonly byte[][] Names = ["damage"u8.ToArray(), "effects"u8.ToArray(), "reconciliations"u8.ToArray(),
        "gaps"u8.ToArray(), "healing"u8.ToArray(), "combatStates"u8.ToArray(), "entities"u8.ToArray()];
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CombatEntityObservation, byte[]> entityCache = [];
    /// <summary>外壳的编码字节。</summary>
    public byte[] Shell { get; }
    /// <summary>外壳中各数组的 '[' 位置，及其属性名起始引号的位置。</summary>
    private readonly int[] open = new int[Slots], name = new int[Slots];
    /// <summary>各数组按外壳中的位置排序的序号。</summary>
    private readonly int[] order;
    /// <summary>六个合并数组的元素内容（不含方括号）；空数组为空。实体快照另存（见 entities）。</summary>
    public ReadOnlyMemory<byte>[] Items { get; } = new ReadOnlyMemory<byte>[Arrays];
    public bool HealingPresent { get; }
    public bool StatesPresent { get; }

    private CombatReportEncoding(byte[] shell, bool healing, bool states)
    {
        Shell = shell; HealingPresent = healing; StatesPresent = states;
        var reader = new Utf8JsonReader(shell);
        var found = 0; var inFrame = false;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            if (reader.CurrentDepth == 1) { inFrame = reader.ValueTextEquals("frame"u8); if (!inFrame) reader.Skip(); continue; }
            if (!inFrame || reader.CurrentDepth != 2) { reader.Skip(); continue; }
            var index = -1;
            for (var candidate = 0; candidate < Slots && index < 0; candidate++) if (reader.ValueTextEquals(Names[candidate])) index = candidate;
            var start = (int)reader.TokenStartIndex;
            reader.Read();
            if (index < 0) { reader.Skip(); continue; }
            if (reader.TokenType != JsonTokenType.StartArray || shell[(int)reader.TokenStartIndex + 1] != (byte)']' || shell[start - 1] != (byte)',')
                throw new InvalidOperationException("combat report shell layout");
            open[index] = (int)reader.TokenStartIndex; name[index] = start; found |= 1 << index;
            reader.Skip();
        }
        if (found != (1 << Slots) - 1) throw new InvalidOperationException("combat report shell layout");
        order = Enumerable.Range(0, Slots).OrderBy(index => open[index]).ToArray();
    }

    /// <summary>编码一份报告（契约序列化选项，与直接编码整份报告的校验相同）。</summary>
    public static CombatReportEncoding Encode(CombatCaptureReport value)
    {
        var frame = value.Frame;
        var shell = JsonSerializer.SerializeToUtf8Bytes(value with { Frame = frame with
            { Entities = [], Damage = [], Effects = [], Reconciliations = [], Gaps = [], Healing = [], CombatStates = [] } }, ContractJson.Options);
        var result = new CombatReportEncoding(shell, frame.Healing is not null, frame.CombatStates is not null);
        result.Items[0] = Inner(frame.Damage); result.Items[1] = Inner(frame.Effects); result.Items[2] = Inner(frame.Reconciliations);
        result.Items[3] = Inner(frame.Gaps); result.Items[Healing] = Inner(frame.Healing); result.Items[States] = Inner(frame.CombatStates);
        result.entities = new byte[frame.Entities.Count][];
        result.entityLength = Math.Max(0, frame.Entities.Count - 1);
        for (var i = 0; i < result.entities.Length; i++)
        {
            result.entities[i] = entityCache.GetValue(frame.Entities[i], static entity => JsonSerializer.SerializeToUtf8Bytes(entity, ContractJson.Options));
            result.entityLength += result.entities[i].Length;
        }
        return result;
    }
    /// <summary>实体快照：各实体的缓存编码（写出时以逗号连接）与连接后的长度。</summary>
    private byte[][] entities = [];
    private int entityLength;
    private static ReadOnlyMemory<byte> Inner<T>(IReadOnlyList<T>? items)
    {
        if (items is not { Count: > 0 }) return ReadOnlyMemory<byte>.Empty;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(items, ContractJson.Options);
        return bytes.AsMemory(1, bytes.Length - 2);
    }

    /// <summary>以本份为外壳、各数组元素内容合计 <paramref name="inner"/> 字节（<paramref name="nonEmpty"/> 份非空）时的编码长度。</summary>
    public int Length(ReadOnlySpan<int> inner, ReadOnlySpan<int> nonEmpty, bool healing, bool states)
    {
        var length = Shell.Length + entityLength;
        for (var index = 0; index < Arrays; index++)
        {
            if (index == Healing && !healing || index == States && !states) { length -= open[index] + 2 - (name[index] - 1); continue; }
            length += inner[index] + Math.Max(0, nonEmpty[index] - 1);
        }
        return length;
    }

    /// <summary>以本份为外壳、依次连接 <paramref name="parts"/> 的各数组，写入 <paramref name="target"/>，返回写入的字节数。</summary>
    public int Write(Span<byte> target, IReadOnlyList<CombatReportEncoding> parts, bool healing, bool states)
    {
        var written = 0; var cursor = 0;
        void Copy(ReadOnlySpan<byte> value, Span<byte> destination) { value.CopyTo(destination[written..]); written += value.Length; }
        foreach (var index in order)
        {
            if (index == Healing && !healing || index == States && !states)
            {
                Copy(Shell.AsSpan(cursor, name[index] - 1 - cursor), target); cursor = open[index] + 2; continue;
            }
            Copy(Shell.AsSpan(cursor, open[index] + 1 - cursor), target); cursor = open[index] + 1;
            if (index == Entities)
            {
                for (var i = 0; i < entities.Length; i++) { if (i > 0) target[written++] = (byte)','; Copy(entities[i], target); }
                continue;
            }
            var first = true;
            foreach (var part in parts)
            {
                var items = part.Items[index];
                if (items.IsEmpty) continue;
                if (!first) target[written++] = (byte)',';
                Copy(items.Span, target); first = false;
            }
        }
        Copy(Shell.AsSpan(cursor), target);
        return written;
    }
}

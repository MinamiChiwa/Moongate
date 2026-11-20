using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Observation.Worker;

/// <summary>
/// 写出队列中的一批战斗记录：同一兴趣与代次、100 ms 内相邻报告的合并结果，或一条容量缺口。
/// 合并批次保存各份报告及其入队时的编码；<see cref="Report"/> 按需构造合并后的报告，<see cref="WriteEnvelope"/> 直接拼出其编码。
/// </summary>
internal sealed class CombatCaptureBatch
{
    private readonly List<(CombatCaptureReport Report, CombatReportEncoding Encoding)> parts = [];
    private CombatCaptureReport? report;
    internal CombatCaptureBatch(CombatCaptureReport report, CombatReportEncoding? encoding)
    {
        if (encoding is null) this.report = report; else parts.Add((report, encoding));
    }
    /// <summary>容量缺口没有入队编码，按报告对象写出。</summary>
    public bool Encoded => parts.Count > 0;
    /// <summary>合并后报告的帧时点（最后一份的时点）。</summary>
    public double MonotonicMs => Head.Frame.MonotonicMs;
    public CombatCaptureReport Head => parts.Count > 0 ? parts[^1].Report : report!;
    /// <summary>编码字节数；不含实体快照的记录数。</summary>
    internal int Bytes, Events;
    internal double FirstMono;
    internal readonly int[] Inner = new int[CombatReportEncoding.Arrays], NonEmpty = new int[CombatReportEncoding.Arrays];
    internal bool HealingPresent, StatesPresent, TimesValid = true;
    internal double MinMono = double.PositiveInfinity, MaxMono = double.NegativeInfinity;
    internal DateTimeOffset MaxAt = DateTimeOffset.MinValue;
    internal IReadOnlyList<(CombatCaptureReport Report, CombatReportEncoding Encoding)> Parts => parts;
    internal void Add(CombatCaptureReport value, CombatReportEncoding encoding) { parts.Add((value, encoding)); report = null; }

    /// <summary>合并后的报告：最后一份的外壳，各数组按入队顺序连接；治疗与战斗状态全部为空时为空。</summary>
    public CombatCaptureReport Report => report ??= Merge();
    private CombatCaptureReport Merge()
    {
        if (parts.Count == 1) return parts[0].Report;
        var head = parts[^1].Report;
        IReadOnlyList<T> Concat<T>(Func<CombatFrame, IReadOnlyList<T>?> select) => parts.SelectMany(part => select(part.Report.Frame) ?? []).ToArray();
        return head with { Frame = head.Frame with
        {
            Damage = Concat(frame => frame.Damage), Effects = Concat(frame => frame.Effects), Reconciliations = Concat(frame => frame.Reconciliations),
            Gaps = Concat(frame => frame.Gaps), Healing = HealingPresent ? Concat(frame => frame.Healing) : null,
            CombatStates = StatesPresent ? Concat(frame => frame.CombatStates) : null
        } };
    }

    /// <summary>写出 <paramref name="prefix"/>、合并后报告的编码与 <paramref name="suffix"/>（例如事件信封 <c>{"combat":</c> 与 <c>}</c>）。</summary>
    public byte[] WriteEnvelope(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> suffix)
    {
        var result = new byte[prefix.Length + Bytes + suffix.Length];
        WriteEnvelope(result, prefix, suffix);
        return result;
    }
    /// <summary>同上，写入调用方提供的缓冲（长度至少为前后缀与 <see cref="Bytes"/> 之和），返回写入的字节数。</summary>
    public int WriteEnvelope(byte[] result, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> suffix)
    {
        prefix.CopyTo(result);
        var written = parts[^1].Encoding.Write(result.AsSpan(prefix.Length), parts.Select(part => part.Encoding).ToArray(), HealingPresent, StatesPresent);
        if (written != Bytes) throw new InvalidOperationException("combat batch length");
        suffix.CopyTo(result.AsSpan(prefix.Length + written));
        return prefix.Length + written + suffix.Length;
    }
}

/// <summary>
/// 按批次和字节限制增量；容量缺口在较早批次排空后发送。每份报告入队时编码一次，批次字节数按各部分长度累加，
/// 合并不重编整批；写出时由各部分拼出与整批直接编码相同的字节。
/// </summary>
public sealed class CombatCaptureQueue
{
    public const int MaximumBatchRecords = 256, MaximumBatchBytes = 256 * 1024;
    private const int MaximumLossBytes = 8 * 1024;
    private sealed class Loss(CombatCaptureReport report, int bytes, ulong count)
    {
        public CombatCaptureReport Last = report;
        public int Bytes = bytes;
        public ulong Count = count;
        public double From = report.Frame.MonotonicMs, To = report.Frame.MonotonicMs;
    }
    private readonly object gate = new();
    private readonly List<CombatCaptureBatch> queue = [];
    private readonly Queue<Loss> losses = [];
    private readonly Dictionary<(string Interest, ulong Generation), Loss> lost = [];
    private readonly int maximumBatches, dataBudget, maximumLosses;
    private int bytes, lossBytes;
    public CombatCaptureQueue(int maximumBatches = 64, int maximumBytes = 4 * 1024 * 1024)
    {
        if (maximumBatches is < 1 or > 64 || maximumBytes is < 16 * 1024 or > 4 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumBatches));
        this.maximumBatches = maximumBatches;
        maximumLosses = Math.Min(64, maximumBytes / (2 * MaximumLossBytes));
        dataBudget = maximumBytes - maximumLosses * MaximumLossBytes;
    }
    public int Count { get { lock (gate) return queue.Count; } }
    public int PendingCount { get { lock (gate) return queue.Count + losses.Count; } }
    public int Bytes { get { lock (gate) return bytes + lossBytes; } }
    /// <summary>不含实体快照的记录数（伤害、效果、缺口、治疗、战斗状态与效果校准）；批次记录数为它们之和加最后一份的实体数。</summary>
    private static int Events(CombatFrame value) => checked(value.Damage.Count + value.Effects.Count + value.Gaps.Count +
        (value.Healing?.Count ?? 0) + (value.CombatStates?.Count ?? 0) + value.Reconciliations.Sum(x => Math.Max(1, x?.Effects?.Count ?? 0)));

    /// <summary>
    /// 一份报告内全部事件时点的汇总：都有效时给出最早、最晚单调时点与最晚 UTC。
    /// 空记录只记为无效（该报告不参与合并），报告照常写出，由 Host 按 worker.frameInvalid 拒绝。
    /// </summary>
    private static (bool Valid, double Min, double Max, DateTimeOffset At) Times(CombatFrame frame)
    {
        var (valid, min, max, at) = (true, double.PositiveInfinity, double.NegativeInfinity, DateTimeOffset.MinValue);
        void Add(double mono, DateTimeOffset observed)
        {
            valid &= double.IsFinite(mono) && mono >= 0 && observed != default && observed.Offset == TimeSpan.Zero;
            min = Math.Min(min, mono); max = Math.Max(max, mono); if (observed > at) at = observed;
        }
        foreach (var value in frame.Damage) if (value is null) valid = false; else Add(value.MonotonicMs, value.ObservedAt);
        foreach (var value in frame.Healing ?? []) if (value is null) valid = false; else Add(value.MonotonicMs, value.ObservedAt);
        foreach (var value in frame.CombatStates ?? []) if (value is null) valid = false; else Add(value.MonotonicMs, value.ObservedAt);
        foreach (var value in frame.Effects) if (value is null) valid = false; else Add(value.MonotonicMs, value.ObservedAt);
        foreach (var value in frame.Reconciliations)
            foreach (var effect in value?.Effects ?? [null!]) if (effect is null) valid = false; else Add(effect.MonotonicMs, effect.ObservedAt);
        return (valid, min, max, at);
    }
    /// <summary>合并批次的事件都落在最后一份帧时点前 250 ms 内，且不晚于其单调与 UTC 时点（Host 的批内时间窗口）。</summary>
    private static bool TimesFit(CombatFrame frame, bool valid, double min, double max, DateTimeOffset at) =>
        double.IsPositiveInfinity(min) || valid && min >= frame.MonotonicMs - 250 && max <= frame.MonotonicMs && at <= frame.ObservedAt;

    public bool TryWrite(CombatCaptureReport value)
    {
        var encoding = CombatReportEncoding.Encode(value);
        var events = Events(value.Frame); var records = Math.Max(1, checked(value.Frame.Entities.Count + events));
        var times = Times(value.Frame);
        Span<int> inner = stackalloc int[CombatReportEncoding.Arrays], nonEmpty = stackalloc int[CombatReportEncoding.Arrays];
        for (var index = 0; index < CombatReportEncoding.Arrays; index++)
        { inner[index] = encoding.Items[index].Length; nonEmpty[index] = encoding.Items[index].IsEmpty ? 0 : 1; }
        var length = encoding.Length(inner, nonEmpty, encoding.HealingPresent, encoding.StatesPresent);
        lock (gate)
        {
            if (lost.Count == 0 && queue.Count > 0 && queue[^1] is { Encoded: true } previousBatch && previousBatch.Head is var previous &&
                previous.InterestId == value.InterestId && previous.Frame.Generation == value.Frame.Generation &&
                previous.Frame.Source == value.Frame.Source && previous.Frame.GameSessionId == value.Frame.GameSessionId &&
                value.Frame.Sequence.Value > previous.Frame.Sequence.Value && value.Frame.MonotonicMs >= previous.Frame.MonotonicMs &&
                value.Frame.MonotonicMs - previousBatch.FirstMono <= 100 &&
                // 合并后的外壳取最后一份：带延续记录的报告之后只并入同样带记录的报告，记录不因合并丢失。
                (previous.Carry is null || value.Carry is not null))
            {
                var mergedEvents = checked(previousBatch.Events + events);
                var (valid, min, max, at) = (previousBatch.TimesValid && times.Valid, Math.Min(previousBatch.MinMono, times.Min),
                    Math.Max(previousBatch.MaxMono, times.Max), times.At > previousBatch.MaxAt ? times.At : previousBatch.MaxAt);
                if (Math.Max(1, checked(value.Frame.Entities.Count + mergedEvents)) <= MaximumBatchRecords && TimesFit(value.Frame, valid, min, max, at))
                {
                    Span<int> mergedInner = stackalloc int[CombatReportEncoding.Arrays], mergedNonEmpty = stackalloc int[CombatReportEncoding.Arrays];
                    for (var index = 0; index < CombatReportEncoding.Arrays; index++)
                    { mergedInner[index] = previousBatch.Inner[index] + inner[index]; mergedNonEmpty[index] = previousBatch.NonEmpty[index] + nonEmpty[index]; }
                    var (healing, states) = (previousBatch.HealingPresent || encoding.HealingPresent, previousBatch.StatesPresent || encoding.StatesPresent);
                    var merged = encoding.Length(mergedInner, mergedNonEmpty, healing, states);
                    if (merged <= MaximumBatchBytes && bytes - previousBatch.Bytes + merged <= dataBudget)
                    {
                        previousBatch.Add(value, encoding);
                        mergedInner.CopyTo(previousBatch.Inner); mergedNonEmpty.CopyTo(previousBatch.NonEmpty);
                        (previousBatch.HealingPresent, previousBatch.StatesPresent) = (healing, states);
                        (previousBatch.TimesValid, previousBatch.MinMono, previousBatch.MaxMono, previousBatch.MaxAt) = (valid, min, max, at);
                        previousBatch.Events = mergedEvents;
                        bytes += merged - previousBatch.Bytes; previousBatch.Bytes = merged; return true;
                    }
                }
            }
            if (lost.Count > 0 || records > MaximumBatchRecords || length > MaximumBatchBytes ||
                queue.Count >= maximumBatches || bytes + length > dataBudget)
            {
                var count = value.Frame.Gaps.Aggregate((ulong)records, (sum, gap) => checked(sum + gap.DroppedEvents.Value));
                var template = value with { Frame = value.Frame with { Entities = value.LocalEntity is { } local ? [local] : [],
                    Damage = [], Effects = [], Reconciliations = [], Gaps = [], Healing = null } };
                var templateBytes = JsonSerializer.SerializeToUtf8Bytes(template, ContractJson.Options).Length;
                if (templateBytes > MaximumLossBytes) throw new WorkerObservationException("combat.observationGap");
                var key = (value.InterestId, value.Frame.Generation.Value);
                if (!lost.TryGetValue(key, out var loss))
                {
                    if (lost.Count >= maximumLosses) throw new WorkerObservationException("combat.observationGap");
                    loss = new(template, templateBytes, 0); lost.Add(key, loss); losses.Enqueue(loss);
                    lossBytes += templateBytes;
                }
                else
                {
                    lossBytes += templateBytes - loss.Bytes; loss.Last = template; loss.Bytes = templateBytes;
                }
                loss.Count = checked(loss.Count + count); loss.From = Math.Min(loss.From, value.Frame.MonotonicMs);
                loss.To = Math.Max(loss.To, value.Frame.MonotonicMs); return false;
            }
            var batch = new CombatCaptureBatch(value, encoding)
            {
                Bytes = length, Events = events, FirstMono = value.Frame.MonotonicMs, HealingPresent = encoding.HealingPresent,
                StatesPresent = encoding.StatesPresent, TimesValid = times.Valid, MinMono = times.Min, MaxMono = times.Max, MaxAt = times.At
            };
            inner.CopyTo(batch.Inner); nonEmpty.CopyTo(batch.NonEmpty);
            queue.Add(batch);
            bytes += length; return true;
        }
    }
    public IReadOnlyList<CombatCaptureReport> Drain() => DrainBatches().Select(batch => batch.Report).ToArray();
    public IReadOnlyList<CombatCaptureReport> Take(int maximumBatches = 8) => TakeBatches(maximumBatches).Select(batch => batch.Report).ToArray();
    internal IReadOnlyList<CombatCaptureBatch> DrainBatches() { lock (gate) return TakeCore(128); }
    internal IReadOnlyList<CombatCaptureBatch> TakeBatches(int maximumBatches = 8)
    {
        if (maximumBatches is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maximumBatches));
        lock (gate) return TakeCore(maximumBatches);
    }
    private List<CombatCaptureBatch> TakeCore(int maximum)
    {
        var result = new List<CombatCaptureBatch>(Math.Min(queue.Count + losses.Count, maximum));
        var take = Math.Min(queue.Count, maximum);
        for (var i = 0; i < take; i++) { result.Add(queue[i]); bytes -= queue[i].Bytes; }
        queue.RemoveRange(0, take);
        while (queue.Count == 0 && result.Count < maximum && losses.TryDequeue(out var loss))
        {
            lost.Remove((loss.Last.InterestId, loss.Last.Frame.Generation.Value)); lossBytes -= loss.Bytes;
            result.Add(new(loss.Last with { Frame = loss.Last.Frame with
                { Gaps = [new("combat.transportCapacity", loss.From, loss.To, new(loss.Count))] } }, null));
        }
        return result;
    }
}

// 来源：MinamiChiwa/MabiSync，固定提交 2cb69b5ddec8de4fe9d20ec98bbb410ac015e671。
using System.Buffers.Binary;

namespace MabiTool.Providers.TwObservation;

public sealed record GameStreamProfile(IReadOnlySet<int> KnownProtocols, IReadOnlySet<int> ObservedProtocols);

public enum GameStreamResetCause
{
    Unknown, PayloadTooLarge, PendingCapacity, GapTimeout, ConflictingRetransmission,
    BufferCapacity, InvalidFrameHeader, DecodeFailure, ConnectionRemoved, CaptureDrops,
    TruncatedPacket, ConnectionSyn, ConnectionClosed, ConnectionReset
}

/// <summary>
/// 重组与分帧的累计诊断计数，可由同一捕获的多条连接共用；只保存整数和协议号。
/// 协议号分布最多保留 <see cref="MaximumFrameTypes"/> 种，之后新出现的协议号只计入 OtherFrames。
/// </summary>
public sealed class GameStreamCounters
{
    public const int MaximumFrameTypes = 24;
    private readonly object gate = new();
    private readonly Dictionary<int, ulong> types = [];
    private readonly ulong[] resets = new ulong[Enum.GetValues<GameStreamResetCause>().Length];
    private ulong segments, bytes, outOfOrder, retransmitted, frames, dispatched, decompressFailures, otherFrames;
    internal void Segment(int length) { lock (gate) { segments++; bytes += (ulong)length; } }
    internal void OutOfOrder() { lock (gate) outOfOrder++; }
    internal void Retransmitted() { lock (gate) retransmitted++; }
    internal void Reset(GameStreamResetCause cause) { lock (gate) if ((int)cause >= 0 && (int)cause < resets.Length) resets[(int)cause]++; }
    internal void DecompressFailure() { lock (gate) decompressFailures++; }
    internal void Frame(int code, bool observed)
    {
        lock (gate)
        {
            frames++; if (observed) dispatched++;
            if (types.TryGetValue(code, out var count)) types[code] = count + 1;
            else if (types.Count < MaximumFrameTypes) types.Add(code, 1);
            else otherFrames++;
        }
    }
    public GameStreamCounterSnapshot Snapshot()
    {
        lock (gate)
        {
            var reset = new SortedDictionary<string, ulong>(StringComparer.Ordinal);
            for (var i = 0; i < resets.Length; i++)
                if (resets[i] > 0) { var name = ((GameStreamResetCause)i).ToString(); reset.Add(char.ToLowerInvariant(name[0]) + name[1..], resets[i]); }
            return new(segments, bytes, outOfOrder, retransmitted, reset, frames, dispatched, decompressFailures,
                new SortedDictionary<int, ulong>(types), otherFrames);
        }
    }
}

/// <param name="Resets">按重置原因（小驼峰）计数，只含非零项。</param>
/// <param name="Types">按协议号的帧数；超过种类上限后的帧计入 OtherFrames。</param>
public sealed record GameStreamCounterSnapshot(ulong Segments, ulong Bytes, ulong OutOfOrder, ulong Retransmitted,
    IReadOnlyDictionary<string, ulong> Resets, ulong Frames, ulong Dispatched, ulong DecompressFailures,
    IReadOnlyDictionary<int, ulong> Types, ulong OtherFrames);

/// <summary>有界单向 TCP 重组；共同帧流按 profile 分发，缺口不填充。</summary>
public sealed class GameTcpStream(Action<int, byte[]> onFrame, Action<string> invalidate, GameStreamProfile? profile = null,
    Func<int, bool>? observe = null, Action<int, string>? rejectFrame = null, GameStreamCounters? counters = null)
{
    private static readonly IReadOnlySet<int> MusicProtocols = new HashSet<int>
        { 20047, 20520, 20533, 120412, 120413, 120692, 120693, 20238, 20307 };
    private readonly GameStreamProfile protocols = profile is null
        ? new(TwGameProtocol.KnownProtocols, MusicProtocols)
        : new(profile.KnownProtocols.ToHashSet(), profile.ObservedProtocols.ToHashSet());
    private readonly List<byte> _buffer = [], _history = [];
    private readonly List<(uint Seq, byte[] Bytes, double Time)> _pending = [];
    private readonly List<(int Count, double Time)> _times = [];
    public double? FrameReceivedLocalMs { get; private set; }
    public ulong FrameStreamOffset { get; private set; }
    public int FrameWireLength { get; private set; }
    public byte FrameEncoding { get; private set; }
    private ulong _bufferOffset;
    private uint _next;
    private bool _started, _framed;
    private int _needed;
    private double? _gapSince;
    public GameStreamResetCause LastResetCause { get; private set; }
    private void Invalidate(string reason, GameStreamResetCause cause)
    { LastResetCause = cause; counters?.Reset(cause); invalidate(reason); }
    public void Restart(string reason, GameStreamResetCause cause = GameStreamResetCause.Unknown)
    {
        _buffer.Clear(); _history.Clear(); _pending.Clear(); _times.Clear(); FrameReceivedLocalMs = null;
        FrameStreamOffset = _bufferOffset = 0; FrameWireLength = 0; FrameEncoding = 0;
        _started = _framed = false; _gapSince = null; _needed = 0; Invalidate(reason, cause);
    }
    public void Feed(uint sequence, ReadOnlySpan<byte> payload, double nowMs, double? receivedLocalMs = null)
    {
        if (payload.IsEmpty) return;
        counters?.Segment(payload.Length);
        if (payload.Length > 65536) { Restart("截获的数据段过长，等待重新识别。", GameStreamResetCause.PayloadTooLarge); return; }
        if (!_started) { _started = true; _next = sequence; }
        var delta = unchecked((int)(sequence - _next));
        if (delta > 0)
        {
            if (_pending.Count >= 128 || _pending.Sum(p => p.Bytes.Length) + payload.Length > 1024 * 1024)
            { Restart("已清理积压的游戏数据，等待重新识别。", GameStreamResetCause.PendingCapacity); return; }
            var duplicate = false;
            foreach (var row in _pending) if (row.Seq == sequence && payload.SequenceEqual(row.Bytes)) { duplicate = true; break; }
            if (!duplicate) { counters?.OutOfOrder(); _pending.Add((sequence, payload.ToArray(), receivedLocalMs ?? nowMs)); }
            else counters?.Retransmitted();
            _gapSince ??= nowMs; CheckGap(nowMs); return;
        }
        Append(sequence, payload, receivedLocalMs ?? nowMs);
        if (!_started) return;
        while (true)
        {
            var at = _pending.FindIndex(p => unchecked((int)(p.Seq - _next)) <= 0);
            if (at < 0) break;
            var row = _pending[at]; _pending.RemoveAt(at); Append(row.Seq, row.Bytes, row.Time);
            if (!_started) return;
        }
        if (_pending.Count == 0) _gapSince = null;
    }
    public void CheckGap(double nowMs)
    {
        if (_gapSince is not { } since || nowMs - since < 1500 || _pending.Count == 0) return;
        var first = _pending.MinBy(p => unchecked((int)(p.Seq - _next)));
        Restart("部分游戏数据未能抓到，需要重新识别昵称和加入顺序。", GameStreamResetCause.GapTimeout);
        Feed(first.Seq, first.Bytes, nowMs, first.Time);
    }
    private void Append(uint sequence, ReadOnlySpan<byte> payload, double time)
    {
        var delta = unchecked((int)(sequence - _next));
        var skip = (int)Math.Min(payload.Length, -(long)delta);
        if (skip > 0) counters?.Retransmitted();
        // Compare overlapping bytes still in our bounded history, including full retransmits.
        for (var i = 0; i < skip; i++)
        {
            var offset = (long)_history.Count + delta + i;
            if (offset >= 0 && offset < _history.Count && _history[(int)offset] != payload[i])
            { Restart("重复收到的游戏数据不一致，需要重新识别。", GameStreamResetCause.ConflictingRetransmission); return; }
        }
        var bytes = payload[skip..].ToArray();
        _next = unchecked(_next + (uint)bytes.Length);
        _history.AddRange(bytes); if (_history.Count > 65536) _history.RemoveRange(0, _history.Count - 65536);
        _buffer.AddRange(bytes);
        if (bytes.Length > 0) _times.Add((bytes.Length, time));
        if (_buffer.Count > TwGameProtocol.MaximumBody + 65545) { Restart("游戏帧过长，等待重新识别。", GameStreamResetCause.BufferCapacity); return; }
        // 已知下一帧长度时，数据未到齐前不重新复制和扫描缓冲；大帧逐段到达不再按段数平方复制。
        if (_buffer.Count < _needed) return;
        Drain();
    }
    /// <summary>异常筛选中记录展开失败；始终返回 true，处理沿用原分支。</summary>
    private bool Decompress() { counters?.DecompressFailure(); return true; }
    private (int Code, int Length, byte Flag)? Header(byte[] data, int offset)
    {
        if (offset < 0 || data.Length - offset < 9) return null;
        var code = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
        var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4)); var flag = data[offset + 8];
        return protocols.KnownProtocols.Contains(code) && length >= 0 && length <= TwGameProtocol.MaximumBody && flag <= 1 ? (code, length, flag) : null;
    }
    private void Drain()
    {
        _needed = 0;
        var data = _buffer.ToArray(); var at = 0;
        while (data.Length - at >= 9)
        {
            var header = Header(data, at);
            if (!_framed)
            {
                if (header is not { } candidate) { at++; continue; }
                var second = (long)at + 9 + candidate.Length;
                if (second + 9 > data.Length) break;
                if (Header(data, (int)second) is null) { at++; continue; }
                _framed = true;
            }
            else if (header is null)
            { _framed = false; Invalidate("游戏数据不连续，需要重新识别。", GameStreamResetCause.InvalidFrameHeader); at++; continue; }
            var frame = header!.Value;
            if (data.Length - at - 9 < frame.Length) { _needed = 9 + frame.Length; break; }
            var body = data.AsSpan(at + 9, frame.Length);
            // Ignore unrelated payloads without expanding or retaining them.
            var observed = protocols.ObservedProtocols.Contains(frame.Code) && (observe?.Invoke(frame.Code) ?? true);
            counters?.Frame(frame.Code, observed);
            if (observed)
            {
                double? received = null; var offset = 0;
                foreach (var span in _times)
                {
                    if (offset < at + 9 + frame.Length && offset + span.Count > at)
                        received = received is null ? span.Time : Math.Max(received.Value, span.Time);
                    offset += span.Count;
                }
                FrameReceivedLocalMs = received is { } stamp && double.IsFinite(stamp) ? stamp : null;
                FrameStreamOffset = checked(_bufferOffset + (ulong)at);
                FrameWireLength = checked(9 + frame.Length);
                FrameEncoding = frame.Flag;
                var expanded = false;
                try { var bytes = TwGameProtocol.Expand(body, frame.Flag); expanded = true; onFrame(frame.Code, bytes); }
                catch (Exception error) when ((error is InvalidDataException or System.Text.DecoderFallbackException) && (expanded || Decompress()))
                {
                    if (rejectFrame is null) Invalidate("无法解析本次游戏数据，需要重新识别。", GameStreamResetCause.DecodeFailure);
                    else rejectFrame(frame.Code, "无法解析本次游戏数据，需要重新识别。");
                }
            }
            at += 9 + frame.Length;
        }
        if (!_framed && at > 65536) at = Math.Max(at, data.Length - 8);
        if (at > 0)
        {
            _buffer.RemoveRange(0, at);
            _bufferOffset = checked(_bufferOffset + (ulong)at);
            var remove = at;
            while (remove > 0 && _times.Count > 0)
            {
                var span = _times[0];
                if (span.Count <= remove) { remove -= span.Count; _times.RemoveAt(0); }
                else { _times[0] = (span.Count - remove, span.Time); remove = 0; }
            }
        }
    }
}

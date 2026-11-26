using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.MusicLibrary.Domain;
using MabiTool.Modules.MusicLibrary.Persistence;

namespace MabiTool.Modules.MusicLibrary.Application;

public sealed class PerformanceSessionService(PerformanceSessionStore store, TimeProvider? timeProvider = null)
{
    private static readonly Regex KeyPattern = new(@"\A[A-Za-z0-9._-]{8,128}\z", RegexOptions.CultureInvariant);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private sealed record FrozenCreate(PerformanceSessionCreateRequest Request, byte[] Bytes);

    /// <summary>会话创建或状态改变并保存成功后触发（进程内通知，供游戏内演奏驱动唤醒）；处理器不得抛出。</summary>
    public event Action<PerformanceQueueState>? Changed;

    public IReadOnlyList<PerformanceQueueState> List(string owner) => store.List(owner);

    public IReadOnlyList<PerformanceQueueState> ListAll() => store.ListAll();

    public PerformanceQueueState? TryGet(string owner, string id) => store.Get(owner, id);

    public PerformanceQueueState Get(string owner, string id) => store.Get(owner, id)
        ?? throw new OperationException("resource.notFound", "演奏会话不存在");

    public async Task<PerformanceQueueState> CreateAsync(string owner, PerformanceSessionCreateRequest request,
        string? idempotencyKey, CancellationToken ct = default)
    {
        var key = ValidateKey(idempotencyKey);
        var frozen = Freeze(request);
        var hash = Convert.ToHexString(SHA256.HashData(frozen.Bytes));
        var id = string.IsNullOrWhiteSpace(frozen.Request.Id) ? Guid.NewGuid().ToString("N") : frozen.Request.Id!;
        var state = new PerformanceQueueStateMachine().Create(id, owner, frozen.Request.Mode, frozen.Request.GameSessionId, frozen.Request.Items,
            frozen.Request.Shuffle, frozen.Request.Repeat, frozen.Request.GapSeconds, frozen.Request.AdvanceMargin);
        var created = await store.CreateIdempotentAsync(state, "performance-create:" + key, hash, ct);
        Notify(created);
        return created;
    }

    private void Notify(PerformanceQueueState state)
    {
        try { Changed?.Invoke(state); }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { }
    }

    public async Task<PerformanceQueueState> CommandAsync(string owner, string id, PerformanceSessionCommand request,
        string? ifMatch, CancellationToken ct = default)
    {
        if (request is null) throw new OperationException("request.invalid", "演奏会话控制请求不能为空");
        var expected = ParseETag(ifMatch);
        var current = Get(owner, id);
        if (expected != current.Generation)
            throw new OperationException("revision.stale", "演奏会话已被其他客户端更新");
        if ((current.Phase is PerformancePhase.Stopped or PerformancePhase.Failed or PerformancePhase.Cancelled) &&
            (request.Action is "stop" or "stopped"))
            return await store.UpdateAsync(owner, id, expected, current, ct);

        var now = request.At ?? clock.GetUtcNow();
        var machine = new PerformanceQueueStateMachine();
        var next = request.Action switch
        {
            "arm" => machine.Arm(current),
            "dispatch" => machine.Dispatch(current, now),
            "playing" => machine.Playing(current),
            "advance" => machine.Advance(current, request.Direction, now),
            "stop" => machine.Stop(current, StopReason(request.StopReason)),
            "failure" => machine.MarkFailure(current, now),
            "stopped" => machine.ObserveStopped(current, StopConfirmationReason(current, request.StopReason), now),
            "edit" => machine.ApplyQueueEdit(current, request.Items ?? throw new OperationException("request.invalid", "edit 动作必须提供 Items")),
            _ => throw new OperationException("request.invalid", "未知演奏控制动作")
        };
        // 原因码（LastError）随失败、停止与停止确认保存，开始与进入演奏时清除。
        next = request.Action switch
        {
            "arm" or "playing" => next with { LastError = null },
            "failure" or "stop" or "stopped" when !string.IsNullOrWhiteSpace(request.Detail) => next with { LastError = Detail(request.Detail) },
            _ => next
        };
        var saved = await store.UpdateAsync(owner, id, expected, next, ct);
        if (saved.Generation != current.Generation) Notify(saved);
        return saved;
    }

    private static string Detail(string value)
    {
        var text = value.Trim();
        if (text.Length is 0 or > 128 || text.Any(char.IsControl)) throw new OperationException("request.invalid", "演奏原因码无效");
        return text;
    }

    public static string ETag(UInt64String generation) => "\"" + generation + "\"";

    private static string ValidateKey(string? key)
    {
        if (key is null) throw new OperationException("idempotency.required", "创建演奏会话需要 Idempotency-Key");
        if (!KeyPattern.IsMatch(key)) throw new OperationException("request.invalid", "幂等键格式无效");
        return key;
    }

    private static FrozenCreate Freeze(PerformanceSessionCreateRequest request)
    {
        if (request is null) throw new OperationException("request.invalid", "创建演奏会话请求不能为空");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request, ContractJson.Options);
            if (bytes.Length > 64 * 1024) throw new OperationException("request.bodyTooLarge", "演奏会话请求超过 64 KiB");
            var frozen = JsonSerializer.Deserialize<PerformanceSessionCreateRequest>(bytes, ContractJson.Options)
                ?? throw new OperationException("request.invalid", "创建演奏会话请求无效");
            return new(frozen, bytes);
        }
        catch (JsonException error) { throw new OperationException("request.invalid", error.Message); }
    }

    private static UInt64String ParseETag(string? value)
    {
        if (value is null) throw new OperationException("revision.required", "演奏会话控制需要 If-Match");
        var normalized = value.Trim();
        if (normalized.Length < 3 || normalized[0] != '"' || normalized[^1] != '"')
            throw new OperationException("request.invalid", "演奏会话 ETag 必须是强 ETag");
        normalized = normalized[1..^1];
        try { return UInt64String.Parse(normalized); }
        catch (FormatException) { throw new OperationException("request.invalid", "演奏会话 ETag 格式无效"); }
    }

    private static PerformanceStopReason StopReason(PerformanceStopReason? requested)
    {
        var reason = requested ?? PerformanceStopReason.ManualStop;
        if (reason is PerformanceStopReason.NaturalEnd or PerformanceStopReason.QueueExhausted)
            throw new OperationException("request.invalid", "停止请求原因不能表示自然结束或队列耗尽");
        return reason;
    }

    private static PerformanceStopReason StopConfirmationReason(PerformanceQueueState current, PerformanceStopReason? requested)
    {
        var reason = requested ?? (current.Phase == PerformancePhase.Stopping
            ? current.StopReason ?? PerformanceStopReason.ManualStop
            : PerformanceStopReason.NaturalEnd);
        if (reason is PerformanceStopReason.Cancelled or PerformanceStopReason.QueueExhausted)
            throw new OperationException("request.invalid", "停止确认原因无效");
        if (current.Phase == PerformancePhase.Stopping && reason != current.StopReason)
            throw new OperationException("performance.invalidState", "停止确认原因与待确认的停止请求不一致");
        return reason;
    }
}

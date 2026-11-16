using MabiTool.Client;

namespace MabiTool.Desktop.Backend;

public sealed partial class LiveBackend
{
    /// <summary>后端存储暂时繁忙时退避重试的间隔；用尽后把错误码交给界面。测试可缩短。</summary>
    internal static IReadOnlyList<TimeSpan> StorageBackoff { get; set; } =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000)];

    private static readonly AsyncLocal<KeyScope?> Keys = new();

    private sealed class KeyScope
    {
        public List<string> Issued { get; } = [];
        public int Next { get; set; }
    }

    /// <summary>
    /// 写请求的幂等键。在 <see cref="WithStorageRetryAsync{T}"/> 内，重试时第 n 个写请求拿到与上一次相同的键，
    /// 已被后端受理的步骤重放原结果，不会执行两遍。
    /// </summary>
    internal static string IdempotencyKey()
    {
        if (Keys.Value is not { } scope) return Guid.NewGuid().ToString("N");
        if (scope.Next == scope.Issued.Count) scope.Issued.Add(Guid.NewGuid().ToString("N"));
        return scope.Issued[scope.Next++];
    }

    /// <summary>后端存储暂时繁忙的错误码：均为 503、可重试，请求没有被受理。</summary>
    internal static bool StorageBusy(Exception error) => error is MabiToolApiException
    {
        Status: 503,
        Problem.Code: "storage.unavailable" or "storage.writeQueueFull" or "storage.readCapacityExceeded"
            or "storage.transactionBudgetExceeded"
    };

    internal static async Task<T> WithStorageRetryAsync<T>(Func<Task<T>> attempt, CancellationToken token)
    {
        var scope = new KeyScope();
        var outer = Keys.Value;
        Keys.Value = scope;
        try
        {
            for (var round = 0; ; round++)
            {
                scope.Next = 0;
                try { return await attempt(); }
                catch (Exception error) when (StorageBusy(error) && round < StorageBackoff.Count)
                {
                    await Task.Delay(StorageBackoff[round], token);
                }
            }
        }
        finally { Keys.Value = outer; }
    }
}

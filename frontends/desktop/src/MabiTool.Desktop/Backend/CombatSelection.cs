namespace MabiTool.Desktop.Backend;

/// <summary>一次读取所针对的选择：范围、目标与选择修订；<see cref="Key"/> 用来认领后端结果。</summary>
public readonly record struct CombatSelectionSnapshot(string Scope, string? Target, long Version)
{
    public string Key => CombatSelection.Key(Scope, Target);
}

/// <summary>
/// DPS检测页的范围与目标选择，由后端适配层持有：主页面与浮窗共用一份，DPS 明细窗口另有一份。
/// 用户的每次选择推进修订；后端读取在发出时记下选择，结果只在仍是当前选择时采用，迟到的旧结果丢弃。
/// 由异步回应触发的切换（开始观测、重设、测量开始或出结果后自动切到测量）带上发出时的修订，期间用户另选了范围就不再覆盖。
/// </summary>
public sealed class CombatSelection
{
    private readonly Lock gate = new();
    private string scope = "segment", live = "segment";
    private string? target;
    private long version;

    public static string Key(string scope, string? target) => scope + "|" + target;

    public CombatSelectionSnapshot Snapshot()
    {
        lock (gate) return new(scope, target, version);
    }

    public string Scope { get { lock (gate) return scope; } }
    public string? Target { get { lock (gate) return target; } }
    public long Version { get { lock (gate) return version; } }

    /// <summary>用户选择范围：observation、segment 或 measurement[:记录]；换了范围时目标回到“全部”。</summary>
    public void SelectScope(string? argument)
    {
        var next = argument is "segment" or "observation" ? argument
            : argument?.StartsWith("measurement", StringComparison.Ordinal) == true ? argument : "segment";
        lock (gate)
        {
            if (next is "segment" or "observation") live = next;
            if (next != scope) target = null;
            scope = next; version++;
        }
    }

    /// <summary>整份换成给定的范围与目标（明细窗口打开时取主页面当时的选择）。</summary>
    public void Assign(string scope, string? target)
    {
        lock (gate)
        {
            if (scope is "segment" or "observation") live = scope;
            this.scope = scope; this.target = target; version++;
        }
    }

    /// <summary>用户选择目标；为空表示全部。</summary>
    public void SelectTarget(string? argument)
    {
        lock (gate) { target = string.IsNullOrEmpty(argument) ? null : argument; version++; }
    }

    /// <summary>从测量结果回到实时范围（最近一次选过的整场或当前分段）。</summary>
    public void BackToLive()
    {
        lock (gate) { scope = live; target = null; version++; }
    }

    /// <summary>
    /// 由异步回应触发的范围切换：只在发出请求后用户没有另选（修订仍为 <paramref name="issuedAt"/>）时采用；
    /// <paramref name="liveToo"/> 为真时实时范围一并改为该值（开始观测、重设回到当前分段）。返回是否采用。
    /// </summary>
    public bool Follow(long issuedAt, string next, bool liveToo = false)
    {
        lock (gate)
        {
            if (version != issuedAt) return false;
            if (next != scope) target = null;
            scope = next; if (liveToo) live = next;
            version++;
            return true;
        }
    }

    /// <summary>所选目标已不在范围内：回到“全部”（用户在此期间另选了目标时不动）。</summary>
    public void DropTarget(string expected)
    {
        lock (gate) if (target == expected) { target = null; version++; }
    }

    /// <summary>结果是否针对当前选择；不是时该结果已过时，不采用。</summary>
    public bool Accepts(string resultKey)
    {
        lock (gate) return resultKey == Key(scope, target);
    }
}

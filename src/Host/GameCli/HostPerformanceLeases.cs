using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.MusicLibrary.Application;

namespace MabiTool.Host.GameCli;

/// <summary>演奏队列与游戏连接器共用的活动控制租约：<c>activity-control:{游戏会话}</c>，与合奏、染色互斥。</summary>
public sealed class HostPerformanceLeases(GameControlContext controls) : IPerformanceActivityLeases
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(30);

    public IPerformanceLease Acquire(string ownerId, string operationId, string gameSessionId)
    {
        var binding = controls.Bindings.Current;
        if (binding is null || binding.Session.Id != gameSessionId)
            throw new OperationException("input.bindingChanged", "演奏所属的游戏会话已变化");
        try
        {
            var lease = controls.Leases.Acquire(new(ownerId, operationId, binding.Session.Id, binding.Session.BindingRevision,
                ["activity-control:" + binding.Session.Id], Duration));
            return new Lease(controls, lease);
        }
        catch (ResourceLeaseException error)
        {
            throw new OperationException(error.Code, "游戏活动正被其他功能占用", error.ConflictingOwner is null ? null
                : new Dictionary<string, JsonElement> { ["occupiedBy"] = JsonSerializer.SerializeToElement(error.ConflictingOwner) });
        }
    }

    /// <summary>核对命令行执行类命令的活动凭证：租约有效、所有者与会话匹配，且游戏会话仍是当前绑定。返回原因码，通过时为空。</summary>
    public static string? Validate(GameControlContext controls, GameActivityProof proof)
    {
        if (!controls.Leases.Validate(proof.LeaseId, "activity-control:" + proof.GameSessionId, proof.FencingToken,
                proof.GameSessionId, proof.BindingRevision, proof.OwnerId))
            return "input.activityLeaseRequired";
        return controls.Bindings.Recent?.Session.Id == proof.GameSessionId ? null : "input.bindingChanged";
    }

    private sealed class Lease(GameControlContext controls, ResourceLeaseSnapshot snapshot) : IPerformanceLease
    {
        private ResourceLeaseSnapshot current = snapshot;
        public GameActivityProof Proof => new(current.Id, current.FencingToken, current.OwnerId, current.GameSessionId, current.BindingRevision);
        public bool IsValid => controls.Leases.Validate(current.Id, "activity-control:" + current.GameSessionId, current.FencingToken,
            current.GameSessionId, current.BindingRevision, current.OwnerId);
        public bool Renew()
        {
            try { current = controls.Leases.Renew(current.Id, current.FencingToken, Duration); return true; }
            catch (ResourceLeaseException) { return false; }
        }
        public void Dispose() => controls.Leases.Release(current.Id);
    }
}

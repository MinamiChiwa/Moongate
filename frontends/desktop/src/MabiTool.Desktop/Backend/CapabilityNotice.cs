using MabiTool.Contracts;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

/// <summary>后端能力编号。前端只为这些编号对应的主操作显示不可用说明；编号以后端能力表为准。</summary>
public static class CapabilityIds
{
    public const string PerformancePlay = "performance.play";
    public const string RoomsCoordinate = "rooms.coordinate";
    public const string ColorSearch = "color.search";
    public const string InventoryFull = "inventory.full";
}

/// <summary>能力表里一项的状态，供界面显示状态行；与 <see cref="CapabilityNotice.Describe"/> 同源，不可用才有说明。</summary>
public enum CapabilityLevel { Unknown, Available, Degraded, Unavailable }

/// <summary>把能力表里的一项排成界面上的短句；只有状态为不可用时才给出说明。</summary>
public static class CapabilityNotice
{
    public static string? Describe(CapabilitySnapshot? capability)
    {
        if (capability is null || capability.Status.KnownValue != CapabilityStatus.Unavailable) return null;
        // 台服没有游戏内选曲演奏依赖的命令行工具，原因码是 module.notImplemented 时直接说明台服的状况。
        if (capability.Id == CapabilityIds.PerformancePlay)
            return capability.ReasonCode is { Length: > 0 } reason && reason != "module.notImplemented"
                ? Pick($"台服暂不支持在游戏内选曲演奏（{reason}）", $"台服暫不支援在遊戲內選曲演奏（{reason}）")
                : Pick("台服暂不支持在游戏内选曲演奏", "台服暫不支援在遊戲內選曲演奏");
        return capability.ReasonCode switch
        {
            "module.notImplemented" => Pick("后端尚未实现，暂不可用", "後端尚未實作，暫不可用"),
            var code => Pick($"暂不可用：{code}", $"暫不可用：{code}")
        };
    }
}

/// <summary>
/// 展示接口里某个主题的缺失说明。<see cref="ReasonCode"/> 为 provider.unavailable 表示台服没有该数据来源，
/// 此时浮窗里不显示对应元件；其他原因显示“暂不可用：原因码”。
/// </summary>
public sealed record TopicNotice(string? ReasonCode, string Text)
{
    public const string ProviderUnavailable = "provider.unavailable";
    public bool PlatformUnsupported => ReasonCode == ProviderUnavailable;
}

/// <summary>展示接口的主题名。</summary>
public static class PresentationTopics
{
    public const string Nearby = "nearby";
    public const string Environment = "environment";
    public static readonly string[] Watched = [Nearby, Environment];
}

public static class TopicNoticeText
{
    /// <summary>主题状态不是不可用时没有说明；缺失原因为 provider.unavailable 时说明台服暂不支持。</summary>
    public static TopicNotice? Describe(PresentationSourceStatus? source)
    {
        if (source is null || source.Status.KnownValue != CapabilityStatus.Unavailable) return null;
        return source.Reason == TopicNotice.ProviderUnavailable
            ? new(source.Reason, Pick("台服暂不支持", "台服暫不支援"))
            : new(source.Reason, source.Reason is { Length: > 0 } code
                ? Pick($"暂不可用：{code}", $"暫不可用：{code}")
                : Pick("暂不可用", "暫不可用"));
    }
}

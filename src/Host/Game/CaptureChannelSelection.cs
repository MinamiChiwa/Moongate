using MabiTool.Infrastructure;
using MabiTool.Contracts;

namespace MabiTool.Host.Game;

/// <summary>
/// 抓包设备的选择：Source 为 automatic（全部设备同时抓，<see cref="ChannelId"/> 为空）、saved（设置里指定的网卡）、
/// fallback（设置里指定的网卡 <see cref="Missing"/> 当前不存在，改为全部设备同时抓）或 request（调用方指定）。
/// </summary>
public sealed record CaptureChannelChoice(string? ChannelId, string? Description, string Source, string? Missing = null)
{
    public static CaptureChannelChoice Automatic { get; } = new(null, null, "automatic");
    public static CaptureChannelChoice Fallback(string? missing) => new(null, null, "fallback", missing);
}

public static class CaptureChannelSelection
{
    /// <summary>
    /// 自动模式不选网卡（不读取设置里留下的 ChannelId）；指定模式下保存的网卡仍在时只抓它，否则报 savedChannelMissing。
    /// </summary>
    public static CaptureChannelChoice Select(IReadOnlyList<CaptureChannelOption> channels, GameCaptureSettings settings)
    {
        if (settings.ChannelMode != GameCaptureSettings.ManualChannel) return CaptureChannelChoice.Automatic;
        var saved = channels.FirstOrDefault(value => value.Id == settings.ChannelId)
            ?? throw new OperationException("observation.savedChannelMissing", "已保存的网卡当前不可用");
        return new(saved.Id, saved.Description, "saved");
    }
}

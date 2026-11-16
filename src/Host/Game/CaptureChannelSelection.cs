using MabiTool.Infrastructure;
using MabiTool.Contracts;

namespace MabiTool.Host.Game;

/// <summary>选定的抓包网卡；Source 为 automatic（按游戏连接本机地址）或 saved（用户指定或检测确认后保存）。</summary>
public sealed record CaptureChannelChoice(string ChannelId, string Description, string Source)
{
    /// <summary>保存的网卡由用户确认，地址对不上时捕获仍按游戏连接五元组过滤。</summary>
    public bool AnyAddress => Source == "saved";
}

public static class CaptureChannelSelection
{
    /// <summary>
    /// 自动模式：只有一张网卡承载游戏数据连接的本机地址时选中它；多张时若其中有已保存网卡则用它，否则报 ambiguous；
    /// 没有时报 notFound。指定模式：已保存网卡存在即用，否则报 savedChannelMissing。
    /// </summary>
    public static CaptureChannelChoice Select(IReadOnlyList<CaptureChannelOption> channels, GameCaptureSettings settings)
    {
        if (settings.ChannelMode == GameCaptureSettings.ManualChannel)
        {
            var saved = channels.FirstOrDefault(value => value.Id == settings.ChannelId)
                ?? throw new OperationException("observation.savedChannelMissing", "已保存的网卡当前不可用");
            return new(saved.Id, saved.Description, "saved");
        }
        var matched = channels.Where(value => value.GameConnections > 0).ToArray();
        if (matched.Length == 1) return new(matched[0].Id, matched[0].Description, "automatic");
        if (matched.Length == 0) throw new OperationException("observation.channelNotFound", "没有网卡承载游戏连接的本机地址");
        var preferred = matched.FirstOrDefault(value => value.Id == settings.ChannelId);
        return preferred is not null ? new(preferred.Id, preferred.Description, "saved")
            : throw new OperationException("observation.channelAmbiguous", "多张网卡承载游戏连接，请在设置中指定");
    }
}

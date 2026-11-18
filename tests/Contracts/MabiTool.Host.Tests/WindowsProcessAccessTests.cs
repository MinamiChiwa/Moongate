using MabiTool.Platform.Windows;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>完整性判定：普通权限的 Moongate 读不到游戏令牌说明游戏已提权；Moongate 自己已是管理员时读不到不再判为“游戏更高”。</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsProcessAccessTests
{
    private const uint Medium = 0x2000, High = 0x3000, SystemLevel = 0x4000;
    private const int AccessDenied = 5, InvalidHandle = 6;

    [Theory]
    [InlineData(Medium, High, 0, ElevationVerdict.Higher)]
    [InlineData(Medium, Medium, 0, ElevationVerdict.NotHigher)]
    [InlineData(High, High, 0, ElevationVerdict.NotHigher)]
    [InlineData(High, SystemLevel, 0, ElevationVerdict.Higher)]
    [InlineData(Medium, null, AccessDenied, ElevationVerdict.Higher)]
    [InlineData(High, null, AccessDenied, ElevationVerdict.UnreadableWhileElevated)]
    [InlineData(Medium, null, InvalidHandle, ElevationVerdict.Unknown)]
    [InlineData(null, High, 0, ElevationVerdict.Unknown)]
    public void IntegrityIsJudgedRelativeToMoongate(uint? own, uint? target, int error, ElevationVerdict expected) =>
        Assert.Equal(expected, WindowsProcessAccess.Judge(own, target, error));
}

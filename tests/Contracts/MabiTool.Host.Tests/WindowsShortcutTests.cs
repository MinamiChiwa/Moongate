using System.Runtime.Versioning;
using MabiTool.ModuleSdk;
using MabiTool.Platform.Windows;
using Xunit;

namespace MabiTool.Host.Tests;

[SupportedOSPlatform("windows")]
[Collection("Persistent backend")]
public sealed class WindowsShortcutTests
{
    [Fact]
    public async Task NativeRegistrationReportsConflictsAndShutdownUnregistersItsKeys()
    {
        await using var first = new WindowsGlobalShortcutSource(); await first.StartAsync(default);
        var original = Assert.Single(first.GetRegistrations()); Assert.Equal("emergency-stop", original.ActionId);
        Assert.Equal(ShortcutKey.F9, original.Configured.Key);
        if (original.Registered is null)
        {
            Assert.Equal(ShortcutRegistrationStatus.Unavailable, original.Status); Assert.NotNull(original.FailureCode); return;
        }
        Assert.Contains(original.Registered.Key, new[] { ShortcutKey.F9, ShortcutKey.F8, ShortcutKey.F7, ShortcutKey.F10, ShortcutKey.Pause });
        await using var second = new WindowsGlobalShortcutSource(); await second.StartAsync(default);
        var occupied = Assert.Single(second.GetRegistrations());
        if (occupied.Registered is not null) { Assert.NotEqual(original.Registered, occupied.Registered); Assert.True(occupied.UsedFallback); }
        else { Assert.Equal(ShortcutRegistrationStatus.Unavailable, occupied.Status); Assert.NotNull(occupied.FailureCode); }
        await first.StopAsync(default);
        Assert.Null(Assert.Single(first.GetRegistrations()).Registered);
        await using var recovered = new WindowsGlobalShortcutSource(); await recovered.StartAsync(default);
        Assert.Equal(original.Registered, Assert.Single(recovered.GetRegistrations()).Registered);
        await recovered.StopAsync(default); await second.StopAsync(default);
    }
}

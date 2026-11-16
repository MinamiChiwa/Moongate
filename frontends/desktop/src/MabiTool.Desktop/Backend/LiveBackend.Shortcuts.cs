using MabiTool.Client;
using MabiTool.Contracts;

namespace MabiTool.Desktop.Backend;

public sealed partial class LiveBackend
{
    internal static async Task RegisterFrontendShortcutsAsync(MabiToolClient api, CancellationToken token)
    {
        string? tag = null;
        var settings = await api.GetShortcutSettingsAsync(token, metadata => tag = metadata.ETag);
        var bindings = settings.Bindings.ToList();
        foreach (var (action, key) in new[] { ("overlay-toggle", "F8"), ("overlay-click-through-toggle", "F7") })
            if (settings.Actions.Any(item => item.ActionId == action) && bindings.All(item => item.ActionId != action))
                bindings.Add(new ShortcutBindingSetting(action, new(key, false, false, false, false)));
        if (bindings.Count == settings.Bindings.Count) return;
        await api.ChangeShortcutSettingsAsync(new(bindings), IdempotencyKey(), tag ?? string.Empty, token);
    }
}

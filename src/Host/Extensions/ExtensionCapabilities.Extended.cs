using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;

namespace MabiTool.Host.Extensions;

public sealed partial class ExtensionCapabilities
{
    private ExtensionUiRegistry Ui => services.GetService<ExtensionUiRegistry>()
        ?? throw new ExtensionCallException("capability.unavailable", "当前宿主没有扩展界面");

    private partial async Task<object?> DispatchExtendedAsync(ExtensionSession session, string method, JsonElement p, CancellationToken ct)
    {
        switch (method)
        {
            case "ui.page.set": return new JsonObject { ["revision"] = Ui.SetSurface(session.Id, "page", p).Revision.ToString() };
            case "ui.overlay.set": return new JsonObject { ["revision"] = Ui.SetSurface(session.Id, "overlay", p).Revision.ToString() };
            case "ui.page.remove": return new JsonObject { ["removed"] = Ui.RemoveSurface(session.Id, "page", Text(p, "id", 32)!) };
            case "ui.overlay.remove": return new JsonObject { ["removed"] = Ui.RemoveSurface(session.Id, "overlay", Text(p, "id", 32)!) };
            case "ui.state.set": Ui.SetState(session.Id, p); return new JsonObject();
            case "ui.activity.set": Ui.SetActivity(session.Id, p); return new JsonObject();
            default: return await DispatchHighRiskAsync(session, method, p, ct);
        }
    }

    private partial async Task ExtendedGrantsChangedAsync(ExtensionSession session)
    {
        if (services.GetService<ExtensionUiRegistry>() is { } ui)
        {
            var removed = new List<string>();
            if (!session.Grants.Contains("ui.page")) removed.Add("page");
            if (!session.Grants.Contains("ui.overlay")) removed.Add("overlay");
            if (!session.Grants.Contains("ui.page") && !session.Grants.Contains("ui.overlay")) removed.Add("state");
            if (!session.Grants.Contains("ui.activity")) removed.Add("activity");
            if (removed.Count > 0) ui.Remove(session.Id, [.. removed]);
            ui.Touch();
        }
        await HighRiskGrantsChangedAsync(session);
    }

    private partial async Task ExtendedReleaseAsync(ExtensionSession session)
    {
        services.GetService<ExtensionUiRegistry>()?.Remove(session.Id);
        await HighRiskReleaseAsync(session);
    }

    /// <summary>设置表单：清单声明的设置项及当前值。</summary>
    public ExtensionSettingsForm Form(RunnableExtension extension)
    {
        var values = Settings(extension.Id, extension.Manifest);
        return new(extension.Id, extension.Manifest.DisplayNames, extension.Manifest.SettingItems.Select(item => new ExtensionSettingField(item.Key, item.Type, item.Labels,
                JsonSerializer.SerializeToElement(values[item.Key]), item.Minimum, item.Maximum,
                item.Options.Count == 0 ? null : item.Options.Select(x => new ExtensionSettingOption(x.Value, x.Labels)).ToArray())).ToArray(),
            new(services.GetService<ExtensionUiRegistry>()?.Revision ?? 0));
    }

    /// <summary>保存设置值：只接受清单声明的键，类型与范围按清单核对，未提交的键保持原值；写入扩展数据目录的 settings.json。</summary>
    public async Task<ExtensionSettingsForm> SaveSettingsAsync(RunnableExtension extension, IReadOnlyDictionary<string, JsonElement> values)
    {
        var saved = SavedSettings(extension.Id);
        foreach (var (key, value) in values)
        {
            var spec = extension.Manifest.SettingItems.FirstOrDefault(x => x.Key == key)
                ?? throw new MabiTool.Infrastructure.OperationException("extension.settingInvalid", "清单没有声明设置项 " + key);
            if (!ExtensionPackageInspector.SettingAccepts(spec, value))
                throw new MabiTool.Infrastructure.OperationException("extension.settingInvalid", "设置项 " + key + " 的值类型或范围不符");
            saved[key] = value.Clone();
        }
        var directory = installer.DataDirectory(extension.Id);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(saved));
        File.Move(temporary, path, overwrite: true);
        return Form(extension);
    }
}

/// <summary>扩展命令在快捷键体系里的动作标识：<c>extension.&lt;扩展标识&gt;.&lt;命令&gt;</c>。</summary>
public static class ExtensionShortcuts
{
    public static string ActionId(string extension, string command) => "extension." + extension + "." + command;
}

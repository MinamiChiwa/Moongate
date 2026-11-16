// 来源：MinamiChiwa/MabiAgent 本机工作树；MIT，Copyright (c) 2026 MinamiChiwa（南千和）。
namespace MabiTool.Providers.TwObservation.ItemCatalog;

public static class ClientItemCatalogLocation
{
    public static string Resolve(string executable, string? fallbackDirectory = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(executable));
        for (var depth = 0; depth < 4 && directory is not null; depth++, directory = Path.GetDirectoryName(directory))
            if (File.Exists(Path.Combine(directory, "ResourcesPackage", "Starter", "index"))) return directory;
        if (!string.IsNullOrWhiteSpace(fallbackDirectory))
        {
            var fallback = Path.GetFullPath(fallbackDirectory);
            if (File.Exists(Path.Combine(fallback, "ResourcesPackage", "Starter", "index"))) return fallback;
        }
        throw new DirectoryNotFoundException("当前客户端路径无法定位道具资源");
    }
}

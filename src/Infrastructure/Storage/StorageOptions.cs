using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

/// <summary>
/// 宿主存储启动参数。不带参数时引导目录为工具根下的 bootstrap，数据根默认为引导目录同级的 data；
/// <c>--portable-root</c> 显式指定工具根，<c>MABITOOL_BOOTSTRAP_DIRECTORY</c> 显式指定引导目录。
/// </summary>
public sealed record StorageOptions(string BootstrapDirectory, bool Initialize = false,
    string? DataRoot = null, string? PortableRoot = null)
{
    /// <summary>未显式指定数据根时的默认位置：引导目录同级的 data。</summary>
    internal static string DefaultDataRoot(string bootstrap) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(bootstrap)) ?? bootstrap, ToolRoot.DataName);

    /// <summary>引导目录与数据根都取工具根默认位置（未用参数或环境变量指定）。</summary>
    public bool UsesToolRootDefaults { get; private init; }

    /// <summary>
    /// 引导目录与数据根都由工具根推出（不带参数，或只用 <c>--portable-root</c> 指定工具根）：程序文件夹整体搬移、改名或复制后
    /// 可在身份核对通过时接管新位置。<c>--data-root</c> 与引导目录环境变量指定的位置不接管。
    /// </summary>
    public bool LocatedByToolRoot => UsesToolRootDefaults || PortableRoot is not null && DataRoot is null;

    public static StorageOptions FromArguments(string[] arguments, string? programDirectory = null)
    {
        var initialize = false;
        string? dataRoot = null, portableRoot = null;
        for (var i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--initialize-data" when !initialize: initialize = true; break;
                case "--data-root" when dataRoot is null && i + 1 < arguments.Length: dataRoot = arguments[++i]; break;
                case "--portable-root" when portableRoot is null && i + 1 < arguments.Length: portableRoot = arguments[++i]; break;
                default: throw new ArgumentException("启动参数无效；支持 --initialize-data、--data-root <绝对路径>、--portable-root <绝对路径>");
            }
        }
        if (dataRoot is not null && portableRoot is not null)
            throw new ArgumentException("--data-root 和 --portable-root 不能同时指定");
        var configured = Environment.GetEnvironmentVariable("MABITOOL_BOOTSTRAP_DIRECTORY");
        var bootstrap = portableRoot is not null ? Path.Combine(portableRoot, ToolRoot.BootstrapName) :
            configured ?? ToolRoot.Bootstrap(ToolRoot.FromProgramDirectory(programDirectory ?? AppContext.BaseDirectory));
        return new(bootstrap, initialize, dataRoot, portableRoot)
        {
            UsesToolRootDefaults = portableRoot is null && dataRoot is null && configured is null
        };
    }
}

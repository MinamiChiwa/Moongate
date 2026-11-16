namespace MabiTool.Host;

public sealed class LocalApiException(string code, string title) : Exception(title)
{
    public string Code { get; } = code;
}

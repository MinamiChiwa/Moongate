namespace MabiTool.Infrastructure;

public sealed class StorageException(string code, string message, Exception? inner = null) : IOException(message, inner)
{
    public string Code { get; } = code;
}

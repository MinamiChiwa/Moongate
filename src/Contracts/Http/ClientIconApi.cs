namespace MabiTool.Contracts;

/// <summary>客户端属性图标的只读接口：目录与单个图标的 PNG 内容（ETag 为 PNG 的 SHA-256）。</summary>
public static class ClientIconApi
{
    public static EndpointDefinition Catalog { get; } = new("/api/v1/client-icons", "getClientIcons", typeof(ClientIconCatalog), ETag: true);
    public static EndpointDefinition Content { get; } = new("/api/v1/client-icons/{id}/content", "downloadClientIcon", typeof(ClientIcon),
        MediaType: "image/png", ETag: true, MaximumResponseBytes: 1024 * 1024, BinaryResponse: true);

    public static IReadOnlyList<EndpointDefinition> Endpoints { get; } = Array.AsReadOnly<EndpointDefinition>([Catalog, Content]);
}

using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AssetContentRequest(int Offset = 0, int Count = 262144);
public sealed record AssetContentChunk(string Id, int Offset, string ContentBase64, UInt64String TotalBytes, string Sha256);

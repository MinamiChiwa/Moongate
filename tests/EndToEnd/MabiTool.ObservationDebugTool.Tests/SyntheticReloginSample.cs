using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MabiTool.Providers.TwObservation.Inventory;

namespace MabiTool.ObservationDebugTool.Tests;

/// <summary>
/// 合成重新登录样本：按 capture-combat-evidence.py --track-process-connections 的输出格式写 packets.jsonl 与 connections.json。
/// 布局沿用当前台服协议号（登录 20590、身份关联 170631、消耗品堆叠 170427），schema 为最小合成结构。
/// </summary>
internal static class SyntheticReloginSample
{
    public const int Login = 20590, Profile = 170631, Stack = 170427, Chat = 20120;
    public const ulong Character = 0x0000_0123_4560_0001;
    public const string SecretName = "SyntheticHeroName";

    public static ClientCaptureLayout Layout { get; } = new("a".PadRight(64, 'a'), "b".PadRight(64, 'b'), new(Login, Profile, Stack), Schema())
    {
        Opcodes = new Dictionary<string, uint>
        {
            ["CHANNEL_MyCharacterPersonalData_NTF"] = Login,
            ["REPLICATION_GuildMembers_UpdateGuildMemberInfo_REPL"] = Profile,
            ["REPLICATION_ConsumableInventory_UpdateConsumableStack_REPL"] = Stack,
        },
        Methods = new Dictionary<uint, InventoryProtocolMethod> { [Profile] = new("UpdateGuildMemberInfo", "GuildMembers", ["AccountId", "Profile"]) },
    };

    private static JsonElement Schema()
    {
        var types = new Dictionary<string, object>();
        void Record(string name, string? parent, params (string Name, string Type)[] fields) =>
            types.Add(name, new { parent = parent ?? "object", fields = fields.Select(f => new { name = f.Name, type = f.Type }).ToArray() });
        Record("Base", null, ("CharacterId", "int64"));
        Record(ClientInventoryDecoder.RootType, "Base", ("ConsumableInventory", "Consumables"));
        Record("Stack", null, ("SheetId", "uint32"), ("Amount", "int64"));
        Record("Consumables", null, ("ConsumableStacks", "System.Collections.Generic.Dictionary`2<uint32,Stack>"));
        Record("AccountId", null, ("rawValue", "int64"));
        Record("Recent", null, ("CharacterId", "int64"), ("Name", "string"));
        Record("Profile", null, ("<RecentCharacterId>k__BackingField", "int64"), ("<RecentCharacter>k__BackingField", "Recent"));
        return JsonSerializer.SerializeToElement(new { types });
    }

    public static byte[] LoginBody(int items)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(Character); w.Write(items);
        for (var i = 0; i < items; i++) { w.Write((uint)(50000 + i)); w.Write((uint)(50000 + i)); w.Write(1L + i); }
        return stream.ToArray();
    }

    public static byte[] ProfileBody()
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write((long)(Character / 0x100000)); w.Write(Character); w.Write(Character);
        var name = Encoding.Unicode.GetBytes(SecretName); w.Write(name.Length); w.Write(name); return stream.ToArray();
    }

    public static byte[] Frame(int code, byte[] body, bool compressed = false)
    {
        if (compressed)
        {
            using var memory = new MemoryStream(); using (var encoder = new BrotliStream(memory, CompressionLevel.Fastest, true)) encoder.Write(body);
            body = memory.ToArray();
        }
        var bytes = new byte[9 + body.Length]; BinaryPrimitives.WriteInt32LittleEndian(bytes, code);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), body.Length); bytes[8] = compressed ? (byte)1 : (byte)0; body.CopyTo(bytes, 9); return bytes;
    }

    /// <summary>
    /// c1：远端 1111，含 SYN-ACK，依次为聊天帧、压缩登录体（3 项）、聊天帧；身份关联按参数放在 c1 或独立的 c2（远端 2222）。
    /// </summary>
    public static void Write(string directory, bool profileOnLoginConnection)
    {
        Directory.CreateDirectory(directory);
        var chat = Frame(Chat, Encoding.Unicode.GetBytes("hello " + SecretName));
        var c1 = new List<byte[]> { chat, Frame(Login, LoginBody(3), compressed: true), chat };
        var c2 = new List<byte[]> { chat };
        (profileOnLoginConnection ? c1 : c2).Add(Frame(Profile, ProfileBody()));
        (profileOnLoginConnection ? c1 : c2).Add(chat);
        var lines = new List<string>();
        void Emit(string id, double at, uint sequence, byte flags, byte[] payload) => lines.Add(JsonSerializer.Serialize(new
        { index = lines.Count + 1, monotonicMs = at, connectionId = id, linktype = 0, tcpSequence = sequence, tcpFlags = flags, payloadBase64 = Convert.ToBase64String(payload) }));
        Emit("conn-one", 100, 999, 0x12, []);
        uint sequence = 1000; var time = 200d;
        foreach (var frame in c1) { for (var at = 0; at < frame.Length; at += 600) Emit("conn-one", time, sequence + (uint)at, 0x18, frame[at..Math.Min(frame.Length, at + 600)]); sequence += (uint)frame.Length; time += 300; }
        sequence = 5000; time = 400;
        foreach (var frame in c2) { Emit("conn-two", time, sequence, 0x18, frame); sequence += (uint)frame.Length; time += 300; }
        File.WriteAllLines(Path.Combine(directory, "packets.jsonl"), lines);
        File.WriteAllText(Path.Combine(directory, "connections.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            connections = new object[]
            {
                new { connectionId = "conn-one", localPort = 50000, remotePort = 1111, remoteScope = "loopback", firstSeenTableMs = 150.0, closedTableMs = (double?)null,
                    synAckMs = 100.0, finMs = (double?)null, rstMs = (double?)null, prearmedPackets = 1 },
                new { connectionId = "conn-two", localPort = 50001, remotePort = 2222, remoteScope = "loopback", firstSeenTableMs = 350.0, closedTableMs = 2000.0,
                    synAckMs = (double?)null, finMs = 1900.0, rstMs = (double?)null, prearmedPackets = 0 },
            },
        }));
        File.WriteAllText(Path.Combine(directory, "summary.json"), "{\"reason\":\"capture.durationComplete\"}");
    }
}

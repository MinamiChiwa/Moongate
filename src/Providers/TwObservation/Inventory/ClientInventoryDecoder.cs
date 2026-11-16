// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
// 来源：MabiAgent ClientInventoryDecoder.cs，工作树 SHA256 ba50e65b2bffd324344c7ee3e602f68ced0e0057d81d434bfc7d113b4606e482。
using System.Security.Cryptography;
using System.Text.Json;
using System.Globalization;
using System.Text;
using MabiTool.Contracts;

namespace MabiTool.Providers.TwObservation.Inventory;

/// <param name="StoppedAt">读取停止处的根字段名（其后的字段未读，见 <see cref="ClientInventoryDecoder.DecodeLogin"/>）；读到帧尾时为空。</param>
/// <param name="Wallet">登录数据里的钱包与补充时间（停止处之后按 <see cref="LoginWalletLocator"/> 定位）；读到帧尾时直接取根字段。</param>
public sealed record DecodedInventoryLogin(UInt64String CharacterId, IReadOnlyList<InventoryItem> Items,
    IReadOnlyList<InventoryCoverage> Coverage, UInt64String? Gold, string BodySha256, string? StoppedAt = null, LoginWallet? Wallet = null);
public sealed record InventoryUpdateLayout(string Category, InventoryContainer Container, string WireType,
    InventoryDeltaKind Kind, string? KeyType = null, bool Repeated = false, bool Collection = false);
public sealed record DecodedInventoryUpdate(IReadOnlyList<InventoryDelta> Changes);

public sealed class ClientInventoryDecoder
{
    public const string RootType = "Shared.Protocols.Replications.GroupReplicatedDatas.CharacterPersonalReplicatedData";
    public const int MaximumBodyBytes = 16 * 1024 * 1024;
    private readonly JsonElement types;
    private readonly InventoryWireSchema wire;
    public ClientInventoryDecoder(JsonElement schema)
    {
        if (!schema.TryGetProperty("types", out var source) || source.ValueKind != JsonValueKind.Object || source.EnumerateObject().Count() > 4096)
            throw new InvalidDataException("inventory.schemaInvalid");
        types = source.Clone(); wire = new(types);
    }
    public IReadOnlyList<string> Categories => types.TryGetProperty(RootType, out var root)
        ? root.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()!)
            .Where(IsInventory).SelectMany(n => n == "MemorialSagaInventory" ? new[] { "MemorialSagaItem", "MemorialSagaMinion" } :
                new[] { n[..^"Inventory".Length] }).ToArray() : [];
    private static bool IsInventory(string name) => name.EndsWith("Inventory", StringComparison.Ordinal);
    private static bool CapturedRoot(string name) => IsInventory(name) || name == "Wallet" ||
        name.EndsWith("Slots", StringComparison.Ordinal) || name.StartsWith("Equipped", StringComparison.Ordinal);
    public DecodedInventoryLogin DecodeLogin(byte[] body, CancellationToken cancellation = default)
    {
        CheckBody(body);
        try
        {
            var reader = new InventoryWireReader(body, wire, cancellation);
            // 按根字段顺序读取：库存域、装备槽都在任务字段之前；之后未选中的字段读不下去时停在那里，其后的 JamItem 与钱包按 LoginWalletLocator 定位。
            var root = reader.RootPrefix(RootType, CapturedRoot, out var stoppedAt);
            if (stoppedAt is null) reader.EnsureEnd();
            var character = root.Field("CharacterId")?.Unsigned();
            if (character is null or 0) throw new InvalidDataException("inventory.characterInvalid");
            var rootFields = root.Fields ?? throw new InvalidDataException("inventory.rootInvalid");
            // 钱包、补充时间与 JamItem：顺序读取停在 Quest 时按整段类型化读取定位（LoginWalletLocator）；读到帧尾的构建直接取根字段。
            var located = stoppedAt is not null ? LoginWalletLocator.Locate(body, wire, RootType, reader.Offset, cancellation)
                : new LocatedLogin(new LoginWallet(root.Field("Wallet") is { } direct ? WalletValues(direct) : null, null, null,
                    root.Field("Wallet") is null ? "inventory.walletNotFound" : null, "inventory.refillNotFound"), null, null);
            var domains = rootFields.Where(p => IsInventory(p.Key)).ToList();
            if (located.JamItems is { } jamItems && !rootFields.ContainsKey("JamItemInventory")) domains.Add(new("JamItemInventory", jamItems));
            var equipped = new HashSet<ulong>();
            foreach (var field in rootFields.Where(p => !IsInventory(p.Key) && p.Key != "Wallet")) CollectInstances(field.Value, equipped);
            foreach (var domain in domains)
                if (domain.Value.Field("EquippedCostumes") is { } costumes) CollectInstances(costumes, equipped);
            var items = new List<InventoryItem>(); var coverage = new List<InventoryCoverage>();
            foreach (var domain in domains)
            {
                var defaultCategory = domain.Key[..^"Inventory".Length];
                if (domain.Value.Fields is null) throw new InvalidDataException("inventory.domainUnsupported:" + domain.Key);
                var complete = new HashSet<(string Category, InventoryContainer Container)>();
                foreach (var field in domain.Value.Fields)
                {
                    if (field.Value.Pairs is null || field.Key is "EquippedCostumes" or "MusicNoteAlbums" or "MusicNoteItemAlbumMap") continue;
                    var category = defaultCategory == "MemorialSaga" ? field.Key switch
                    { "MemorialSagaItems" => "MemorialSagaItem", "MemorialSagaMinions" => "MemorialSagaMinion", _ => defaultCategory } : defaultCategory;
                    var stored = field.Key.EndsWith("InStorage", StringComparison.Ordinal);
                    var container = field.Key.EndsWith("InAccountStorage", StringComparison.Ordinal) ? InventoryContainer.AccountStorage :
                        field.Key.EndsWith("InCharacterStorage", StringComparison.Ordinal) ? InventoryContainer.CharacterStorage : InventoryContainer.Backpack;
                    if (stored)
                    {
                        complete.Add((category, InventoryContainer.AccountStorage)); complete.Add((category, InventoryContainer.CharacterStorage));
                    }
                    else complete.Add((category, container));
                    foreach (var pair in field.Value.Pairs)
                    {
                        var actual = container;
                        var value = pair.Value;
                        if (stored)
                        {
                            if (value.Field("IsAccountStorage")?.Scalar is not bool account) throw new InvalidDataException("inventory.storageIdentityMissing");
                            actual = account ? InventoryContainer.AccountStorage : InventoryContainer.CharacterStorage;
                            value = value.Field("Data") ?? throw new InvalidDataException("inventory.storageDataMissing");
                        }
                        var item = Item(category, actual, pair.Key, value, pair.KeyHex);
                        if (item.InstanceId is { } instance && equipped.Contains(instance.Value)) item = item with { Equipped = true };
                        items.Add(item);
                        if (items.Count > 10000) throw new InvalidDataException("inventory.itemBudget");
                    }
                }
                if (complete.Count == 0) throw new InvalidDataException("inventory.domainUnsupported:" + domain.Key);
                foreach (var category in complete.Select(p => p.Category).Distinct())
                    foreach (var container in Enum.GetValues<InventoryContainer>())
                        coverage.Add(new(category, container, complete.Contains((category, container)),
                            complete.Contains((category, container)) ? null : "inventory.containerNotDeclared"));
            }
            if (coverage.Count == 0) throw new InvalidDataException("inventory.domainsMissing");
            // 停止处之后、也没有定位到的库存域：类别照常列出，各容器标为未读。
            if (stoppedAt is not null)
                foreach (var unread in Categories.Where(category => !coverage.Any(value => value.Category == category)))
                    foreach (var container in Enum.GetValues<InventoryContainer>()) coverage.Add(new(unread, container, false, "inventory.domainUnread"));
            UInt64String? gold = located.Wallet.Values is { } wallet && wallet.TryGetValue("goldPaid", out var paid) && wallet.TryGetValue("goldFree", out var free)
                ? new(checked(paid + free)) : null;
            return new(new(character.Value), items, coverage, gold, Convert.ToHexStringLower(SHA256.HashData(body)), stoppedAt, located.Wallet);
        }
        catch (Exception error) when (error is ArgumentException or KeyNotFoundException or InvalidOperationException or OverflowException)
        { throw new InvalidDataException("inventory.schemaOrBodyInvalid", error); }
    }
    private static IReadOnlyDictionary<string, ulong>? WalletValues(InventoryWireValue wallet)
    {
        var result = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var (key, _, _) in WalletProtocol.Fields)
            if (wallet.Field(key)?.Unsigned() is { } value) result[key] = value; else return null;
        return result;
    }
    public DecodedInventoryUpdate DecodeUpdate(byte[] body, InventoryUpdateLayout layout, CancellationToken cancellation = default)
    {
        CheckBody(body); var reader = new InventoryWireReader(body, wire, cancellation);
        var changes = new List<InventoryDelta>();
        var count = layout.Collection ? reader.Count() : 1;
        do
        {
            for (var i = 0; i < count; i++)
            {
                var key = layout.KeyType is null ? null : reader.Value(layout.KeyType);
                var value = reader.Value(layout.WireType);
                if (key is null) key = value.Field("Key") ?? value.Field("Id") ??
                    value.Field("SheetId") ?? value;
                var hex = Convert.ToHexStringLower(body.AsSpan(key.Start, key.End - key.Start));
                var item = Item(layout.Category, layout.Container, key, value, hex, layout.Kind == InventoryDeltaKind.Remove);
                changes.Add(new(layout.Kind, item));
                if (changes.Count > 10000) throw new InvalidDataException("inventory.itemBudget");
            }
        } while (layout.Repeated && reader.Offset < body.Length);
        reader.EnsureEnd();
        if (changes.Select(p => p.Item.Key).Distinct(StringComparer.Ordinal).Count() != changes.Count) throw new InvalidDataException("inventory.duplicateUpdate");
        return new(changes);
    }
    public DecodedInventoryUpdate DecodeMethod(byte[] body, InventoryProtocolMethod method, CancellationToken cancellation = default)
    {
        if (method.Parameters.Count is < 1 or > 2 || !(method.Name.StartsWith("Add", StringComparison.Ordinal) ||
            method.Name.StartsWith("Update", StringComparison.Ordinal) || method.Name.StartsWith("Remove", StringComparison.Ordinal)) ||
            method.Name.Contains("Album", StringComparison.Ordinal) || method.Name.Contains("Equipped", StringComparison.Ordinal))
            throw new InvalidDataException("inventory.updateUnsupported");
        var category = method.Category == "MemorialSaga" ? method.Name.Contains("Minion", StringComparison.Ordinal) ? "MemorialSagaMinion" : "MemorialSagaItem" : method.Category;
        var container = method.Name.Contains("AccountStorage", StringComparison.Ordinal) ? InventoryContainer.AccountStorage :
            method.Name.Contains("CharacterStorage", StringComparison.Ordinal) ? InventoryContainer.CharacterStorage : InventoryContainer.Backpack;
        var kind = method.Name.StartsWith("Remove", StringComparison.Ordinal) ? InventoryDeltaKind.Remove : InventoryDeltaKind.Upsert;
        CheckBody(body); var reader = new InventoryWireReader(body, wire, cancellation); var changes = new List<InventoryDelta>();
        do
        {
            var key = reader.Value(method.Parameters[0]);
            var value = method.Parameters.Count == 2 ? reader.Value(method.Parameters[1]) : key;
            if (method.Parameters.Count == 1 && kind != InventoryDeltaKind.Remove) key = value.Field("Id") ?? value.Field("Key") ?? value.Field("SheetId") ?? key;
            var actual = container;
            if (method.Name.Contains("Storage", StringComparison.Ordinal) && value.Field("IsAccountStorage")?.Scalar is bool account)
            { actual = account ? InventoryContainer.AccountStorage : InventoryContainer.CharacterStorage; value = value.Field("Data") ?? throw new InvalidDataException("inventory.storageDataMissing"); }
            var hex = Convert.ToHexStringLower(body.AsSpan(key.Start, key.End - key.Start));
            changes.Add(new(kind, Item(category, actual, key, value, hex, kind == InventoryDeltaKind.Remove)));
            if (changes.Count > 10000) throw new InvalidDataException("inventory.itemBudget");
        } while (reader.Offset < body.Length);
        reader.EnsureEnd();
        if (changes.Select(p => p.Item.Key).Distinct(StringComparer.Ordinal).Count() != changes.Count) throw new InvalidDataException("inventory.duplicateUpdate");
        return new(changes);
    }
    public InventoryIdentity DecodeIdentity(byte[] body, InventoryProtocolMethod method)
    {
        if (method.Name != "UpdateGuildMemberInfo" || method.Parameters.Count != 2) throw new InvalidDataException("inventory.identityUnsupported");
        CheckBody(body); var reader = new InventoryWireReader(body, wire);
        var account = reader.Value(method.Parameters[0]).Unsigned(); var profile = reader.Value(method.Parameters[1]); reader.EnsureEnd();
        // 字段名随构建变化：MabiAgent 时期为自动属性的后备字段，台服 2026-11 构建为普通字段。
        var character = (profile.Field("RecentCharacterId") ?? profile.Field("<RecentCharacterId>k__BackingField"))?.Unsigned();
        var recent = profile.Field("RecentCharacter") ?? profile.Field("<RecentCharacter>k__BackingField");
        if (account is null or 0 || character is null or 0 || recent?.Field("CharacterId")?.Unsigned() != character)
            throw new InvalidDataException("inventory.identityConflict");
        if (recent.Field("Name")?.Scalar is not string name || name.Length is < 1 or > 128 || string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
            throw new InvalidDataException("inventory.nameInvalid");
        return new(account.Value.ToString(CultureInfo.InvariantCulture), character.Value.ToString(CultureInfo.InvariantCulture), name, name.Normalize(NormalizationForm.FormKC));
    }
    private static InventoryItem Item(string category, InventoryContainer container, InventoryWireValue key,
        InventoryWireValue value, string hex, bool removal = false)
    {
        var sheet = value.Field("SheetId") ?? value.Field("sheetId") ?? key.Field("SheetId") ?? key.Field("sheetId") ??
            value.Fields?.FirstOrDefault(p => p.Key.EndsWith("SheetId", StringComparison.Ordinal)).Value ??
            (key.Type.EndsWith("SheetId", StringComparison.Ordinal) ? key : null);
        var typeId = sheet?.Unsigned() ?? (category == "JamItem" ? key.Unsigned() : null);
        var idField = value.Field("Id");
        var id = idField?.Type.EndsWith("NonStackableId", StringComparison.Ordinal) == true ? idField.Unsigned() : null;
        // 玩家自制乐谱（台服 2026-11 实采）表编号为 0，以实例 ID 区分；其他类别仍须有表编号。
        var composedScore = category == "MusicNoteItem" && typeId == 0 && (id is > 0 || key.Type.EndsWith("NonStackableId", StringComparison.Ordinal));
        if (!removal && typeId is null or 0 && !composedScore) throw new InvalidDataException("inventory.typeIdentityMissing:" + category);
        if (key.Type.EndsWith("NonStackableId", StringComparison.Ordinal))
        {
            var wireId = key.Unsigned();
            if (wireId is null or 0 || id.HasValue && id != wireId) throw new InvalidDataException("inventory.instanceIdentityConflict");
            id = wireId;
        }
        if (value.Field("Key") is { } copy && copy.JsonValue() is { } copyValue &&
            JsonSerializer.Serialize(copyValue) != JsonSerializer.Serialize(key.JsonValue())) throw new InvalidDataException("inventory.keyCopiesConflict");
        var amount = value.Field("Amount") ?? value.Field("Count") ?? value.Field("StackCount");
        ulong? quantity = amount is null ? value.Scalar is not null ? value.Unsigned() : id.HasValue ? 1UL : null : amount.Unsigned();
        if (amount is not null && !quantity.HasValue) throw new InvalidDataException("inventory.quantityInvalid");
        if (removal) quantity = 0;
        var table = sheet?.Type.Split('.').Last();
        if (table?.EndsWith("SheetId", StringComparison.Ordinal) == true) table = table[..^"SheetId".Length];
        else table = category;
        return new(category, container, hex, new(typeId ?? 0), id.HasValue ? new(id.Value) : null,
            quantity.HasValue ? new(quantity.Value) : null,
            JsonSerializer.SerializeToElement(new { key = key.JsonValue(), data = value.JsonValue() }), table);
    }
    private static void CollectInstances(InventoryWireValue value, HashSet<ulong> result)
    {
        if (value.Type.EndsWith("NonStackableId", StringComparison.Ordinal) && value.Unsigned() is { } id && id > 0) result.Add(id);
        if (value.Fields is not null) foreach (var child in value.Fields.Values) CollectInstances(child, result);
        if (value.Pairs is not null) foreach (var pair in value.Pairs) { CollectInstances(pair.Key, result); CollectInstances(pair.Value, result); }
        if (value.Elements is not null) foreach (var child in value.Elements) CollectInstances(child, result);
    }
    private static void CheckBody(byte[] body) { if (body.Length == 0 || body.Length > MaximumBodyBytes) throw new InvalidDataException("inventory.bodyBudget"); }
}

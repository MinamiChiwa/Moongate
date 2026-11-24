using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Providers.TwObservation.Inventory;
using MabiTool.Providers.TwObservation.ItemCatalog;
using Xunit;

namespace MabiTool.Providers.TwObservation.Tests;

/// <summary>
/// 台服 2026-11 构建的登录个人数据（结构按 2026-11-15 五份实采与客户端布局合成，不含实采内容）：任务字段按元数据顺序读不下去、
/// 玩家自制乐谱表编号为 0、身份关联字段为普通字段且在登录数据之后数秒到十余秒才到、期间夹着装备槽等无逐件方法的复制帧。
/// </summary>
public sealed class InventoryTw202611LoginTests
{
    private const uint Login = 1, Profile = 2, AddScore = 3, SlotUpdate = 4, SilverChange = 10, Increased = 11, SilverRefill = 12, Refilled = 13;
    private const long Character = 25000000000001;
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    [Fact]
    public void LoginStoppedAtQuestStillGivesFullAfterLateGuildIdentityAndAppliesQueuedChanges()
    {
        var frames = new List<InventoryFrame>(); ulong generation = 0;
        var observer = new InventoryProtocolObserver("game", new("fixture", "1", "TW", "offline"), Layout(), frames.Add, () => ++generation);
        observer.Accept((int)Login, LoginBody(), 0, At);
        Assert.Equal("inventory.awaitingIdentity", Assert.Single(frames).Reason);
        // 等待身份期间：装备槽帧跳过，乐谱增加暂存，其他公会成员与读不出的 170631 都不撤销。
        observer.Accept((int)SlotUpdate, [1, 2, 3], 1000, At);
        observer.Accept((int)AddScore, [.. BitConverter.GetBytes(13L), .. ScoreBody(13, 0)], 2000, At);
        observer.Accept((int)Profile, ProfileBody(Character + 1, "他人"), 3000, At);
        observer.Accept((int)Profile, [0xFF], 3001, At);
        observer.Tick(15000, At);
        Assert.Single(frames);
        // 实采中 170631 最晚在 20590 之后 16.4 s 到达。
        observer.Accept((int)Profile, ProfileBody(Character, "本机角色"), 16400, At);
        var full = frames[1];
        Assert.Equal(InventoryFrameKind.Full, full.Kind.KnownValue);
        Assert.Equal("7000001", full.Identity!.AccountId);
        Assert.Equal("本机角色", full.Identity.RawName);
        Assert.Equal(2, full.Items.Count);
        Assert.Equal(0UL, Assert.Single(full.Items, item => item.InstanceId?.Value == 12UL).TypeId.Value);
        Assert.Null(full.Gold);
        Assert.All(full.Coverage.Where(value => value.Category == "JamItem"), value => { Assert.False(value.Complete); Assert.Equal("inventory.domainUnread", value.Reason); });
        Assert.True(full.Coverage.Single(value => value.Category == "MusicNoteItem" && value.Container.KnownValue == InventoryContainer.Backpack).Complete);
        var delta = frames[2];
        Assert.Equal(InventoryFrameKind.Delta, delta.Kind.KnownValue);
        Assert.Equal(13UL, Assert.Single(delta.Changes).Item.InstanceId!.Value.Value);
        observer.Accept((int)SlotUpdate, [1, 2, 3], 17000, At);
        Assert.Equal(3, frames.Count);
    }

    [Theory]
    [InlineData("本机角色")]
    [InlineData(null)]
    public void WithoutGuildIdentityTheCharacterOwnsTheSnapshotOnlyWhenTheLocalNameIsKnown(string? name)
    {
        var frames = new List<InventoryFrame>(); ulong generation = 0;
        var observer = new InventoryProtocolObserver("game", new("fixture", "1", "TW", "offline"), Layout(), frames.Add, () => ++generation,
            character => character == (ulong)Character ? name : null);
        observer.Accept((int)Login, LoginBody(), 0, At);
        observer.Tick(InventoryProtocolObserver.IdentityWindowMs, At);
        Assert.Single(frames);
        observer.Tick(InventoryProtocolObserver.IdentityWindowMs + 1, At);
        if (name is null)
        {
            Assert.Equal("inventory.identityTimeout", frames[^1].Reason);
            return;
        }
        var full = frames[^1];
        Assert.Equal(InventoryFrameKind.Full, full.Kind.KnownValue);
        Assert.Equal(InventoryProtocolObserver.CharacterAccountPrefix + Character, full.Identity!.AccountId);
        Assert.Equal(Character.ToString(System.Globalization.CultureInfo.InvariantCulture), full.Identity.CharacterId);
        Assert.Equal(name, full.Identity.RawName);
    }

    [Fact]
    public void WalletMessagesBecomeResourceChangesOnlyOnceTheCharacterIsKnown()
    {
        var frames = new List<InventoryFrame>(); ulong generation = 0;
        var observer = new InventoryProtocolObserver("game", new("fixture", "1", "TW", "offline"), Layout(), frames.Add, () => ++generation);
        // 本连接还没有登录数据：无法归属到角色，跳过。
        observer.Accept((int)SilverChange, BitConverter.GetBytes(61L), 0, At);
        Assert.Empty(frames);
        observer.Accept((int)Login, LoginBody(), 10, At);
        // 等待身份期间：变化消息与增减通知暂存；未知 MoneyType 与长度不符的负载跳过。
        observer.Accept((int)SilverChange, BitConverter.GetBytes(62L), 20, At);
        observer.Accept((int)Increased, [.. BitConverter.GetBytes(9), .. BitConverter.GetBytes(2L)], 21, At);
        observer.Accept((int)Increased, [.. BitConverter.GetBytes(99), .. BitConverter.GetBytes(5L)], 22, At);
        observer.Accept((int)SilverChange, [1, 2, 3], 23, At);
        observer.Accept((int)SilverRefill, BitConverter.GetBytes(1794730369529L), 24, At);
        observer.Accept((int)Profile, ProfileBody(Character, "本机角色"), 5000, At);
        var full = frames.Single(f => f.Kind.KnownValue == InventoryFrameKind.Full);
        var meta = Assert.Single(full.Resources!, r => r.Key == "silverCoin");
        Assert.Equal(("銀幣", "進入地下城、狩獵場時支付的入場費", 1800d, (UInt64String?)null), (meta.RawName, meta.Description, meta.RefillIntervalSeconds, meta.Value));
        var changes = frames.SkipWhile(f => f != full).Skip(1).Select(f => Assert.Single(f.Resources!)).ToList();
        Assert.Equal(3, changes.Count);
        Assert.Equal(("silverCoin", 62UL), (changes[0].Key, changes[0].Value!.Value.Value));
        Assert.Equal(("silverCoin", 2UL), (changes[1].Key, changes[1].Increase!.Value.Value));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1794730369529), changes[2].RefilledAt);
        Assert.DoesNotContain(frames, f => f.Kind.KnownValue == InventoryFrameKind.Invalidated && f != frames[0]);
        // 补充通知按 CurrencyInfo 编号对应到银币。
        observer.Accept((int)Refilled, [.. BitConverter.GetBytes(5288547UL), .. BitConverter.GetBytes(875550523u)], 6000, At);
        Assert.True(Assert.Single(frames[^1].Resources!).RefillNotified);
    }

    /// <summary>
    /// 登录数据停在任务字段之后，钱包、补充时间与 JamItem 按整段类型化读取定位（结构按台服 2026-11 客户端的根字段顺序合成，数值脱敏）：
    /// 唯一定位时完整同步带全部钱包数值与补充时间，其后的变化消息与登录值连续；钱包段出现两处时不给出数值。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoginWalletIsLocatedAfterTheStopAndContinuesIntoChangeMessages(bool ambiguous)
    {
        var frames = new List<InventoryFrame>(); ulong generation = 0;
        var observer = new InventoryProtocolObserver("game", new("fixture", "1", "TW", "offline"), Layout() with { Schema = WalletSchema() }, frames.Add, () => ++generation);
        observer.Accept((int)Login, WalletLoginBody(ambiguous), 0, At);
        observer.Accept((int)SilverChange, BitConverter.GetBytes(62L), 100, At);
        observer.Accept((int)Increased, [.. BitConverter.GetBytes(9), .. BitConverter.GetBytes(2L)], 101, At);
        observer.Accept((int)SilverRefill, BitConverter.GetBytes(SilverRefilled + 2 * 1800_000), 102, At);
        observer.Accept((int)Profile, ProfileBody(Character, "本机角色"), 3000, At);
        var full = frames.Single(f => f.Kind.KnownValue == InventoryFrameKind.Full);
        var login = full.Resources!.ToDictionary(r => r.Key);
        var jam = full.Coverage.Single(c => c.Category == "JamItem" && c.Container.KnownValue == InventoryContainer.Backpack);
        if (ambiguous)
        {
            // 钱包段两处都读得通：不猜，数值为空（视图未读取）；JamItem 依赖钱包起点，同样未读。
            Assert.Null(full.Gold);
            Assert.All(full.Resources!, r => Assert.Null(r.Value));
            Assert.Equal((false, "inventory.domainUnread"), (jam.Complete, jam.Reason));
            return;
        }
        Assert.Equal(988534UL, full.Gold!.Value.Value);
        Assert.Equal((60UL, 1UL, 529UL, 20UL), (login["silverCoin"].Value!.Value.Value, login["passDeep"].Value!.Value.Value,
            login["heartToken"].Value!.Value.Value, login[WalletProtocol.AbyssPrefix + "FossilofAbyss"].Value!.Value.Value));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(SilverRefilled), login["silverCoin"].RefilledAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(PassDeepRefilled), login["passDeep"].RefilledAt);
        Assert.Equal((true, (string?)null), (jam.Complete, jam.Reason));
        Assert.Equal(3UL, Assert.Single(full.Items, i => i.Category == "JamItem").Quantity!.Value.Value);
        // 登录值 + 增加通知 = 其后的变化消息；补充时间按整数个间隔后移。
        var changes = frames.SkipWhile(f => f != full).Skip(1).Select(f => Assert.Single(f.Resources!)).ToList();
        Assert.Equal(login["silverCoin"].Value!.Value.Value + changes[1].Increase!.Value.Value, changes[0].Value!.Value.Value);
        Assert.Equal(TimeSpan.FromSeconds(2 * 1800), changes[2].RefilledAt - login["silverCoin"].RefilledAt);
    }

    /// <summary>
    /// 账户主角色（<c>AccountProfile.MasterCharacterId</c>，随补充时间段读出）由 170631 关联到游戏账户后，同一游戏进程的下一条连接
    /// 在登录数据到达时即以该账户完成同步；之后匹配的 170631 给出不同账户时撤销为身份冲突并停用该对应。
    /// </summary>
    [Fact]
    public void KnownMasterCharacterResolvesTheAccountAtLoginAndAConflictingGuildRecordRevokesIt()
    {
        var accounts = new InventoryAccountMemory(); var frames = new List<InventoryFrame>(); ulong generation = 0;
        InventoryProtocolObserver Connection() => new("game", new("fixture", "1", "TW", "offline"), Layout() with { Schema = WalletSchema() }, frames.Add, () => ++generation, accounts: accounts);
        var master = Character.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var first = Connection();
        first.Accept((int)Login, WalletLoginBody(false), 0, At);
        Assert.Single(frames);
        first.Accept((int)Profile, ProfileBody(Character, "本机角色"), 3000, At);
        Assert.Equal(("7000001", master), (frames[^1].Identity!.AccountId, frames[^1].Identity!.MasterCharacterId));
        // 换频道：新连接的登录数据到达即完整同步，账户与名称来自上一条连接的 170631。
        frames.Clear(); var second = Connection();
        second.Accept((int)Login, WalletLoginBody(false), 10000, At);
        Assert.Equal(InventoryFrameKind.Full, frames[^1].Kind.KnownValue);
        Assert.Equal(("7000001", "本机角色", master), (frames[^1].Identity!.AccountId, frames[^1].Identity!.RawName, frames[^1].Identity!.MasterCharacterId));
        second.Accept((int)Profile, ProfileBody(Character, "本机角色"), 13000, At);
        Assert.Equal(InventoryFrameKind.Full, frames[^1].Kind.KnownValue);
        second.Accept((int)Profile, ProfileBody(Character, "本机角色", 7000002), 14000, At);
        Assert.Equal("7000001", accounts.Account((ulong)Character));
        // 下一条连接：170631 给出另一账户，撤销并停用对应；再下一条连接只凭主角色归户（master-），170631 到达后改用编号并发空增量。
        var third = Connection();
        third.Accept((int)Login, WalletLoginBody(false), 20000, At);
        third.Accept((int)Profile, ProfileBody(Character, "本机角色", 7000002), 21000, At);
        Assert.Equal("inventory.identityConflict", frames[^1].Reason);
        Assert.Null(accounts.Account((ulong)Character));
        frames.Clear(); var fourth = Connection();
        fourth.Accept((int)Login, WalletLoginBody(false), 30000, At);
        Assert.Equal((InventoryFrameKind.Full, InventoryProtocolObserver.MasterAccountPrefix + master), (frames[^1].Kind.KnownValue, frames[^1].Identity!.AccountId));
        fourth.Accept((int)Profile, ProfileBody(Character, "本机角色", 7000002), 31000, At);
        Assert.Equal((InventoryFrameKind.Delta, "7000002", 0), (frames[^1].Kind.KnownValue, frames[^1].Identity!.AccountId, frames[^1].Changes.Count));
    }

    /// <summary>没有公会、从未收到 170631 的角色：读出主角色且本机昵称已知时，登录数据到达即以 <c>master-</c> 账户完整同步，不等 30 秒窗口。</summary>
    [Fact]
    public void GuildlessCharacterJoinsItsMasterAccountAtLogin()
    {
        var frames = new List<InventoryFrame>(); ulong generation = 0;
        var observer = new InventoryProtocolObserver("game", new("fixture", "1", "TW", "offline"), Layout() with { Schema = WalletSchema() }, frames.Add, () => ++generation,
            character => character == (ulong)Character ? "本机角色" : null, new InventoryAccountMemory());
        observer.Accept((int)Login, WalletLoginBody(false), 0, At);
        var full = frames[^1];
        Assert.Equal((InventoryFrameKind.Full, InventoryProtocolObserver.MasterAccountPrefix + Character, "本机角色"),
            (full.Kind.KnownValue, full.Identity!.AccountId, full.Identity.RawName));
    }

    /// <summary>
    /// 补充上限的会员判定：没有生效会员为基础上限；全方位（<c>Combination</c>）生效且其余生效项都认识时加会员加成，其余三项只是并列的普通会员，
    /// 不带加成；会员数据没有读到、到期时刻读不出、有不认识的生效编号或只有其余三项生效（没有样本）时为空。
    /// </summary>
    [Fact]
    public void RefillCapFollowsTheActiveCombinationMembership()
    {
        uint combination = ClientRefillCaps.Combination, adventurer = 0x488CD169, pioneer = 0x0666828C, life = 0x1E25A5DE, unknown = 0x12345678;
        Assert.Equal(0x3D92AC47u, combination);
        var caps = new ClientRefillCaps(new Dictionary<string, int> { ["SilverCoin"] = 100, ["Pass_Deep"] = 10 },
            new Dictionary<string, int> { ["SilverCoin"] = 50, ["Pass_Deep"] = 5 }, new HashSet<uint> { adventurer, pioneer, life, combination });
        var now = DateTimeOffset.FromUnixTimeMilliseconds(SilverRefilled);
        LoginMembership Active(uint id) => new(id, now.AddDays(2));
        LoginMembership Expired(uint id) => new(id, now.AddSeconds(-1));
        (int?, int?) Caps(params LoginMembership[] memberships) => (caps.For("SilverCoin", memberships, now), caps.For("Pass_Deep", memberships, now));
        // 没有会员、全部到期、到期时刻恰为登录时刻：基础上限。
        Assert.Equal((100, 10), Caps());
        Assert.Equal((100, 10), Caps(Expired(adventurer), Expired(pioneer), Expired(life), Expired(combination)));
        Assert.Equal((100, 10), Caps(new LoginMembership(combination, now)));
        // 全方位生效：登录数据里与其余三项并列（四项）、只有全方位一项、其余三项已到期。
        Assert.Equal((150, 15), Caps(Active(adventurer), Active(pioneer), Active(life), Active(combination)));
        Assert.Equal((150, 15), Caps(Active(combination)));
        Assert.Equal((150, 15), Caps(Expired(adventurer), Expired(pioneer), Expired(life), Active(combination)));
        // 只有其余三项生效（全方位项缺失或已到期）：没有样本，未知。
        Assert.Equal(((int?)null, (int?)null), Caps(Active(adventurer), Active(pioneer), Active(life)));
        Assert.Equal(((int?)null, (int?)null), Caps(Active(life), Expired(combination)));
        // 不认识的生效编号：未知；不认识但已到期的项不影响判定。
        Assert.Equal(((int?)null, (int?)null), Caps(Active(combination), Active(unknown)));
        Assert.Equal((150, 15), Caps(Active(combination), Expired(unknown)));
        Assert.Equal((100, 10), Caps(Expired(unknown)));
        // 到期时刻读不出、没有读到会员、其他货币：未知。
        Assert.Equal(((int?)null, (int?)null), Caps(Active(combination), new LoginMembership(life, null)));
        Assert.Null(caps.For("SilverCoin", null, now));
        Assert.Null(caps.For("Gold", [], now));
        // 会员表或加成读不出：没有会员仍是基础上限，全方位生效时未知。
        var withoutTable = caps with { Memberships = null, MemberBonus = new Dictionary<string, int>() };
        Assert.Equal((100, (int?)null), (withoutTable.For("SilverCoin", [], now), withoutTable.For("SilverCoin", [Active(combination)], now)));
        // 契约 refillCap 的最大值为 1,000,000：加成后超过时未知。
        var large = caps with { Base = new Dictionary<string, int> { ["SilverCoin"] = ClientRefillCaps.MaximumCap - 10 } };
        Assert.Equal((ClientRefillCaps.MaximumCap - 10, (int?)null), (large.For("SilverCoin", [], now), large.For("SilverCoin", [Active(combination)], now)));
    }

    /// <summary>
    /// 会员生效的登录数据（四项并列，开始与到期时刻相同；结构取自台服 2026-11-24 的会员角色登录数据，数值为合成）：
    /// 完整同步帧的银币、魔族貢品上限在登录时刻之前为 150／15，到期之后重新登录为 100／10。
    /// </summary>
    [Fact]
    public void MembershipLoginGivesMemberCapsUntilItsExpiry()
    {
        uint[] ids = [0x488CD169, 0x0666828C, 0x1E25A5DE, ClientRefillCaps.Combination];
        var memberships = ids.Select(id => (id, Start: 1794000000000L, Expire: 1794200000000L)).ToArray();
        var caps = new ClientRefillCaps(new Dictionary<string, int> { ["SilverCoin"] = 100, ["Pass_Deep"] = 10 },
            new Dictionary<string, int> { ["SilverCoin"] = 50, ["Pass_Deep"] = 5 }, ids.ToHashSet());
        var layout = Layout() with { Schema = WalletSchema() };
        layout = layout with { Resources = layout.Resources! with { RefillCaps = caps } };
        (int?, int?) FullCaps(DateTimeOffset utc)
        {
            var frames = new List<InventoryFrame>(); ulong generation = 0;
            var observer = new InventoryProtocolObserver("game", new("fixture", "1", "TW", "offline"), layout, frames.Add, () => ++generation);
            observer.Accept((int)Login, WalletLoginBody(false, memberships: memberships), 0, utc);
            observer.Accept((int)Profile, ProfileBody(Character, "本机角色"), 3000, utc);
            var resources = frames.Single(f => f.Kind.KnownValue == InventoryFrameKind.Full).Resources!.ToDictionary(r => r.Key);
            return (resources["silverCoin"].RefillCap, resources["passDeep"].RefillCap);
        }
        var expire = DateTimeOffset.FromUnixTimeMilliseconds(memberships[0].Expire);
        Assert.Equal((150, 15), FullCaps(expire.AddHours(-1)));
        Assert.Equal((100, 10), FullCaps(expire.AddSeconds(1)));
    }

    private const long SilverRefilled = 1794726769529, PassDeepRefilled = 1794705984874;

    private static JsonElement WalletSchema()
    {
        var types = new Dictionary<string, object>();
        void Record(string name, string? parent, params (string Name, string Type)[] fields) =>
            types.Add(name, new { parent = parent ?? "object", fields = fields.Select(f => new { name = f.Name, type = f.Type }).ToArray() });
        Record("Base", null, ("CharacterId", "int64"));
        Record(ClientInventoryDecoder.RootType, "Base", ("MusicNoteItemInventory", "Music"), ("Quest", "Quest"), ("JamItemInventory", "Jam"),
            ("SkillReserve", "Reserve"), ("Wallet", "Wallet"), ("AccountExpirableMoney", "Expirable"), ("SkillSlot", "Slots"),
            ("FollowCameraForceAngle", "Angle"), ("SilverCoin", "Silver"), ("PassDeep", "Deep"), ("Membership", "Membership"), ("AccountProfile", "Profile"), ("Tail", "Tail"));
        Record("Music", null, ("MusicNoteItems", "System.Collections.Generic.Dictionary`2<Items.NonStackableId,Score>"));
        Record("Items.NonStackableId", "System.ValueType", ("rawValue", "int64"));
        Record("ScoreSheetId", "System.ValueType", ("Id", "int32"));
        Record("Score", null, ("Id", "Items.NonStackableId"), ("SheetId", "ScoreSheetId"));
        Record("Quest", null, ("DisplayName", "string"));
        Record("Jam", null, ("MyJamItems", "System.Collections.Generic.Dictionary`2<uint32,int64>"));
        Record("Reserve", null, ("Pending", "int32"));
        Record("Wallet", null, [.. WalletProtocol.Fields.Select(f => (f.Key, "int64")), ("AbyssRaidMoneySet", "System.Collections.Generic.Dictionary`2<uint8,int64>")]);
        Record("Expirable", null, ("Monies", "System.Collections.Generic.Dictionary`2<int32,int64>"));
        Record("Slots", null, ("SkillSlots", "System.Collections.Generic.Dictionary`2<int32,int32>"));
        Record("Angle", null, ("Value", "float32"));
        Record("RefillData", null, ("LastUpdateTime", "int64"));
        Record("Silver", null, ("SilverCoinRefillData", "RefillData"));
        Record("Deep", null, ("LastUpdatedTime", "int64"));
        Record("Core.Times.RealTime", "System.ValueType", ("rawValue", "int64"));
        Record("Shared.Symbols.MembershipSheetId", "System.ValueType", ("Id", "int32"));
        Record("MembershipInfo", "System.ValueType", ("Id", "Shared.Symbols.MembershipSheetId"), ("StartTime", "Core.Times.RealTime"), ("ExpireTime", "Core.Times.RealTime"),
            ("DailyRewardedTime", "Core.Times.RealTime"), ("WeeklyRewardedTime", "Core.Times.RealTime"));
        Record("Membership", null, ("ActivatedMemberships", "System.Collections.Generic.Dictionary`2<Shared.Symbols.MembershipSheetId,MembershipInfo>"));
        Record("Profile", null, ("MasterCharacterId", "int64"));
        Record("Tail", null, ("Value", "int32"));
        Record("AccountId", "System.ValueType", ("int64Value", "int64"));
        Record("CharacterId", "System.ValueType", ("int64Value", "int64"));
        Record("Recent", null, ("CharacterId", "CharacterId"), ("Name", "string"));
        Record("Member", null, ("RecentCharacterId", "CharacterId"), ("RecentCharacter", "Recent"));
        return JsonSerializer.SerializeToElement(new { types });
    }

    /// <summary>新角色的技能栏为空：钱包段只有一处读通时照常定位，不要求技能栏有内容。</summary>
    [Fact]
    public void NewCharacterWithEmptySkillSlotsStillGetsItsWallet()
    {
        var frames = new List<InventoryFrame>(); ulong generation = 0;
        var observer = new InventoryProtocolObserver("game", new("fixture", "1", "TW", "offline"), Layout() with { Schema = WalletSchema() }, frames.Add, () => ++generation);
        observer.Accept((int)Login, WalletLoginBody(false, emptySkills: true), 0, At);
        observer.Accept((int)Profile, ProfileBody(Character, "本机角色"), 3000, At);
        var full = frames.Single(f => f.Kind.KnownValue == InventoryFrameKind.Full);
        Assert.Equal(988534UL, full.Gold!.Value.Value);
        Assert.Equal(60UL, full.Resources!.Single(r => r.Key == "silverCoin").Value!.Value.Value);
    }

    private static byte[] WalletLoginBody(bool duplicateWallet, bool emptySkills = false, (uint Id, long Start, long Expire)[]? memberships = null)
    {
        var values = new Dictionary<string, long> { ["goldFree"] = 988534, ["silverCoin"] = 60, ["passDeep"] = 1, ["heartToken"] = 529, ["guildCoin"] = 1410, ["decaFree"] = 5548,
            // 最后一个字段的低 4 字节作为集合长度非法，错位一个 int64 的候选读不通（实采里由真实字段分布排除）。
            ["packagePoint"] = 999_999_999_999 };
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(Character);
        w.Write(1); w.Write(11L); w.Write(ScoreBody(11, 5));
        // 任务字段：文本长度非法，顺序读取停在这里。
        w.Write(-3); w.Write(Enumerable.Repeat((byte)0xEE, 40).ToArray());
        w.Write(1); w.Write(7001u); w.Write(3L);
        w.Write(0);
        for (var copy = 0; copy < (duplicateWallet ? 2 : 1); copy++)
        {
            foreach (var (key, _, _) in WalletProtocol.Fields) w.Write(values.GetValueOrDefault(key));
            w.Write(1); w.Write((byte)0); w.Write(20L);
            w.Write(0);
            if (emptySkills) w.Write(0);
            else { w.Write(2); w.Write(1); w.Write(101); w.Write(2); w.Write(102); }
            w.Write(0.5f);
        }
        w.Write(SilverRefilled); w.Write(PassDeepRefilled);
        // 会员字典：键 4 字节，值为编号加开始、到期、每日、每周领取时刻（RealTime 各 8 字节）。
        w.Write(memberships?.Length ?? 0);
        foreach (var (id, start, expire) in memberships ?? [])
        { w.Write(id); w.Write(id); w.Write(start); w.Write(expire); w.Write(start - 1000); w.Write(start - 1000); }
        w.Write(Character); w.Write(7);
        return stream.ToArray();
    }

    private static ClientCaptureLayout Layout() => new("assembly", "metadata", new(Login, Profile, AddScore), Schema())
    {
        Opcodes = new Dictionary<string, uint>
        {
            ["login"] = Login, ["profile"] = Profile, ["add"] = AddScore, ["slot"] = SlotUpdate,
            ["REPLICATION_Wallet_ChangeSilverCoin_REPL"] = SilverChange, [WalletProtocol.IncreasedName] = Increased,
            [WalletProtocol.SilverRefillName] = SilverRefill, [WalletProtocol.RefilledName] = Refilled,
        },
        Resources = new ClientResourceCatalog(new Dictionary<string, string> { ["SilverCoin"] = "銀幣", ["Gold"] = "金幣" },
            new Dictionary<string, TimeSpan> { ["SilverCoin"] = TimeSpan.FromSeconds(1800) }, new Dictionary<uint, string> { [875550523] = "SilverCoin" },
            new Dictionary<string, string> { ["SilverCoin"] = "進入地下城、狩獵場時支付的入場費" }),
        Methods = new Dictionary<uint, InventoryProtocolMethod>
        {
            [Profile] = new("UpdateGuildMemberInfo", "GuildMembers", ["AccountId", "Member"]),
            [AddScore] = new("AddMusicNoteItem", "MusicNoteItem", ["Items.NonStackableId", "Score"])
        }
    };

    private static JsonElement Schema()
    {
        var types = new Dictionary<string, object>();
        void Record(string name, string? parent, params (string Name, string Type)[] fields) =>
            types.Add(name, new { parent = parent ?? "object", fields = fields.Select(f => new { name = f.Name, type = f.Type }).ToArray() });
        Record("Base", null, ("CharacterId", "int64"));
        Record(ClientInventoryDecoder.RootType, "Base", ("MusicNoteItemInventory", "Music"), ("Quest", "Quest"), ("JamItemInventory", "Jam"), ("Wallet", "Wallet"));
        Record("Music", null, ("MusicNoteItems", "System.Collections.Generic.Dictionary`2<Items.NonStackableId,Score>"));
        Record("Items.NonStackableId", "System.ValueType", ("rawValue", "int64"));
        Record("ScoreSheetId", "System.ValueType", ("Id", "int32"));
        Record("Score", null, ("Id", "Items.NonStackableId"), ("SheetId", "ScoreSheetId"));
        Record("Quest", null, ("DisplayName", "string"));
        Record("Jam", null, ("Items", "System.Collections.Generic.Dictionary`2<uint32,int64>"));
        Record("Wallet", null, ("goldPaid", "int64"), ("goldFree", "int64"));
        Record("AccountId", "System.ValueType", ("int64Value", "int64"));
        Record("CharacterId", "System.ValueType", ("int64Value", "int64"));
        Record("Recent", null, ("CharacterId", "CharacterId"), ("Name", "string"));
        Record("Member", null, ("RecentCharacterId", "CharacterId"), ("RecentCharacter", "Recent"));
        return JsonSerializer.SerializeToElement(new { types });
    }

    private static byte[] LoginBody()
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(Character);
        w.Write(2); w.Write(11L); w.Write(ScoreBody(11, 5)); w.Write(12L); w.Write(ScoreBody(12, 0));
        // 任务字段：按元数据读到的文本长度非法，读取停在这里；其后的 JamItem 与钱包未读。
        w.Write(-5); w.Write(new byte[64]);
        return stream.ToArray();
    }

    private static byte[] ScoreBody(long id, int sheet)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(id); w.Write(sheet);
        return stream.ToArray();
    }

    private static byte[] ProfileBody(long character, string name, long account = 7000001)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(account); w.Write(character); w.Write(character);
        var text = Encoding.Unicode.GetBytes(name); w.Write(text.Length); w.Write(text);
        return stream.ToArray();
    }
}

namespace MabiTool.GameIconExtractor;

/// <summary>
/// 一个随包图标：<see cref="Group"/>/<see cref="Key"/>.png。<see cref="Origin"/> 是客户端资源路径（相对 Assets/PatchableAssets/），
/// 为空时由 <see cref="DrawnIcons"/> 按矢量形状自绘。<see cref="Glyph"/> 为真表示白色字形（界面按主题着色），否则为彩色图标原样显示。
/// <see cref="Names"/> 是游戏原文名称（生活技能与货币按名称对应图标），<see cref="MoneyTypes"/> 是该图标对应的钱包货币类型。
/// </summary>
internal sealed record IconSpec(string Group, string Key, string Role, string? Origin, bool Glyph = true,
    string[]? Names = null, string[]? MoneyTypes = null)
{
    public string File => Group + "/" + Key.Replace('.', '-') + ".png";
}

/// <summary>
/// 固定清单：属性图标经 <c>ClientAttributeIcons</c> 按客户端 ParameterInfo 表取（键与后端 /api/v1/client-icons 相同），
/// 其余按核对过的资源路径取。生活技能与货币的名称、路径取自客户端 LivingSkill 与 MoneyInfo 表（2026-11-26 台服客户端）。
/// </summary>
internal static class IconManifest
{
    private const string I64 = "UI/Sprites/Common/Icon64/";
    private const string Life = "UI/Sprites/LivingSkill/";
    private const string Item = "Icons/CommonIcon/Item_Icon/";

    private static string Coin(string name, int side = 64) => $"{Item}{name}_Textures/{name}_{side}.png";

    /// <summary>属性图标的键与用途（简体），顺序同后端目录。</summary>
    public static readonly (string Key, string Role)[] Attributes =
    [
        ("critical", "暴击"), ("powerfulHit", "强击"), ("rapidHit", "连击"), ("doubleHit", "追击"), ("breakedHit", "无防备"),
        ("breakOccurred", "破防"), ("multiHit", "多重打击"), ("element.bleed", "出血"), ("element.dark", "暗黑"), ("element.fire", "火焰"),
        ("element.holy", "神圣"), ("element.ice", "冰冻"), ("element.lightning", "触电"), ("element.poison", "毒"), ("element.soul", "精神")
    ];

    public static readonly IconSpec[] Sprites =
    [
        // 角色概况与评分
        new("character", "nickname", "昵称", I64 + "Icon64_Profile.png"),
        new("character", "server", "服务器", null),
        new("character", "title", "称号", I64 + "Icon64_TitleMedal.png"),
        new("character", "level", "等级", I64 + "Icon64_Level.png"),
        new("character", "job", "战斗职业", I64 + "Icon64_Weapon.png"),
        new("character", "combatScore", "战斗力", I64 + "Icon64_Combat.png"),
        new("character", "livingScore", "生活力", I64 + "Icon64_Sprout.png"),
        new("character", "attractivenessScore", "魅力", I64 + "Icon64_Like_line.png"),
        new("character", "decorScore", "装饰分数", I64 + "Icon64_DecoScore.png"),
        // 属性（get_my_info 的字段名）
        new("stat", "HealthMax", "最大体力", I64 + "Icon64_State_Health.png"),
        new("stat", "AttackPower", "攻击力", I64 + "Icon64_Buff_Damage.png"),
        new("stat", "DefencePower", "防御力", I64 + "Icon64_Shield_B.png"),
        new("stat", "STR", "力量", I64 + "Icon64_State_Strength.png"),
        new("stat", "DEX", "敏捷", I64 + "Icon64_State_Dexterity.png"),
        new("stat", "INT", "智力", I64 + "Icon64_State_Intellect.png"),
        new("stat", "LUCK", "幸运", I64 + "Icon64_State_Luck.png"),
        new("stat", "WILL", "意志", I64 + "Icon64_State_Will.png"),
        new("stat", "ArcaneResistance", "魔法抗性", I64 + "Icon64_MagicDefense.png"),
        new("stat", "paladin", "圣职属性", I64 + "Icon64_Paladin.png"),
        // 状态
        new("status", "health", "体力", I64 + "Icon64_HPDrop.png"),
        new("status", "shield", "护盾", I64 + "Icon64_Buff_Shield.png"),
        new("status", "satiety", "饱腹", I64 + "Icon64_Stomach.png"),
        new("status", "weight", "背包重量", I64 + "Icon64_Weight_B.png"),
        new("status", "buffs", "生效 Buff", I64 + "Icon64_Buff_PotionEffect.png"),
        // 任务与成长
        new("mission", "missions", "任务", I64 + "Icon64_Mission.png"),
        new("mission", "daily", "日常", null),
        new("mission", "weekly", "周常", I64 + "Icon64_Calendar_W.png"),
        new("mission", "ready", "已完成待领奖", I64 + "Icon64_GiftSelect.png"),
        new("mission", "growth", "成长指南", I64 + "Icon64_LevelUp2.png"),
        // 周边
        new("surroundings", "area", "区域", I64 + "Icon64_MapTown.png"),
        new("surroundings", "channel", "频道", I64 + "Icon64_ChatChannelSelect.png"),
        new("surroundings", "position", "位置", "UI/Sprites/Common/Icon/Icon_Locate.png"),
        new("surroundings", "time", "游戏内时间", I64 + "Icon64_ClockLine.png"),
        new("surroundings", "housing", "住宅", I64 + "Icon64_Sofa.png"),
        new("surroundings", "players", "附近玩家", I64 + "Icon64_Community.png"),
        new("surroundings", "performing", "演奏中", I64 + "Icon64_MusicPlaying.png"),
        new("weather", "sunny", "晴", I64 + "Icon64_Weather_Sunny.png"),
        new("weather", "cloudy", "多云", null),
        new("weather", "fog", "雾", null),
        new("weather", "rain", "雨", I64 + "Icon64_Weather_Rainy.png"),
        new("weather", "drizzle", "小雨", I64 + "Icon64_Weather_Drizzle.png"),
        new("weather", "storm", "雷雨", I64 + "Icon64_Weather_HeavyRain.png"),
        new("weather", "snow", "雪", I64 + "Icon64_Weather_Snowflake.png"),
        new("weather", "night", "夜晚", I64 + "Icon64_Weather_Night.png"),
        // 背包
        new("inventory", "items", "道具种类", I64 + "Icon64_Bag.png"),
        new("inventory", "quantity", "总件数", I64 + "Icon64_Stack.png"),
        new("inventory", "valuation", "估价", I64 + "Icon64_TradeMarket_Circle.png"),
        new("inventory", "coverage", "读取范围", I64 + "Icon64_Contents.png"),
        new("inventory", "catalog", "道具名称目录", I64 + "Icon64_Book.png"),
        new("inventory", "currency", "货币（其他）", I64 + "Icon64_GoldBag.png"),
        new("inventory", "membership", "会员", I64 + "Icon64_Membership.png"),
        // 生活技能（客户端 LivingSkill 表的 DisplayName 与 IconPath）
        new("life", "usualGathering", "日常采集", Life + "LivingSkill_UsualGathering.png", Names: ["日常採集"]),
        new("life", "logging", "伐木", Life + "LivingSkill_Logging.png", Names: ["伐木"]),
        new("life", "mining", "采矿", Life + "LivingSkill_Mining.png", Names: ["採礦"]),
        new("life", "herbGathering", "采集药草", Life + "LivingSkill_HerbGathering.png", Names: ["採集藥草"]),
        new("life", "sheepShearing", "剪羊毛", Life + "LivingSkill_SheepShearing.png", Names: ["剪羊毛"]),
        new("life", "sickling", "收割", Life + "LivingSkill_Sickling.png", Names: ["收割"]),
        new("life", "hoeing", "锄地", Life + "LivingSkill_Hoeing.png", Names: ["鋤地"]),
        new("life", "insect", "昆虫采集", Life + "LivingSkill_Insect.png", Names: ["昆蟲採集"]),
        new("life", "fishing", "钓鱼", Life + "LivingSkill_Fishing.png", Names: ["釣魚"]),
        new("life", "blacksmithing", "锻造技术", Life + "LivingSkill_Blacksmithing.png", Names: ["鍛造技術"]),
        new("life", "carpentry", "木工", Life + "LivingSkill_Carpentry.png", Names: ["木工"]),
        new("life", "magicCraft", "魔法工艺", Life + "LivingSkill_MagicCraft.png", Names: ["魔法工藝"]),
        new("life", "heavyArmoring", "重甲制作", Life + "LivingSkill_HeavyArmoring.png", Names: ["重甲製作"]),
        new("life", "lightArmoring", "轻甲制作", Life + "LivingSkill_LightArmoring.png", Names: ["輕甲製作"]),
        new("life", "tailoring", "布衣制作", Life + "LivingSkill_Tailoring.png", Names: ["布衣製作"]),
        new("life", "potionMaking", "药水调制", Life + "LivingSkill_PotionMaking.png", Names: ["藥水調製"]),
        new("life", "cooking", "料理", Life + "LivingSkill_Cooking.png", Names: ["料理"]),
        new("life", "handicraft", "手工艺", Life + "LivingSkill_Handicraft.png", Names: ["手工藝"]),
        new("life", "alchemy", "炼金术", Life + "LivingSkill_Alchemy.png", Names: ["鍊金術"]),
        new("life", "arbeit", "兼职", I64 + "Icon64_GoldBag.png", Names: ["兼職"]),
        // 货币（客户端 MoneyInfo 表的 DisplayName 与 IconPath，取 64 像素版本；金币只有 128 像素，缩到 64）
        new("currency", "gold", "金币", Coin("Coin_Gold", 128), false, ["金幣"], ["Gold"]),
        new("currency", "silverCoin", "银币", Coin("Coin_SilverCoin"), false, ["銀幣"], ["SilverCoin"]),
        new("currency", "passDeep", "魔族贡品", Coin("Item_PassDeep"), false, ["魔族貢品"], ["Pass_Deep"]),
        new("currency", "heartToken", "爱心币", Coin("Coin_SouthRegionToken"), false, ["愛心幣"], ["Coin_SouthRegionToken"]),
        new("currency", "guildCoin", "公会币", Coin("Token_Guild"), false, ["公會幣"], ["GuildCoin"]),
        new("currency", "catToken", "喵币", Coin("Coin_CatToken"), false, ["喵幣"], ["Coin_CatToken"]),
        new("currency", "renewalTicket", "刷新券", Coin("Coin_RenewalTicket"), false, ["刷新券"], ["RenewalTicket"]),
        new("currency", "monsterKillToken", "魔物讨伐证明", Coin("Coin_MonsterKillToken"), false, ["魔物討伐證明"], ["Coin_MonsterKillToken"]),
        new("currency", "weca", "韦卡", Coin("Coin_Weka"), false, ["韋卡"], ["Weca"]),
        new("currency", "deca", "德卡", Coin("Coin_Deka"), false, ["德卡"], ["RedCrystal"]),
        new("currency", "mCash", "M币", Coin("Coin_MMCash"), false, ["M幣", "M幣(免費)"], ["Crystal", "FreeCrystal"]),
        new("currency", "cashShopPoint", "商城点数", Coin("Coin_CashShopPoint"), false, ["商城點數"], ["Mileage"]),
        new("currency", "packagePoint", "套组点数", Coin("Coin_PackagePoint"), false, ["套組點數"], ["SpecialCoin"]),
        new("currency", "memorialSagaCoin", "纪念传说货币", Coin("Coin_MemorialSaga"), false, [], ["MemorialSagaCoin"])
    ];
}

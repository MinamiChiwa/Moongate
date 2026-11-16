using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

public enum LocalBindingKind { Key, Button, ChatDraft }
public enum MusicStopRule { Manual, Duration, FirstCycle }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InputBindingPreference(OpenEnum<LocalBindingKind> Kind, ushort? Key = null,
    bool Control = false, bool Alt = false, bool Shift = false, int? X = null, int? Y = null,
    int? ClientWidth = null, int? ClientHeight = null, uint? Dpi = null);
public sealed record ModeInputPreferences(InputBindingPreference? Start, InputBindingPreference? Stop, bool StopIsToggle);
public sealed record RoomPreferences(UInt64String Revision, string Endpoint, string PublicProvider, string LanEndpoint,
    string Nickname, int CountdownMs, int TimingOffsetMs, int MusicDurationMs, int MusicTailHoldMs,
    OpenEnum<MusicStopRule> MusicStopRule, bool HideFromCapture, double OverlayOpacity,
    IReadOnlyDictionary<string, ModeInputPreferences> Bindings, IReadOnlyDictionary<string, int> StartInputKinds,
    bool RequiresGameBinding, string LocalStopActionId)
{
    /// <summary>原工具内置的服务器直连入口，使用系统默认证书校验。</summary>
    public const string BuiltInCommunityEndpoint = "wss://47.111.3.134/";
    public string CommunityEndpoint => BuiltInCommunityEndpoint;
    public static RoomPreferences Default => new(new(0), "", "community", "", "", 5000, 0, 30000, 0,
        global::MabiTool.Contracts.MusicStopRule.FirstCycle, false, .9,
        new Dictionary<string, ModeInputPreferences> { ["emote"] = new(null, null, false),
            ["independent"] = new(null, null, false), ["ensemble"] = new(null, null, false) },
        new Dictionary<string, int> { ["emote"] = 0, ["independent"] = 0, ["ensemble"] = 0 }, true, "emergency-stop");
}

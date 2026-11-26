using System.Text;

namespace MabiTool.Providers.TwCli;

public static class TwCliCodes
{
    /// <summary>把游戏给出的 snake_case 原因（如 <c>not_available_on_combat</c>）规范为 <c>gameCli.notAvailableOnCombat</c>。</summary>
    public static string FromGame(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "gameCli.failed";
        var builder = new StringBuilder("gameCli.");
        var upper = false;
        foreach (var ch in code.Trim())
        {
            if (ch is '_' or '-' or ' ') { upper = builder.Length > "gameCli.".Length; continue; }
            if (!char.IsAsciiLetterOrDigit(ch)) continue;
            builder.Append(upper ? char.ToUpperInvariant(ch) : builder.Length == "gameCli.".Length ? char.ToLowerInvariant(ch) : ch);
            upper = false;
        }
        return builder.Length == "gameCli.".Length ? "gameCli.failed" : builder.ToString();
    }
}

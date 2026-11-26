namespace MabiTool.Contracts;

/// <summary>游戏连接器与合奏乐器准备的错误码及 HTTP 状态。</summary>
public static class GameConnectorErrors
{
    public static IReadOnlyList<ErrorDefinition> Definitions { get; } = Array.AsReadOnly<ErrorDefinition>([
        new("gameCli.gameRunning", 409, false), new("gameCli.processUnverifiable", 409, false),
        new("gameCli.directoryUnknown", 422, false), new("gameCli.directoryInvalid", 422, false),
        new("gameCli.cliUnavailable", 422, false), new("gameCli.deployedDiffers", 409, false),
        new("gameCli.noBackup", 409, false), new("gameCli.alreadyEnabled", 409, false), new("gameCli.notEnabled", 409, false),
        new("gameCli.riskNotAcknowledged", 409, false), new("gameCli.preferencesMissing", 422, false),
        new("gameCli.preferencesInvalid", 422, false), new("gameCli.preferencesChanged", 409, true),
        new("gameCli.verifyFailed", 503, false), new("gameCli.writeFailed", 503, true),
        new("room.instrumentNotOwned", 409, false), new("room.instrumentAmbiguous", 409, false), new("room.instrumentChangeFailed", 409, false)
    ]);
}

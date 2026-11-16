using System.Security.Cryptography;
using System.Text;
using MabiTool.Contracts;

namespace MabiTool.Host;

public sealed class LocalAuthService
{
    public const int MaximumChallenges = 8;
    public const int MaximumClients = 32;
    public const int MaximumAttemptsPerMinute = 16;
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ClientLifetime = TimeSpan.FromHours(8);
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly string ownerToken;
    private readonly byte[] ownerHash;
    private readonly HashSet<string> origins;
    private readonly string epoch;
    private readonly Dictionary<string, ChallengeState> challenges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClientState> clients = new(StringComparer.Ordinal);
    private long attemptWindow;
    private int attempts;
    private ulong revision;

    public LocalAuthService(LocalApiOptions options, string epoch, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        ownerToken = options.Token;
        ownerHash = Hash(ownerToken);
        origins = options.AllowedOrigins.ToHashSet(StringComparer.Ordinal);
        this.epoch = epoch;
        attemptWindow = this.clock.GetTimestamp();
    }

    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public AccessGrant OwnerGrant() => new(ownerToken, new("owner", "desktop-owner",
                [ApiPermissions.SystemRead, ApiPermissions.AuthManage, ApiPermissions.ModulesConfigure, ApiPermissions.ReplayControl, ApiPermissions.CombatControl, ApiPermissions.InventoryControl, ApiPermissions.InventoryEdit, ApiPermissions.GameObserve, ApiPermissions.GameControl, ApiPermissions.MusicEdit, ApiPermissions.RoomsCoordinate, ApiPermissions.ColorEdit, ApiPermissions.ColorControl, ApiPermissions.DesktopCapture, ApiPermissions.DesktopControl, ApiPermissions.DiagnosticsExport, ApiPermissions.DiagnosticsDebug, ApiPermissions.FilesTransfer, ApiPermissions.StorageManage, ApiPermissions.UpdatesManage], null, null, true));

    public AuthSession? Authenticate(string token, string? origin)
    {
        if (token.Length is < 43 or > 256 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) return null;
        var hash = Hash(token);
        lock (gate)
        {
            Prune();
            if (CryptographicOperations.FixedTimeEquals(hash, ownerHash))
            {
                RequireOrigin(null, origin);
                return OwnerGrant().Session;
            }
            foreach (var client in clients.Values)
                if (CryptographicOperations.FixedTimeEquals(hash, client.Hash))
                {
                    RequireOrigin(client.Session.Origin, origin);
                    return client.Session;
                }
            return null;
        }
    }

    public PairingChallenge CreateChallenge(PairingChallengeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientName) || request.ClientName.Length > 80 || request.ClientName.Any(char.IsControl) ||
            (request.Origin is not null && !origins.Contains(request.Origin)) ||
            request.Permissions.Count is < 1 or > 3 || request.Permissions.Distinct(StringComparer.Ordinal).Count() != request.Permissions.Count ||
                request.Permissions.Any(p => p is not ApiPermissions.SystemRead and not ApiPermissions.ModulesConfigure and not ApiPermissions.ReplayControl and not ApiPermissions.CombatControl and not ApiPermissions.InventoryControl and not ApiPermissions.InventoryEdit and not ApiPermissions.GameObserve and not ApiPermissions.GameControl and not ApiPermissions.MusicEdit and not ApiPermissions.RoomsCoordinate and not ApiPermissions.ColorControl and not ApiPermissions.ColorEdit and not ApiPermissions.DesktopCapture and not ApiPermissions.DesktopControl and not ApiPermissions.DiagnosticsExport and not ApiPermissions.FilesTransfer and not ApiPermissions.StorageManage and not ApiPermissions.UpdatesManage))
            throw new LocalApiException("request.invalid", "配对名称、Origin 或授权范围无效");
        lock (gate)
        {
            Prune();
            if (challenges.Count >= MaximumChallenges || clients.Count >= MaximumClients)
                throw new LocalApiException("auth.pairingCapacityExceeded", "配对容量已满");
            var id = "challenge-" + Guid.NewGuid().ToString("N");
            var code = NewSecret();
            var expires = clock.GetUtcNow() + ChallengeLifetime;
            var session = new AuthSession("client-" + Guid.NewGuid().ToString("N"), request.ClientName,
                request.Permissions.ToArray(), request.Origin, null, false);
            challenges.Add(id, new(Hash(code), clock.GetTimestamp(), session));
            return new(id, code, expires);
        }
    }

    public AccessGrant Redeem(PairingRequest request, string? origin)
    {
        lock (gate)
        {
            Prune();
            if (clock.GetElapsedTime(attemptWindow) >= TimeSpan.FromMinutes(1))
            {
                attempts = 0;
                attemptWindow = clock.GetTimestamp();
            }
            if (attempts >= MaximumAttemptsPerMinute)
                throw new LocalApiException("auth.pairingRateLimited", "配对尝试过于频繁");
            attempts++;
            if (request.ChallengeId.Length > 64 || request.Code.Length != 43 ||
                !challenges.TryGetValue(request.ChallengeId, out var challenge) ||
                !CryptographicOperations.FixedTimeEquals(Hash(request.Code), challenge.Hash))
                throw new LocalApiException("auth.pairingInvalid", "配对码无效、过期或已使用");
            RequireOrigin(challenge.Session.Origin, origin);
            if (clients.Count >= MaximumClients)
                throw new LocalApiException("auth.pairingCapacityExceeded", "配对容量已满");
            var token = NewSecret();
            var session = challenge.Session with { ExpiresAt = clock.GetUtcNow() + ClientLifetime };
            challenges.Remove(request.ChallengeId);
            clients.Add(session.ClientId, new(Hash(token), clock.GetTimestamp(), session));
            revision++;
            return new(token, session);
        }
    }

    public ApiList<AuthSession> Snapshot()
    {
        lock (gate)
        {
            Prune();
            return new(clients.Values.Select(x => x.Session).OrderBy(x => x.ClientId, StringComparer.Ordinal).ToArray(),
                null, epoch + ":" + revision);
        }
    }

    public bool HasPermission(string clientId, string permission)
    {
        lock (gate)
        {
            Prune();
            return clientId == "owner" ? OwnerGrant().Session.Permissions.Contains(permission, StringComparer.Ordinal) :
                clients.TryGetValue(clientId, out var client) && client.Session.Permissions.Contains(permission, StringComparer.Ordinal);
        }
    }

    public ClientRevocation Revoke(string clientId)
    {
        if (clientId == "owner") throw new LocalApiException("auth.ownerCannotRevoke", "宿主所有者通过退出宿主结束会话");
        lock (gate)
        {
            if (clients.Remove(clientId)) revision++;
            return new(clientId, true);
        }
    }

    private void Prune()
    {
        foreach (var id in challenges.Where(x => clock.GetElapsedTime(x.Value.Created) >= ChallengeLifetime).Select(x => x.Key).ToArray())
            challenges.Remove(id);
        foreach (var id in clients.Where(x => clock.GetElapsedTime(x.Value.Created) >= ClientLifetime).Select(x => x.Key).ToArray())
        {
            clients.Remove(id);
            revision++;
        }
    }

    private static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    private static void RequireOrigin(string? expected, string? supplied)
    {
        if (!string.Equals(expected, supplied, StringComparison.Ordinal))
            throw new LocalApiException("auth.originMismatch", "凭据或配对授权与当前 Origin 不匹配");
    }
    private sealed record ChallengeState(byte[] Hash, long Created, AuthSession Session);
    private sealed record ClientState(byte[] Hash, long Created, AuthSession Session);
}

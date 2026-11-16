namespace MabiTool.ModuleSdk;

public enum ShortcutKey
{
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, Insert, Home, End, Pause,
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    F13, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,
    Space, Tab, Escape, Backspace, Delete, PageUp, PageDown, Left, Up, Right, Down, Enter
}
public static class ShortcutKeys
{
    public static bool TryParse(string? text, out ShortcutKey key)
    {
        if (text is { Length: 1 } && char.IsAsciiDigit(text[0])) text = "D" + text;
        if (text is null || !Enum.GetNames<ShortcutKey>().Contains(text, StringComparer.OrdinalIgnoreCase))
        { key = default; return false; }
        return Enum.TryParse(text, true, out key);
    }
    public static int VirtualKey(ShortcutKey key) => key switch
    {
        >= ShortcutKey.F1 and <= ShortcutKey.F12 => 0x70 + (int)key,
        >= ShortcutKey.A and <= ShortcutKey.Z => 0x41 + (int)key - (int)ShortcutKey.A,
        >= ShortcutKey.D0 and <= ShortcutKey.D9 => 0x30 + (int)key - (int)ShortcutKey.D0,
        >= ShortcutKey.F13 and <= ShortcutKey.F24 => 0x7C + (int)key - (int)ShortcutKey.F13,
        ShortcutKey.Insert => 0x2D, ShortcutKey.Home => 0x24, ShortcutKey.End => 0x23, ShortcutKey.Pause => 0x13,
        ShortcutKey.Space => 0x20, ShortcutKey.Tab => 0x09, ShortcutKey.Escape => 0x1B, ShortcutKey.Backspace => 0x08,
        ShortcutKey.Delete => 0x2E, ShortcutKey.PageUp => 0x21, ShortcutKey.PageDown => 0x22,
        ShortcutKey.Left => 0x25, ShortcutKey.Up => 0x26, ShortcutKey.Right => 0x27, ShortcutKey.Down => 0x28,
        ShortcutKey.Enter => 0x0D, _ => throw new ArgumentOutOfRangeException(nameof(key))
    };
}
public enum ShortcutRegistrationStatus { Registered, Unavailable }
public sealed record ShortcutChord(ShortcutKey Key, bool Control = false, bool Alt = false, bool Shift = false, bool Windows = false);
public sealed record ShortcutRequest(string ActionId, ShortcutChord Preferred, IReadOnlyList<ShortcutChord> Alternatives);
public sealed record ShortcutRegistrationResult(string ActionId, ShortcutChord Configured,
    ShortcutChord? Registered, ShortcutRegistrationStatus Status, string? FailureCode, bool UsedFallback);

public interface IGlobalShortcutRegistrar
{
    bool TryRegister(int registrationId, ShortcutChord chord, out string? failureCode);
    void Unregister(int registrationId);
}

public interface IGlobalShortcutSource : IAsyncDisposable
{
    event Action<string>? ActionReceived;
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    IReadOnlyList<ShortcutRegistrationResult> GetRegistrations();
    bool CanReconfigure => false;
    Task<IReadOnlyList<ShortcutRegistrationResult>> ReconfigureAsync(IReadOnlyList<ShortcutRequest> requests, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ShortcutRegistrationResult>>(requests.Select(value => new ShortcutRegistrationResult(
            value.ActionId, value.Preferred, null, ShortcutRegistrationStatus.Unavailable, "shortcut.sourceUnavailable", false)).ToArray());
}

public sealed class GlobalShortcutRegistry(IGlobalShortcutRegistrar registrar) : IDisposable
{
    private const string EmergencyStopAction = "emergency-stop";
    private static readonly ShortcutChord DefaultEmergencyStop = new(ShortcutKey.F9);
    private static readonly ShortcutChord[] EmergencyFallbacks =
    [
        new(ShortcutKey.F8), new(ShortcutKey.F7), new(ShortcutKey.F10), new(ShortcutKey.Pause)
    ];
    private readonly object gate = new();
    private readonly Dictionary<int, ShortcutRegistrationResult> registrations = [];
    private readonly Dictionary<string, ShortcutRegistrationResult> resultsByAction = new(StringComparer.Ordinal);
    private int nextRegistrationId;
    private bool disposed;

    public IReadOnlyList<ShortcutRegistrationResult> Configure(IEnumerable<ShortcutRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var configured = requests.ToArray();
        if (!configured.Any(request => request?.ActionId == EmergencyStopAction))
            configured = [new(EmergencyStopAction, DefaultEmergencyStop, EmergencyFallbacks), .. configured];
        ValidateRequests(configured);

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            UnregisterAll();
            var results = new List<ShortcutRegistrationResult>(configured.Length);
            var claimed = new HashSet<ShortcutChord>();
            var ordered = configured.OrderBy(request => request.ActionId == EmergencyStopAction ? 0 : 1)
                .ThenBy(request => request.ActionId, StringComparer.Ordinal);

            foreach (var request in ordered)
            {
                var candidates = CandidateChords(request).Distinct().ToArray();
                string? lastFailure = null;
                ShortcutChord? actual = null;
                var usedFallback = false;
                foreach (var candidate in candidates)
                {
                    if (claimed.Contains(candidate))
                    {
                        lastFailure = "shortcut.conflictWithConfiguredAction";
                        continue;
                    }

                    var registrationId = checked(++nextRegistrationId);
                    if (registrar.TryRegister(registrationId, candidate, out var failureCode))
                    {
                        registrations.Add(registrationId, new(request.ActionId, request.Preferred,
                            candidate, ShortcutRegistrationStatus.Registered, null, candidate != request.Preferred));
                        claimed.Add(candidate);
                        actual = candidate;
                        usedFallback = candidate != request.Preferred;
                        break;
                    }
                    lastFailure = string.IsNullOrWhiteSpace(failureCode) ? "shortcut.registrationFailed" : failureCode;
                }

                var result = actual is null
                    ? new(request.ActionId, request.Preferred, null, ShortcutRegistrationStatus.Unavailable,
                        lastFailure ?? "shortcut.noCandidate", false)
                    : registrations.Values.Single(result => result.ActionId == request.ActionId);
                resultsByAction.Add(request.ActionId, result);
                results.Add(result);
            }

            return results.OrderBy(result => result.ActionId == EmergencyStopAction ? 0 : 1)
                .ThenBy(result => result.ActionId, StringComparer.Ordinal).ToArray();
        }
    }

    public IReadOnlyList<ShortcutRegistrationResult> GetRegistrations()
    {
        lock (gate) return resultsByAction.Values.OrderBy(result => result.ActionId, StringComparer.Ordinal).ToArray();
    }

    public bool TryGetAction(int registrationId, out string? actionId)
    {
        lock (gate)
        {
            if (registrations.TryGetValue(registrationId, out var result))
            {
                actionId = result.ActionId;
                return true;
            }
            actionId = null;
            return false;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            UnregisterAll();
            disposed = true;
        }
    }

    private static IEnumerable<ShortcutChord> CandidateChords(ShortcutRequest request)
    {
        if (request.ActionId == EmergencyStopAction)
        {
            yield return request.Preferred;
            yield return DefaultEmergencyStop;
            foreach (var fallback in EmergencyFallbacks) yield return fallback;
        }
        else
        {
            yield return request.Preferred;
            foreach (var alternative in request.Alternatives) yield return alternative;
        }
    }

    private void UnregisterAll()
    {
        foreach (var registrationId in registrations.Keys.ToArray()) registrar.Unregister(registrationId);
        registrations.Clear();
        resultsByAction.Clear();
    }

    public static void ValidateRequests(IReadOnlyList<ShortcutRequest> requests)
    {
        if (requests.Any(request => request is null || string.IsNullOrWhiteSpace(request.ActionId) ||
                                    request.Preferred is null || request.Alternatives is null ||
                                    !Enum.IsDefined(request.Preferred.Key) ||
                                    request.Alternatives.Any(chord => chord is null || !Enum.IsDefined(chord.Key))))
            throw new ArgumentException("shortcut.requestInvalid", nameof(requests));
        if (requests.Select(request => request.ActionId).Distinct(StringComparer.Ordinal).Count() != requests.Count)
            throw new ArgumentException("shortcut.actionIdDuplicate", nameof(requests));
        if (requests.Count(request => request.ActionId == EmergencyStopAction) > 1)
            throw new ArgumentException("shortcut.emergencyStopDuplicate", nameof(requests));
    }
}

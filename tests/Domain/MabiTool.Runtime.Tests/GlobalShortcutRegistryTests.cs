using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Runtime.Tests;

public sealed class GlobalShortcutRegistryTests
{
    [Fact]
    public void EmergencyStopGetsCustomChordBeforeConflictingImportedShortcut()
    {
        var registrar = new FakeRegistrar();
        var registry = new GlobalShortcutRegistry(registrar);

        var results = registry.Configure
        ([
            new("overlay-toggle", new(ShortcutKey.F10), [new(ShortcutKey.F8), new(ShortcutKey.F7)]),
            new("emergency-stop", new(ShortcutKey.F10), [new(ShortcutKey.F8)])
        ]);

        Assert.Equal(new(ShortcutKey.F10), Assert.Single(results, result => result.ActionId == "emergency-stop").Registered);
        var overlay = Assert.Single(results, result => result.ActionId == "overlay-toggle");
        Assert.Equal(new(ShortcutKey.F8), overlay.Registered);
        Assert.True(overlay.UsedFallback);
        Assert.Equal(ShortcutRegistrationStatus.Registered, overlay.Status);
    }

    [Fact]
    public void OccupiedEmergencyCandidatesFallThroughAndFailureIsQueryable()
    {
        var registrar = new FakeRegistrar { Occupied = [new(ShortcutKey.F9), new(ShortcutKey.F8)] };
        var registry = new GlobalShortcutRegistry(registrar);

        var result = Assert.Single(registry.Configure([]));

        Assert.Equal(new(ShortcutKey.F7), result.Registered);
        Assert.True(result.UsedFallback);
        Assert.Equal("shortcut.globalConflict", registrar.Attempts[0].FailureCode);
        Assert.Single(registry.GetRegistrations());
    }

    [Fact]
    public void UnavailableShortcutReportsActualFailureAndAlternativeAttempts()
    {
        var registrar = new FakeRegistrar { Occupied = [new(ShortcutKey.F9), new(ShortcutKey.F8),
            new(ShortcutKey.F7), new(ShortcutKey.F10), new(ShortcutKey.Pause), new(ShortcutKey.Insert)] };
        var registry = new GlobalShortcutRegistry(registrar);

        var results = registry.Configure([new("overlay-toggle", new(ShortcutKey.Insert), [])]);

        var overlay = Assert.Single(results, result => result.ActionId == "overlay-toggle");
        Assert.Equal(ShortcutRegistrationStatus.Unavailable, overlay.Status);
        Assert.Null(overlay.Registered);
        Assert.Equal("shortcut.globalConflict", overlay.FailureCode);
        Assert.Equal(6, registrar.Attempts.Count);
        Assert.Equal(ShortcutRegistrationStatus.Unavailable,
            registry.GetRegistrations().Single(result => result.ActionId == "overlay-toggle").Status);
    }

    [Fact]
    public void ReconfigurationAndDisposeUnregisterOwnedRegistrations()
    {
        var registrar = new FakeRegistrar();
        var registry = new GlobalShortcutRegistry(registrar);
        registry.Configure([new("overlay-toggle", new(ShortcutKey.Insert), [])]);
        var previous = Assert.Single(registry.GetRegistrations(), result => result.ActionId == "overlay-toggle");
        var previousId = registrar.RegisteredChords.Single(pair => pair.Value == previous.Registered).Key;

        registry.Configure([new("overlay-toggle", new(ShortcutKey.Home), [])]);
        Assert.Contains(previousId, registrar.UnregisteredIds);
        var activeId = registrar.RegisteredChords.Single(pair => pair.Value == new ShortcutChord(ShortcutKey.Home)).Key;
        Assert.True(registry.TryGetAction(activeId, out var actionId));
        Assert.Equal("overlay-toggle", actionId);
        Assert.NotEqual(previous.Registered, registry.GetRegistrations().Single(result => result.ActionId == "overlay-toggle").Registered);

        registry.Dispose();
        Assert.Contains(activeId, registrar.UnregisteredIds);
        Assert.Empty(registry.GetRegistrations());
    }

    [Fact]
    public void DuplicateConfiguredCombinationsAreAssignedByActionOrder()
    {
        var registry = new GlobalShortcutRegistry(new FakeRegistrar());

        var results = registry.Configure
        ([
            new("overlay-toggle", new(ShortcutKey.F8), [new(ShortcutKey.Insert)]),
            new("emergency-stop", new(ShortcutKey.F8), [])
        ]);

        Assert.Equal(new(ShortcutKey.F8), results.Single(result => result.ActionId == "emergency-stop").Registered);
        Assert.Equal(new(ShortcutKey.Insert), results.Single(result => result.ActionId == "overlay-toggle").Registered);
    }

    [Fact]
    public void InvalidReconfigurationPreservesRegisteredActions()
    {
        var registrar = new FakeRegistrar();
        using var registry = new GlobalShortcutRegistry(registrar);
        var before = registry.Configure([]).ToArray();
        Assert.Throws<ArgumentException>(() => registry.Configure([new("emergency-stop", new((ShortcutKey)999), [])]));
        Assert.Equal(before, registry.GetRegistrations());
        Assert.Empty(registrar.UnregisteredIds);
    }

    [Fact]
    public void CustomEmergencyConflictUsesDeduplicatedFixedFallbacks()
    {
        var custom = new ShortcutChord(ShortcutKey.K, Control: true);
        var registrar = new FakeRegistrar { Occupied = [custom, new(ShortcutKey.F9)] };
        using var registry = new GlobalShortcutRegistry(registrar);
        var result = Assert.Single(registry.Configure([new("emergency-stop", custom, [])]));
        Assert.Equal(new(ShortcutKey.F8), result.Registered);
        Assert.True(result.UsedFallback);
        Assert.Equal(new[] { custom, new(ShortcutKey.F9), new(ShortcutKey.F8) }, registrar.Attempts.Select(value => value.Chord));
    }

    [Fact]
    public void ExpandedKeysPreserveOriginalEnumValuesAndNativeCodes()
    {
        Assert.Equal(8, (int)ShortcutKey.F9); Assert.Equal(15, (int)ShortcutKey.Pause);
        Assert.Equal(0x78, ShortcutKeys.VirtualKey(ShortcutKey.F9));
        Assert.Equal(0x4B, ShortcutKeys.VirtualKey(ShortcutKey.K));
        Assert.Equal(0x37, ShortcutKeys.VirtualKey(ShortcutKey.D7));
        Assert.Equal(0x87, ShortcutKeys.VirtualKey(ShortcutKey.F24));
        Assert.True(ShortcutKeys.TryParse("7", out var key)); Assert.Equal(ShortcutKey.D7, key);
        Assert.False(ShortcutKeys.TryParse("999", out _)); Assert.False(ShortcutKeys.TryParse(" F9", out _));
    }

    private sealed class FakeRegistrar : IGlobalShortcutRegistrar
    {
        public HashSet<ShortcutChord> Occupied { get; init; } = [];
        public Dictionary<int, ShortcutChord> RegisteredChords { get; } = [];
        public List<int> UnregisteredIds { get; } = [];
        public List<(ShortcutChord Chord, string? FailureCode)> Attempts { get; } = [];
        public IEnumerable<int> SuccessfulIds => RegisteredChords.Keys;

        public bool TryRegister(int registrationId, ShortcutChord chord, out string? failureCode)
        {
            if (Occupied.Contains(chord) || RegisteredChords.Values.Contains(chord))
            {
                failureCode = "shortcut.globalConflict";
                Attempts.Add((chord, failureCode));
                return false;
            }
            RegisteredChords.Add(registrationId, chord);
            failureCode = null;
            Attempts.Add((chord, null));
            return true;
        }

        public void Unregister(int registrationId)
        {
            if (RegisteredChords.Remove(registrationId)) UnregisteredIds.Add(registrationId);
        }
    }
}

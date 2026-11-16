using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.DataImports;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.Color.Domain;
using MabiTool.Modules.Color.Persistence;
using MabiTool.Modules.Rooms.Domain;
using MabiTool.Modules.Rooms.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class SettingsImportApiTests
{
    private const string Token = "settings_import_tests_0123456789012345678901234567890123456789";
    private const string Origin = "http://localhost:5173";
    private static ImportSourceFile Source(string name, string json) => new(name, Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
    private static SettingsImportRequest Request(params ImportSourceFile[] files) => new("original-backup", "原設定", files);
    private static SettingsImportRequest Full(string project)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "tests/Contracts/MabiTool.Host.Tests/Fixtures/settings-import.v1.json")));
        return Request(document.RootElement.GetProperty(project).EnumerateObject().Select(x => Source(x.Name, x.Value.GetRawText())).ToArray());
    }

    [Fact]
    public async Task SdkPreservesModesOffsetsStopsAndRequiresCurrentWindow()
    {
        using var workspace = new Workspace(); await using var host = await Host.Start(workspace); using var sdk = host.Sdk();
        var files = Full("mabisync"); var original = workspace.SaveOriginals(files);
        var preview = await sdk.PreviewRoomImportAsync(files, "room-settings-preview");
        Assert.Equal("ready", preview.State);
        var preferences = preview.RoomPreferences!;
        Assert.Equal(-25, preferences.TimingOffsetMs); Assert.Equal(12000, preferences.CountdownMs);
        Assert.Equal(12345, preferences.MusicDurationMs); Assert.Equal(1500, preferences.MusicTailHoldMs);
        Assert.Equal(MusicStopRule.FirstCycle, preferences.MusicStopRule.KnownValue);
        Assert.True(preferences.RequiresGameBinding); Assert.Equal("emergency-stop", preferences.LocalStopActionId);
        Assert.Null(preferences.Bindings["emote"].Stop);
        Assert.Equal(LocalBindingKind.ChatDraft, preferences.Bindings["emote"].Start!.Kind.KnownValue);
        Assert.True(preferences.Bindings["independent"].StopIsToggle);
        Assert.Equal(preferences.Bindings["independent"], preferences.Bindings["ensemble"]);
        Assert.Contains(preview.Issues, x => x.Code == "import.bindingInvalidated" && x.OldId == "window");
        Assert.DoesNotContain("synthetic-expired-token", JsonSerializer.Serialize(preview, ContractJson.Options));
        ApiResponseMetadata? metadata = null;
        var operation = await sdk.CommitRoomImportAsync(preview.Id, "room-settings-commit", preview.TargetETag, onResponse: value => metadata = value);
        Assert.Equal(202, metadata!.Status); Assert.Equal("/api/v1/operations/" + operation.Id, metadata.Location);
        var complete = await Complete(sdk, operation); Assert.Equal(OperationState.Succeeded, complete.State.KnownValue); Assert.Equal(EffectStatus.None, complete.EffectStatus.KnownValue);
        var saved = await sdk.GetRoomPreferencesAsync();
        Assert.Equal(JsonSerializer.Serialize(preferences, ContractJson.Options), JsonSerializer.Serialize(saved, ContractJson.Options));
        Assert.Equal("disconnected", (await sdk.GetRoomSessionAsync()).State);
        foreach (var file in original) Assert.Equal(file.Bytes, File.ReadAllBytes(file.Path));
    }

    [Fact]
    public async Task ColorSdkImportsProfilesRawDraftAndHistoricalMeasurements()
    {
        using var workspace = new Workspace(); await using var host = await Host.Start(workspace); using var sdk = host.Sdk();
        var files = Full("color-studio"); var original = workspace.SaveOriginals(files);
        var preview = await sdk.PreviewColorImportAsync(files, "color-settings-preview");
        Assert.Equal("ready", preview.State); Assert.Equal(ColorStrategy.Precise, preview.Draft!.Strategy.KnownValue);
        Assert.Equal(new[] { 2, 3, 1 }, preview.Draft.Regions.Select(x => x.Priority));
        Assert.Equal(ColorMatchMode.ExactHex, preview.Draft.Regions[0].MatchMode.KnownValue);
        Assert.Equal(ColorMatchMode.Similar, preview.Draft.Regions[1].MatchMode.KnownValue);
        Assert.Equal(new[] { "#00FF00", "#0000FF" }, preview.Draft.Regions[0].AlternativeHex);
        var profile = Assert.Single(preview.Profiles); Assert.NotEqual("legacy-preset", profile.Id);
        Assert.Equal(ColorStrategy.Screenshot, profile.Strategy.KnownValue);
        var history = Assert.Single(preview.History); Assert.Equal("2026-11-08 21:00:00", history.LocalTime);
        Assert.False(history.Restored); Assert.Equal(2.4, history.Result.Maximum); Assert.Equal(1.5, history.Best!.Maximum);
        Assert.Equal(ObservationQuality.Estimated, history.Quality.KnownValue);
        var operation = await Complete(sdk, await sdk.CommitColorImportAsync(preview.Id, "color-settings-commit", preview.TargetETag));
        Assert.Equal(OperationState.Succeeded, operation.State.KnownValue);
        var library = await sdk.GetColorLibraryAsync(); Assert.Equal(profile.Id, Assert.Single(library.Profiles).Id);
        Assert.Equal(ColorStrategy.Precise, library.Draft.Strategy.KnownValue);
        Assert.Equal(JsonSerializer.Serialize(history, ContractJson.Options), JsonSerializer.Serialize(Assert.Single((await sdk.QueryImportedColorHistoryAsync(new())).Items), ContractJson.Options));
        Assert.Empty((await sdk.QueryColorHistoryAsync(new())).Items);
        var preferences = await sdk.GetColorPreferencesAsync(); Assert.Equal("繁體中文", preferences.Language);
        Assert.Equal(ColorStrategy.Precise, preferences.DraftStrategy!.Value.KnownValue);
        Assert.Equal(-120, preferences.PresentationPreferences.GetProperty("overlay_position")[0].GetInt32());
        Assert.NotNull(preferences.OriginalDraft);
        foreach (var file in original) Assert.Equal(file.Bytes, File.ReadAllBytes(file.Path));
    }

    [Theory]
    [InlineData("manual", null, MusicStopRule.Manual)]
    [InlineData("duration", 1, MusicStopRule.Duration)]
    [InlineData("first-cycle", 2, MusicStopRule.FirstCycle)]
    [InlineData(null, null, MusicStopRule.FirstCycle)]
    [InlineData(null, 2, MusicStopRule.Duration)]
    [InlineData("old-unknown", 1, MusicStopRule.Manual)]
    public void LegacyStopRuleRestoresOriginalChoice(string? name, int? choice, MusicStopRule expected)
    {
        var request = Request(Source("settings.json", JsonSerializer.Serialize(new { musicStopRule = name, musicStopChoice = choice })));
        var source = LegacyJsonFiles.Read(request, MabiSyncSettingsImport.Files, default);
        Assert.Equal(expected, MabiSyncSettingsImport.Plan(source, request, []).Preferences.MusicStopRule.KnownValue);
    }

    [Fact]
    public async Task NativeStrategyMarkerAndIncompleteDraftKeepOriginalSemantics()
    {
        using var workspace = new Workspace(); await using var host = await Host.Start(workspace); using var sdk = host.Sdk();
        var full = Full("color-studio");
        var draft = full.Files.Single(x => x.Name == "profile.json");
        var value = Encoding.UTF8.GetString(Convert.FromBase64String(draft.ContentBase64)).Replace("\"screenshot_default_revision\": 1,", "");
        var missing = await sdk.PreviewColorImportAsync(Request(Source("profile.json", value)), "missing-marker-preview");
        Assert.Equal(ColorStrategy.Screenshot, missing.Draft!.Strategy.KnownValue);
        Assert.Single(missing.Issues, x => x.Code == "import.strategyAdjusted");
        var incomplete = JsonSerializer.Serialize(new { schema = 1, search_strategy = "native", screenshot_default_revision = 1,
            priority_order = new[] { 0, 1, 2 }, regions = Enumerable.Range(0, 3).Select(x => new { enabled = false, target = x == 0 ? "#20" : "#202020", alt = "", mode = "精准 HEX", tolerance = 8 }) });
        var preview = await sdk.PreviewColorImportAsync(Request(Source("profile.json", incomplete)) with { SourceId = "unfinished" }, "unfinished-preview");
        Assert.Equal("ready", preview.State); Assert.Null(preview.Draft); Assert.Contains(preview.Issues, x => x.Code == "import.draftNeedsReview");
        await Complete(sdk, await sdk.CommitColorImportAsync(preview.Id, "unfinished-commit", preview.TargetETag));
        var saved = await sdk.GetColorPreferencesAsync();
        Assert.Equal("#20", saved.OriginalDraft!.Value.GetProperty("regions")[0].GetProperty("target").GetString());
        Assert.Equal(ColorStrategy.Precise, saved.DraftStrategy!.Value.KnownValue);
    }

    [Fact]
    public async Task BindingConflictRequiresExplicitOverridesAndFreezesCentralRegistrations()
    {
        using var workspace = new Workspace(); var shortcuts = new ShortcutSource();
        await using var host = await Host.Start(workspace, shortcuts: shortcuts); using var sdk = host.Sdk();
        var request = Request(Source("settings.json", """{"bindings":{"independent":{"Start":{"type":"key","Key":120},"Stop":{"type":"key","Key":66}}}}"""));
        var preview = await sdk.PreviewRoomImportAsync(request, "shortcut-conflict-preview");
        Assert.Equal("conflict", preview.State); Assert.Equal(2, preview.Issues.Count(x => x.Code == "import.shortcutConflict"));
        Assert.Equal("import.conflict", (await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.CommitRoomImportAsync(preview.Id, "shortcut-conflict-commit", preview.TargetETag))).Problem!.Code);
        var replacement = new InputBindingPreference(LocalBindingKind.Key, 67, Alt: true);
        var resolved = await sdk.PreviewRoomImportAsync(request with { BindingOverrides = new Dictionary<string, InputBindingPreference>
            { ["independent.start"] = replacement, ["ensemble.start"] = replacement } }, "shortcut-resolved-preview");
        Assert.Equal("ready", resolved.State);
        shortcuts.Registrations = [new("emergency-stop", new(ShortcutKey.F9), new(ShortcutKey.F8), ShortcutRegistrationStatus.Registered, null, true)];
        Assert.Equal("revision.stale", (await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.CommitRoomImportAsync(resolved.Id, "shortcut-changed-commit", resolved.TargetETag))).Problem!.Code);
        var fresh = await sdk.PreviewRoomImportAsync(request with { BindingOverrides = new Dictionary<string, InputBindingPreference>
            { ["independent.start"] = replacement, ["ensemble.start"] = replacement } }, "shortcut-fresh-preview");
        Assert.Equal(OperationState.Succeeded, (await Complete(sdk, await sdk.CommitRoomImportAsync(fresh.Id, "shortcut-fresh-commit", fresh.TargetETag))).State.KnownValue);
        Assert.True((await sdk.GetRoomPreferencesAsync()).RequiresGameBinding);
    }

    [Fact]
    public async Task NameConflictDoesNotOverwriteAndExplicitNameMappingCommits()
    {
        using var workspace = new Workspace(); await using var host = await Host.Start(workspace); using var sdk = host.Sdk();
        var library = await sdk.GetColorLibraryAsync();
        await sdk.EditColorLibraryAsync(new([library.Draft with { Id = "existing", Name = "晴天" }], [], null), "existing-profile-save", ColorLibraryStore.ETag(library.Revision));
        var request = Full("color-studio"); var conflict = await sdk.PreviewColorImportAsync(request, "name-conflict-preview");
        Assert.Equal("conflict", conflict.State); Assert.Contains(conflict.Issues, x => x.Code == "import.profileNameConflict");
        var mapped = await sdk.PreviewColorImportAsync(request with { ProfileNames = new Dictionary<string, string> { ["legacy-preset"] = "晴天副本" } }, "mapped-name-preview");
        Assert.Equal("ready", mapped.State);
        await Complete(sdk, await sdk.CommitColorImportAsync(mapped.Id, "mapped-name-commit", mapped.TargetETag));
        var saved = await sdk.GetColorLibraryAsync(); Assert.Equal(2, saved.Profiles.Count);
        Assert.Equal(JsonSerializer.Serialize(library.Draft.Regions, ContractJson.Options),
            JsonSerializer.Serialize(saved.Profiles.Single(x => x.Id == "existing").Regions, ContractJson.Options));
        Assert.Equal("晴天副本", saved.Profiles.Single(x => x.Id != "existing").Name);
    }

    [Fact]
    public async Task FailureAtReceiptEventRollsBackProfilesPreferencesAndHistory()
    {
        using var workspace = new Workspace(); await using var host = await Host.Start(workspace);
        var imports = host.App.Services.GetRequiredService<SettingsImportCoordinator>(); var database = host.App.Services.GetRequiredService<SqliteStore>();
        var preview = await imports.PreviewAsync("color-studio", "direct-owner", Full("color-studio"), "rollback-preview");
        await database.WriteAsync(tx => tx.Execute("""
            CREATE TRIGGER block_settings_event BEFORE INSERT ON core_outbox WHEN NEW.event_type='color.import.committed'
            BEGIN SELECT RAISE(ABORT,'fixture-blocked'); END
            """));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => imports.CommitAsync("color-studio", "direct-owner", preview.Id, "rollback-commit", preview.TargetETag));
        Assert.Equal(0UL, host.App.Services.GetRequiredService<ColorLibraryStore>().Read().Revision.Value);
        Assert.Equal(0UL, host.App.Services.GetRequiredService<ColorPreferenceStore>().Read("direct-owner").Revision.Value);
        Assert.Equal(0L, database.ReadScalar("SELECT count(*) FROM color_import_history"));
        Assert.Equal(0L, database.ReadScalar("SELECT count(*) FROM settings_imports WHERE receipt IS NOT NULL"));
        await database.WriteAsync(tx => tx.Execute("DROP TRIGGER block_settings_event"));
        var receipt = await imports.CommitAsync("color-studio", "direct-owner", preview.Id, "rollback-commit", preview.TargetETag);
        Assert.Equal(1, receipt.Profiles); Assert.Equal(1, receipt.History);
    }

    [Fact]
    public async Task RepeatedSourceSurvivesRestartWhilePendingPreviewExpires()
    {
        using var workspace = new Workspace(); SettingsImportPreview pending, committed;
        await using (var host = await Host.Start(workspace))
        {
            using var sdk = host.Sdk(); var request = Full("mabisync");
            committed = await sdk.PreviewRoomImportAsync(request, "restart-committed-preview");
            await Complete(sdk, await sdk.CommitRoomImportAsync(committed.Id, "restart-committed-commit", committed.TargetETag));
            pending = await sdk.PreviewRoomImportAsync(request with { SourceId = "another-backup" }, "restart-pending-preview");
        }
        await using (var host = await Host.Start(workspace))
        {
            using var sdk = host.Sdk();
            var preview = await sdk.PreviewRoomImportAsync(Full("mabisync"), "restart-repeated-preview");
            Assert.Equal(committed.Id, preview.Id); Assert.Equal("committed", preview.State);
            Assert.Equal("expired", (await sdk.GetRoomImportAsync(pending.Id)).State);
            Assert.Equal("import.previewExpired", (await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.CommitRoomImportAsync(pending.Id, "restart-pending-commit", pending.TargetETag))).Problem!.Code);
            Assert.Equal(1UL, (await sdk.GetRoomPreferencesAsync()).Revision.Value);
        }
    }

    [Fact]
    public async Task QueuedCancellationDoesNotWriteAndApplyingCancellationCompletes()
    {
        using var workspace = new Workspace(); await using var host = await Host.Start(workspace);
        var imports = host.App.Services.GetRequiredService<SettingsImportCoordinator>(); var operations = host.App.Services.GetRequiredService<OperationStore>();
        var preview = await imports.PreviewAsync("mabisync", "direct-owner", Full("mabisync"), "cancellation-preview");
        var queued = await operations.AdmitSettingsImportAsync("mabisync", "direct-owner", preview.Id, "queued-cancel-key", preview.TargetETag, true);
        var cancelled = await operations.CancelAsync(queued.Snapshot.Id, "direct-owner", false, "fixture-cancel");
        Assert.Equal(OperationState.Cancelled, cancelled.State.KnownValue); Assert.Null(await operations.BeginSettingsImportAsync(cancelled.Id));
        Assert.Equal(0UL, host.App.Services.GetRequiredService<RoomPreferenceStore>().Read("direct-owner").Revision.Value);
        var applying = await operations.AdmitSettingsImportAsync("mabisync", "direct-owner", preview.Id, "applying-cancel-key", preview.TargetETag, true);
        await operations.BeginSettingsImportAsync(applying.Snapshot.Id);
        Assert.Equal(CancellationState.Deferred, (await operations.CancelAsync(applying.Snapshot.Id, "direct-owner", false, "late-cancel")).Cancellation.State.KnownValue);
        var receipt = await imports.CommitAsync("mabisync", "direct-owner", preview.Id, "applying-cancel-key", preview.TargetETag);
        var finished = await operations.FinishSettingsImportAsync(applying.Snapshot.Id, receipt, null);
        Assert.Equal(OperationState.Succeeded, finished.State.KnownValue); Assert.Equal(CancellationState.Completed, finished.Cancellation.State.KnownValue);
    }

    [Fact]
    public async Task PermissionsOwnersAndEventsRemainIsolated()
    {
        using var workspace = new Workspace(); await using var host = await Host.Start(workspace); using var owner = host.Sdk();
        using var editor = await host.Pair([ApiPermissions.SystemRead, ApiPermissions.ColorEdit]); using var reader = await host.Pair([ApiPermissions.SystemRead]);
        var preview = await editor.PreviewColorImportAsync(Full("color-studio"), "editor-import-preview");
        Assert.Equal("permission.denied", (await Assert.ThrowsAsync<MabiToolApiException>(() => reader.GetColorImportAsync(preview.Id))).Problem!.Code);
        Assert.Equal("permission.denied", (await Assert.ThrowsAsync<MabiToolApiException>(() => editor.PreviewRoomImportAsync(Full("mabisync"), "editor-room-preview"))).Problem!.Code);
        Assert.Equal("resource.notFound", (await Assert.ThrowsAsync<MabiToolApiException>(() => owner.GetColorImportAsync(preview.Id))).Problem!.Code);
        await Complete(editor, await editor.CommitColorImportAsync(preview.Id, "editor-import-commit", preview.TargetETag));
        Assert.Empty((await reader.QueryImportedColorHistoryAsync(new())).Items);
        Assert.Equal(0UL, (await reader.GetColorPreferencesAsync()).Revision.Value);
        var filter = new EventStreamOptions(["color.import.committed", "color.preferences.changed"], [], null);
        for (var i = 0; i < 100 && (await editor.GetEventSnapshotAsync(filter)).Resources.Count < 2; i++) await Task.Delay(20);
        Assert.Equal(2, (await editor.GetEventSnapshotAsync(filter)).Resources.Count);
        Assert.Empty((await reader.GetEventSnapshotAsync(filter)).Resources);
    }

    [Fact]
    public async Task UploadBudgetAndChunkedBrowserImportUseAuthorizedDataOnly()
    {
        using var workspace = new Workspace(); await using var host = await Host.Start(workspace); using var sdk = host.Sdk();
        var challenge = await sdk.CreatePairingChallengeAsync(new("settings-browser", Origin, [ApiPermissions.SystemRead, ApiPermissions.RoomsCoordinate]));
        using var pairing = host.Http(); pairing.DefaultRequestHeaders.Add("Origin", Origin);
        using var redemption = await pairing.PostAsJsonAsync(ApiContract.Pairings.Path, new PairingRequest(challenge.ChallengeId, challenge.Code), ContractJson.Options);
        var grant = (await redemption.Content.ReadFromJsonAsync<AccessGrant>(ContractJson.Options))!;
        using var browser = host.Http(grant.Token); browser.DefaultRequestHeaders.Add("Origin", Origin);
        var request = Request(Source("settings.json", JsonSerializer.Serialize(new { nickname = "瀏覽器", timingOffsetMs = 35, padding = new string('x', 100_000) })));
        using var post = new HttpRequestMessage(HttpMethod.Post, ApiContract.PreviewRoomImport.Path) { Content = new ChunkedContent(JsonSerializer.SerializeToUtf8Bytes(request, ContractJson.Options)) };
        post.Headers.Add("Idempotency-Key", "browser-settings-preview");
        using var response = await browser.SendAsync(post); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var preview = (await response.Content.ReadFromJsonAsync<SettingsImportPreview>(ContractJson.Options))!;
        Assert.Equal(35, preview.RoomPreferences!.TimingOffsetMs);
        using var unauthorized = host.Http("invalid"); unauthorized.DefaultRequestHeaders.ExpectContinue = true;
        using var huge = new HttpRequestMessage(HttpMethod.Post, ApiContract.PreviewRoomImport.Path) { Content = JsonContent.Create(new { padding = new string('x', LegacyJsonFiles.MaximumRequestBytes + 1) }) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await unauthorized.SendAsync(huge)).StatusCode);
        var badFile = Request(new ImportSourceFile("settings.json", Convert.ToBase64String(new byte[LegacyJsonFiles.MaximumSourceBytes + 1])));
        Assert.Equal("request.bodyTooLarge", (await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.PreviewRoomImportAsync(badFile, "oversized-source-preview"))).Problem!.Code);
    }

    [Theory]
    [InlineData("""{"nickname":"甲","nickname":"乙"}""")]
    [InlineData("""{"nickname":"\uD800"}""")]
    [InlineData("""{"bindings":{"independent":{"Start":{"type":"key","Key":65,"key":65}}}}""")]
    [InlineData("""{"bindings":{"independent":{"Start":{"type":"key","Key":256}}}}""")]
    [InlineData("""{"bindings":{"independent":{"Start":{"type":"button","X":20,"Y":0,"ClientWidth":20,"ClientHeight":20,"Dpi":96}}}}""")]
    [InlineData("""{"bindings":{"independent":{"Start":{"type":"button","X":0,"Y":0,"ClientWidth":20,"ClientHeight":20,"Dpi":0}}}}""")]
    public void InvalidLegacyJsonAndBindingsAreRejected(string json)
    {
        var request = Request(Source("settings.json", json));
        var error = Assert.Throws<OperationException>(() =>
            MabiSyncSettingsImport.Plan(LegacyJsonFiles.Read(request, MabiSyncSettingsImport.Files, default), request, []));
        Assert.Equal("import.invalidInput", error.Code);
    }

    [Fact]
    public void UnicodeNicknameKeepsSupplementaryCharacters()
    {
        var request = Request(Source("settings.json", """{"nickname":"測試者😀"}"""));
        var source = LegacyJsonFiles.Read(request, MabiSyncSettingsImport.Files, default);
        Assert.Equal("測試者😀", MabiSyncSettingsImport.Plan(source, request, []).Preferences.Nickname);
    }

    [Fact]
    public async Task ChangedTargetAndElapsedPreviewDoNotWritePreferences()
    {
        var clock = new ImportClock();
        using var workspace = new Workspace(); await using var host = await Host.Start(workspace, clock: clock); using var sdk = host.Sdk();
        var color = await sdk.PreviewColorImportAsync(Full("color-studio"), "stale-color-preview");
        var library = await sdk.GetColorLibraryAsync();
        await sdk.EditColorLibraryAsync(new([library.Draft with { Id = "another-profile", Name = "新的方案" }], [], null),
            "other-client-profile", ColorLibraryStore.ETag(library.Revision));
        Assert.Equal("revision.stale", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.CommitColorImportAsync(color.Id, "stale-color-commit", color.TargetETag))).Problem!.Code);
        Assert.Equal(0UL, (await sdk.GetColorPreferencesAsync()).Revision.Value);
        Assert.Empty((await sdk.QueryImportedColorHistoryAsync(new())).Items);
        var room = await sdk.PreviewRoomImportAsync(Full("mabisync"), "elapsed-room-preview");
        clock.Advance(SettingsImportCoordinator.PreviewLifetime);
        Assert.Equal("expired", (await sdk.GetRoomImportAsync(room.Id)).State);
        Assert.Equal("import.previewExpired", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.CommitRoomImportAsync(room.Id, "elapsed-room-commit", room.TargetETag))).Problem!.Code);
        Assert.Equal(0UL, (await sdk.GetRoomPreferencesAsync()).Revision.Value);
    }

    [Fact]
    public async Task ImportedHistoryPagesByOwnerAndStableIdentity()
    {
        using var workspace = new Workspace(); await using var host = await Host.Start(workspace); using var sdk = host.Sdk();
        var full = Full("color-studio");
        using var history = JsonDocument.Parse(Convert.FromBase64String(full.Files.Single(x => x.Name == "history.json").ContentBase64));
        var entry = history.RootElement[0].GetRawText();
        var entries = "[" + string.Join(",", Enumerable.Range(0, 3).Select(i => entry.Replace("21:00:00", $"21:00:0{i}"))) + "]";
        var request = Request(full.Files.Select(x => x.Name == "history.json" ? Source(x.Name, entries) : x).ToArray());
        var preview = await sdk.PreviewColorImportAsync(request, "history-paging-preview");
        await Complete(sdk, await sdk.CommitColorImportAsync(preview.Id, "history-paging-commit", preview.TargetETag));
        var first = await sdk.QueryImportedColorHistoryAsync(new(2));
        Assert.Equal(2, first.Items.Count); Assert.Equal(first.Items[^1].Id, first.NextCursor);
        var second = await sdk.QueryImportedColorHistoryAsync(new(2, first.NextCursor));
        Assert.Single(second.Items); Assert.Null(second.NextCursor);
        Assert.Equal(new[] { "2026-11-08 21:00:02", "2026-11-08 21:00:01", "2026-11-08 21:00:00" },
            first.Items.Concat(second.Items).Select(x => x.LocalTime));
        Assert.Equal(3, first.Items.Concat(second.Items).Select(x => x.Id).Distinct().Count());
        using var reader = await host.Pair([ApiPermissions.SystemRead]);
        Assert.Equal("resource.notFound", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            reader.QueryImportedColorHistoryAsync(new(2, first.NextCursor)))).Problem!.Code);
        Assert.Equal("request.invalid", (await Assert.ThrowsAsync<MabiToolApiException>(() =>
            sdk.QueryImportedColorHistoryAsync(new(0)))).Problem!.Code);
    }

    [Fact]
    public async Task IdempotencyConflictsAndFullKeyBudgetPreserveOriginalReceipt()
    {
        using var workspace = new Workspace(); await using var host = await Host.Start(workspace);
        var imports = host.App.Services.GetRequiredService<SettingsImportCoordinator>();
        var database = host.App.Services.GetRequiredService<SqliteStore>();
        var request = Full("mabisync");
        var preview = await imports.PreviewAsync("mabisync", "direct-owner", request, "key-conflict-preview");
        Assert.Equal(preview.Id, (await imports.PreviewAsync("mabisync", "direct-owner", request, "key-conflict-preview")).Id);
        Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<OperationException>(() =>
            imports.PreviewAsync("mabisync", "direct-owner", request with { SourceLabel = "另一来源" }, "key-conflict-preview"))).Code);
        var receipt = await imports.CommitAsync("mabisync", "direct-owner", preview.Id, "key-conflict-commit", preview.TargetETag);
        Assert.Equal("idempotency.conflict", (await Assert.ThrowsAsync<OperationException>(() =>
            imports.CommitAsync("mabisync", "direct-owner", preview.Id, "key-conflict-commit", "\"settings-" + new string('0', 64) + "\""))).Code);
        await database.WriteAsync(tx => tx.Execute("""
            WITH RECURSIVE n(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<998)
            INSERT INTO settings_import_keys(owner,project,kind,key,hash,import_id,expires_at)
            SELECT 'direct-owner','mabisync','commit','capacity-key-'||i,'fixture-hash',$id,$expiry FROM n
            """, ("$id", preview.Id), ("$expiry", DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds())));
        Assert.Equal(1000L, database.ReadScalar("SELECT count(*) FROM settings_import_keys"));
        Assert.Equal(receipt, await imports.CommitAsync("mabisync", "direct-owner", preview.Id, "key-conflict-commit", preview.TargetETag));
        Assert.Equal(1UL, host.App.Services.GetRequiredService<RoomPreferenceStore>().Read("direct-owner").Revision.Value);
    }

    [Fact]
    public async Task DamagedOrOversizedPrivateSnapshotsCannotCommit()
    {
        var packed = ImportSnapshotCodec.Pack(new string('x', 4096), 8192);
        Assert.Equal("storage.assetDamaged", Assert.Throws<StorageException>(() =>
            ImportSnapshotCodec.Read<string>(packed, 1024)).Code);
        using var workspace = new Workspace(); await using var host = await Host.Start(workspace);
        var imports = host.App.Services.GetRequiredService<SettingsImportCoordinator>();
        var database = host.App.Services.GetRequiredService<SqliteStore>();
        var preview = await imports.PreviewAsync("color-studio", "direct-owner", Full("color-studio"), "damaged-plan-preview");
        var plan = (string)database.ReadScalar("SELECT plan FROM settings_imports WHERE id=$id", ("$id", preview.Id))!;
        var damaged = plan[..4] + (plan[4] == 'A' ? 'B' : 'A') + plan[5..];
        await database.WriteAsync(tx => tx.Execute("UPDATE settings_imports SET plan=$plan WHERE id=$id", ("$plan", damaged), ("$id", preview.Id)));
        Assert.Equal("storage.assetDamaged", Assert.Throws<StorageException>(() =>
            imports.ValidateCommit("color-studio", "direct-owner", preview.Id, preview.TargetETag)).Code);
        Assert.Equal("storage.assetDamaged", (await Assert.ThrowsAsync<StorageException>(() =>
            imports.CommitAsync("color-studio", "direct-owner", preview.Id, "damaged-plan-commit", preview.TargetETag))).Code);
        Assert.Equal(0UL, host.App.Services.GetRequiredService<ColorLibraryStore>().Read().Revision.Value);
        Assert.Equal(0UL, host.App.Services.GetRequiredService<ColorPreferenceStore>().Read("direct-owner").Revision.Value);
        Assert.Equal(0L, database.ReadScalar("SELECT count(*) FROM color_import_history"));
    }

    private sealed class ImportClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }

    private static async Task<OperationSnapshot> Complete(MabiToolClient sdk, OperationSnapshot operation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!SqliteStore.IsTerminal(operation.State.KnownValue)) { await Task.Delay(20, deadline.Token); operation = await sdk.GetOperationAsync(operation.Id, deadline.Token); }
        return operation;
    }
    private sealed class ChunkedContent : HttpContent
    {
        private readonly byte[] bytes;
        public ChunkedContent(byte[] bytes) { this.bytes = bytes; Headers.ContentType = new("application/json"); }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class ShortcutSource : IGlobalShortcutSource
    {
        public IReadOnlyList<ShortcutRegistrationResult> Registrations { get; set; } = [new("emergency-stop", new(ShortcutKey.F9), new(ShortcutKey.F9), ShortcutRegistrationStatus.Registered, null, false)];
        public event Action<string>? ActionReceived;
        public void Emit(string action) => ActionReceived?.Invoke(action);
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public IReadOnlyList<ShortcutRegistrationResult> GetRegistrations() => Registrations;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required Uri Address { get; init; }
        public static async Task<Host> Start(Workspace workspace, IGlobalShortcutSource? shortcuts = null, TimeProvider? clock = null)
        {
            var app = BackendApplication.Build(new(0, Token, new HashSet<string> { Origin }), storage: workspace.Options, shortcutSource: shortcuts, clock: clock);
            await app.StartAsync();
            return new() { App = app, Address = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        }
        public MabiToolClient Sdk(string token = Token) => new(Address, _ => ValueTask.FromResult(token));
        public HttpClient Http(string token = Token)
        { var client = new HttpClient { BaseAddress = Address }; client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return client; }
        public async Task<MabiToolClient> Pair(IReadOnlyList<string> permissions)
        {
            using var owner = Sdk(); var challenge = await owner.CreatePairingChallengeAsync(new("settings-reader", null, permissions));
            return Sdk((await owner.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code))).Token);
        }
        public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); }
    }
    private static string Root()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MabiTool.slnx"))) root = root.Parent;
        return root!.FullName;
    }
    private sealed class Workspace : IDisposable
    {
        private readonly string directory = Path.Combine(Root(), "artifacts", "tmp", "settings-import-tests", Guid.NewGuid().ToString("N"));
        public StorageOptions Options => new(Path.Combine(directory, "bootstrap"), true, Path.Combine(directory, "data"));
        public (string Path, byte[] Bytes)[] SaveOriginals(SettingsImportRequest request)
        {
            Directory.CreateDirectory(Path.Combine(directory, "originals"));
            var files = request.Files.Select(x => (Path: Path.Combine(directory, "originals", x.Name), Bytes: Convert.FromBase64String(x.ContentBase64))).ToArray();
            foreach (var file in files) File.WriteAllBytes(file.Path, file.Bytes); return files;
        }
        public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

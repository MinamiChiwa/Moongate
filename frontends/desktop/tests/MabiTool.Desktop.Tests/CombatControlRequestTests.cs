using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using Xunit;

namespace MabiTool.Desktop.Tests;

public sealed class CombatControlRequestTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static CombatMeasurementState Running(string id = "measurement-a") => new("running", new(90000, 2000), new(42),
        false, "combat.measurementActive", "obs", id, 40000, 50000, new(new BigInteger(1000)), 20, true, [], null, null, new(7));
    private static MabiToolClient Client(HttpMessageHandler handler) => new(new Uri("http://127.0.0.1:18543"),
        _ => ValueTask.FromResult("test-token"), handler);

    [Fact]
    public async Task ExplicitCancelKeepsTheDisplayedMeasurementId()
    {
        using var handler = new ControlHandler { Measurement = Running("measurement-b") };
        using var api = Client(handler);
        var call = new LiveBackend.MeasurementCall("combat.measure.cancel", Running(), "obs");
        var error = await Assert.ThrowsAsync<MabiToolApiException>(() => call.ExecuteAsync(api, Token));
        Assert.Equal("combat.measurementInactive", error.Problem!.Code);
        Assert.Equal("measurement-a", Assert.Single(handler.Writes).Body.GetProperty("measurementId").GetString());
        Assert.Equal(0, handler.Reads);
        Assert.Equal("measurement-b", handler.Measurement.MeasurementId);
    }

    [Fact]
    public async Task ToggleReadsTheCurrentPhaseOnceAndAnOldCancelRetryDoesNotTargetTheNextMeasurement()
    {
        using var handler = new ControlHandler { FailFirstWrite = true, ReplaceMeasurementOnFailure = true };
        using var api = Client(handler);
        var shownIdle = Running() with { Phase = "idle", MeasurementId = null };
        var call = new LiveBackend.MeasurementCall("combat.measure.toggle", shownIdle, "obs");
        var error = await Assert.ThrowsAsync<MabiToolApiException>(() =>
            LiveBackend.WithStorageRetryAsync(() => call.ExecuteAsync(api, Token), Token));
        Assert.Equal("combat.measurementInactive", error.Problem!.Code);
        Assert.Equal(1, handler.Reads);
        Assert.Equal(2, handler.Writes.Count);
        Assert.All(handler.Writes, request =>
        {
            Assert.EndsWith("/measurement/cancel", request.Path);
            Assert.Equal("measurement-a", request.Body.GetProperty("measurementId").GetString());
        });
        Assert.Single(handler.Writes.Select(request => request.Key).Distinct());
        Assert.Equal("measurement-b", handler.Measurement.MeasurementId);
    }

    [Fact]
    public async Task StartRetriesKeepTheSameKeyAndDisplayedSettings()
    {
        using var handler = new ControlHandler { FailFirstWrite = true };
        using var api = Client(handler);
        var call = new LiveBackend.MeasurementCall("combat.measure.start", Running() with { Phase = "idle" }, "obs");
        await LiveBackend.WithStorageRetryAsync(() => call.ExecuteAsync(api, Token), Token);
        Assert.Equal(0, handler.Reads);
        Assert.Equal(2, handler.Writes.Count);
        Assert.Single(handler.Writes.Select(request => request.Key).Distinct());
        Assert.All(handler.Writes, request =>
        {
            Assert.Equal("obs", request.Body.GetProperty("observationId").GetString());
            Assert.Equal(90000, request.Body.GetProperty("durationMs").GetInt32());
            // 预备倒计时已停用：测量从第一次有效伤害起计时，请求里的预备时长固定为 0，不取设定里的旧值。
            Assert.Equal(0, request.Body.GetProperty("countdownMs").GetInt32());
        });
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("auto")]
    public async Task PreferenceRetriesKeepTheConfigurationEtagWhileRuntimeRevisionChanges(string kind)
    {
        using var handler = new ControlHandler { FailFirstWrite = true };
        using var api = Client(handler);
        var settings = LiveBackend.MeasurementSettingsWrite(new(120000, 5000), handler.Measurement.SettingsRevision.Value);
        var auto = LiveBackend.AutoObservationWrite(false);
        await LiveBackend.WithStorageRetryAsync(async () =>
        {
            if (kind == "settings") await settings(api, Token); else await auto(api, Token);
            return true;
        }, Token);
        Assert.Equal(kind == "settings" ? 0 : 1, handler.Reads);
        Assert.Equal(2, handler.Writes.Count);
        Assert.Single(handler.Writes.Select(request => request.Key).Distinct());
        var expected = kind == "settings" ? ControlHandler.SettingsTag : ControlHandler.AutoTag;
        Assert.All(handler.Writes, request => Assert.Equal(expected, request.IfMatch));
        Assert.Equal(new UInt64String(99), handler.Measurement.Revision);
    }

    [Fact]
    public async Task AnAutomaticPreferenceWithoutAConfigurationEtagCannotOverwriteSettings()
    {
        using var handler = new ControlHandler { OmitTag = true };
        using var api = Client(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => LiveBackend.AutoObservationWrite(false)(api, Token));
        Assert.Equal(1, handler.Reads);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task AStaleSettingsLayerCannotOverwriteAConfigurationSavedByAnotherFrontend()
    {
        using var handler = new ControlHandler();
        using var api = Client(handler);
        var write = LiveBackend.MeasurementSettingsWrite(new(120000, 5000), handler.Measurement.SettingsRevision.Value);
        handler.Measurement = handler.Measurement with { Settings = new(180000, 0), SettingsRevision = new(8) };

        var error = await Assert.ThrowsAsync<MabiToolApiException>(() =>
            LiveBackend.WithStorageRetryAsync(() => write(api, Token), Token));
        Assert.Equal("revision.stale", error.Problem!.Code);
        Assert.Equal(0, handler.Reads);
        Assert.Equal(ControlHandler.SettingsTag, Assert.Single(handler.Writes).IfMatch);
        Assert.Equal(new CombatMeasurementSettings(180000, 0), handler.Measurement.Settings);
        Assert.Equal(new UInt64String(8), handler.Measurement.SettingsRevision);
    }

    private sealed record Write(string Path, string Key, string? IfMatch, JsonElement Body);
    private sealed class ControlHandler : HttpMessageHandler
    {
        internal const string SettingsTag = "\"combat-measurement-settings-7\"";
        internal const string AutoTag = "\"combat-auto-observation-5\"";
        public CombatMeasurementState Measurement { get; set; } = Running();
        public bool FailFirstWrite { get; init; }
        public bool ReplaceMeasurementOnFailure { get; init; }
        public bool OmitTag { get; init; }
        public int Reads { get; private set; }
        public List<Write> Writes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            var automatic = path.EndsWith("/auto-observation", StringComparison.Ordinal);
            var tag = automatic ? AutoTag : $"\"combat-measurement-settings-{Measurement.SettingsRevision}\"";
            if (request.Method == HttpMethod.Get)
            {
                Reads++;
                var response = automatic ? Json(new CombatAutoObservationState(true, "observing", null, "obs", new(0))) : Json(Measurement);
                if (!OmitTag) response.Headers.ETag = new(tag);
                return response;
            }
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var write = new Write(path, request.Headers.GetValues("Idempotency-Key").Single(), request.Headers.IfMatch.FirstOrDefault()?.Tag,
                document.RootElement.Clone());
            Writes.Add(write);
            if (FailFirstWrite && Writes.Count == 1)
            {
                Measurement = Measurement with { Revision = new(99), MeasurementId = ReplaceMeasurementOnFailure ? "measurement-b" : Measurement.MeasurementId };
                return Json(ApiErrors.Create("storage.unavailable", "busy", "trace"), HttpStatusCode.ServiceUnavailable);
            }
            if (path.EndsWith("/measurement/cancel", StringComparison.Ordinal))
            {
                if (write.Body.GetProperty("measurementId").GetString() != Measurement.MeasurementId)
                    return Json(ApiErrors.Create("combat.measurementInactive", "different", "trace"), HttpStatusCode.Conflict);
                return Json(Measurement with { Phase = "idle", MeasurementId = null });
            }
            if (automatic || path.EndsWith("/measurement/settings", StringComparison.Ordinal))
            {
                if (write.IfMatch != tag)
                    return Json(ApiErrors.Create("revision.stale", "updated", "trace"), HttpStatusCode.PreconditionFailed);
                if (!automatic)
                    Measurement = Measurement with
                    {
                        Settings = write.Body.Deserialize<CombatMeasurementSettings>(ContractJson.Options)!,
                        SettingsRevision = new(Measurement.SettingsRevision.Value + 1)
                    };
                return automatic ? Json(new CombatAutoObservationState(false, "off", "combat.autoDisabled", null, new(0))) : Json(Measurement);
            }
            Assert.EndsWith("/measurement/start", path);
            return Json(Measurement with { Phase = "countdown" });
        }

        private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, ContractJson.Options), Encoding.UTF8,
                status == HttpStatusCode.OK ? "application/json" : "application/problem+json")
        };
    }
}

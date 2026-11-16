using System.Net.Http.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

public sealed partial class RoomScoreDraftApiTests
{
    [Fact]
    public async Task DeferredCancellationKeepsTheSentCommandAndCancelledOrRevokedQueuedJobsDoNotSend()
    {
        using var area = new RoomInputApiTests.Area(); await using var app = area.Build(); await app.StartAsync();
        var grant = Pair(app); using var client = RoomInputApiTests.Client(app, grant.Token); using var owner = RoomInputApiTests.Client(app);
        await using var server = await RoomSessionManagerTests.FakeRoomServer.StartAsync(); server.SupportsScoreDraft = true;
        var sentSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CommandHook = message => { if (message.GetProperty("type").GetString() != "publish-score-draft") return Task.CompletedTask;
            sentSignal.TrySetResult(); return resume.Task; };
        try
        {
            var room = await Create(client, server.Endpoint); var selection = await Seed(app, grant.Session.ClientId);
            var request = new RoomScoreDraftRequest(selection, [new(room.MemberId!, null, "piano")]); var initial = await Read(client);
            var sent = await Post(client, ApiContract.PublishRoomScoreDraft.Path, request, "sent-score-draft", initial.ETag);
            await sentSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var deferred = await RoomInputApiTests.Post<OperationSnapshot>(client, ApiContract.CancelOperation.Path.Replace("{id}", sent.Id), new CancelOperationRequest("取消确认等待"));
            Assert.Equal(CancellationState.Deferred, deferred.Cancellation.State.KnownValue);
            var queued = await Post(client, ApiContract.PublishRoomScoreDraft.Path, request, "queued-score-draft", initial.ETag);
            var cancelled = await RoomInputApiTests.Post<OperationSnapshot>(client, ApiContract.CancelOperation.Path.Replace("{id}", queued.Id), new CancelOperationRequest("取消排队"));
            Assert.Equal(OperationState.Cancelled, cancelled.State.KnownValue); Assert.Equal(EffectStatus.None, cancelled.EffectStatus.KnownValue);
            var revoked = await Post(client, ApiContract.PublishRoomScoreDraft.Path, request, "revoked-score-draft", initial.ETag);
            app.Services.GetRequiredService<LocalAuthService>().Revoke(grant.Session.ClientId); resume.TrySetResult();
            var completed = await Terminal(owner, sent.Id); Assert.Equal(OperationState.Succeeded, completed.State.KnownValue);
            Assert.Equal(CancellationState.Completed, completed.Cancellation.State.KnownValue);
            var refused = await Terminal(owner, revoked.Id); Assert.Equal("permission.denied", refused.Problem!.Code);
            Assert.Single(server.Commands, message => message.GetProperty("type").GetString() == "publish-score-draft");
            await Wait(() => Task.FromResult(!app.Services.GetRequiredService<ActivityBarrier>().Snapshot().Activities.Any(item => item.Kind == "room.scoreDraft")));
            Assert.Empty(app.Services.GetRequiredService<ModuleRuntime>().ActiveOperations("rooms"));
            Assert.Empty(app.Services.GetRequiredService<ModuleRuntime>().ActiveOperations("music-library"));
        }
        finally { resume.TrySetResult(); await app.StopAsync(); }
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task ACommandWithLostConfirmationSurvivesRestartAndItsIdempotentRetryDoesNotReplay()
    {
        using var area = new RoomInputApiTests.Area(); await using var server = await RoomSessionManagerTests.FakeRoomServer.StartAsync();
        server.SupportsScoreDraft = true; server.ConfirmCommands = false;
        RoomScoreDraftRequest request; string id, etag;
        await using (var app = area.Build())
        {
            await app.StartAsync(); using var client = RoomInputApiTests.Client(app);
            var room = await Create(client, server.Endpoint); var selection = await Seed(app, "owner");
            request = new(selection, [new(room.MemberId!, null, "piano")]); etag = (await Read(client)).ETag;
            var operation = await Post(client, ApiContract.PublishRoomScoreDraft.Path, request, "lost-draft-confirmation", etag); id = operation.Id;
            var failed = await Terminal(client, id); Assert.Equal(OperationState.Failed, failed.State.KnownValue);
            Assert.Equal("room.commandUnconfirmed", failed.Problem!.Code); Assert.Equal(EffectStatus.Unknown, failed.EffectStatus.KnownValue);
            await app.StopAsync();
        }
        await using (var app = area.Build(false))
        {
            await app.StartAsync(); using var client = RoomInputApiTests.Client(app);
            var retry = await Post(client, ApiContract.PublishRoomScoreDraft.Path, request, "lost-draft-confirmation", etag);
            Assert.Equal(id, retry.Id); Assert.Equal(OperationState.Failed, retry.State.KnownValue); Assert.Equal(EffectStatus.Unknown, retry.EffectStatus.KnownValue);
            Assert.Single(server.Commands, message => message.GetProperty("type").GetString() == "publish-score-draft");
            Assert.False((await Read(client)).Snapshot.Supported); Assert.Empty(area.Device.Commands);
            await app.StopAsync();
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

public sealed class AuthTests
{
    private const string Origin = "http://localhost:5173";
    private const string OtherOrigin = "http://localhost:5174";

    [Fact]
    public async Task BrowserPairsOnceUsesScopedCredentialsAndCanBeRevoked()
    {
        await using var host = await TestHost.Start();
        using var owner = host.Sdk();
        var challenge = await owner.CreatePairingChallengeAsync(new("browser", Origin, [ApiPermissions.SystemRead]));
        using var pairing = host.Sdk("unused", Origin, anonymous: true);
        var grant = await pairing.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code));
        Assert.NotEqual(host.Token, grant.Token);
        Assert.False(grant.Session.Owner);
        Assert.Equal(Origin, grant.Session.Origin);
        Assert.Equal([ApiPermissions.SystemRead], grant.Session.Permissions);
        using var browser = host.Sdk(grant.Token, Origin);
        Assert.Equal("healthy", (await browser.GetHealthAsync()).Status);
        Assert.Equal(grant.Session.ClientId, (await browser.GetAuthSessionAsync()).ClientId);
        Assert.Equal(grant.Session.ClientId, Assert.Single((await owner.GetAuthClientsAsync()).Items).ClientId);
        var denied = await Assert.ThrowsAsync<MabiToolApiException>(() => browser.GetAuthClientsAsync());
        Assert.Equal("permission.denied", denied.Problem!.Code);
        Assert.Equal(403, denied.Status);
        await Assert.ThrowsAsync<MabiToolApiException>(() => browser.CreatePairingChallengeAsync(new("escalate", null, [ApiPermissions.SystemRead])));
        var replay = await Assert.ThrowsAsync<MabiToolApiException>(() => pairing.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code)));
        Assert.Equal("auth.pairingInvalid", replay.Problem!.Code);
        foreach (var wrongOrigin in new string?[] { null, OtherOrigin })
        {
            using var wrong = host.Sdk(grant.Token, wrongOrigin);
            var mismatch = await Assert.ThrowsAsync<MabiToolApiException>(() => wrong.GetHealthAsync());
            Assert.Equal("auth.originMismatch", mismatch.Problem!.Code);
        }
        var revoked = await owner.RevokeClientAsync(grant.Session.ClientId);
        Assert.True(revoked.Revoked);
        Assert.True((await owner.RevokeClientAsync(grant.Session.ClientId)).Revoked);
        var after = await Assert.ThrowsAsync<MabiToolApiException>(() => browser.GetHealthAsync());
        Assert.Equal(401, after.Status);
        Assert.Empty((await owner.GetAuthClientsAsync()).Items);
    }

    [Fact]
    public async Task PairingOriginAndOwnerScopeCannotBeBypassedByWritingRequests()
    {
        await using var host = await TestHost.Start();
        using var owner = host.Sdk();
        var challenge = await owner.CreatePairingChallengeAsync(new("browser", Origin, [ApiPermissions.SystemRead]));
        using var wrong = host.Sdk("unused", OtherOrigin, anonymous: true);
        var mismatch = await Assert.ThrowsAsync<MabiToolApiException>(() => wrong.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code)));
        Assert.Equal("auth.originMismatch", mismatch.Problem!.Code);
        foreach (var origin in new[] { Origin, "https://attacker.example" })
        {
            using var source = host.Sdk(host.Token, origin);
            var error = await Assert.ThrowsAsync<MabiToolApiException>(() => source.CreatePairingChallengeAsync(new("unexpected", origin, [ApiPermissions.SystemRead])));
            Assert.Equal(403, error.Status);
        }
        Assert.Empty((await owner.GetAuthClientsAsync()).Items);
        using var nativePairing = host.Sdk("unused", anonymous: true);
        var nativeChallenge = await owner.CreatePairingChallengeAsync(new("native", null, [ApiPermissions.SystemRead]));
        var native = await nativePairing.RedeemPairingAsync(new(nativeChallenge.ChallengeId, nativeChallenge.Code));
        using var nativeClient = host.Sdk(native.Token);
        var errorResult = await Assert.ThrowsAsync<MabiToolApiException>(() => nativeClient.RevokeClientAsync("owner"));
        Assert.Equal("permission.denied", errorResult.Problem!.Code);
        var ownerRemoval = await Assert.ThrowsAsync<MabiToolApiException>(() => owner.RevokeClientAsync("owner"));
        Assert.Equal("auth.ownerCannotRevoke", ownerRemoval.Problem!.Code);
    }

    [Theory]
    [InlineData("{}", "application/json", 400)]
    [InlineData("{\"clientName\":null,\"origin\":null,\"permissions\":[]}", "application/json", 400)]
    [InlineData("{\"clientName\":\"x\",\"origin\":null,\"permissions\":[\"auth.manage\"]}", "application/json", 400)]
    [InlineData("{\"clientName\":\"x\",\"origin\":\"https://attacker.example\",\"permissions\":[\"system.read\"]}", "application/json", 400)]
    [InlineData("{\"clientName\":\"x\",\"clientName\":\"y\",\"origin\":null,\"permissions\":[\"system.read\"]}", "application/json", 400)]
    [InlineData("clientName=x", "text/plain", 415)]
    public async Task InvalidPairingBodiesProduceStructuredErrors(string body, string mediaType, int status)
    {
        await using var host = await TestHost.Start();
        using var http = host.Http();
        using var response = await http.PostAsync(ApiContract.PairingChallenges.Path, new StringContent(body, System.Text.Encoding.UTF8, mediaType));
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(status, (await response.Content.ReadFromJsonAsync<ApiProblem>(ContractJson.Options))!.Status);
    }

    [Fact]
    public async Task UrlCredentialsOversizeBodiesAndInvalidPreflightsDoNotChangeAuthState()
    {
        await using var host = await TestHost.Start();
        using var http = host.Http();
        using var query = await http.PostAsJsonAsync(ApiContract.PairingChallenges.Path + "?token=" + host.Token,
            new PairingChallengeRequest("browser", Origin, [ApiPermissions.SystemRead]));
        Assert.Equal(400, (int)query.StatusCode);
        Assert.DoesNotContain(host.Token, await query.Content.ReadAsStringAsync());
        using var large = await http.PostAsync(ApiContract.PairingChallenges.Path,
            new StringContent(new string('x', 65537), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(413, (int)large.StatusCode);
        foreach (var method in new[] { "PUT", "PATCH", "INVALID" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, ApiContract.PairingChallenges.Path);
            request.Headers.Add("Origin", Origin);
            request.Headers.Add("Access-Control-Request-Method", method);
            using var denied = await http.SendAsync(request);
            Assert.Equal(400, (int)denied.StatusCode);
        }
        using var valid = new HttpRequestMessage(HttpMethod.Options, ApiContract.PairingChallenges.Path);
        valid.Headers.Add("Origin", Origin);
        valid.Headers.Add("Access-Control-Request-Method", "POST");
        valid.Headers.Add("Access-Control-Request-Headers", "authorization, content-type");
        using var accepted = await http.SendAsync(valid);
        Assert.Equal(204, (int)accepted.StatusCode);
        Assert.DoesNotContain("*", accepted.Headers.GetValues("Access-Control-Allow-Origin"));
        Assert.Empty((await host.AuthSnapshot()).Items);
    }

    [Fact]
    public void PairingExpirationUsesMonotonicTimeAndCredentialsAreBounded()
    {
        var clock = new TestClock();
        var options = new LocalApiOptions(0, LocalAuthService.NewSecret(), new HashSet<string>([Origin]));
        var auth = new LocalAuthService(options, "test-epoch", clock);
        var expired = auth.CreateChallenge(new("browser", Origin, [ApiPermissions.SystemRead]));
        clock.Advance(LocalAuthService.ChallengeLifetime);
        Assert.Equal("auth.pairingInvalid", Assert.Throws<LocalApiException>(() => auth.Redeem(new(expired.ChallengeId, expired.Code), Origin)).Code);
        var challenge = auth.CreateChallenge(new("browser", Origin, [ApiPermissions.SystemRead]));
        var grant = auth.Redeem(new(challenge.ChallengeId, challenge.Code), Origin);
        clock.UtcNow -= TimeSpan.FromDays(10);
        Assert.NotNull(auth.Authenticate(grant.Token, Origin));
        clock.Advance(LocalAuthService.ClientLifetime);
        Assert.Null(auth.Authenticate(grant.Token, Origin));
        for (var i = 0; i < LocalAuthService.MaximumChallenges; i++) auth.CreateChallenge(new("pending", null, [ApiPermissions.SystemRead]));
        Assert.Equal("auth.pairingCapacityExceeded", Assert.Throws<LocalApiException>(() => auth.CreateChallenge(new("extra", null, [ApiPermissions.SystemRead]))).Code);
        clock.Advance(LocalAuthService.ChallengeLifetime);
        Assert.NotNull(auth.CreateChallenge(new("recovered", null, [ApiPermissions.SystemRead])));
    }

    [Fact]
    public async Task ConcurrentRedeemsIssueOnlyOneCredentialAndRateLimitRecovers()
    {
        var clock = new TestClock();
        var auth = new LocalAuthService(new(0, LocalAuthService.NewSecret(), new HashSet<string>()), "test-epoch", clock);
        var challenge = auth.CreateChallenge(new("native", null, [ApiPermissions.SystemRead]));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try { return auth.Redeem(new(challenge.ChallengeId, challenge.Code), null); }
            catch (LocalApiException) { return null; }
        })));
        Assert.Single(results.OfType<AccessGrant>());
        for (var i = 0; i < 8; i++) Assert.Throws<LocalApiException>(() => auth.Redeem(new("unknown", "bad"), null));
        Assert.Equal("auth.pairingRateLimited", Assert.Throws<LocalApiException>(() => auth.Redeem(new("unknown", "bad"), null)).Code);
        clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = auth.CreateChallenge(new("new-native", null, [ApiPermissions.SystemRead]));
        Assert.NotNull(auth.Redeem(new(recovered.ChallengeId, recovered.Code), null));
    }

    [Fact]
    public void ClientCapacityRecoversAfterRevocation()
    {
        var clock = new TestClock();
        var auth = new LocalAuthService(new(0, LocalAuthService.NewSecret(), new HashSet<string>()), "test-epoch", clock);
        string? first = null;
        for (var i = 0; i < LocalAuthService.MaximumClients; i++)
        {
            if (i != 0 && i % LocalAuthService.MaximumAttemptsPerMinute == 0) clock.Advance(TimeSpan.FromMinutes(1));
            var challenge = auth.CreateChallenge(new("native", null, [ApiPermissions.SystemRead]));
            var grant = auth.Redeem(new(challenge.ChallengeId, challenge.Code), null);
            first ??= grant.Session.ClientId;
        }
        Assert.Equal(LocalAuthService.MaximumClients, auth.Snapshot().Items.Count);
        Assert.Equal("auth.pairingCapacityExceeded", Assert.Throws<LocalApiException>(() => auth.CreateChallenge(new("extra", null, [ApiPermissions.SystemRead]))).Code);
        auth.Revoke(first!);
        Assert.NotNull(auth.CreateChallenge(new("after-revoke", null, [ApiPermissions.SystemRead])));
    }

    [Fact]
    public async Task ExistingInsecureRuntimeDirectoryIsRejectedWithoutChangingItsAclOrFiles()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var directory = Workspace();
        var runtime = Path.Combine(directory, "runtime");
        System.IO.Directory.CreateDirectory(runtime);
        var sentinel = Path.Combine(runtime, "user-file.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        var before = new DirectoryInfo(runtime).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        try
        {
            await using var app = BackendApplication.Build(new(0, LocalAuthService.NewSecret(), new HashSet<string>(), runtime));
            await Assert.ThrowsAsync<IOException>(() => app.StartAsync());
            Assert.Equal(before, new DirectoryInfo(runtime).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
            Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DiscoveryWithAnOldProcessIdentityIsRejected()
    {
        await using var host = await TestHost.Start(discovery: true);
        var file = Path.Combine(host.Directory!, "runtime", "instance.json");
        var discovery = JsonSerializer.Deserialize<LocalDiscoveryInfo>(await File.ReadAllTextAsync(file), ContractJson.Options)!;
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(discovery with { ProcessStartTime = discovery.ProcessStartTime - TimeSpan.FromSeconds(1) }, ContractJson.Options));
        await Assert.ThrowsAsync<IOException>(() => LocalBootstrapClient.ConnectAsync(file, "stale-process"));
    }

    [Fact]
    public async Task OccupiedPortIsReportedWithItsOwnReasonAndTheOwnerCanShutDownTheHost()
    {
        await using var first = await TestHost.Start();
        var port = first.BaseUri.Port;
        await using (var second = BackendApplication.Build(new(port, LocalAuthService.NewSecret(), new HashSet<string>())))
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => second.StartAsync());
            // 占用者是 Moongate 宿主：不带凭据的健康请求得到 auth.required，辨认为另一份 Moongate。
            var failure = await HostStartupFailure.DescribeAsync(error, port, started: false);
            Assert.Equal(("host.portInUse", HostStartupFailure.PortInUseExitCode), (failure.Code, failure.ExitCode));
            Assert.Contains($"本机端口 {port} 已被另一个正在运行的 Moongate 后端占用", failure.Message);
            Assert.DoesNotContain("数据", failure.Message);
            // 占用者回了其他 HTTP 响应：按其他程序占用给出单独的退出码。
            var other = await HostStartupFailure.DescribeAsync(error, port, false,
                (_, _) => Task.FromResult(HostStartupFailure.Occupant.Other));
            Assert.Equal(("host.portUnavailable", HostStartupFailure.PortUnavailableExitCode), (other.Code, other.ExitCode));
            // 已开始监听之后的同一异常属于运行期，不按端口或数据目录解释。
            Assert.Equal(HostStartupFailure.UnknownExitCode, (await HostStartupFailure.DescribeAsync(error, port, started: true)).ExitCode);
        }
        // 未指定端口时占用者是 Moongate 宿主：不改用别的端口，仍按端口冲突以退出码 5 结束。
        var occupied = await Assert.ThrowsAsync<SocketException>(() => HostStartupFailure.ResolveDefaultPortAsync(
            new(port, LocalAuthService.NewSecret(), new HashSet<string>(), PortFallback: true)));
        Assert.Equal(HostStartupFailure.PortInUseExitCode, (await HostStartupFailure.DescribeAsync(occupied, port, started: false)).ExitCode);

        // 所有者会话可以退出宿主：回应受理后宿主进入停止流程。
        using var sdk = first.Sdk();
        Assert.Equal("stopping", (await sdk.ShutdownHostAsync()).Status);
        await Task.Delay(Timeout.Infinite, first.Stopping).ContinueWith(_ => { }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(first.Stopping.IsCancellationRequested);
    }

    [Fact]
    public async Task DefaultPortTakenByAnotherProgramFallsBackToASystemPortButAnExplicitPortDoesNot()
    {
        // 占用者接受连接后回一行非 HTTP 内容，可确定不是 Moongate 宿主。
        var occupant = new TcpListener(IPAddress.Loopback, 0);
        occupant.Start();
        var port = ((IPEndPoint)occupant.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            while (true)
            {
                using var connection = await occupant.AcceptTcpClientAsync(stop.Token);
                var stream = connection.GetStream();
                _ = await stream.ReadAsync(new byte[4096], stop.Token);
                await stream.WriteAsync("SSH-2.0-occupant\r\n"u8.ToArray(), stop.Token);
            }
        });
        var directory = Workspace();
        try
        {
            var requested = new LocalApiOptions(port, LocalAuthService.NewSecret(), new HashSet<string>(), Path.Combine(directory, "runtime"),
                PortFallback: true);
            var (options, fallback) = await HostStartupFailure.ResolveDefaultPortAsync(requested);
            Assert.Equal((0, port, "occupied"), (options.Port, fallback?.RequestedPort, fallback?.Reason));
            await using (var app = BackendApplication.Build(options))
            {
                await app.StartAsync();
                // 前端按发现文件握手，连到系统分配的实际端口。
                var handshake = await LocalBootstrapClient.ConnectAsync(Path.Combine(directory, "runtime", "instance.json"), "test-desktop");
                var actual = new Uri(handshake.ApiBaseUri);
                Assert.NotEqual(port, actual.Port);
                Assert.Equal(new Uri(app.Urls.Single()).Port, actual.Port);
                using var sdk = new MabiToolClient(actual, _ => ValueTask.FromResult(handshake.Grant.Token));
                Assert.True((await sdk.GetAuthSessionAsync()).Owner);
                await app.StopAsync();
            }

            // 显式指定的端口：不改用别的端口，绑定失败按其他程序占用以退出码 6 结束。
            var pinned = requested with { PortFallback = false, RuntimeDirectory = null };
            Assert.Equal((pinned, (HostStartupFailure.PortFallback?)null), await HostStartupFailure.ResolveDefaultPortAsync(pinned));
            await using var second = BackendApplication.Build(pinned);
            var error = await Assert.ThrowsAnyAsync<Exception>(() => second.StartAsync());
            var failure = await HostStartupFailure.DescribeAsync(error, port, started: false);
            Assert.Equal(("host.portUnavailable", HostStartupFailure.PortUnavailableExitCode), (failure.Code, failure.ExitCode));
        }
        finally
        {
            await stop.CancelAsync();
            occupant.Stop();
            await serving.ContinueWith(_ => { }, TaskScheduler.Default);
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ProtectedDiscoveryBootstrapsNativeClientAndPreventsSecondHost()
    {
        await using var host = await TestHost.Start(discovery: true);
        var file = Path.Combine(host.Directory!, "runtime", "instance.json");
        var json = await File.ReadAllTextAsync(file);
        Assert.DoesNotContain(host.Token, json);
        var bootstrap = await LocalBootstrapClient.ConnectAsync(file, "test-desktop");
        Assert.Equal(host.Token, bootstrap.Grant.Token);
        Assert.Equal(host.BaseUri.ToString(), bootstrap.ApiBaseUri);
        using var sdk = host.Sdk(bootstrap.Grant.Token);
        Assert.True((await sdk.GetAuthSessionAsync()).Owner);
        await using var second = BackendApplication.Build(new(0, LocalAuthService.NewSecret(), new HashSet<string>(), Path.GetDirectoryName(file)));
        await Assert.ThrowsAsync<IOException>(() => second.StartAsync());
        Assert.Equal(json, await File.ReadAllTextAsync(file));
        Assert.Equal(host.Token, (await LocalBootstrapClient.ConnectAsync(file, "still-live")).Grant.Token);
        await host.Stop();
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task RestartReplacesStaleDiscoveryAndInvalidatesOldCredentials()
    {
        await using var first = await TestHost.Start(discovery: true);
        var file = Path.Combine(first.Directory!, "runtime", "instance.json");
        var previous = await File.ReadAllTextAsync(file);
        using var owner = first.Sdk();
        var challenge = await owner.CreatePairingChallengeAsync(new("native", null, [ApiPermissions.SystemRead]));
        using var redeem = first.Sdk("unused", anonymous: true);
        var grant = await redeem.RedeemPairingAsync(new(challenge.ChallengeId, challenge.Code));
        await first.Stop();
        await File.WriteAllTextAsync(file, previous);
        await using var second = await TestHost.Start(discoveryDirectory: Path.GetDirectoryName(file));
        Assert.NotEqual(previous, await File.ReadAllTextAsync(file));
        using var stale = second.Sdk(grant.Token);
        Assert.Equal(401, (await Assert.ThrowsAsync<MabiToolApiException>(() => stale.GetHealthAsync())).Status);
        using var staleOwner = second.Sdk(first.Token);
        Assert.Equal(401, (await Assert.ThrowsAsync<MabiToolApiException>(() => staleOwner.GetHealthAsync())).Status);
        Assert.Equal(second.Token, (await LocalBootstrapClient.ConnectAsync(file, "new-desktop")).Grant.Token);
    }

    [Fact]
    public async Task InsecureDiscoveryAclAndBadIpcFramesAreRejected()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        await using var host = await TestHost.Start(discovery: true);
        var file = Path.Combine(host.Directory!, "runtime", "instance.json");
        var discovery = JsonSerializer.Deserialize<LocalDiscoveryInfo>(await File.ReadAllTextAsync(file), ContractJson.Options)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using (var pipe = new System.IO.Pipes.NamedPipeClientStream(".", discovery.PipeName, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly))
        {
            await pipe.ConnectAsync(timeout.Token);
            await pipe.WriteAsync(BitConverter.GetBytes(LocalIpcProtocol.MaximumFrameBytes + 1), timeout.Token);
            var buffer = new byte[1];
            Assert.Equal(0, await pipe.ReadAsync(buffer, timeout.Token));
        }
        Assert.NotNull(await LocalBootstrapClient.ConnectAsync(file, "after-bad-frame"));
        var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var info = new FileInfo(file);
        var directory = info.Directory!;
        GrantWorldRead(info);
        // 运行目录仍是当前用户独占：只有发现文件多出主体时先改回当前用户独占，再照常连接。
        Assert.NotNull(await LocalBootstrapClient.ConnectAsync(file, "repaired-file"));
        Assert.False(GrantsWorld(info));
        // 运行目录本身也授予了其他主体：不修复，拒绝连接。
        GrantWorldRead(directory);
        GrantWorldRead(info);
        try { await Assert.ThrowsAsync<IOException>(() => LocalBootstrapClient.ConnectAsync(file, "insecure-file")); }
        finally
        {
            var restored = directory.GetAccessControl();
            restored.RemoveAccessRuleAll(new FileSystemAccessRule(world, FileSystemRights.Read, AccessControlType.Allow));
            directory.SetAccessControl(restored);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool GrantsWorld(FileInfo file)
    {
        var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        foreach (FileSystemAccessRule rule in file.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (world.Equals(rule.IdentityReference)) return true;
        return false;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void GrantWorldRead(FileSystemInfo target)
    {
        var world = new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow);
        if (target is DirectoryInfo directory) { var security = directory.GetAccessControl(); security.AddAccessRule(world); directory.SetAccessControl(security); }
        else { var file = (FileInfo)target; var security = file.GetAccessControl(); security.AddAccessRule(world); file.SetAccessControl(security); }
    }

    [Fact]
    public void SecretRecordsDoNotPrintCredentials()
    {
        var code = LocalAuthService.NewSecret();
        var token = LocalAuthService.NewSecret();
        Assert.DoesNotContain(code, new PairingRequest("challenge", code).ToString());
        Assert.DoesNotContain(code, new PairingChallenge("challenge", code, DateTimeOffset.UtcNow).ToString());
        Assert.DoesNotContain(token, new AccessGrant(token, new("owner", "native", [], null, null, true)).ToString());
        Assert.DoesNotContain(token, new LocalApiOptions(0, token, new HashSet<string>()).ToString());
    }

    [Theory]
    [InlineData("release", 1)]
    [InlineData("held", 4)]
    [InlineData("cancel", 1)]
    public async Task DiscoveryPublicationRetriesRealFileLocksWithBoundedCancellation(string outcome, int expectedDelays)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var directory = Workspace();
        var runtime = Path.Combine(directory, "runtime");
        LocalDiscoverySecurity.EnsureDirectory(runtime);
        var target = Path.Combine(runtime, "instance.json");
        var temporary = Path.Combine(runtime, "new.tmp");
        try
        {
            await File.WriteAllTextAsync(target, "old");
            await File.WriteAllTextAsync(temporary, "new");
            using var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var cancellation = new CancellationTokenSource();
            var delays = 0;
            Task Delay(TimeSpan duration, CancellationToken token)
            {
                Assert.Equal(TimeSpan.FromMilliseconds(25 * (1 << delays)), duration);
                Assert.Equal(cancellation.Token, token);
                delays++;
                if (outcome == "release") held.Dispose();
                if (outcome == "cancel") cancellation.Cancel();
                return Task.Delay(TimeSpan.Zero, token);
            }
            var publish = LocalDiscoveryService.PublishAsync(temporary, target, cancellation.Token, Delay);
            if (outcome == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publish);
            else if (outcome == "held")
            {
                var error = await Record.ExceptionAsync(() => publish);
                Assert.True(error is IOException or UnauthorizedAccessException);
                Assert.Contains(error!.HResult & 0xffff, new[] { 5, 32, 33 });
            }
            else await publish;
            Assert.Equal(expectedDelays, delays);
            Assert.Equal(outcome == "release" ? "new" : "old", await File.ReadAllTextAsync(target));
            Assert.Equal(outcome != "release", File.Exists(temporary));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DiscoveryPublicationRechecksPermissionsAfterAnOccupiedAttempt()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var directory = Workspace();
        var runtime = Path.Combine(directory, "runtime");
        LocalDiscoverySecurity.EnsureDirectory(runtime);
        var target = Path.Combine(runtime, "instance.json");
        var temporary = Path.Combine(runtime, "new.tmp");
        try
        {
            await File.WriteAllTextAsync(target, "old");
            await File.WriteAllTextAsync(temporary, "new");
            using var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            var delays = 0;
            // 占用期间运行目录被授予了其他主体：重试前重新校验目录，拒绝发布。
            Task Delay(TimeSpan duration, CancellationToken token)
            {
                if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
                delays++;
                held.Dispose();
                GrantWorldRead(new DirectoryInfo(runtime));
                return Task.CompletedTask;
            }
            await Assert.ThrowsAsync<IOException>(() => LocalDiscoveryService.PublishAsync(temporary, target, default, Delay));
            Assert.Equal(1, delays);
            Assert.Equal("old", await File.ReadAllTextAsync(target));
            Assert.True(File.Exists(temporary));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("release")]
    [InlineData("held")]
    [InlineData("cancel")]
    public async Task HostDiscoveryPublicationReleasesTemporaryFileLockAndAllPipeListeners(string outcome)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var directory = Workspace();
        var runtime = Path.Combine(directory, "runtime");
        LocalDiscoverySecurity.EnsureDirectory(runtime);
        var target = Path.Combine(runtime, "instance.json");
        try
        {
            await File.WriteAllTextAsync(target, "old");
            using var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var cancellation = new CancellationTokenSource();
            await using var app = BackendApplication.Build(new(0, LocalAuthService.NewSecret(), new HashSet<string>(), runtime));
            var info = app.Services.GetRequiredService<SystemInfo>();
            var temporary = Path.Combine(runtime, info.InstanceId + ".tmp");
            var startup = app.StartAsync(cancellation.Token);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!File.Exists(temporary) && !startup.IsCompleted)
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("启动未进入发现发布");
                await Task.Delay(2);
            }
            Assert.True(File.Exists(temporary));
            if (outcome == "release") held.Dispose();
            if (outcome == "cancel") cancellation.Cancel();
            if (outcome == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
            else if (outcome == "held")
                Assert.True(await Record.ExceptionAsync(() => startup) is IOException or UnauthorizedAccessException);
            else
            {
                await startup;
                Assert.Equal(info.InstanceId, (await LocalBootstrapClient.ConnectAsync(target, "after-occupied-publication")).InstanceId);
                await app.StopAsync();
            }
            Assert.False(File.Exists(temporary));
            if (outcome != "release") Assert.Equal("old", await File.ReadAllTextAsync(target));
            using var instanceLock = new FileStream(Path.Combine(runtime, "host.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var probes = new List<System.IO.Pipes.NamedPipeServerStream>();
            try
            {
                for (var i = 0; i < 4; i++) probes.Add(new("mabitool-" + info.InstanceId, System.IO.Pipes.PipeDirection.InOut, 4,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly));
            }
            finally { foreach (var pipe in probes) pipe.Dispose(); }
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    private sealed class TestClock : TimeProvider
    {
        private long timestamp;
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public void Advance(TimeSpan duration) { timestamp += duration.Ticks; UtcNow += duration; }
    }

    private static string Workspace()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "MabiTool.slnx"))) root = root.Parent!;
        return Path.Combine(root.FullName, "artifacts/tmp/auth-tests", Guid.NewGuid().ToString("N"));
    }

    private sealed class OriginHandler(string origin) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { request.Headers.Add("Origin", origin); return base.SendAsync(request, cancellationToken); }
    }

    private sealed class TestHost : IAsyncDisposable
    {
        private readonly Microsoft.AspNetCore.Builder.WebApplication app;
        private bool stopped;
        public string Token { get; }
        public Uri BaseUri { get; }
        public string? Directory { get; }
        public CancellationToken Stopping => app.Lifetime.ApplicationStopping;
        private TestHost(Microsoft.AspNetCore.Builder.WebApplication app, string token, string? directory)
        {
            this.app = app; Token = token; Directory = directory;
            BaseUri = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        }
        public static async Task<TestHost> Start(bool discovery = false, string? discoveryDirectory = null)
        {
            string? directory = null;
            if (discovery)
            {
                directory = Workspace();
                discoveryDirectory = Path.Combine(directory, "runtime");
            }
            var token = LocalAuthService.NewSecret();
            var app = BackendApplication.Build(new(0, token, new HashSet<string>([Origin, OtherOrigin]), discoveryDirectory));
            await app.StartAsync();
            return new(app, token, directory);
        }
        public MabiToolClient Sdk(string? token = null, string? origin = null, bool anonymous = false) =>
            new(BaseUri, _ => anonymous ? throw new InvalidOperationException("匿名配对不得请求 bearer") : ValueTask.FromResult(token ?? Token),
                origin is null ? null : new OriginHandler(origin));
        public HttpClient Http()
        {
            var http = new HttpClient { BaseAddress = BaseUri };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            return http;
        }
        public async Task<ApiList<AuthSession>> AuthSnapshot() { using var sdk = Sdk(); return await sdk.GetAuthClientsAsync(); }
        public async Task Stop() { if (!stopped) { await app.StopAsync(); stopped = true; } }
        public async ValueTask DisposeAsync()
        {
            await Stop(); await app.DisposeAsync();
            if (Directory is not null && System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}

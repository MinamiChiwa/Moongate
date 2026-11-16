using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MabiTool.Infrastructure.Updates;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
[SupportedOSPlatform("windows")]
public sealed class UpdateHandoffTests
{
    private sealed record Payload(string Value);
    private static readonly string Transaction = new('1', 32);
    private static readonly string Session = new('2', 32);
    private static UpdateHandoffEnvelope Envelope() => new(1, Session, Transaction, 1, "offer", JsonSerializer.SerializeToElement(new Payload("hello"), UpdateHandoffProtocol.Json));
    private static UpdatePeerIdentity Current()
    {
        using var process = Process.GetCurrentProcess();
        return UpdatePeerIdentity.Capture(process);
    }

    [Fact]
    public async Task FramesSupportPartialReadsAndMultipleConsecutiveMessages()
    {
        using var stream = new MemoryStream();
        await UpdateHandoffProtocol.WriteAsync(stream, Envelope(), default);
        await UpdateHandoffProtocol.WriteAsync(stream, Envelope() with { Sequence = 2 }, default);
        stream.Position = 0;
        using var split = new SplitReadStream(stream);
        Assert.Equal(1, (await UpdateHandoffProtocol.ReadAsync(split, default)).Sequence);
        Assert.Equal(2, (await UpdateHandoffProtocol.ReadAsync(split, default)).Sequence);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_048_577)]
    public async Task InvalidLengthRejectedBeforeBodyRead(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        Assert.Equal("update.handoffInvalid", (await Assert.ThrowsAsync<UpdatePlanException>(() => UpdateHandoffProtocol.ReadAsync(stream, default))).Code);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("session")]
    [InlineData("transaction")]
    [InlineData("zero-sequence")]
    [InlineData("large-sequence")]
    [InlineData("kind")]
    [InlineData("payload")]
    [InlineData("duplicate")]
    [InlineData("nested-duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("malformed")]
    [InlineData("depth")]
    public async Task InvalidEnvelopeCannotBeRead(string change)
    {
        var envelope = Envelope();
        envelope = change switch
        {
            "version" => envelope with { ProtocolVersion = 2 },
            "session" => envelope with { SessionId = "bad" },
            "transaction" => envelope with { TransactionId = "bad" },
            "zero-sequence" => envelope with { Sequence = 0 },
            "large-sequence" => envelope with { Sequence = 65 },
            "kind" => envelope with { Kind = "execute-shell" },
            "payload" => envelope with { Payload = JsonSerializer.SerializeToElement("wrong") },
            _ => envelope
        };
        var json = JsonSerializer.Serialize(envelope, UpdateHandoffProtocol.Json);
        if (change == "duplicate") json = json.Insert(1, "\"protocolVersion\":1,");
        if (change == "nested-duplicate") json = json.Replace("\"value\":\"hello\"", "\"value\":\"hello\",\"value\":\"other\"");
        if (change == "unknown") json = json.Insert(1, "\"unexpected\":true,");
        if (change == "missing") json = json.Replace("\"sequence\":1,", "");
        if (change == "null") json = "null";
        if (change == "malformed") json = "{";
        if (change == "depth") json = json.Replace("\"hello\"", new string('[', 25) + "0" + new string(']', 25));
        using var stream = RawFrame(json);
        Assert.Equal("update.handoffInvalid", (await Assert.ThrowsAsync<UpdatePlanException>(() => UpdateHandoffProtocol.ReadAsync(stream, default))).Code);
    }

    [Fact]
    public async Task OversizeWriteAndTruncatedReadDoNotProduceValidMessages()
    {
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<UpdatePlanException>(() => UpdateHandoffProtocol.WriteAsync(output,
            Envelope() with { Payload = JsonSerializer.SerializeToElement(new Payload(new string('x', 1_048_576))) }, default));
        Assert.Equal(0, output.Length);
        using var input = RawFrame("{}");
        input.SetLength(input.Length - 1);
        await Assert.ThrowsAsync<EndOfStreamException>(() => UpdateHandoffProtocol.ReadAsync(input, default));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("image")]
    [InlineData("pid")]
    public void WrongProcessIdentityCannotBePinned(string change)
    {
        var actual = Current();
        var wrong = change switch
        {
            "start" => actual with { StartTimeUtcTicks = actual.StartTimeUtcTicks + 1 },
            "image" => actual with { ExecutablePath = Path.Combine(Path.GetDirectoryName(actual.ExecutablePath)!, "wrong.exe") },
            _ => actual with { ProcessId = -1 }
        };
        Assert.Equal("update.handoffPeerInvalid", Assert.Throws<UpdatePlanException>(() => new PinnedUpdatePeer(wrong)).Code);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("wrong-client")]
    [InlineData("wrong-server")]
    public async Task RealChildVerifiesBothOperatingSystemPipePeers(string mode)
    {
        using var fixture = new ChildFixture();
        using var listener = new UpdateHandoffListener(Transaction);
        fixture.WriteInput(Current(), mode);
        using var child = fixture.Start(listener.PipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var expected = mode == "wrong-client" ? Current() : UpdatePeerIdentity.Capture(child);
            fixture.Release();
            if (mode == "wrong-client")
                Assert.Equal("update.handoffPeerInvalid", (await Assert.ThrowsAsync<UpdatePlanException>(() => listener.AcceptAsync(expected, timeout.Token))).Code);
            else
            {
                using var server = await listener.AcceptAsync(expected, timeout.Token);
                if (mode == "wrong-server") await Assert.ThrowsAnyAsync<IOException>(() => server.ReceiveAsync<Payload>("accepted", timeout.Token));
                else
                {
                    Assert.Equal("child", (await server.ReceiveAsync<Payload>("accepted", timeout.Token)).Value);
                    await server.SendAsync("healthCheck", new Payload("parent"), timeout.Token);
                    while (!File.Exists(fixture.Result)) await Task.Delay(20, timeout.Token);
                    using var result = JsonDocument.Parse(File.ReadAllText(fixture.Result));
                    Assert.Equal("parent", result.RootElement.GetProperty("Value").GetString());
                    Assert.Equal(Environment.ProcessId, result.RootElement.GetProperty("ProcessId").GetInt32());
                    await server.SendAsync("abort", new Payload("done"), timeout.Token);
                    server.Dispose();
                }
            }
            await child.WaitForExitAsync(timeout.Token);
            Assert.Equal(mode == "success" ? 0 : 3, child.ExitCode);
            if (mode == "wrong-server") Assert.Equal("update.handoffPeerInvalid", File.ReadAllText(fixture.Result));
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task ExitedPeerCannotSendFurtherHealthMessages()
    {
        using var fixture = new ChildFixture();
        using var listener = new UpdateHandoffListener(Transaction);
        fixture.WriteInput(Current(), "success");
        using var child = fixture.Start(listener.PipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var identity = UpdatePeerIdentity.Capture(child);
            fixture.Release();
            using var server = await listener.AcceptAsync(identity, timeout.Token);
            await server.ReceiveAsync<Payload>("accepted", timeout.Token);
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(timeout.Token);
            Assert.Equal("update.handoffPeerInvalid", (await Assert.ThrowsAsync<UpdatePlanException>(() =>
                server.SendAsync("healthCheck", new Payload("late"), timeout.Token))).Code);
            Assert.Equal("update.handoffPeerInvalid", Assert.Throws<UpdatePlanException>(() => new PinnedUpdatePeer(identity)).Code);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task PipeAclGrantsOnlyCurrentUserAndDeniesNetworkIdentity()
    {
        using var listener = new UpdateHandoffListener(Transaction);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepting = listener.AcceptAsync(Current(), timeout.Token);
        using var raw = new NamedPipeClientStream(".", listener.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(timeout.Token);
        using var server = await accepting;
        var acl = raw.GetAccessControl();
        var user = WindowsIdentity.GetCurrent().User!;
        Assert.True(acl.AreAccessRulesProtected);
        Assert.Equal(user, acl.GetOwner(typeof(SecurityIdentifier)));
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
        Assert.All(rules.Where(rule => rule.AccessControlType == AccessControlType.Allow), rule => Assert.Equal(user, rule.IdentityReference));
        Assert.Contains(rules, rule => rule.AccessControlType == AccessControlType.Deny &&
            rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.NetworkSid, null)));
    }

    [Fact]
    public async Task ListenerDeadlineAndDisposalTerminateWaitingAcceptance()
    {
        var time = new DeadlineClock();
        using var listener = new UpdateHandoffListener(Transaction, time);
        var pending = listener.AcceptAsync(Current());
        time.Expire();
        Assert.Equal("update.handoffTimeout", (await Assert.ThrowsAsync<UpdatePlanException>(() => pending)).Code);
        await Assert.ThrowsAsync<UpdatePlanException>(() => listener.AcceptAsync(Current()));
        using var other = new UpdateHandoffListener(Transaction);
        var waiting = other.AcceptAsync(Current());
        other.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => waiting);
    }

    [Fact]
    public async Task ListenerAcceptsAfterSixtyOneSecondsWithoutRestartingItsTotalDeadline()
    {
        var time = new DeadlineClock();
        using var listener = new UpdateHandoffListener(Transaction, time);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepting = listener.AcceptAsync(Current(), timeout.Token);
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.False(accepting.IsCompleted);
        using var client = await UpdateHandoffConnection.ConnectAsync(listener.PipeName, Current(), Transaction, timeout.Token);
        using var server = await accepting;
        await ExchangeAsync(client, server, "61", timeout.Token);
        time.Advance(TimeSpan.FromSeconds(58));
        await ExchangeAsync(client, server, "119", timeout.Token);
        var pending = server.ReceiveAsync<Payload>("offer", timeout.Token);
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("update.handoffTimeout", (await Assert.ThrowsAsync<UpdatePlanException>(() => pending)).Code);
        await Assert.ThrowsAsync<UpdatePlanException>(() => server.SendAsync("abort", new Payload("expired"), timeout.Token));
        await Assert.ThrowsAnyAsync<IOException>(() => client.ReceiveAsync<Payload>("healthCheck", timeout.Token));
    }

    [Fact]
    public async Task ConnectedClientExchangesAfterSixtyOneSecondsButMessagesDoNotExtendTotalDeadline()
    {
        var time = new DeadlineClock();
        using var listener = new UpdateHandoffListener(Transaction);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepting = listener.AcceptAsync(Current(), timeout.Token);
        using var client = await UpdateHandoffConnection.ConnectAsync(listener.PipeName, Current(), Transaction, timeout.Token, time);
        using var server = await accepting;
        time.Advance(TimeSpan.FromSeconds(61));
        await ExchangeAsync(client, server, "61", timeout.Token);
        time.Advance(TimeSpan.FromSeconds(58));
        await ExchangeAsync(client, server, "119", timeout.Token);
        var pending = client.ReceiveAsync<Payload>("healthCheck", timeout.Token);
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("update.handoffTimeout", (await Assert.ThrowsAsync<UpdatePlanException>(() => pending)).Code);
        await Assert.ThrowsAsync<UpdatePlanException>(() => client.SendAsync("abort", new Payload("expired"), timeout.Token));
        await Assert.ThrowsAnyAsync<IOException>(() => server.ReceiveAsync<Payload>("offer", timeout.Token));
    }

    [Fact]
    public async Task DisposingConnectionCancelsConcurrentReadersAndRejectsLaterWrites()
    {
        using var listener = new UpdateHandoffListener(Transaction);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepting = listener.AcceptAsync(Current(), timeout.Token);
        using var client = await UpdateHandoffConnection.ConnectAsync(listener.PipeName, Current(), Transaction, timeout.Token);
        using var server = await accepting;
        var first = server.ReceiveAsync<Payload>("offer", timeout.Token);
        var second = server.ReceiveAsync<Payload>("offer", timeout.Token);
        server.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => first);
        await Assert.ThrowsAnyAsync<Exception>(() => second);
        await Assert.ThrowsAsync<UpdatePlanException>(() => server.SendAsync("abort", new Payload("closed")));
    }

    [Fact]
    public async Task ConnectingToMissingPipeHonorsCancellationAndTotalDeadline()
    {
        using var cancel = new CancellationTokenSource();
        var name = UpdateHandoffListener.Prefix + Guid.NewGuid().ToString("N");
        var pending = UpdateHandoffConnection.ConnectAsync(name, Current(), Transaction, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var time = new DeadlineClock();
        var expired = UpdateHandoffConnection.ConnectAsync(name, Current(), Transaction, time: time);
        time.Expire();
        Assert.Equal("update.handoffTimeout", (await Assert.ThrowsAsync<UpdatePlanException>(() => expired)).Code);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("transaction")]
    [InlineData("sequence")]
    [InlineData("kind")]
    [InlineData("payload")]
    public async Task WrongMessageClosesAuthenticatedChannel(string change)
    {
        using var listener = new UpdateHandoffListener(Transaction);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepting = listener.AcceptAsync(Current(), timeout.Token);
        using var raw = new NamedPipeClientStream(".", listener.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(timeout.Token);
        using var server = await accepting;
        var envelope = Envelope() with { SessionId = listener.SessionId };
        envelope = change switch
        {
            "session" => envelope with { SessionId = new string('3', 32) },
            "transaction" => envelope with { TransactionId = new string('3', 32) },
            "sequence" => envelope with { Sequence = 2 },
            "kind" => envelope with { Kind = "commit" },
            _ => envelope with { Payload = JsonSerializer.SerializeToElement(new { value = "hello", unknown = true }) }
        };
        await UpdateHandoffProtocol.WriteAsync(raw, envelope, timeout.Token);
        Assert.Equal("update.handoffInvalid", (await Assert.ThrowsAsync<UpdatePlanException>(() => server.ReceiveAsync<Payload>("offer", timeout.Token))).Code);
        await Assert.ThrowsAsync<UpdatePlanException>(() => server.SendAsync("abort", new Payload("closed")));
    }

    [Fact]
    public async Task ReplayOfAlreadyReceivedSequenceClosesChannel()
    {
        using var listener = new UpdateHandoffListener(Transaction);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepting = listener.AcceptAsync(Current(), timeout.Token);
        using var raw = new NamedPipeClientStream(".", listener.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(timeout.Token);
        using var server = await accepting;
        var frame = Envelope() with { SessionId = listener.SessionId };
        await UpdateHandoffProtocol.WriteAsync(raw, frame, timeout.Token);
        await server.ReceiveAsync<Payload>("offer", timeout.Token);
        await UpdateHandoffProtocol.WriteAsync(raw, frame, timeout.Token);
        await Assert.ThrowsAsync<UpdatePlanException>(() => server.ReceiveAsync<Payload>("offer", timeout.Token));
        await Assert.ThrowsAsync<UpdatePlanException>(() => server.SendAsync("abort", new Payload("closed")));
    }

    [Fact]
    public async Task MessageLimitClosesChannelAfterSixtyFourFrames()
    {
        using var listener = new UpdateHandoffListener(Transaction);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepting = listener.AcceptAsync(Current(), timeout.Token);
        using var client = await UpdateHandoffConnection.ConnectAsync(listener.PipeName, Current(), Transaction, timeout.Token);
        using var server = await accepting;
        for (var count = 0; count < 64; count++)
        {
            await client.SendAsync("offer", new Payload(count.ToString()), timeout.Token);
            Assert.Equal(count.ToString(), (await server.ReceiveAsync<Payload>("offer", timeout.Token)).Value);
        }
        await Assert.ThrowsAsync<UpdatePlanException>(() => client.SendAsync("offer", new Payload("overflow"), timeout.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineOrCallerCancellationClosesPendingRead(bool caller)
    {
        var time = new DeadlineClock();
        using var listener = new UpdateHandoffListener(Transaction, time);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepting = listener.AcceptAsync(Current(), timeout.Token);
        using var client = await UpdateHandoffConnection.ConnectAsync(listener.PipeName, Current(), Transaction, timeout.Token);
        using var server = await accepting;
        using var cancel = new CancellationTokenSource();
        var pending = server.ReceiveAsync<Payload>("offer", cancel.Token);
        if (caller) cancel.Cancel(); else time.Expire();
        if (caller) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else Assert.Equal("update.handoffTimeout", (await Assert.ThrowsAsync<UpdatePlanException>(() => pending)).Code);
        await Assert.ThrowsAsync<UpdatePlanException>(() => server.SendAsync("abort", new Payload("closed")));
    }

    private static MemoryStream RawFrame(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        stream.Write(header); stream.Write(bytes); stream.Position = 0;
        return stream;
    }

    private static async Task ExchangeAsync(UpdateHandoffConnection client, UpdateHandoffConnection server, string value, CancellationToken token)
    {
        await client.SendAsync("offer", new Payload(value), token);
        Assert.Equal(value, (await server.ReceiveAsync<Payload>("offer", token)).Value);
        await server.SendAsync("healthCheck", new Payload(value), token);
        Assert.Equal(value, (await client.ReceiveAsync<Payload>("healthCheck", token)).Value);
    }

    private sealed class SplitReadStream(Stream inner) : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], token);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<TimerHandle> timers = [];
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return timestamp; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(UpdateHandoffProtocol.Timeout, dueTime);
            var timer = new TimerHandle(this, () => callback(state));
            lock (gate) { timers.Add(timer); timer.Change(dueTime, period); }
            return timer;
        }
        internal void Expire() => Advance(UpdateHandoffProtocol.Timeout);
        internal void Advance(TimeSpan elapsed)
        {
            Action[] callbacks;
            lock (gate)
            {
                timestamp += elapsed.Ticks;
                callbacks = timers.Select(timer => timer.TakeDue(timestamp)).OfType<Action>().ToArray();
            }
            foreach (var callback in callbacks) callback();
        }
        private sealed class TimerHandle(DeadlineClock clock, Action callback) : ITimer
        {
            private long? dueAt;
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                lock (clock.gate)
                {
                    if (disposed) return false;
                    dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock.timestamp + dueTime.Ticks;
                    return true;
                }
            }
            internal Action? TakeDue(long now)
            {
                if (disposed || dueAt is null || now < dueAt) return null;
                dueAt = null;
                return callback;
            }
            public void Dispose() { lock (clock.gate) { disposed = true; dueAt = null; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class ChildFixture : IDisposable
    {
        private readonly string repository;
        private readonly string root;
        private string Input => Path.Combine(root, "input.json");
        internal string Result => Path.Combine(root, "result.json");
        internal ChildFixture()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) directory = directory.Parent;
            repository = directory!.FullName;
            root = Path.Combine(repository, "artifacts", "tmp", "update-handoff-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
        }
        internal void WriteInput(UpdatePeerIdentity parent, string mode) => File.WriteAllText(Input,
            JsonSerializer.Serialize(new { Parent = parent, TransactionId = Transaction, Mode = mode }, UpdateHandoffProtocol.Json));
        internal void Release() => File.WriteAllText(Input + ".go", "start");
        internal Process Start(string pipe)
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(Path.Combine(Path.GetFullPath("../../..", AppContext.BaseDirectory), "bin", "MabiTool.PersistenceProbe", configuration, "MabiTool.PersistenceProbe.dll"));
            foreach (var arg in new[] { "update-handoff", pipe, Input, Result }) info.ArgumentList.Add(arg);
            return Process.Start(info)!;
        }
        public void Dispose() => Directory.Delete(root, true);
    }
}

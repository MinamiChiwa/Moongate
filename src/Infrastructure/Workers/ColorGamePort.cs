using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure.Workers;

/// <summary>染色 Worker 的类型化宿主端口；资产与输入均绑定本次运行。</summary>
[SupportedOSPlatform("windows")]
public sealed class ColorGamePort : IDisposable
{
    private readonly GameInteractionSession interaction;
    private readonly string runId;
    private readonly string revision;
    private readonly string fence;
    private readonly string folder;
    private readonly bool processReadAllowed;
    private string? lastAsset;
    private volatile bool disposed;
    private bool cleaned;
    private readonly object assets = new();

    public ColorGamePort(GameInteractionSession interaction, string runId, string fencingToken, string assetRoot,
        bool processReadAllowed = false)
    {
        if (!WorkerProtocol.IsId(runId) || !WorkerProtocol.IsUInt64(fencingToken) ||
            fencingToken != interaction.FencingToken.ToString() || !Path.IsPathFullyQualified(assetRoot))
            throw new ArgumentException("染色运行端口配置无效。");
        this.interaction = interaction; this.runId = runId; fence = fencingToken;
        this.processReadAllowed = processReadAllowed;
        revision = interaction.Binding.Session.BindingRevision.ToString();
        folder = Path.Combine(assetRoot, runId);
        LocalDiscoverySecurity.RejectLinks(folder);
        Directory.CreateDirectory(folder);
        LocalDiscoverySecurity.RejectLinks(folder);
    }

    public async Task<JsonElement> HandleAsync(WorkerFrame frame, CancellationToken token)
    {
        if (disposed || frame.RunId != runId || frame.BindingRevision != revision || frame.FencingToken != fence)
            throw new WorkerException("worker.requestIdentityInvalid");
        var method = frame.Payload.GetProperty("method").GetString();
        var arguments = frame.Payload.GetProperty("arguments");
        using var cancellation = token.Register(interaction.Dispose);
        token.ThrowIfCancellationRequested();
        object result;
        switch (method)
        {
            case "game.snapshot":
                Empty(arguments); interaction.Check();
                var b = interaction.Binding;
                result = new { title = "bound", pid = b.Session.ProcessId, hwnd = b.Session.WindowIdentity.ToString(),
                    geometry = new[] { b.Bounds.X, b.Bounds.Y, b.Bounds.Width, b.Bounds.Height } };
                break;
            case "game.check":
                Empty(arguments); interaction.Check(); result = new { }; break;
            case "game.nativeAccess":
                Empty(arguments); interaction.Check();
                if (!processReadAllowed) throw new WorkerException("permission.missing");
                var session = interaction.Binding.Session;
                result = new { pid = session.ProcessId, hwnd = session.WindowIdentity.ToString(),
                    server = session.Region.ToLowerInvariant(),
                    processCreationTime = session.ProcessStartTime.UtcDateTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture) };
                break;
            case "game.capture":
                Empty(arguments);
                var image = await interaction.CaptureAsync(token).ConfigureAwait(false);
                var id = Guid.NewGuid().ToString("N");
                var path = Path.Combine(folder, id + ".blob");
                LocalDiscoverySecurity.RejectLinks(folder);
                try
                {
                    await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                    await file.WriteAsync(image.Rgb, token).ConfigureAwait(false);
                }
                catch { if (File.Exists(path)) File.Delete(path); throw; }
                lock (assets)
                {
                    if (disposed) { File.Delete(path); throw new GameInputException("input.runStopped"); }
                    try { if (lastAsset is not null) File.Delete(lastAsset); }
                    finally { lastAsset = path; }
                }
                result = new { width = image.Width, height = image.Height,
                    asset = new WorkerAssetReference(id, runId, image.Rgb.LongLength,
                        Convert.ToHexStringLower(SHA256.HashData(image.Rgb)), DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds()) };
                break;
            case "game.gesture":
                Fields(arguments, "gesture", "remainingSeconds");
                var gesture = arguments.GetProperty("gesture").Deserialize<PointerGesture>(WorkerProtocol.Json)
                    ?? throw new WorkerException("worker.portInvalid");
                result = new { performed = await interaction.ExecuteAsync(gesture, token,
                    arguments.GetProperty("remainingSeconds").GetDouble()).ConfigureAwait(false) };
                break;
            case "game.move":
                Fields(arguments, "x", "y");
                interaction.MovePointer(new(arguments.GetProperty("x").GetInt32(), arguments.GetProperty("y").GetInt32()));
                result = new { }; break;
            case "game.wheel":
                Fields(arguments, "delta"); interaction.Wheel(arguments.GetProperty("delta").GetInt32());
                result = new { }; break;
            case "game.release":
                Empty(arguments); interaction.ReleaseHeldInputs(); result = new { }; break;
            default: throw new WorkerException("worker.portMethodUnavailable");
        }
        return JsonSerializer.SerializeToElement(result, WorkerProtocol.Json);
    }

    private static void Empty(JsonElement value) => Fields(value);
    private static void Fields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !fields.ToHashSet(StringComparer.Ordinal).SetEquals(value.EnumerateObject().Select(p => p.Name)))
            throw new WorkerException("worker.portInvalid");
    }

    public void AbortInput()
    {
        disposed = true;
        interaction.Dispose();
    }

    public void Dispose()
    {
        try { AbortInput(); }
        finally
        {
            lock (assets)
            {
                if (!cleaned)
                {
                    cleaned = true;
                    LocalDiscoverySecurity.RejectLinks(folder);
                    if (lastAsset is not null) File.Delete(lastAsset);
                }
            }
        }
    }
}

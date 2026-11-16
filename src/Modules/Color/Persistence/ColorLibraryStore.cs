using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.Color.Domain;

namespace MabiTool.Modules.Color.Persistence;

public sealed record ColorLibraryImport(UInt64String ExpectedRevision, ColorLibrary Value, string Json, ColorLibraryChanged? Change);

public sealed class ColorLibraryStore(SqliteStore database, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim editing = new(8, 8);
    public const int MaximumProfiles = 100;
    public static DatabaseMigration Migration { get; } = new("color-library", 1, """
        CREATE TABLE color_library_workspace (
            id INTEGER PRIMARY KEY CHECK(id=1), revision TEXT NOT NULL, snapshot TEXT NOT NULL
        ) STRICT;
        CREATE TABLE color_library_receipts (
            principal TEXT NOT NULL, key TEXT NOT NULL, hash TEXT NOT NULL,
            result TEXT NOT NULL, expires_at INTEGER NOT NULL, PRIMARY KEY(principal,key)
        ) STRICT;
        """);

    public static string ETag(UInt64String revision) => "\"color-library-" + revision + "\"";
    public ColorLibrary Read() => Decode(database.ReadScalar("SELECT snapshot FROM color_library_workspace WHERE id=1") as string);
    private static ColorLibrary Decode(string? json) => json is null ? new(new(0), ColorProfiles.Default, [])
        : JsonSerializer.Deserialize<ColorLibrary>(json, ContractJson.Options)!;
    public ColorProfile Get(string id) => Read().Profiles.SingleOrDefault(p => p.Id == id)
        ?? throw new OperationException("resource.notFound", "配色方案不存在");

    public ColorLibraryImport PrepareImport(ColorLibrary current, ColorProfile? draft, IReadOnlyList<ColorProfile> imported)
    {
        if (draft is not null) ColorProfiles.Validate(draft, requireEnabled: false);
        foreach (var profile in imported) ColorProfiles.Validate(profile);
        if (draft is null && imported.Count == 0) return new(current.Revision, current, "", null);
        if (current.Revision.Value == ulong.MaxValue) throw new OperationException("resource.capacityExceeded", "配色库修订已耗尽");
        var revision = new UInt64String(current.Revision.Value + 1);
        var profiles = current.Profiles.Concat(imported.Select(p => p with { Name = p.Name.Trim(), Revision = revision })).ToArray();
        if (profiles.Length > MaximumProfiles) throw new OperationException("resource.capacityExceeded", "配色库最多保存 100 个方案");
        if (profiles.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != profiles.Length ||
            profiles.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profiles.Length)
            throw new OperationException("import.conflict", "导入配色方案的名称或 ID 冲突");
        var next = new ColorLibrary(revision, draft is null ? current.Draft : draft with { Revision = revision },
            profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray());
        var json = JsonSerializer.Serialize(next, ContractJson.Options);
        if (Encoding.UTF8.GetByteCount(json) > 2 * 1024 * 1024) throw new OperationException("resource.capacityExceeded", "配色库超过 2 MiB");
        return new(current.Revision, next, json, new(revision, profiles.Length, draft is not null));
    }

    public static UInt64String ImportRevision(DatabaseTransaction tx) => UInt64String.Parse(
        tx.Scalar("SELECT revision FROM color_library_workspace WHERE id=1") as string ?? "0");

    public void CommitImport(DatabaseTransaction tx, ColorLibraryImport import)
    {
        if (ImportRevision(tx) != import.ExpectedRevision) throw new OperationException("revision.stale", "配色库已变化，请重新预览");
        if (import.Change is null) return;
        tx.Execute("INSERT INTO color_library_workspace VALUES(1,$revision,$snapshot) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,snapshot=excluded.snapshot",
            ("$revision", import.Value.Revision.ToString()), ("$snapshot", import.Json));
        SqliteStore.AppendEvent(tx, new("color-library-" + import.Value.Revision, "color-library", "color-library", import.Value.Revision,
            "color.library.changed", JsonSerializer.SerializeToElement(import.Change, ContractJson.Options)));
    }

    public async Task<ColorLibraryChanged> EditAsync(string principal, ColorLibraryEdit request, string? key, string? ifMatch,
        CancellationToken ct = default)
    {
        if (key is null) throw new OperationException("idempotency.required", "配色编辑需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z")) throw Invalid("幂等键格式无效");
        if (ifMatch is null) throw new OperationException("revision.required", "配色编辑需要 If-Match");
        if (!Regex.IsMatch(ifMatch, @"\A""color-library-(0|[1-9][0-9]{0,19})""\z")) throw Invalid("配色库 ETag 格式无效");
        if (!await editing.WaitAsync(0, ct)) throw new OperationException("resource.capacityExceeded", "配色编辑并发达到上限");
        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(request, ContractJson.Options);
            if (body.Length > 64 * 1024) throw new OperationException("request.bodyTooLarge", "配色编辑超过 64 KiB");
            var frozen = JsonSerializer.Deserialize<ColorLibraryEdit>(body, ContractJson.Options);
            if (frozen is null || frozen.PutProfiles is null || frozen.DeleteProfileIds is null ||
                frozen.PutProfiles.Count + frozen.DeleteProfileIds.Count is > 100 ||
                frozen.PutProfiles.Count + frozen.DeleteProfileIds.Count == 0 && frozen.Draft is null)
                throw Invalid("编辑需要方案、删除项或草稿，单次最多 100 项");
            foreach (var p in frozen.PutProfiles) ColorProfiles.Validate(p);
            if (frozen.Draft is not null) ColorProfiles.Validate(frozen.Draft, requireEnabled: false);
            var ids = frozen.PutProfiles.Select(p => p.Id).Concat(frozen.DeleteProfileIds).ToArray();
            if (ids.Any(id => !ColorProfiles.IsId(id)) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                throw Invalid("编辑中的 ID 无效、重复或同时保存和删除");
            var hash = Convert.ToHexString(SHA256.HashData(body.Concat(Encoding.UTF8.GetBytes("\n" + ifMatch)).ToArray()));
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            return await database.WriteAsync(tx =>
            {
                var receipt = tx.Scalar("SELECT json_object('hash',hash,'result',json(result)) FROM color_library_receipts WHERE principal=$p AND key=$key AND expires_at>$now",
                    ("$p", principal), ("$key", key), ("$now", now)) as string;
                if (receipt is not null)
                {
                    using var decoded = JsonDocument.Parse(receipt);
                    if (decoded.RootElement.GetProperty("hash").GetString() != hash)
                        throw new OperationException("idempotency.conflict", "幂等键已用于其他内容或修订");
                    return decoded.RootElement.GetProperty("result").Deserialize<ColorLibraryChanged>(ContractJson.Options)!;
                }
                var current = Decode(tx.Scalar("SELECT snapshot FROM color_library_workspace WHERE id=1") as string);
                if (ETag(current.Revision) != ifMatch) throw new OperationException("revision.stale", "配色库已变化，请重新读取");
                if (current.Revision.Value == ulong.MaxValue) throw new OperationException("resource.capacityExceeded", "配色库修订已耗尽");
                var revision = new UInt64String(current.Revision.Value + 1);
                var profiles = current.Profiles.ToDictionary(p => p.Id, StringComparer.Ordinal);
                foreach (var id in frozen.DeleteProfileIds)
                    if (!profiles.Remove(id)) throw new OperationException("resource.notFound", "待删除方案不存在");
                foreach (var p in frozen.PutProfiles) profiles[p.Id] = p with { Name = p.Name.Trim(), Revision = revision };
                if (profiles.Count > MaximumProfiles) throw new OperationException("resource.capacityExceeded", "配色库最多保存 100 个方案");
                if (profiles.Values.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profiles.Count)
                    throw new OperationException("resource.conflict", "配色方案名称重复");
                var draft = frozen.Draft is null ? current.Draft : frozen.Draft with { Name = frozen.Draft.Name.Trim(), Revision = revision };
                var next = new ColorLibrary(revision, draft, profiles.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray());
                var json = JsonSerializer.Serialize(next, ContractJson.Options);
                if (Encoding.UTF8.GetByteCount(json) > 2 * 1024 * 1024) throw new OperationException("resource.capacityExceeded", "配色库超过 2 MiB");
                tx.Execute("DELETE FROM color_library_receipts WHERE expires_at<=$now", ("$now", now));
                if ((long)tx.Scalar("SELECT count(*) FROM color_library_receipts")! >= 1000)
                    throw new OperationException("resource.capacityExceeded", "配色幂等记录达到上限");
                var result = new ColorLibraryChanged(revision, profiles.Count, frozen.Draft is not null);
                tx.Execute("INSERT INTO color_library_workspace VALUES(1,$r,$s) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,snapshot=excluded.snapshot",
                    ("$r", revision.ToString()), ("$s", json));
                tx.Execute("INSERT INTO color_library_receipts VALUES($p,$key,$hash,$result,$expiry) ON CONFLICT(principal,key) DO UPDATE SET hash=excluded.hash,result=excluded.result,expires_at=excluded.expires_at",
                    ("$p", principal), ("$key", key), ("$hash", hash), ("$result", JsonSerializer.Serialize(result, ContractJson.Options)), ("$expiry", now + 86_400_001));
                SqliteStore.AppendEvent(tx, new("color-library-" + revision, "color-library", "color-library", revision,
                    "color.library.changed", JsonSerializer.SerializeToElement(result, ContractJson.Options)));
                return result;
            }, ct);
        }
        finally { editing.Release(); }
    }
    private static OperationException Invalid(string text) => new("request.invalid", text);
}

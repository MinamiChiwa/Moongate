using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using MabiTool.Contracts;
using MabiTool.Contracts.Generation;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : Environment.CurrentDirectory);
if (!File.Exists(Path.Combine(root, "MabiTool.slnx")))
    throw new ArgumentException("根目录缺少 MabiTool.slnx");
var output = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(root, "artifacts", "generated"));
var metadataDirectory = Path.Combine(output, "contracts");
Directory.CreateDirectory(metadataDirectory);
using var generationLock = AcquireLock(Path.Combine(metadataDirectory, ".generation.lock"));
var statePath = Path.Combine(metadataDirectory, ".generation.state");
var outputListPath = Path.Combine(metadataDirectory, ".generation.outputs");
var outputListDigestPath = Path.Combine(metadataDirectory, ".generation.outputs.sha256");
var workerSchemaPath = Path.Combine(root, "contracts", "worker", "observation-worker.v1.schema.json");
var inputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", new[]
{
    Assembly.GetExecutingAssembly().Location, typeof(ApiContract).Assembly.Location, workerSchemaPath
}.Select(HashFile)))));
var expectedOutputs = new[]
{
    "contracts/openapi/v1.json", "contracts/room/score-draft.v1.schema.json",
    "contracts/worker/observation-worker.v1.schema.json", "contracts/problem.v1.schema.json",
    "contracts/error-codes.v1.json", "sdk/typescript/client.ts", "sdk/csharp/MabiToolClient.g.cs"
}.Concat(ContractEvents.Definitions.Select(x => "contracts/events/" + x.Type + ".v1.schema.json"))
    .Order(StringComparer.Ordinal).ToArray();
GenerationState? saved = null;
try
{
    if (File.Exists(statePath)) saved = JsonSerializer.Deserialize<GenerationState>(File.ReadAllBytes(statePath));
}
catch (Exception error) when (error is JsonException or IOException) { }
if (saved?.Outputs is not null && saved.InputHash == inputHash &&
    saved.Outputs.Keys.Order(StringComparer.Ordinal).SequenceEqual(expectedOutputs) &&
    saved.Outputs.All(x => File.Exists(OutputPath(x.Key)) && HashFile(OutputPath(x.Key)) == x.Value))
{
    WriteOutputList();
    // 输入只被触碰时也更新完成水位，实际契约文件保持原时间。
    File.SetLastWriteTimeUtc(statePath, DateTime.UtcNow);
    Console.WriteLine("契约输入与产物未变，复用已有输出：" + output);
    return;
}
var pretty = new JsonSerializerOptions { WriteIndented = true };
var outputHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
void Write(string relative, string contents)
{
    var path = OutputPath(relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    contents = contents.Replace("\r\n", "\n").TrimEnd() + "\n";
    var bytes = Encoding.UTF8.GetBytes(contents);
    WriteAtomic(path, bytes);
    outputHashes[relative] = Convert.ToHexString(SHA256.HashData(bytes));
}
Write("contracts/openapi/v1.json", OpenApiDocument.Create().ToJsonString(pretty));
Write("contracts/room/score-draft.v1.schema.json", RoomScoreDraftProtocolSchema.Create().ToJsonString(pretty));
Write("contracts/worker/observation-worker.v1.schema.json",
    File.ReadAllText(workerSchemaPath));
foreach (var definition in ContractEvents.Definitions)
    Write("contracts/events/" + definition.Type + ".v1.schema.json", ContractSchemas.EventSchema(definition).ToJsonString(pretty));
var problemSchema = ContractSchemas.Export(typeof(ApiProblem));
problemSchema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
Write("contracts/problem.v1.schema.json", problemSchema.ToJsonString(pretty));
Write("contracts/error-codes.v1.json", JsonSerializer.Serialize(ApiErrors.Definitions, new JsonSerializerOptions(ContractJson.Options) { WriteIndented = true }));
Write("sdk/typescript/client.ts", ClientGenerator.TypeScript());
Write("sdk/csharp/MabiToolClient.g.cs", ClientGenerator.CSharp());
WriteOutputList();
WriteAtomic(statePath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new GenerationState(inputHash, outputHashes), pretty) + "\n"), force: true);
Console.WriteLine("已生成 OpenAPI 3.1、事件/错误 Schema 与 TypeScript/C# SDK：" + output);

string OutputPath(string relative) => Path.Combine(output, relative.Replace('/', Path.DirectorySeparatorChar));

void WriteOutputList()
{
    var bytes = Encoding.UTF8.GetBytes(string.Join("\n", expectedOutputs) + "\n");
    WriteAtomic(outputListPath, bytes);
    WriteAtomic(outputListDigestPath, Encoding.UTF8.GetBytes(Convert.ToHexString(SHA256.HashData(bytes)) + "\n"));
}

static string HashFile(string path)
{
    using var file = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(file));
}

static FileStream AcquireLock(string path)
{
    var waiting = Stopwatch.StartNew();
    while (true)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) when (waiting.Elapsed < TimeSpan.FromSeconds(60)) { Thread.Sleep(50); }
    }
}

static void WriteAtomic(string path, byte[] bytes, bool force = false)
{
    if (!force && File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
    var temporary = path + "." + Environment.ProcessId + ".tmp";
    try
    {
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
}

sealed record GenerationState(string InputHash, SortedDictionary<string, string> Outputs);

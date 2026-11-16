// 旧 CSV 字段兼容来源：MinamiChiwa/MabiAgent；MIT，Copyright (c) 2026 MinamiChiwa（南千和）。
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.Inventory.Domain;

namespace MabiTool.Modules.Inventory.Adapters;

public sealed partial class InventoryHistoryStore
{
    private sealed record ImportPlan(InventoryImportPreview Preview, string Hash, IReadOnlyDictionary<string, InventorySavedAccount> Accounts);
    private readonly Dictionary<string, ImportPlan> imports = new(StringComparer.Ordinal);
    public InventoryImportPreview GetPreview(string id)
    {
        lock(imports) { if(imports.TryGetValue(id,out var value) && clock.GetUtcNow()-value.Preview.CreatedAt<=TimeSpan.FromMinutes(15)) return value.Preview; }
        throw new OperationException("inventory.planStale","导入预览已过期或不存在");
    }
    public InventoryImportPreview PreviewImport(IReadOnlyList<InventoryImportFile> files, string? accountId = null)
    {
        if (files is null || files.Any(f=>f is null || string.IsNullOrWhiteSpace(f.Name) || f.Content is null) || files.Count is < 1 or > 32 || files.Sum(f => (long)Encoding.UTF8.GetByteCount(f.Content)) > 20 * 1024 * 1024 ||
            files.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != files.Count)
            throw new OperationException("request.invalid", "导入文件数量、重复名称或大小超出预算");
        var parsed = new Dictionary<string, InventorySavedAccount>(StringComparer.Ordinal);
        foreach (var file in files.Where(f => Path.GetFileName(f.Name) == "accounts.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(file.Content.TrimStart('\uFEFF'));
                if(document.RootElement.ValueKind != JsonValueKind.Object) throw InvalidCsv("账户索引格式无效");
                foreach(var property in document.RootElement.EnumerateObject())
                {
                    var saved=EmptyAccount(property.Name); var metadata=property.Value;
                    if(metadata.TryGetProperty("label",out var label)) saved=saved with { Label=label.GetString()??property.Name };
                    var people=new Dictionary<string,InventorySavedCharacter>(StringComparer.Ordinal);
                    if(metadata.TryGetProperty("characters",out var characters))
                        foreach(var character in characters.EnumerateObject())
                        { var name=character.Value.GetString()??character.Name;people.Add(character.Name,new(new(property.Name,character.Name,name,name),DateTimeOffset.MinValue,[],[],null)); }
                    var slots=metadata.TryGetProperty("slots",out var slotData)?slotData.Deserialize<InventoryCharacterSlot[]>(ContractJson.Options)??[]:[];
                    if(slots.Length>6 || slots.Select(x=>x.CharacterId).Distinct(StringComparer.Ordinal).Count()!=slots.Length) throw InvalidCsv("角色槽位无效");
                    var complete=metadata.TryGetProperty("slots_complete",out var completeData)&&completeData.GetBoolean();
                    if(!parsed.TryAdd(property.Name,saved with {Characters=people,Slots=slots,SlotsComplete=complete})) throw InvalidCsv("重复账户索引");
                }
            }
            catch(JsonException){throw InvalidCsv("账户索引 JSON 无效");}
        }
        foreach (var file in files.Where(f => Path.GetFileName(f.Name) == "inventory.csv"))
        {
            foreach (var row in CsvRows(file.Content))
            {
                var account = Required(row, "account_id"); var character = Value(row,"character_id");
                var container = ParseContainer(Required(row,"container"));
                if (container != InventoryContainer.AccountStorage && string.IsNullOrWhiteSpace(character)) throw InvalidCsv("角色身份缺失");
                var name = Value(row,"character"); var category = Value(row,"category"); if (category.Length == 0) category = "Consumable";
                var observedText=Value(row,"observed_at");
                var observed = observedText.Length==0?DateTimeOffset.MinValue:DateTimeOffset.TryParse(observedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : throw InvalidCsv("库存观察时间无效");
                if (!parsed.TryGetValue(account, out var saved)) saved = EmptyAccount(account);
                var characters = new Dictionary<string,InventorySavedCharacter>(saved.Characters, StringComparer.Ordinal);
                InventorySavedCharacter? person = null;
                if (container != InventoryContainer.AccountStorage)
                {
                    if (!characters.TryGetValue(character, out person)) person = new(new(account, character, name.Length == 0 ? character : name, name.Length == 0 ? character : name), observed, [], [], null);
                    else if (name.Length > 0 && person.Identity.RawName != name) throw InvalidCsv("同一角色 ID 对应多个名称");
                }
                var itemId = Value(row,"item_id");
                if (itemId.Length == 0)
                {
                    if (Value(row,"source") != "full_coverage") throw InvalidCsv("空道具 ID 缺少覆盖标记");
                    var coverage = new InventoryCoverage(category,container,true);
                    if (person is not null) person = person with { Coverage = person.Coverage.Append(coverage).Distinct().ToArray() };
                    else saved = saved with { SharedCoverage = saved.SharedCoverage.Append(coverage).Distinct().ToArray() };
                }
                else
                {
                    var type = Number(itemId); var quantityText = Value(row,"quantity"); UInt64String? quantity = quantityText.Length == 0 ? null : Number(quantityText, true);
                    var instanceText = Value(row,"instance_id"); UInt64String? instance = instanceText.Length == 0 ? null : Number(instanceText);
                    var key = Value(row,"key"); if (key.Length == 0) key = instanceText.Length == 0 ? itemId : instanceText;
                    var attributesText = Value(row,"attributes"); JsonElement attributes;
                    try { attributes = JsonSerializer.Deserialize<JsonElement>(attributesText.Length == 0 ? "{}" : attributesText); }
                    catch (JsonException) { throw InvalidCsv("实例属性 JSON 无效"); }
                    if(attributesText.Length==0)
                    {
                        var provenance=new[] {"source","evidence","frame_sha256","revision","full_sync_revision","verified_through","full_sync_observed_at","capture_source"}.Where(field=>Value(row,field).Length>0).ToDictionary(field=>field,field=>Value(row,field),StringComparer.Ordinal);
                        attributes=JsonSerializer.SerializeToElement(new { legacyCsv=provenance },ContractJson.Options);
                    }
                    if (attributes.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(attributes.GetRawText()) > 65536) throw InvalidCsv("实例属性无效或超出预算");
                    var item = new InventoryItem(category,container,key,type,instance,quantity,attributes,Value(row,"sheet") is {Length:>0} sheet ? sheet : category,Value(row,"equipped") == "true");
                    var existing = person?.Items ?? saved.SharedItems;
                    if (existing.Any(i => ItemKey(i) == ItemKey(item))) throw InvalidCsv("CSV 包含重复实例");
                    if (person is not null) person = person with { Items = person.Items.Append(item).ToArray(), LastObservedAt = observed > person.LastObservedAt ? observed : person.LastObservedAt };
                    else saved = saved with { SharedItems = saved.SharedItems.Append(item).ToArray(), SharedObservedAt = observed };
                }
                if (person is not null) characters[character] = person;
                parsed[account] = saved with { Characters = characters };
            }
        }
        foreach (var file in files.Where(f => Path.GetFileName(f.Name) == "associations.csv"))
        {
            foreach (var row in CsvRows(file.Content))
            {
                var account = Value(row,"account_id");
                if (account.Length == 0) account = accountId ?? AccountFromFolder(file.Name, parsed.Keys) ?? (parsed.Count == 1 ? parsed.Keys.Single() : throw InvalidCsv("关联文件缺少可确定的账户 ID"));
                if (!parsed.TryGetValue(account, out var saved)) saved = EmptyAccount(account);
                var category = Value(row,"category"); if (category.Length == 0) category = "Consumable";
                var type = new InventoryTypeKey(category,Number(Required(row,"item_id")),Value(row,"sheet") is {Length:>0} sheet ? sheet : category);
                var status = Value(row,"association") switch { "user_confirmed" => InventoryNameState.UserConfirmed, "builtin_verified" => InventoryNameState.VerifiedSource, "preset_candidate" => InventoryNameState.Candidate, "user_cleared" => InventoryNameState.UserCleared, _ => InventoryNameState.Unknown };
                var raw = Value(row,"name"); var name = status == InventoryNameState.UserCleared || raw.Length == 0 ? null : NormalizeName(raw);
                var associations = new Dictionary<string,InventoryNameAssociation>(saved.Associations,StringComparer.Ordinal);
                if (!associations.TryAdd(TypeKey(type),new(type,name,name,status,Value(row,"source"),Value(row,"evidence")))) throw InvalidCsv("CSV 包含重复关联");
                parsed[account] = saved with { Associations = associations };
            }
        }
        if (parsed.Count == 0 || parsed.Count > 128 || parsed.Values.Sum(a => a.Characters.Values.Sum(c => c.Items.Count) + a.SharedItems.Count + a.Associations.Count) > 100000)
            throw InvalidCsv("没有可导入数据或条目超过预算");
        if (files.Any(f => Path.GetFileName(f.Name) is not ("inventory.csv" or "associations.csv" or "accounts.json"))) throw InvalidCsv("导入文件类型不支持");
        var snapshot = state;
        var issues = parsed.Values.Where(a => snapshot.Accounts.ContainsKey(a.AccountId) || a.Characters.Values.GroupBy(c=>c.Identity.RawName,StringComparer.Ordinal).Any(g=>g.Count()>1) || a.Associations.Values.Where(n=>n.DisplayName is not null).GroupBy(n=>n.DisplayName,StringComparer.Ordinal).Any(g=>g.Count()>1)).Select(a => new InventoryImportIssue("account:"+a.AccountId,"account-conflict",a.AccountId,null,null,
            "账户 "+a.AccountId+"："+(snapshot.Accounts.ContainsKey(a.AccountId)?"已有历史；":"")+
            string.Join("；",a.Characters.Values.GroupBy(c=>c.Identity.RawName,StringComparer.Ordinal).Where(g=>g.Count()>1).Select(g=>"同名角色 "+g.Key+" 的 ID："+string.Join(",",g.Select(c=>c.Identity.CharacterId))))+
            string.Join("；",a.Associations.Values.Where(n=>n.DisplayName is not null).GroupBy(n=>n.DisplayName,StringComparer.Ordinal).Where(g=>g.Count()>1).Select(g=>"同名道具 "+g.Key+" 的类型："+string.Join(",",g.Select(n=>n.Type.Category+":"+n.Type.TypeId))))+
            "。replace 导入这些独立身份；keep-existing 保留已有账户；skip 跳过该账户")).ToArray();
        var preview = new InventoryImportPreview(Guid.NewGuid().ToString("N"), snapshot.Revision, clock.GetUtcNow(),parsed.Values.Select(a => new InventoryImportAccount(a.AccountId,a.Label,a.Characters.Count,a.Characters.Values.Sum(c=>c.Items.Count)+a.SharedItems.Count,a.Associations.Count)).ToArray(),issues,files.Count,parsed.Values.Sum(a=>a.Characters.Values.Sum(c=>c.Items.Count)+a.SharedItems.Count),parsed.Values.Sum(a=>a.Associations.Count));
        lock(imports)
        {
            foreach(var key in imports.Where(p=>preview.CreatedAt-p.Value.Preview.CreatedAt>TimeSpan.FromMinutes(15)).Select(p=>p.Key).ToArray()) imports.Remove(key);
            if(imports.Count>=16) throw new OperationException("resource.capacityExceeded","导入预览达到 16 个上限");
            imports.Add(preview.Id,new(preview,Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(files,ContractJson.Options))),parsed));
        }
        return preview;
    }
    public async Task<InventoryImportPreview> CommitImportAsync(string id, InventoryImportCommitRequest request, UInt64String expectedRevision, CancellationToken ct = default)
    {
        var requestHash=Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request,ContractJson.Options)));
        await mutation.WaitAsync(ct);
        try
        {
            if(database.ReadScalar("SELECT document FROM inventory_import_receipts WHERE id=$id",("$id",id)) is string receipt)
            {
                if(database.ReadScalar("SELECT hash FROM inventory_import_receipts WHERE id=$id",("$id",id)) as string != requestHash) throw new OperationException("idempotency.conflict","导入提交内容已变化");
                return JsonSerializer.Deserialize<InventoryImportPreview>(receipt,ContractJson.Options)!;
            }
            ImportPlan plan; lock(imports) { if(!imports.TryGetValue(id,out plan!)) throw new OperationException("inventory.planStale","导入预览已失效"); }
            var previous=state;
            if(expectedRevision!=previous.Revision || plan.Preview.Revision!=previous.Revision || clock.GetUtcNow()-plan.Preview.CreatedAt>TimeSpan.FromMinutes(15)) throw new OperationException("inventory.planStale","导入预览已过期或库存发生变化");
            if(request.Resolutions.Select(r=>r.IssueId).Distinct(StringComparer.Ordinal).Count()!=request.Resolutions.Count || request.Resolutions.Count!=plan.Preview.Issues.Count || request.Resolutions.Any(r=>!plan.Preview.Issues.Any(i=>i.Id==r.IssueId) || r.Choice is not ("keep-existing" or "replace" or "skip"))) throw InvalidCsv("每个导入冲突需要有效且唯一的处理选择");
            var accounts=new Dictionary<string,InventorySavedAccount>(previous.Accounts,StringComparer.Ordinal);
            foreach(var account in plan.Accounts.Values)
            {
                ct.ThrowIfCancellationRequested();
                var resolution=request.Resolutions.SingleOrDefault(r=>r.IssueId=="account:"+account.AccountId);
                if(resolution is not null && resolution.Choice!="replace") continue;
                accounts[account.AccountId]=account;
            }
            if(accounts.Count>128 || accounts.Values.Sum(a=>a.Characters.Values.Sum(c=>c.Items.Count)+a.SharedItems.Count)>100000) throw new OperationException("resource.capacityExceeded","库存历史超过预算");
            var revision=Next(previous.Revision); var result=plan.Preview with { Revision=revision };
            var next=previous with { Revision=revision, Accounts=accounts, Workspace=previous.Workspace is { } w ? w with { Revision=revision,Current=false,Quality=ObservationQuality.Stale,Reasons=["inventory.historyImported"], Items=w.Items.Select(i=>i with {Current=false,Quality=ObservationQuality.Stale}).ToArray() } : null };
            var receiptJson=JsonSerializer.Serialize(result,ContractJson.Options);
            await SaveStateAsync(previous,next,new(revision,null,null,false),ct,tx=>tx.Execute("INSERT INTO inventory_import_receipts VALUES($id,$hash,$doc)",("$id",id),("$hash",requestHash),("$doc",receiptJson)));
            lock(imports) imports.Remove(id);
            return result;
        }
        finally { mutation.Release(); }
    }
    public IReadOnlyList<InventoryExportFile> Export(InventoryExportRequest request)
    {
        var snapshot=state; var accounts=snapshot.Accounts.Values.Where(a=>request.AccountId is null || request.AccountId==a.AccountId).ToArray();
        if(request.AccountId is not null && accounts.Length==0) throw new OperationException("resource.notFound","库存账户不存在");
        List<string[]> inventory = [["account_id","character_id","character","container","item_id","name","quantity","observed_at","source","category","sheet","instance_id","key","attributes","equipped"]];
        List<string[]> associations = [["account_id","item_id","name","association","source","category","sheet","evidence"]];
        foreach(var account in accounts)
        {
            foreach(var character in account.Characters.Values)
                AddExportRows(inventory,account,character.Identity,character.Items,character.Coverage,character.LastObservedAt,request.IncludeHistory,snapshot);
            AddExportRows(inventory,account,null,account.SharedItems,account.SharedCoverage,account.SharedObservedAt??DateTimeOffset.MinValue,request.IncludeHistory,snapshot);
            foreach(var value in account.Associations.Values) associations.Add([account.AccountId,value.Type.TypeId.ToString(),value.RawName??"",value.State.KnownValue switch {InventoryNameState.UserConfirmed=>"user_confirmed",InventoryNameState.UserCleared=>"user_cleared",InventoryNameState.VerifiedSource=>"builtin_verified",InventoryNameState.Candidate=>"preset_candidate",_=>"unknown"},value.Source??"",value.Type.Category,value.Type.Sheet??"",value.Evidence??""]);
        }
        var index = accounts.ToDictionary(a=>a.AccountId,a=>new { label=a.Label, characters=a.Characters.Values.ToDictionary(c=>c.Identity.CharacterId,c=>c.Identity.RawName), slots=a.Slots, slots_complete=a.SlotsComplete });
        var result=new[] {new InventoryExportFile("inventory.csv",Csv(inventory)),new InventoryExportFile("associations.csv",Csv(associations)),new InventoryExportFile("accounts.json",JsonSerializer.Serialize(index,ContractJson.Options),"application/json")};
        if(result.Sum(f=>(long)Encoding.UTF8.GetByteCount(f.Content))>20*1024*1024) throw new OperationException("resource.capacityExceeded","导出文件超过 20 MiB");
        return result;
    }
    private void AddExportRows(List<string[]> rows, InventorySavedAccount account,InventoryIdentity? identity,IReadOnlyList<InventoryItem> items,IReadOnlyList<InventoryCoverage> coverage,DateTimeOffset at,bool history,InventorySavedState snapshot)
    {
        var current=snapshot.Workspace is {Current:true} w && w.Identity?.AccountId==account.AccountId && (identity is null || w.Identity.CharacterId==identity.CharacterId);
        if(!history && !current) return;
        foreach(var item in items) rows.Add([account.AccountId,identity?.CharacterId??"",identity?.RawName??"账号共享",ContainerText(item.Container.KnownValue!.Value),item.TypeId.ToString(),Name(item.Type,account).RawName??"",item.Quantity?.ToString()??"",at.ToString("O"),"history",item.Category,item.Sheet??"",item.InstanceId?.ToString()??"",item.Key,item.Attributes.GetRawText(),item.Equipped?"true":"false"]);
        foreach(var cover in coverage.Where(c=>c.Complete)) rows.Add([account.AccountId,identity?.CharacterId??"",identity?.RawName??"账号共享",ContainerText(cover.Container.KnownValue!.Value),"","","",at.ToString("O"),"full_coverage",cover.Category,"","","","{}","false"]);
    }
    private static string? AccountFromFolder(string name,IEnumerable<string> accounts)
    { var folder=Path.GetFileName(Path.GetDirectoryName(name)); return accounts.SingleOrDefault(a=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(a)))[..24]==folder); }
    private static InventorySavedAccount EmptyAccount(string id)=>new(id,id,new Dictionary<string,InventorySavedCharacter>(),[],[],null,[],false,new Dictionary<string,InventoryNameAssociation>());
    private static string Value(IReadOnlyDictionary<string,string> row,string key)=>row.GetValueOrDefault(key)??"";
    private static string Required(IReadOnlyDictionary<string,string> row,string key)=>Value(row,key) is {Length:>0} value?value:throw InvalidCsv("CSV 缺少 "+key);
    private static OperationException InvalidCsv(string message)=>new("request.invalid",message);
    private static UInt64String Number(string value,bool zero=false)=>ulong.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var n)&&(zero||n>0)?new(n):throw InvalidCsv("CSV 数字无效");
    private static InventoryContainer ParseContainer(string value)=>value switch {"backpack"=>InventoryContainer.Backpack,"character_storage"=>InventoryContainer.CharacterStorage,"account_storage"=>InventoryContainer.AccountStorage,_=>throw InvalidCsv("CSV 容器无效")};
    private static string ContainerText(InventoryContainer value)=>value switch {InventoryContainer.Backpack=>"backpack",InventoryContainer.CharacterStorage=>"character_storage",_=>"account_storage"};
    private static string Csv(IEnumerable<string[]> rows)=>"\uFEFF"+string.Join("\r\n",rows.Select(row=>string.Join(',',row.Select(cell=>"\""+cell.Replace("\"","\"\"")+"\""))))+"\r\n";
    private static IReadOnlyList<IReadOnlyDictionary<string,string>> CsvRows(string content)
    {
        var rows=new List<string[]>();var row=new List<string>();var cell=new StringBuilder();bool quoted=false,closed=false;
        var text=content.TrimStart('\uFEFF');
        for(var i=0;i<text.Length;i++)
        {
            var c=text[i];
            if(quoted) { if(c=='"') {if(i+1<text.Length&&text[i+1]=='"'){cell.Append('"');i++;}else{quoted=false;closed=true;}}else cell.Append(c); }
            else if(c=='"') {if(cell.Length>0||closed)throw InvalidCsv("CSV 引号位置无效");quoted=true;}
            else if(c==','||c=='\r'||c=='\n') {row.Add(cell.ToString());cell.Clear();closed=false;if(c!=','){if(c=='\r'&&i+1<text.Length&&text[i+1]=='\n')i++;rows.Add(row.ToArray());row.Clear();if(rows.Count>100001)throw InvalidCsv("CSV 行数超过预算");}}
            else {if(closed)throw InvalidCsv("CSV 引号后有额外字符");cell.Append(c);}
        }
        if(quoted)throw InvalidCsv("CSV 引号截断");if(row.Count>0||cell.Length>0||closed){row.Add(cell.ToString());rows.Add(row.ToArray());}
        if(rows.Count==0)throw InvalidCsv("CSV 缺少表头");var header=rows[0];if(header.Any(string.IsNullOrWhiteSpace)||header.Distinct(StringComparer.Ordinal).Count()!=header.Length)throw InvalidCsv("CSV 表头为空或重复");
        return rows.Skip(1).Where(r=>r.Any(c=>c.Length>0)).Select(r=>r.Length==header.Length?(IReadOnlyDictionary<string,string>)header.Zip(r).ToDictionary(p=>p.First,p=>p.Second,StringComparer.Ordinal):throw InvalidCsv("CSV 列数不匹配")).ToArray();
    }
}

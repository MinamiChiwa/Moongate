// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Files;
using MabiTool.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class InventoryApiTests
{
    private const string Token="inventory_tests_012345678901234567890123456789012345678901";
    [Fact] public async Task AuthenticationAndControlPermissionsProtectInventoryRoutes()
    {
        using var area=new Workspace();await using var app=Build(area);await app.StartAsync();using var owner=Sdk(app);using var anonymous=Client(app,null);
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync(InventoryApi.Snapshot.Path)).StatusCode);
        var challenge=await owner.CreatePairingChallengeAsync(new("inventory-reader",null,[ApiPermissions.SystemRead]));var grant=await owner.RedeemPairingAsync(new(challenge.ChallengeId,challenge.Code));using var reader=Client(app,grant.Token);
        Assert.Equal(HttpStatusCode.OK,(await reader.GetAsync(InventoryApi.Capabilities.Path)).StatusCode);
        var binding=(await owner.GetGameBindingAsync()).Binding!;
        using var denied=await Send(reader,InventoryApi.StartObservation.Path,new InventoryObservationRequest(binding.Session.Id),"inventory-reader-denied");Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        using var deniedEdit=await Send(reader,InventoryApi.EditAssociations.Path,new InventoryAssociationEditRequest("account-replay",[]),"inventory-reader-edit","\"inventory-fake-0\"");Assert.Equal(HttpStatusCode.Forbidden,deniedEdit.StatusCode);
        await app.StopAsync();
    }
    [Fact] public async Task MockObservationSharesAuthorityAndStopPreservesOnlyHistoricalItems()
    {
        using var area=new Workspace();await using var app=Build(area);await app.StartAsync();using var first=Client(app);using var second=Client(app);using var sdk=Sdk(app);
        var before=await Get<InventorySnapshot>(first,InventoryApi.Snapshot.Path);Assert.Null(before.Data);
        var game=(await sdk.GetGameBindingAsync()).Binding!.Session.Id;var request=new InventoryObservationRequest(game,ReplayIntervalMs:1000);
        var operation=await Post<OperationSnapshot>(first,InventoryApi.StartObservation.Path,request,"inventory-shared-observe");
        Assert.Equal(operation.Id,(await Post<OperationSnapshot>(second,InventoryApi.StartObservation.Path,request,"inventory-shared-observe")).Id);
        var active=await Ready(first);var other=await Get<InventorySnapshot>(second,InventoryApi.Snapshot.Path);Assert.Equal(Json(active.Data),Json(other.Data));Assert.Equal(active.ResourceRevision,other.ResourceRevision);
        Assert.Contains(active.Data!.Items,i=>i.Item.Category=="Equipment");Assert.Contains(active.Data.Items,i=>i.Item.Category=="EquipmentTool");Assert.Equal("mabiagent-inventory-replay",active.Data.Source!.Provider);
        using var different=await Send(second,InventoryApi.StartObservation.Path,request with {ReplayIntervalMs=900},"inventory-shared-observe");Assert.Equal(HttpStatusCode.Conflict,different.StatusCode);
        var stopped=await Post<OperationSnapshot>(first,InventoryApi.StopObservation.Path.Replace("{id}",operation.Id),new{});Assert.True(SqliteStore.IsTerminal(stopped.State.KnownValue));
        var final=await Get<InventorySnapshot>(second,InventoryApi.Snapshot.Path);Assert.False(final.Data!.Current);Assert.Empty((await Post<InventoryItemPage>(first,InventoryApi.Items.Path,new InventoryItemQuery(CurrentOnly:true))).Items);
        var historical=await Post<InventoryItemPage>(second,InventoryApi.Items.Path,new InventoryItemQuery());Assert.NotEmpty(historical.Items);Assert.All(historical.Items,i=>{Assert.False(i.Current);Assert.NotNull(i.Source);});
        Assert.Equal(stopped.Id,(await Post<OperationSnapshot>(second,InventoryApi.StopObservation.Path.Replace("{id}",operation.Id),new{})).Id);
        await app.StopAsync();
    }
    [Fact] public async Task PaginationEditsAndSlotsUseRevisionsAndDurableIdempotentResponses()
    {
        using var area=new Workspace();string condition;InventoryAssociationEditRequest request;InventoryAssociationSnapshot saved;
        await using(var app=Build(area))
        {
            await app.StartAsync();using var http=Client(app);using var sdk=Sdk(app);await Seed(http,sdk);
            var first=await Post<InventoryItemPage>(http,InventoryApi.Items.Path,new InventoryItemQuery(PageSize:1));Assert.Single(first.Items);Assert.NotNull(first.NextCursor);
            var second=await Post<InventoryItemPage>(http,InventoryApi.Items.Path,new InventoryItemQuery(PageSize:1,Cursor:first.NextCursor));Assert.Single(second.Items);Assert.NotEqual(first.Items[0].Item.Category,second.Items[0].Item.Category);
            using var mismatched=await Send(http,InventoryApi.Items.Path,new InventoryItemQuery(PageSize:1,Search:"other",Cursor:first.NextCursor));Assert.Equal(HttpStatusCode.Conflict,mismatched.StatusCode);
            var snapshot=await Get<InventorySnapshot>(http,InventoryApi.Snapshot.Path);var identity=snapshot.Data!.Identity!;var associationsPath=InventoryApi.Associations.Path.Replace("{id}",identity.AccountId);
            condition=await ETag(http,associationsPath);request=new(identity.AccountId,[new(first.Items[0].Item.Type,"我的名称")]);
            saved=await Post<InventoryAssociationSnapshot>(http,InventoryApi.EditAssociations.Path,request,"inventory-batch-edit",condition);
            Assert.Equal(Json(saved),Json(await Post<InventoryAssociationSnapshot>(http,InventoryApi.EditAssociations.Path,request,"inventory-batch-edit",condition)));
            using var stale=await Send(http,InventoryApi.EditAssociations.Path,request,"inventory-other-frontend",condition);Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
            using var oldCursor=await Send(http,InventoryApi.Items.Path,new InventoryItemQuery(PageSize:1,Cursor:first.NextCursor));Assert.Equal(HttpStatusCode.Conflict,oldCursor.StatusCode);
            var slotCondition=await ETag(http,InventoryApi.Snapshot.Path);var slots=new InventoryCharacterSlotsRequest(identity.AccountId,[new(identity.CharacterId,identity.RawName,identity.DisplayName)],true);
            var changed=await Post<InventoryAccountView>(http,InventoryApi.SetCharacterSlots.Path,slots,"inventory-slots-edit",slotCondition);Assert.True(changed.SlotsComplete);
            Assert.Equal(changed.Revision,(await Get<InventorySnapshot>(http,InventoryApi.Snapshot.Path)).Data!.Revision);
            await app.StopAsync();
        }
        await using var restart=Build(area);await restart.StartAsync();using var next=Client(restart);
        Assert.Equal(Json(saved),Json(await Post<InventoryAssociationSnapshot>(next,InventoryApi.EditAssociations.Path,request,"inventory-batch-edit",condition)));
        Assert.False((await Get<InventorySnapshot>(next,InventoryApi.Snapshot.Path)).Data!.Current);await restart.StopAsync();
    }
    [Fact] public async Task AuthorizedCatalogImportUpdatesAutomaticNamesAndRetainsUserClear()
    {
        using var area=new Workspace();await using var app=Build(area);await app.StartAsync();using var http=Client(app);using var sdk=Sdk(app);await Seed(http,sdk);
        var items=(await Post<InventoryItemPage>(http,InventoryApi.Items.Path,new InventoryItemQuery())).Items;
        var catalog=new InventoryCatalogData("synthetic-catalog","TW",new string('a',64),DateTimeOffset.UtcNow,[],items.Select((item,index)=>new InventoryCatalogEntry(item.Item.Type,"目录名称"+index,"目录名称"+index,item.Item.Category,new((ulong)index),new(0),new(0),new(0),item.Item.Category=="EquipmentTool")).ToArray(),[],[]);
        var file=Path.Combine(area.Root,"catalog.json");var original=Encoding.UTF8.GetBytes(Json(catalog));await File.WriteAllBytesAsync(file,original);
        var selection=await Selected(app,file);var condition=await ETag(http,InventoryApi.Catalog.Path);
        var operation=await Post<OperationSnapshot>(http,InventoryApi.ImportCatalog.Path,new InventoryCatalogImportRequest(selection),"inventory-catalog-import",condition);Assert.Equal(OperationState.Succeeded,(await Complete(http,operation.Id)).State.KnownValue);
        Assert.Equal(original,await File.ReadAllBytesAsync(file));var linked=(await Post<InventoryItemPage>(http,InventoryApi.Items.Path,new InventoryItemQuery())).Items;Assert.All(linked,item=>Assert.Equal(InventoryNameState.ClientCatalog,item.Name.State.KnownValue));
        var snapshot=await Get<InventorySnapshot>(http,InventoryApi.Snapshot.Path);var edit=new InventoryAssociationEditRequest(snapshot.Data!.Identity!.AccountId,[new(linked[0].Item.Type,null,true)]);
        await Post<InventoryAssociationSnapshot>(http,InventoryApi.EditAssociations.Path,edit,"inventory-catalog-clear",await ETag(http,InventoryApi.Snapshot.Path));
        var page=await Post<InventoryCatalogPage>(http,InventoryApi.QueryCatalog.Path,new InventoryCatalogQuery(PageSize:1));Assert.Single(page.Items);Assert.NotNull(page.NextCursor);
        var after=(await Post<InventoryItemPage>(http,InventoryApi.Items.Path,new InventoryItemQuery())).Items;Assert.Null(after.Single(i=>i.Item.Type==linked[0].Item.Type).Name.DisplayName);
        await app.StopAsync();await app.DisposeAsync();
        await using var restarted=Build(area);await restarted.StartAsync();using var next=Client(restarted);
        var retried=await Post<OperationSnapshot>(next,InventoryApi.ImportCatalog.Path,new InventoryCatalogImportRequest(selection),"inventory-catalog-import",condition);Assert.Equal(operation.Id,retried.Id);Assert.Equal(OperationState.Succeeded,retried.State.KnownValue);
        using var changedCondition=await Send(next,InventoryApi.ImportCatalog.Path,new InventoryCatalogImportRequest(selection),"inventory-catalog-import",await ETag(next,InventoryApi.Catalog.Path));Assert.Equal(HttpStatusCode.Conflict,changedCondition.StatusCode);
        Assert.Equal(catalog.Entries.Count,(await Get<InventoryCatalogSummary>(next,InventoryApi.Catalog.Path)).Entries);await restarted.StopAsync();
    }
    [Fact] public async Task CsvSelectionPreviewCommitAndExportPreserveSourceAndPublishAuthorizedAsset()
    {
        using var area=new Workspace();await using var app=Build(area);await app.StartAsync();using var http=Client(app);using var sdk=Sdk(app);
        var file=Path.Combine(area.Root,"inventory.csv");var bytes=Encoding.UTF8.GetBytes("account_id,character_id,character,container,item_id,quantity,observed_at,source\r\nlegacy-account,legacy-character,原角色,backpack,123,7,2026-01-01T00:00:00Z,full\r\n");await File.WriteAllBytesAsync(file,bytes);
        var selected=await Selected(app,file);var preview=await Post<InventoryImportPreview>(http,InventoryApi.PreviewImport.Path,new InventoryImportRequest(selected),"inventory-csv-preview");Assert.Equal(1,preview.Items);Assert.Empty(preview.Issues);
        Assert.Equal(preview.Id,(await Get<InventoryImportPreview>(http,InventoryApi.ImportPreview.Path.Replace("{id}",preview.Id))).Id);
        var commitCondition=await ETag(http,InventoryApi.ImportPreview.Path.Replace("{id}",preview.Id));
        var commit=await Post<OperationSnapshot>(http,InventoryApi.CommitImport.Path.Replace("{id}",preview.Id),new InventoryImportCommitRequest([]),"inventory-csv-commit",commitCondition);
        Assert.Equal(OperationState.Succeeded,(await Complete(http,commit.Id)).State.KnownValue);Assert.Equal(bytes,await File.ReadAllBytesAsync(file));Assert.Null((await Get<InventorySnapshot>(http,InventoryApi.Snapshot.Path)).Data);
        var imported=await Post<InventoryItemPage>(http,InventoryApi.Items.Path,new InventoryItemQuery(AccountId:"legacy-account"));Assert.Equal(7UL,Assert.Single(imported.Items).Item.Quantity!.Value.Value);Assert.False(imported.Items[0].Current);
        var exported=await Post<OperationSnapshot>(http,InventoryApi.Export.Path,new InventoryExportRequest("legacy-account"),"inventory-csv-export");var final=await Complete(http,exported.Id);Assert.Equal(OperationState.Succeeded,final.State.KnownValue);
        var artifact=final.Result!.Value.Deserialize<FileExportArtifact>(ContractJson.Options)!;Assert.Equal("inventory-export.zip",artifact.Name);Assert.Contains("inventory",artifact.ContentCategories);
        using var content=new MemoryStream();await using var downloaded=await sdk.DownloadAssetAsync(artifact.Asset.Id);await downloaded.Content.CopyToAsync(content);content.Position=0;using var zip=new ZipArchive(content);Assert.NotNull(zip.GetEntry("inventory.csv"));Assert.NotNull(zip.GetEntry("associations.csv"));Assert.NotNull(zip.GetEntry("accounts.json"));
        await app.StopAsync();await app.DisposeAsync();
        await using var restarted=Build(area);await restarted.StartAsync();using var next=Client(restarted);
        var retried=await Post<OperationSnapshot>(next,InventoryApi.CommitImport.Path.Replace("{id}",preview.Id),new InventoryImportCommitRequest([]),"inventory-csv-commit",commitCondition);Assert.Equal(commit.Id,retried.Id);Assert.Equal(OperationState.Succeeded,retried.State.KnownValue);
        using var changedCondition=await Send(next,InventoryApi.CommitImport.Path.Replace("{id}",preview.Id),new InventoryImportCommitRequest([]),"inventory-csv-commit",await ETag(next,InventoryApi.Snapshot.Path));Assert.Equal(HttpStatusCode.Conflict,changedCondition.StatusCode);
        Assert.Equal(7UL,Assert.Single((await Post<InventoryItemPage>(next,InventoryApi.Items.Path,new InventoryItemQuery(AccountId:"legacy-account"))).Items).Item.Quantity!.Value.Value);await restarted.StopAsync();
    }
    [Fact] public async Task TwoEventReadersResumeTheSameInventorySnapshotEvent()
    {
        using var area=new Workspace();await using var app=Build(area);await app.StartAsync();using var http=Client(app);using var first=Sdk(app);using var second=Sdk(app);
        var before=await Get<InventorySnapshot>(http,InventoryApi.Snapshot.Path);await Seed(http,first);
        var filter=new EventStreamOptions(["inventory.snapshot.changed","inventory.catalog.changed","inventory.observation.degraded"],[],before.EventCursor);Assert.Equal(await FirstSnapshotEvent(first,filter),await FirstSnapshotEvent(second,filter));
        var restored=await first.GetEventSnapshotAsync(new(["inventory.snapshot.changed"],[],null));Assert.NotEmpty(restored.Resources);
        var a=await Get<InventorySnapshot>(http,InventoryApi.Snapshot.Path);var b=await Get<InventorySnapshot>(http,InventoryApi.Snapshot.Path);Assert.Equal(a.EventCursor,b.EventCursor);Assert.Equal(Json(a.Data),Json(b.Data));await app.StopAsync();
    }
    [Trait("Category", "Slow")]
    [InstalledTwCatalogFact]
    public async Task FullActualCatalogAndLargeCsvConflictsCommitWithBoundedOperationEvents()
    {
        var catalog=MabiTool.Providers.TwObservation.ItemCatalog.ClientItemCatalogExtractor.Extract(InstalledTwCatalogFactAttribute.ClientDirectory);
        Assert.Equal(12486,catalog.Entries.Count);Assert.Equal(192,catalog.Entries.Count(e=>e.IsInstrument==true));
        using var area=new Workspace();await using var app=Build(area);await app.StartAsync();using var http=Client(app);using var sdk=Sdk(app);
        var before=await Get<InventorySnapshot>(http,InventoryApi.Snapshot.Path);
        var catalogPath=Path.Combine(area.Root,"full-catalog.json");var original=Encoding.UTF8.GetBytes(Json(catalog));Assert.True(original.Length>65536);await File.WriteAllBytesAsync(catalogPath,original);
        var selection=await Selected(app,catalogPath);var imported=await Post<OperationSnapshot>(http,InventoryApi.ImportCatalog.Path,new InventoryCatalogImportRequest(selection),"inventory-full-catalog-import",await ETag(http,InventoryApi.Catalog.Path));var directoryResult=await Complete(http,imported.Id);
        Assert.Equal(OperationState.Succeeded,directoryResult.State.KnownValue);Assert.True(Encoding.UTF8.GetByteCount(directoryResult.Result!.Value.GetRawText())<65536);Assert.Equal(12486,directoryResult.Result.Value.Deserialize<InventoryCatalogSummary>(ContractJson.Options)!.Entries);Assert.Equal(original,await File.ReadAllBytesAsync(catalogPath));
        const int count=5000;var inventory=new StringBuilder("account_id,character_id,character,container,item_id,quantity,observed_at,source\r\n");var associations=new StringBuilder("item_id,name,association,source\r\n");
        for(var n=0;n<count;n++) {var id=n+10000;inventory.Append($"conflict-account,conflict-character,历史角色,backpack,{id},1,2026-01-01T00:00:00Z,full\r\n");associations.Append($"{id},共用名称,user_confirmed,user\r\n");}
        var archivePath=Path.Combine(area.Root,"conflicts.zip");
        using(var archiveFile=File.Create(archivePath))using(var archive=new ZipArchive(archiveFile,ZipArchiveMode.Create))
        {
            foreach(var file in new[]{new InventoryExportFile("inventory.csv",inventory.ToString()),new InventoryExportFile("associations.csv",associations.ToString())})
            {using var stream=archive.CreateEntry(file.Name).Open();using var writer=new StreamWriter(stream,new UTF8Encoding(false));writer.Write(file.Content);}
        }
        var csvSelection=await Selected(app,archivePath);var preview=await Post<InventoryImportPreview>(http,InventoryApi.PreviewImport.Path,new InventoryImportRequest(csvSelection),"inventory-large-conflict-preview");
        Assert.Equal(count,preview.Items);Assert.Equal(count,preview.Associations);Assert.True(Encoding.UTF8.GetByteCount(Json(preview))>65536);Assert.NotEmpty(preview.Issues);
        var commit=await Post<OperationSnapshot>(http,InventoryApi.CommitImport.Path.Replace("{id}",preview.Id),new InventoryImportCommitRequest(preview.Issues.Select(i=>new InventoryImportResolution(i.Id,"replace")).ToArray()),"inventory-large-conflict-commit",await ETag(http,InventoryApi.ImportPreview.Path.Replace("{id}",preview.Id)));
        var final=await Complete(http,commit.Id);Assert.Equal(OperationState.Succeeded,final.State.KnownValue);Assert.True(Encoding.UTF8.GetByteCount(final.Result!.Value.GetRawText())<65536);var receipt=final.Result.Value.Deserialize<InventoryImportReceipt>(ContractJson.Options)!;Assert.Equal(count,receipt.Items);Assert.Equal(count,receipt.Associations);
        var eventRecord=await FirstSnapshotEvent(sdk,new(["inventory.snapshot.changed","inventory.catalog.changed","inventory.observation.degraded"],[],before.EventCursor));Assert.True(Encoding.UTF8.GetByteCount(eventRecord)<65536);
        Assert.Equal(12486,(await Get<InventoryCatalogSummary>(http,InventoryApi.Catalog.Path)).Entries);await app.StopAsync();
    }
    private static async Task<string> FirstSnapshotEvent(MabiToolClient sdk,EventStreamOptions options)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));await foreach(var message in sdk.StreamEventsAsync(options,timeout.Token)){if(message.Event=="inventory.snapshot.changed")return message.Id+":"+message.Data.GetRawText();}throw new InvalidOperationException("缺少库存事件");
    }
    private static async Task Seed(HttpClient http,MabiToolClient sdk)
    {var operation=await Post<OperationSnapshot>(http,InventoryApi.StartObservation.Path,new InventoryObservationRequest((await sdk.GetGameBindingAsync()).Binding!.Session.Id,ReplayIntervalMs:1000),"inventory-seed-observe");await Ready(http);await Post<OperationSnapshot>(http,InventoryApi.StopObservation.Path.Replace("{id}",operation.Id),new{});}
    private static async Task<InventorySnapshot> Ready(HttpClient http)
    {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(true){var value=await http.GetFromJsonAsync<InventorySnapshot>(InventoryApi.Snapshot.Path,ContractJson.Options,timeout.Token);if(value?.Data is {Current:true,Items.Count:>0})return value;await Task.Delay(20,timeout.Token);}}
    private static async Task<OperationSnapshot> Complete(HttpClient http,string id)
    {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(true){var value=await http.GetFromJsonAsync<OperationSnapshot>("/api/v1/operations/"+id,ContractJson.Options,timeout.Token);if(SqliteStore.IsTerminal(value!.State.KnownValue))return value;await Task.Delay(20,timeout.Token);}}
    private static async Task<string> Selected(WebApplication app,string path)=> (await app.Services.GetRequiredService<FileSelectionRegistry>().RegisterReadAsync(app.Services.GetRequiredService<LocalAuthService>().OwnerGrant().Session,path)).Id;
    private static async Task<string> ETag(HttpClient http,string path){using var response=await http.GetAsync(path);response.EnsureSuccessStatusCode();return response.Headers.ETag!.ToString();}
    private static async Task<T> Get<T>(HttpClient http,string path)=>(await http.GetFromJsonAsync<T>(path,ContractJson.Options))!;
    private static async Task<T> Post<T>(HttpClient http,string path,object body,string? key=null,string? condition=null){using var response=await Send(http,path,body,key,condition);var text=await response.Content.ReadAsStringAsync();Assert.True(response.IsSuccessStatusCode,$"{path}: {(int)response.StatusCode} {text}");return JsonSerializer.Deserialize<T>(text,ContractJson.Options)!;}
    private static async Task<HttpResponseMessage> Send(HttpClient http,string path,object body,string? key=null,string? condition=null){using var request=new HttpRequestMessage(HttpMethod.Post,path){Content=JsonContent.Create(body,options:ContractJson.Options)};if(key is not null)request.Headers.Add("Idempotency-Key",key);if(condition is not null)request.Headers.TryAddWithoutValidation("If-Match",condition);return await http.SendAsync(request);}
    private static string Json<T>(T value)=>JsonSerializer.Serialize(value,ContractJson.Options);
    private static WebApplication Build(Workspace area)=>BackendApplication.Build(new(0,Token,new HashSet<string>()),storage:area.Options,mock:true);
    private static Uri Address(WebApplication app)=>new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    private static HttpClient Client(WebApplication app,string? token=Token){var client=new HttpClient{BaseAddress=Address(app)};if(token is not null)client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);return client;}
    private static MabiToolClient Sdk(WebApplication app)=>new(Address(app),_=>ValueTask.FromResult(Token));
    private sealed class Workspace:IDisposable
    {
        public string Root {get;}=Path.Combine(Path.GetTempPath(),"inventory-api",Guid.NewGuid().ToString("N"));
        public Workspace()=>Directory.CreateDirectory(Root);
        public StorageOptions Options=>new(Path.Combine(Root,"bootstrap"),true,PortableRoot:Root);
        public void Dispose(){if(Directory.Exists(Root))Directory.Delete(Root,true);}
    }
}


public sealed class InstalledTwCatalogFactAttribute:FactAttribute
{
    public static string ClientDirectory=>Environment.GetEnvironmentVariable("MABITOOL_TEST_TW_CLIENT")??@"C:\Nexon\Library\MabinogiMobile_TW\appdata";
    public InstalledTwCatalogFactAttribute()
    {
        if(!File.Exists(Path.Combine(ClientDirectory,"ResourcesPackage","Starter","index")))Skip="需要当前台服客户端静态资源；设置 MABITOOL_TEST_TW_CLIENT 指向 appdata";
    }
}

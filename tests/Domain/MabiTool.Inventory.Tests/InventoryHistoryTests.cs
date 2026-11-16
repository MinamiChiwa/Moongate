// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.Inventory.Adapters;
using Xunit;
namespace MabiTool.Inventory.Tests;
public sealed class InventoryHistoryTests
{
    private static InventoryItem Item(string category="Consumable", ulong id=1, ulong count=2, InventoryContainer container=InventoryContainer.Backpack,string? key=null)=>new(category,container,key??id.ToString(),new(id),null,new(count),JsonSerializer.SerializeToElement(new{}),category);
    private static readonly ObservationSource Source=new("test","offline","TW",null);
    private static InventoryFrame Frame(ulong sequence,InventoryFrameKind kind=InventoryFrameKind.Full,InventoryItem[]? items=null,InventoryDelta[]? changes=null,string account="a",string character="one")=>new(1,Source,"game",new(1),new(sequence),DateTimeOffset.UtcNow,sequence,kind,new(account,character,character,character),items??[Item()],changes??[],(items??[Item()]).Select(i=>i.Category).Append("Consumable").Distinct().SelectMany(c=>Enum.GetValues<InventoryContainer>().Select(container=>new InventoryCoverage(c,container,true))).ToArray());
    private static InventoryResourceView Item(InventoryResourcesView view,string key,string? character=null)=>view.Items.Single(item=>item.Key==key && item.CharacterId==character && item.Reason!="inventory.resourceOwnerUnknown");
    /// <summary>补充上限随完整同步给出：持有量达到或超过上限时不再给出下次补充时刻（超过上限的数值照实返回），低于上限时恢复；增量帧保留上限，下一次完整同步未能判定时清空。</summary>
    [Fact] public async Task RefillStopsAtOrAboveTheCapFromTheLatestFullSync()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");
        var refilled=new DateTimeOffset(2026,11,14,18,31,1,TimeSpan.Zero);
        InventoryResourceChange Silver(ulong? value=null,int? cap=null,DateTimeOffset? at=null)=>new("silverCoin","SilverCoin",value is { } v?new(v):null,RefilledAt:at,RefillIntervalSeconds:1800,RefillCap:cap);
        InventoryResourceView View()=>Item(store.Resources(),"silverCoin","one");
        await store.ApplyFrameAsync(Frame(1) with {Resources=[Silver(100,100,refilled)]});
        Assert.Equal((100,(DateTimeOffset?)null),(View().RefillCap,View().NextRefillAt));
        await store.ApplyFrameAsync(Frame(2,InventoryFrameKind.Delta,[],[]) with {Resources=[Silver(105)]});
        Assert.Equal((105UL,100,(DateTimeOffset?)null),(View().Value!.Value.Value,View().RefillCap,View().NextRefillAt));
        await store.ApplyFrameAsync(Frame(3,InventoryFrameKind.Delta,[],[]) with {Resources=[Silver(99)]});
        Assert.Equal((100,(DateTimeOffset?)refilled.AddSeconds(1800)),(View().RefillCap,View().NextRefillAt));
        await store.ApplyFrameAsync(Frame(4) with {Resources=[Silver(120,null,refilled)]});
        Assert.Equal(((int?)null,(DateTimeOffset?)refilled.AddSeconds(1800)),(View().RefillCap,View().NextRefillAt));
    }
    [Fact] public async Task AccountScopedResourcesAreSharedAndCharacterScopedResourcesStayPerCharacter()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");
        var refilled=new DateTimeOffset(2026,11,15,8,12,49,TimeSpan.Zero);
        InventoryFrame Resources(ulong sequence,string character,params InventoryResourceChange[] changes)=>Frame(sequence,InventoryFrameKind.Delta,[],[],character:character) with {Resources=changes};
        // 完整同步只带名称与说明；此时全部资源仍是未读取，账户级与角色级各按范围列出。
        await store.ApplyFrameAsync(Frame(1) with {Resources=[new("silverCoin","SilverCoin",RawName:"銀幣",RefillIntervalSeconds:1800)]});
        Assert.All(store.Resources().Items,item=>{Assert.Equal("unread",item.State);Assert.Null(item.Value);});
        Assert.Equal(InventoryResourceScope.Account,Item(store.Resources(),"mCashFree").Scope);
        Assert.Equal(InventoryResourceScope.Character,Item(store.Resources(),"silverCoin","one").Scope);
        await store.ApplyFrameAsync(Resources(2,"one",new InventoryResourceChange("silverCoin","SilverCoin",new(62)),new InventoryResourceChange("mCashFree","FreeCrystal",new(300))));
        await store.ApplyFrameAsync(Resources(3,"one",new InventoryResourceChange("silverCoin","SilverCoin",Increase:new(2)),new InventoryResourceChange("silverCoin","SilverCoin",RefilledAt:refilled)));
        var silver=Item(store.Resources(),"silverCoin","one");
        Assert.Equal(("observed",62UL,2UL,"銀幣"),(silver.State,silver.Value!.Value.Value,silver.LastIncrease!.Value.Value,silver.RawName));
        Assert.Equal(refilled.AddSeconds(1800),silver.NextRefillAt);
        // 同账户另一个角色：账户级 M 币是同一份，该角色的变化也更新它；角色级银币各自一份。
        await store.ApplyFrameAsync(Frame(4,character:"two"));
        Assert.Equal(300UL,Item(store.Resources(),"mCashFree").Value!.Value.Value);
        Assert.Equal("unread",Item(store.Resources(),"silverCoin","two").State);
        Assert.Equal(62UL,Item(store.Resources(),"silverCoin","one").Value!.Value.Value);
        await store.ApplyFrameAsync(Resources(5,"two",new InventoryResourceChange("mCashFree","FreeCrystal",new(250)),new InventoryResourceChange("silverCoin","SilverCoin",new(10))));
        var view=store.Resources("a");
        Assert.Equal((250UL,"two"),(Item(view,"mCashFree").Value!.Value.Value,Item(view,"mCashFree").ObservedCharacterId));
        Assert.Equal((10UL,62UL),(Item(view,"silverCoin","two").Value!.Value.Value,Item(view,"silverCoin","one").Value!.Value.Value));
        Assert.Equal(["two","one"],view.Characters!.Select(c=>c.CharacterId));
        // 另一个账户：账户级为那个账户自己的。
        await store.ApplyFrameAsync(Frame(6,account:"b",character:"three"));
        Assert.Equal("unread",Item(store.Resources(),"mCashFree").State);
        // 工具重启：上次已知值，非当前。
        var restart=new InventoryHistoryStore(db,"next");var known=restart.Resources("a");
        Assert.False(known.Current);Assert.Equal(("observed",62UL,false),(Item(known,"silverCoin","one").State,Item(known,"silverCoin","one").Value!.Value.Value,Item(known,"silverCoin","one").Current));
    }
    [Fact] public async Task FallbackIdentityMergesByScopeAndPreviousAccountRecordsMoveBackToTheirCharacters()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");
        var t0=new DateTimeOffset(2026,11,15,8,0,0,TimeSpan.Zero);
        InventoryFrame At(InventoryFrame frame,int minutes,params InventoryResourceChange[] changes)=>frame with {ObservedAt=t0.AddMinutes(minutes),Resources=changes};
        var fallback=InventoryResources.CharacterAccountPrefix+"one";
        // 先按角色归属：金币 100（较旧）、银币 50（较新）、德卡 7。
        await store.ApplyFrameAsync(At(Frame(1,account:fallback),0));
        await store.ApplyFrameAsync(At(Frame(2,InventoryFrameKind.Delta,[],[],account:fallback),1,new InventoryResourceChange("goldFree","Gold",new(100)),new InventoryResourceChange("decaFree","RedCrystal",new(7))));
        await store.ApplyFrameAsync(At(Frame(3,InventoryFrameKind.Delta,[],[],account:fallback),9,new InventoryResourceChange("silverCoin","SilverCoin",new(50))));
        // 关联出游戏账户并收到金币 120：按范围并入，逐项取较新值，保底身份下不再留一份。
        await store.ApplyFrameAsync(At(Frame(4,account:"acc"),5,new InventoryResourceChange("goldFree","Gold",new(120))));
        var merged=store.Resources("acc");
        Assert.Equal((120UL,50UL,7UL),(Item(merged,"goldFree","one").Value!.Value.Value,Item(merged,"silverCoin","one").Value!.Value.Value,Item(merged,"decaFree").Value!.Value.Value));
        Assert.All(store.Resources(fallback).Items,item=>Assert.Equal("unread",item.State));
        // 新账户完整同步后，按角色归属的旧账户删除（资源已按较新值并入）。
        Assert.DoesNotContain(store.Accounts().Items,a=>a.AccountId==fallback);
        // 上一版把全部资源记在账户上：角色级的按“最近一次更新的角色”迁回；找不到角色的保留为账户下的历史值。
        var node=System.Text.Json.Nodes.JsonNode.Parse((string)db.ReadScalar("SELECT document FROM inventory_state WHERE id=1")!)!;
        var account=node["accounts"]!["acc"]!;var own=account["characters"]!["one"]!["resources"]!.AsObject();
        var shared=account["resources"]!.AsObject();
        foreach (var key in own.Select(p=>p.Key).ToArray()) { var value=own[key]!.DeepClone(); own.Remove(key); shared[key]=value; }
        var ghost=shared["silverCoin"]!.DeepClone();ghost["key"]="catToken";ghost["characterId"]="ghost";shared["catToken"]=ghost;
        var legacy=node.ToJsonString();
        await db.WriteAsync(tx=>{tx.Execute("UPDATE inventory_state SET document=$d WHERE id=1",("$d",legacy));return true;});
        var restart=new InventoryHistoryStore(db,"next");var moved=restart.Resources("acc");
        Assert.Equal((120UL,50UL,7UL),(Item(moved,"goldFree","one").Value!.Value.Value,Item(moved,"silverCoin","one").Value!.Value.Value,Item(moved,"decaFree").Value!.Value.Value));
        var orphan=moved.Items.Single(item=>item.Reason=="inventory.resourceOwnerUnknown");
        Assert.Equal(("catToken",(string?)null,false,InventoryResourceScope.Character),(orphan.Key,orphan.CharacterId,orphan.Current,orphan.Scope));
        // 表里查不到的货币按角色记，视图带原因。
        await restart.ApplyFrameAsync(At(Frame(5,account:"acc"),20,new InventoryResourceChange("futureCoin","FutureCoin",new(3))));
        var unknown=Item(restart.Resources("acc"),"futureCoin","one");
        Assert.Equal((InventoryResourceScope.Character,"inventory.resourceScopeUnknown",3UL),(unknown.Scope,unknown.Reason,unknown.Value!.Value.Value));
    }
    /// <summary>
    /// 账户主角色单独确定账户：同一主角色的两个角色（都没有游戏账户编号）共用一个 <c>master-</c> 账户与公共保管箱；之后收到编号时记为别名、
    /// 账户键不变，重启后只带编号的同步仍记入该账户。
    /// </summary>
    [Fact] public async Task MasterCharacterAloneDeterminesTheAccountAndKeepsTheGameAccountAsAlias()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");
        InventoryFrame With(InventoryFrame frame,string? master)=>frame with {Identity=frame.Identity! with {MasterCharacterId=master}};
        var key=InventoryResources.MasterAccountPrefix+"m";
        InventoryItem[] Items(ulong count)=>[Item(),Item(id:9,count:count,container:InventoryContainer.AccountStorage)];
        await store.ApplyFrameAsync(With(Frame(1,items:Items(5),account:key,character:"two"),"m"));
        await store.ApplyFrameAsync(With(Frame(2,items:Items(5),account:key,character:"three"),"m"));
        var account=Assert.Single(store.Accounts().Items);
        Assert.Equal((key,2),(account.AccountId,account.Characters.Count));
        Assert.Equal(5UL,Assert.Single(account.Aggregates,x=>x.Type.TypeId.Value==9).ObservedQuantity.Value);
        await store.ApplyFrameAsync(With(Frame(3,items:Items(5),account:"acc",character:"two"),"m"));
        account=Assert.Single(store.Accounts().Items);
        Assert.Equal((key,"acc"),(account.AccountId,account.Label));
        var restart=new InventoryHistoryStore(db,"next");
        await restart.ApplyFrameAsync(Frame(4,items:Items(6),account:"acc",character:"four"));
        await restart.ApplyFrameAsync(With(Frame(5,items:Items(6),account:key,character:"five"),"m"));
        account=Assert.Single(restart.Accounts().Items);
        Assert.Equal((key,4),(account.AccountId,account.Characters.Count));
    }
    /// <summary>已按游戏账户编号保存的账户在读到主角色后，与先出现的 <c>master-</c> 账户并成一份；带另一编号的账户不并入。</summary>
    [Fact] public async Task ExistingGameAccountAbsorbsTheMasterAccountWhenTheMasterCharacterAppears()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");
        InventoryFrame With(InventoryFrame frame,string? master)=>frame with {Identity=frame.Identity! with {MasterCharacterId=master}};
        var key=InventoryResources.MasterAccountPrefix+"m";
        await store.ApplyFrameAsync(Frame(1,account:"acc",character:"one"));
        await store.ApplyFrameAsync(With(Frame(2,account:key,character:"two"),"m"));
        Assert.Equal(2,store.Accounts().Items.Count);
        await store.ApplyFrameAsync(With(Frame(3,account:"acc",character:"one"),"m"));
        var account=Assert.Single(store.Accounts().Items);
        Assert.Equal("acc",account.AccountId);Assert.Equal(new[]{"one","two"},account.Characters.Select(c=>c.Identity.CharacterId).Order().ToArray());
        await store.ApplyFrameAsync(With(Frame(4,InventoryFrameKind.Delta,[],[new(InventoryDeltaKind.Upsert,Item(count:7))],account:key,character:"one"),"m"));
        Assert.Equal(("acc",true),(store.Workspace()!.Identity!.AccountId,store.Workspace()!.Current));
        await store.ApplyFrameAsync(With(Frame(5,account:"other",character:"three"),"m"));
        Assert.Equal(new[]{"acc","other"},store.Accounts().Items.Select(a=>a.AccountId).Order().ToArray());
    }
    /// <summary>旧 <c>character-</c> 账户只在同一角色写入新账户的完整同步后删除：其他角色的完整同步、同一角色仍按角色归属时都保留。</summary>
    [Fact] public async Task LegacyCharacterAccountStaysUntilThatCharacterHasAFullSyncElsewhere()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");
        var fallback=InventoryResources.CharacterAccountPrefix+"one";
        await store.ApplyFrameAsync(Frame(1,account:fallback));
        await store.ApplyFrameAsync(Frame(2,account:"acc",character:"two"));
        await store.ApplyFrameAsync(Frame(3,account:fallback));
        Assert.Equal(new[]{"acc",fallback},store.Accounts().Items.Select(a=>a.AccountId).Order(StringComparer.Ordinal).ToArray());
        await store.ApplyFrameAsync(Frame(4,account:InventoryResources.MasterAccountPrefix+"m"));
        Assert.DoesNotContain(store.Accounts().Items,a=>a.AccountId==fallback);
    }
    [Fact] public async Task DeltaGapInvalidatesAndRestartKeepsHistory()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");
        await store.ApplyFrameAsync(Frame(1));await store.ApplyFrameAsync(Frame(2,InventoryFrameKind.Delta,[],[new(InventoryDeltaKind.Upsert,Item(count:7))]));
        Assert.Equal(7UL,Assert.Single(store.Items().Items).Item.Quantity!.Value.Value);
        await store.ApplyFrameAsync(Frame(4,InventoryFrameKind.Delta,[],[new(InventoryDeltaKind.Upsert,Item(count:9))]));Assert.False(store.Workspace()!.Current);
        Assert.Equal(7UL,Assert.Single(store.Items().Items).Item.Quantity!.Value.Value);
        var restart=new InventoryHistoryStore(db,"next");Assert.False(restart.Workspace()!.Current);Assert.Single(restart.Accounts().Items);
    }
    [Fact] public async Task SharedStorageIsCountedOnceAndSlotsDetermineCompleteTotal()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");
        await store.ApplyFrameAsync(Frame(1,items:[Item(count:2),Item(count:5,container:InventoryContainer.AccountStorage)]));
        await store.ApplyFrameAsync(Frame(2,items:[Item(count:3),Item(count:5,container:InventoryContainer.AccountStorage)],character:"two"));
        var account=Assert.Single(store.Accounts().Items);Assert.Equal(10UL,Assert.Single(account.Aggregates).ObservedQuantity.Value);Assert.Null(Assert.Single(account.Aggregates).CompleteQuantity);
        await store.SetCharacterSlotsAsync(new("a",[new("one","one","one"),new("two","two","two")],true),store.Revision);
        account=Assert.Single(store.Accounts().Items);Assert.Equal(10UL,Assert.Single(account.Aggregates).CompleteQuantity!.Value.Value);Assert.Equal(store.Revision,store.Workspace()!.Revision);
        Assert.Empty(store.Items(new(AccountId:"other",CurrentOnly:true)).Items);
    }
    [Fact] public async Task ItemsCarryTheirCharacterAndAccountStorageCoverageIsReported()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");
        var before=store.Accounts();Assert.Empty(before.Items);
        await store.ApplyFrameAsync(Frame(1,items:[Item(count:2),Item(id:9,count:5,container:InventoryContainer.AccountStorage)]));
        await store.ApplyFrameAsync(Frame(2,items:[Item(count:3),Item(id:9,count:5,container:InventoryContainer.AccountStorage)],character:"two"));
        var all=store.Items().Items;
        Assert.All(all.Where(i=>i.Item.Container.KnownValue==InventoryContainer.AccountStorage),i=>Assert.Null(i.CharacterId));
        Assert.Equal(new[]{"one","two"},all.Where(i=>i.Item.Container.KnownValue==InventoryContainer.Backpack).Select(i=>i.CharacterId!).Order().ToArray());
        var one=store.Items(new(AccountId:"a",CharacterId:"one")).Items;
        Assert.Equal(2,one.Count);Assert.Contains(one,i=>i.CharacterId is null&&i.Item.Container.KnownValue==InventoryContainer.AccountStorage);
        Assert.Equal("one",Assert.Single(one,i=>i.Item.Container.KnownValue==InventoryContainer.Backpack).CharacterId);
        Assert.Equal("two",Assert.Single(store.Items(new(CurrentOnly:true)).Items,i=>i.Item.Container.KnownValue==InventoryContainer.Backpack).CharacterId);
        var account=Assert.Single(store.Accounts().Items);
        Assert.NotNull(account.SharedObservedAt);
        Assert.Contains(account.SharedCoverage!,c=>c.Container.KnownValue==InventoryContainer.AccountStorage&&c.Complete);
    }
    [Fact] public async Task ManualClearAndConfirmationSurviveCatalogRefreshAndCrossCategoryIds()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");
        await store.ApplyFrameAsync(Frame(1,items:[Item(),Item("EquipmentTool")]));
        InventoryCatalogData Catalog(string name)=>new("test","TW",new string('a',64),DateTimeOffset.UtcNow,[],[new(new("Consumable",new(1)),name,name,"Consumable",new(0),new(0),new(0),new(0)),new(new("EquipmentTool",new(1)),name,name,"EquipmentTool",new(0),new(0),new(0),new(0),true)],[],[]);
        await store.ReplaceCatalogAsync(Catalog("相同"));Assert.Equal(2,store.ResolveName("a","相同").Count);
        await store.EditAssociationsAsync(new("a",[new(new("Consumable",new(1)),null,true),new(new("EquipmentTool",new(1)),"我的琴")]),store.Revision);
        await store.ReplaceCatalogAsync(Catalog("更新"));var names=store.Associations("a").Items;Assert.Null(names.Single(n=>n.Type.Category=="Consumable").DisplayName);Assert.True(names.Single(n=>n.Type.Category=="EquipmentTool").IsInstrument);Assert.Equal("我的琴",names.Single(n=>n.Type.Category=="EquipmentTool").DisplayName);
        var restart=new InventoryHistoryStore(db,"next");Assert.Equal("我的琴",restart.Associations("a").Items.Single(n=>n.Type.Category=="EquipmentTool").DisplayName);
    }
    [Fact] public async Task CsvRoundTripPreservesInstanceAttributesSlotsAndIdempotentReceipt()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");
        await store.ApplyFrameAsync(Frame(1,items:[Item("Equipment",ulong.MaxValue) with {InstanceId=new(123),Equipped=true,Attributes=JsonSerializer.SerializeToElement(new { dye="红色,蓝色" })}]));
        await store.SetCharacterSlotsAsync(new("a",[new("one","one","one")],true),store.Revision);
        var files=store.Export(new()).Select(f=>new InventoryImportFile(f.Name,f.Content)).ToArray();var preview=store.PreviewImport(files);Assert.Single(preview.Issues);
        var request=new InventoryImportCommitRequest([new(preview.Issues[0].Id,"replace")]);var result=await store.CommitImportAsync(preview.Id,request,store.Revision);
        Assert.False(store.Workspace()!.Current);var item=Assert.Single(store.Items().Items).Item;Assert.Equal(ulong.MaxValue,item.TypeId.Value);Assert.True(item.Equipped);Assert.Equal("红色,蓝色",item.Attributes.GetProperty("dye").GetString());Assert.True(Assert.Single(store.Accounts().Items).SlotsComplete);
        var restart=new InventoryHistoryStore(db,"next");var repeated=await restart.CommitImportAsync(preview.Id,request,new(0));Assert.Equal(result.Id,repeated.Id);Assert.Equal(result.Revision,repeated.Revision);Assert.Equal(result.Revision,restart.Revision);
    }
    [Fact] public async Task LegacyCsvAndConflictChoicesKeepExistingHistory()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");
        var files=new[]{new InventoryImportFile("inventory.csv","account_id,character_id,character,container,item_id,quantity,observed_at,source\r\na,one,角色,backpack,5,7,2026-01-01T00:00:00Z,full\r\na,one,角色,backpack,,,2026-01-01T00:00:00Z,full_coverage\r\n"),new InventoryImportFile("associations.csv","item_id,name,association,source\r\n5,\"测试,名称\",user_confirmed,user\r\n")};
        var preview=store.PreviewImport(files);await store.CommitImportAsync(preview.Id,new([]),store.Revision);Assert.Null(store.Workspace());Assert.Equal("测试,名称",Assert.Single(store.Associations("a").Items).DisplayName);
        preview=store.PreviewImport(files);await store.CommitImportAsync(preview.Id,new([new(preview.Issues[0].Id,"keep-existing")]),store.Revision);Assert.Equal(7UL,Assert.Single(store.Items().Items).Item.Quantity!.Value.Value);
        Assert.Throws<OperationException>(()=>store.PreviewImport([new("inventory.csv","account_id,account_id\na,a\n")]));
        Assert.Throws<OperationException>(()=>store.PreviewImport([new("inventory.csv","account_id\n\"truncated")]));
    }
    [Fact] public async Task InvalidFullRevokesCurrentAndCancelledImportKeepsRevision()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");
        await store.ApplyFrameAsync(Frame(1));
        await Assert.ThrowsAsync<OperationException>(()=>store.ApplyFrameAsync(Frame(2) with {Items=null!}));Assert.False(store.Workspace()!.Current);
        var files=store.Export(new()).Select(f=>new InventoryImportFile(f.Name,f.Content)).ToArray();var preview=store.PreviewImport(files);var revision=store.Revision;
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.CommitImportAsync(preview.Id,new([new(preview.Issues[0].Id,"replace")]),revision,cancellation.Token));Assert.Equal(revision,store.Revision);
    }
    [Fact] public async Task ComposedScoresWithoutTableIdAreStoredAndOtherZeroTypesAreRejected()
    {
        // 台服 2026-11 实采：完整同步含 88 张表编号为 0 的自制乐谱，此前整帧被拒，常驻同步反复中断。
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");
        InventoryItem Score(ulong instance)=>new("MusicNoteItem",InventoryContainer.Backpack,instance.ToString(),new(0),new(instance),new(1),JsonSerializer.SerializeToElement(new{}),"MusicNoteItem");
        await store.ApplyFrameAsync(Frame(1,items:[Item(),Score(563530255421662208),Score(563530255421662209)]));
        Assert.True(store.Workspace()!.Current);Assert.Equal(2,store.Workspace()!.Items.Count(i=>i.Item.Category=="MusicNoteItem"));
        await store.ApplyFrameAsync(Frame(2,InventoryFrameKind.Delta,[],[new(InventoryDeltaKind.Remove,Score(563530255421662209) with {Quantity=new(0)})]));
        Assert.Equal(1,store.Workspace()!.Items.Count(i=>i.Item.Category=="MusicNoteItem"));
        var rejected=await Assert.ThrowsAsync<OperationException>(()=>store.ApplyFrameAsync(Frame(3,items:[Item(),Item(id:0) with {InstanceId=new(5)}])));
        Assert.Equal("itemIdentity",rejected.Data[InventoryHistoryStore.RejectionBranch]);Assert.False(store.Workspace()!.Current);
    }
    [Fact] public async Task DuplicateFramesStaleEditsAndInvalidCatalogCannotAdvanceState()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");
        await store.ApplyFrameAsync(Frame(1));var revision=store.Revision;await store.ApplyFrameAsync(Frame(1,items:[Item(count:77)]));Assert.Equal(revision,store.Revision);Assert.Equal(2UL,Assert.Single(store.Items().Items).Item.Quantity!.Value.Value);
        await Assert.ThrowsAsync<OperationException>(()=>store.EditAssociationsAsync(new("a",[new(new("Consumable",new(1)),"名称")]),new(0)));Assert.Equal(revision,store.Revision);
        var entry=new InventoryCatalogEntry(new("Consumable",new(1)),"名称","名称","Consumable",new(0),new(0),new(0),new(0));
        var catalog=new InventoryCatalogData("test","TW",new string('a',64),DateTimeOffset.UtcNow,[],[entry],[],[]);
        await store.ReplaceCatalogAsync(catalog);var before=store.CatalogSummary();await Assert.ThrowsAsync<OperationException>(()=>store.ReplaceCatalogAsync(catalog with {Entries=[entry,entry]}));Assert.Equal(before.Revision,store.CatalogSummary().Revision);
    }
    [Fact] public async Task SameNameImportedCharactersRequireExplicitIndependentIdentityChoice()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");
        var preview=store.PreviewImport([new("accounts.json","{\"a\":{\"characters\":{\"one\":\"同名\",\"two\":\"同名\"}}}"),new("inventory.csv","account_id,character_id,character,container,item_id,quantity,observed_at,source\r\na,one,同名,backpack,5,7,2026-01-01T00:00:00Z,full\r\n")]);
        Assert.Contains("one,two",Assert.Single(preview.Issues).Description);await Assert.ThrowsAsync<OperationException>(()=>store.CommitImportAsync(preview.Id,new([]),store.Revision));
        await store.CommitImportAsync(preview.Id,new([new(preview.Issues[0].Id,"replace")]),store.Revision);Assert.Equal(2,Assert.Single(store.Accounts().Items).Characters.Count);
    }
    [Fact] public async Task EditReceiptsPersistOriginalResponseAndRejectChangedBodyOrRevision()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");await store.ApplyFrameAsync(Frame(1));
        var revision=store.Revision;var request=new InventoryAssociationEditRequest("a",[new(new("Consumable",new(1)),"我的名称")]);
        var response=await store.EditAssociationsAsync(request,revision,"front-a","key");var next=store.Revision;
        var restart=new InventoryHistoryStore(db,"next");var repeat=await restart.EditAssociationsAsync(request,revision,"front-a","key");Assert.Equal(response.Revision,repeat.Revision);Assert.Equal("我的名称",Assert.Single(repeat.Items).DisplayName);Assert.Equal(next,restart.Revision);
        await Assert.ThrowsAsync<OperationException>(()=>restart.EditAssociationsAsync(request,next,"front-a","key"));await Assert.ThrowsAsync<OperationException>(()=>restart.EditAssociationsAsync(request,revision,"front-a","other"));
        var slots=new InventoryCharacterSlotsRequest("a",[new("one","one","one")],true);var slotRevision=restart.Revision;var slotResponse=await restart.SetCharacterSlotsAsync(slots,slotRevision,"front-a","slots");
        var again=new InventoryHistoryStore(db,"again");var slotRepeat=await again.SetCharacterSlotsAsync(slots,slotRevision,"front-a","slots");Assert.Equal(slotResponse.Revision,slotRepeat.Revision);Assert.True(slotRepeat.SlotsComplete);Assert.Equal(slotResponse.Revision,again.Revision);
    }
    [Fact] public async Task FullDeclaredFieldsCanRetainSixUnknownContainersAmongFiftyOneCoverages()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");
        var categories=Enumerable.Range(0,16).Select(n=>"Category"+n).Append("Consumable").ToArray();
        var coverage=categories.SelectMany((category,index)=>Enum.GetValues<InventoryContainer>().Select(container=>new InventoryCoverage(category,container,!(index<6&&container==InventoryContainer.AccountStorage),index<6&&container==InventoryContainer.AccountStorage?"schema-container-unrepresented":null))).ToArray();
        Assert.Equal(51,coverage.Length);await store.ApplyFrameAsync(Frame(1) with {Coverage=coverage});Assert.True(store.Workspace()!.Current);Assert.Equal(6,store.Workspace()!.Coverage.Count(c=>!c.Complete));Assert.Equal(ObservationQuality.Partial,store.Workspace()!.Quality.KnownValue);Assert.Equal(Source,Assert.Single(store.Items().Items).Source);
        var restart=new InventoryHistoryStore(db,"next");Assert.Equal(Source,Assert.Single(restart.Items().Items).Source);Assert.False(restart.Workspace()!.Current);
        await Assert.ThrowsAsync<OperationException>(()=>store.ApplyFrameAsync(Frame(2) with {Items=[Item("Category0",container:InventoryContainer.AccountStorage)],Coverage=coverage}));Assert.False(store.Workspace()!.Current);
    }
    [Fact] public async Task CommittedHotDeletionReloadsCachesAndRollbackRetainsHistory()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");await store.ApplyFrameAsync(Frame(1,items:[Item(),Item("Equipment")]));
        var page=store.Items(new(PageSize:1));Assert.NotNull(page.NextCursor);var preview=store.PreviewImport(store.Export(new()).Select(f=>new InventoryImportFile(f.Name,f.Content)).ToArray());var revision=store.Revision;
        var owner=InventoryStorageData.Owner(db,store);var deletion=owner.PrepareDeletion();await Assert.ThrowsAsync<InvalidOperationException>(()=>db.WriteAsync<bool>(tx=>{deletion.Apply(tx);throw new InvalidOperationException("rollback");}));Assert.Equal(revision,store.Revision);Assert.Equal(2,store.Items().Items.Count);Assert.Equal(preview.Id,store.GetPreview(preview.Id).Id);
        deletion=owner.PrepareDeletion();await db.WriteAsync(tx=>{deletion.Apply(tx);return true;});deletion.Committed!();Assert.True(store.Revision.Value>revision.Value);Assert.Null(store.Workspace());Assert.Empty(store.Accounts().Items);Assert.Equal(0,store.CatalogSummary().Entries);Assert.Throws<OperationException>(()=>store.GetPreview(preview.Id));Assert.Throws<OperationException>(()=>store.Items(new(PageSize:1,Cursor:page.NextCursor)));
        await store.ApplyFrameAsync(Frame(2));Assert.True(store.Workspace()!.Current);
    }
    [Fact] public async Task UnknownQuantityStaysPartialAndDeltaRequiresCoveredBaseline()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");await store.ApplyFrameAsync(Frame(1,items:[Item() with {Quantity=null}]));
        Assert.Equal(ObservationQuality.Partial,store.Workspace()!.Quality.KnownValue);Assert.False(Assert.Single(store.Workspace()!.Items).Current);Assert.Null(Assert.Single(store.Accounts().Items).Aggregates[0].CompleteQuantity);
        await Assert.ThrowsAsync<OperationException>(()=>store.ApplyFrameAsync(Frame(2,InventoryFrameKind.Delta,[],[new(InventoryDeltaKind.Upsert,Item("Equipment"))])));Assert.False(store.Workspace()!.Current);Assert.Null(Assert.Single(store.Items().Items).Item.Quantity);
    }
    [Fact] public async Task OriginalStrongEditTagReplaysAfterEpochChangeAndNewKeysRejectOldEpoch()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var epoch=new string('a',32);var store=new InventoryHistoryStore(db,epoch);await store.ApplyFrameAsync(Frame(1));
        var etag="\"inventory-"+epoch+"-"+store.Revision+"\"";var request=new InventoryAssociationEditRequest("a",[new(new("Consumable",new(1)),"原确认")]);var response=await store.EditAssociationsAsync(request,etag,"front","strong-key");
        var restart=new InventoryHistoryStore(db,new string('b',32));var replay=await restart.EditAssociationsAsync(request,etag,"front","strong-key");Assert.Equal(response.Revision,replay.Revision);Assert.Equal("原确认",Assert.Single(replay.Items).DisplayName);
        await Assert.ThrowsAsync<OperationException>(()=>restart.EditAssociationsAsync(request,etag,"front","new-key"));
        await Assert.ThrowsAsync<OperationException>(()=>restart.EditAssociationsAsync(request,"W/"+etag,"front","weak-key"));
        await Assert.ThrowsAsync<OperationException>(()=>restart.EditAssociationsAsync(request,"\"inventory-"+new string('b',32)+"-18446744073709551616\"","front","overflow-key"));
    }
    [Fact] public async Task HotDeletionForgetsCatalogImportAndExportReceiptsAndEvents()
    {
        using var area=new Area();using var lease=area.Lease();await using var db=SqliteStore.Open(lease,[InventoryHistoryStore.Migration]);var store=new InventoryHistoryStore(db,"epoch");var operations=new OperationStore(db);var request=new { value="test" };
        foreach(var kind in new[]{"inventory.catalog.import","inventory.export"})
        {
            var route="/api/v1/inventory/"+kind;var admitted=await operations.AdmitInventoryAsync("front",route,kind,request,"receipt-key",true);await operations.BeginInventoryAsync(admitted.Snapshot.Id);await operations.FinishInventoryAsync(admitted.Snapshot.Id);
            Assert.NotNull(operations.InventoryRetry("front",route,request,"receipt-key"));
        }
        var owner=InventoryStorageData.Owner(db,store);var deletion=owner.PrepareDeletion();await db.WriteAsync(tx=>{operations.ForgetModuleHistory(tx,owner.OperationKinds,owner.EventTypes);deletion.Apply(tx);return true;});deletion.Committed!();
        foreach(var kind in new[]{"inventory.catalog.import","inventory.export"})Assert.Null(operations.InventoryRetry("front","/api/v1/inventory/"+kind,request,"receipt-key"));
        Assert.Equal(0L,db.ReadScalar("SELECT count(*) FROM core_operations"));Assert.Equal(0L,db.ReadScalar("SELECT count(*) FROM core_outbox WHERE event_type='operation.changed'"));Assert.Equal(0L,db.ReadScalar("SELECT count(*) FROM core_state_projection WHERE event_type='operation.changed'"));
    }
    private sealed class Area:IDisposable
    {
        private readonly string root=Path.Combine(Path.GetTempPath(),"inventory-tests",Guid.NewGuid().ToString("N"));
        public DataRootLease Lease()=>DataRootLease.Open(new(Path.Combine(root,"bootstrap"),true,Path.Combine(root,"data")));
        public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}


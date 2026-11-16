// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using System.Text;
using MabiTool.Providers.TwObservation.ItemCatalog;
using Xunit;
namespace MabiTool.InventoryCatalog.Tests;
public sealed class CatalogTests
{
    private static readonly IReadOnlyDictionary<string,IReadOnlyList<string>> Fields=new Dictionary<string,IReadOnlyList<string>>{["Consumable"]=["Id","DisplayName","FlavorText"],["EquipmentTool"]=["Id","DisplayName","FlavorText","EquipmentToolTypeSheetId","InstrumentType"],["Equipment"]=["Id","DisplayName","FlavorText"],["AquariumFish"]=["Id","DisplayName","FlavorText"]};
    [Fact] public void DifferentIndexLayoutsPreserveSameIdAcrossCategoriesAndInstrumentEvidence()
    {
        var fixture=Resource();var entries=ClientItemCatalogExtractor.Decode(fixture.Meta,fixture.Text,fixture.Data,new byte[4],Fields,default,out var excluded,out var categories);
        Assert.Empty(excluded);Assert.Equal(4,entries.Count);Assert.Equal(4,entries.Count(e=>e.Type.TypeId.Value==7));Assert.Equal("鱼",entries.Single(e=>e.Type.Category=="AquariumFish").DisplayName);
        var instrument=entries.Single(e=>e.Type.Category=="EquipmentTool");Assert.True(instrument.IsInstrument);Assert.Equal(99UL,instrument.InstrumentTypeId!.Value.Value);Assert.Equal("琴",instrument.DisplayName);Assert.Equal("剑",entries.Single(e=>e.Type.Category=="Equipment").DisplayName);
        Assert.False(categories.Single(c=>c.Category=="MusicNoteItem").Supported);
    }
    [Fact] public void BodyPrefixCannotReadAcrossNextRowOrTableBoundary()
    {
        var fixture=Resource();Assert.Throws<InvalidDataException>(()=>ClientItemCatalogExtractor.Decode(fixture.Meta,fixture.Text,fixture.Data[..3],new byte[4],Fields,default,out _,out _));
        Assert.Throws<InvalidDataException>(()=>ClientItemCatalogExtractor.Decode(fixture.Meta[..^1],fixture.Text,fixture.Data,new byte[4],Fields,default,out _,out _));
    }
    [Fact] public void CancelledDecodeDoesNotPublishPartialEntries()
    {
        var fixture=Resource();using var cancellation=new CancellationTokenSource();cancellation.Cancel();Assert.Throws<OperationCanceledException>(()=>ClientItemCatalogExtractor.Decode(fixture.Meta,fixture.Text,fixture.Data,new byte[4],Fields,cancellation.Token,out _,out _));
    }
    [Fact] public void DisplayNamesRetainRawInputAndRejectUnresolvedTokens()
    {
        var strings=new Dictionary<string,IReadOnlyDictionary<string,string>>{["Common"]=new Dictionary<string,string>{["NoRichText_RuneTierFormat"]="{0}阶"}};
        Assert.Equal("2阶符文",ClientItemCatalogExtractor.DisplayName("<color=#123456>%RuneTier, Season1, 2%符文</color>",strings));
        Assert.ThrowsAny<Exception>(()=>ClientItemCatalogExtractor.DisplayName("%Unknown, token%",strings));
    }
    private static (byte[] Meta,byte[] Text,byte[] Data) Resource()
    {
        using var text=new MemoryStream();int Text(string value){var at=(int)text.Position;var bytes=Encoding.UTF8.GetBytes(value);text.WriteByte((byte)bytes.Length);text.Write(bytes);text.WriteByte(0);return at;}
        var tableNames=new[]{Text("Consumable"),Text("Equipment"),Text("EquipmentTool"),Text("Instrument"),Text("AquariumFish")};var names=new[]{Text("药"),Text("剑"),Text("琴"),Text("鱼")};
        using var data=new MemoryStream();data.WriteByte((byte)names[1]);data.WriteByte(0);data.WriteByte((byte)names[2]);data.WriteByte(0);using(var w=new BinaryWriter(data,Encoding.UTF8,true)){w.Write((uint)42);w.Write((uint)99);}data.WriteByte((byte)names[3]);data.WriteByte(0);
        var blocks=new byte[5][];using(var b=new MemoryStream()){using(var w=new BinaryWriter(b,Encoding.UTF8,true)){w.Write((uint)7);w.Write((byte)names[0]);w.Write((byte)0);}blocks[0]=b.ToArray();}
        blocks[1]=BitConverter.GetBytes((uint)7);blocks[2]=BitConverter.GetBytes((uint)7);blocks[3]=BitConverter.GetBytes((uint)99);blocks[4]=BitConverter.GetBytes((uint)7);
        using var meta=new MemoryStream();void Var(uint n){while(n>=128){meta.WriteByte((byte)(n|128));n>>=7;}meta.WriteByte((byte)n);}Var(5);
        var sizes=new uint[]{0,2,10,0,2};
        for(var i=0;i<5;i++){Var((uint)tableNames[i]);Var(0);Var(1);Var((uint)blocks[i].Length);Var(sizes[i]);}
        foreach(var size in sizes)Var(size);foreach(var block in blocks)meta.Write(block);
        return(meta.ToArray(),text.ToArray(),data.ToArray());
    }
}

using System.Collections.Generic;
using System.ComponentModel;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class DirtyLookupTests
{
    [Fact]
    public void Indexer_EmptyLookup_ReturnsFalse()
    {
        var d = new DirtyLookup();
        Assert.False(d["Xyz"]);
    }

    [Fact]
    public void Mark_NewProperty_SetsIndexerTrue_AndRaisesItemArrayNotify()
    {
        var d = new DirtyLookup();
        var notifies = new List<string>();
        d.PropertyChanged += (_, e) => notifies.Add(e.PropertyName ?? "");
        d.Mark("DefaultModelsDirectory");
        Assert.True(d["DefaultModelsDirectory"]);
        Assert.True(d.Any);
        Assert.Equal(1, d.Count);
        // WPF 重新评估所有索引器绑定的约定 key
        Assert.Contains("Item[]", notifies);
        Assert.Contains(nameof(DirtyLookup.Any), notifies);
        Assert.Contains(nameof(DirtyLookup.Count), notifies);
    }

    [Fact]
    public void Mark_SamePropertyTwice_NoDoubleCount_NoSpuriousNotify()
    {
        var d = new DirtyLookup();
        d.Mark("X");
        var notifiesAfterFirst = 0;
        d.PropertyChanged += (_, e) => { if (e.PropertyName == "Item[]") notifiesAfterFirst++; };
        d.Mark("X");
        Assert.Equal(1, d.Count);
        Assert.Equal(0, notifiesAfterFirst);
    }

    [Fact]
    public void Clear_RemovesAll_AndRaisesNotify()
    {
        var d = new DirtyLookup();
        d.Mark("A"); d.Mark("B"); d.Mark("C");
        Assert.Equal(3, d.Count);
        var notifies = new List<string>();
        d.PropertyChanged += (_, e) => notifies.Add(e.PropertyName ?? "");
        d.Clear();
        Assert.Equal(0, d.Count);
        Assert.False(d.Any);
        Assert.False(d["A"]);
        Assert.Contains("Item[]", notifies);
    }

    [Fact]
    public void Clear_EmptyLookup_NoNotify()
    {
        var d = new DirtyLookup();
        var notifies = 0;
        d.PropertyChanged += (_, _) => notifies++;
        d.Clear();
        Assert.Equal(0, notifies);
    }
}
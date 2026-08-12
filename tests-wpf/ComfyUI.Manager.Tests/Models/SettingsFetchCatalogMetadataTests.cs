using System.Text.Json;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

/// <summary>v0.6.13-B:Settings.FetchCatalogMetadata toggle 默认 false + JSON 往返。</summary>
public class SettingsFetchCatalogMetadataTests
{
    [Fact]
    public void FetchCatalogMetadata_DefaultsToFalse()
    {
        var s = new Settings();
        Assert.False(s.FetchCatalogMetadata);
    }

    [Fact]
    public void FetchCatalogMetadata_RoundtripsThroughJson()
    {
        var original = new Settings { FetchCatalogMetadata = true };
        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<Settings>(json)!;
        Assert.True(restored.FetchCatalogMetadata);
    }

    [Fact]
    public void CopyInto_CopiesFetchCatalogMetadata()
    {
        var target = new Settings { FetchCatalogMetadata = false };
        var source = new Settings { FetchCatalogMetadata = true };
        Settings.CopyInto(target, source);
        Assert.True(target.FetchCatalogMetadata);
    }
}
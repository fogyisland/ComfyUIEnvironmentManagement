using System.Collections.Generic;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.11+ SDD A: ExtractReference 3-key 优先级 (reference → url → repository)。
/// raw_metadata 空串视为"未配置"—— 不返回空串本身,继续查下一个 key。
/// </summary>
public class CatalogRefreshServiceExtractReferenceTests
{
    [Fact]
    public void ExtractReference_ReferenceKeyOnly_ReturnsReference()
    {
        var entry = new CatalogEntry
        {
            RawMetadata = new Dictionary<string, object?>
            {
                ["reference"] = "https://github.com/a/b",
            },
        };
        Assert.Equal("https://github.com/a/b", CatalogRefreshService.ExtractReference(entry));
    }

    [Fact]
    public void ExtractReference_UrlKeyOnly_ReturnsUrl()
    {
        var entry = new CatalogEntry
        {
            RawMetadata = new Dictionary<string, object?>
            {
                ["url"] = "https://github.com/c/d",
            },
        };
        Assert.Equal("https://github.com/c/d", CatalogRefreshService.ExtractReference(entry));
    }

    [Fact]
    public void ExtractReference_RepositoryKeyOnly_ReturnsRepository()
    {
        var entry = new CatalogEntry
        {
            RawMetadata = new Dictionary<string, object?>
            {
                ["repository"] = "https://github.com/e/f",
            },
        };
        Assert.Equal("https://github.com/e/f", CatalogRefreshService.ExtractReference(entry));
    }

    [Fact]
    public void ExtractReference_ReferenceAndUrl_ReturnsReference_Priority()
    {
        var entry = new CatalogEntry
        {
            RawMetadata = new Dictionary<string, object?>
            {
                ["reference"] = "https://github.com/a/b",
                ["url"] = "https://github.com/c/d",
            },
        };
        Assert.Equal("https://github.com/a/b", CatalogRefreshService.ExtractReference(entry));
    }

    [Fact]
    public void ExtractReference_AllThree_ReturnsReference_Priority()
    {
        var entry = new CatalogEntry
        {
            RawMetadata = new Dictionary<string, object?>
            {
                ["reference"] = "https://github.com/a/b",
                ["url"] = "https://github.com/c/d",
                ["repository"] = "https://github.com/e/f",
            },
        };
        Assert.Equal("https://github.com/a/b", CatalogRefreshService.ExtractReference(entry));
    }

    [Fact]
    public void ExtractReference_AllEmpty_ReturnsEmptyString()
    {
        var entry = new CatalogEntry
        {
            RawMetadata = new Dictionary<string, object?>
            {
                ["reference"] = "",
                ["url"] = "",
                ["repository"] = "",
            },
        };
        Assert.Equal("", CatalogRefreshService.ExtractReference(entry));
    }

    [Fact]
    public void ExtractReference_NullRawMetadata_ReturnsEmptyString()
    {
        var entry = new CatalogEntry { RawMetadata = null! };
        Assert.Equal("", CatalogRefreshService.ExtractReference(entry));
    }

    [Fact]
    public void ExtractReference_EmptyStringValues_FallsThroughToNextKey()
    {
        // reference="" url="" 但 repository="https://github.com/g/h" → 应返回 repository(不因 reference 空串继续返回 "")
        var entry = new CatalogEntry
        {
            RawMetadata = new Dictionary<string, object?>
            {
                ["reference"] = "",
                ["url"] = "",
                ["repository"] = "https://github.com/g/h",
            },
        };
        Assert.Equal("https://github.com/g/h", CatalogRefreshService.ExtractReference(entry));
    }
}

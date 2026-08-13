using System.Collections.Generic;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public class CatalogEntryHasherTests
{
    [Fact]
    public void ComputeHash_SameCanonicalContent_SameHash()
    {
        var entry1 = new CatalogEntry
        {
            Package = "pkg-x",
            RawMetadata = new Dictionary<string, object?>
            {
                ["author"] = "alice",
                ["title"] = "Title",
                ["description"] = "Desc",
                ["id"] = "node-x",
            },
        };
        var entry2 = new CatalogEntry
        {
            Package = "pkg-x",
            RawMetadata = new Dictionary<string, object?>
            {
                ["id"] = "node-x",
                ["title"] = "Title",
                ["description"] = "Desc",
                ["author"] = "alice",
            },
        };
        Assert.Equal(
            CatalogEntryHasher.ComputeHash(entry1),
            CatalogEntryHasher.ComputeHash(entry2));
    }

    [Fact]
    public void ComputeHash_DifferentContent_DifferentHash()
    {
        var entry1 = new CatalogEntry { Package = "pkg-x" };
        var entry2 = new CatalogEntry { Package = "pkg-y" };
        Assert.NotEqual(
            CatalogEntryHasher.ComputeHash(entry1),
            CatalogEntryHasher.ComputeHash(entry2));
    }

    [Fact]
    public void ComputeHash_MetadataFieldsDoNotAffectHash()
    {
        // stars/license 等 metadata 改了,hash 必须不变(metadata refresh 触发 row 重写 = 死循环)
        var entry1 = new CatalogEntry { Package = "pkg-x", Stars = 100 };
        var entry2 = new CatalogEntry { Package = "pkg-x", Stars = 999 };
        Assert.Equal(
            CatalogEntryHasher.ComputeHash(entry1),
            CatalogEntryHasher.ComputeHash(entry2));
    }

    [Fact]
    public void ComputeHash_RawMetadataSkippedKeysDoNotAffectHash()
    {
        // apt_dependency/badges/files/js_path/last_update/nickname/nodename_pattern/
        // pip/preemptions/reference2/version — 这些字段变,hash 不变
        var entry1 = new CatalogEntry
        {
            Package = "pkg-x",
            RawMetadata = new Dictionary<string, object?>
            {
                ["pip"] = new List<object?> { "torch>=2.0" },
            },
        };
        var entry2 = new CatalogEntry
        {
            Package = "pkg-x",
            RawMetadata = new Dictionary<string, object?>
            {
                ["pip"] = new List<object?> { "torch>=2.5" },
            },
        };
        Assert.Equal(
            CatalogEntryHasher.ComputeHash(entry1),
            CatalogEntryHasher.ComputeHash(entry2));
    }
}

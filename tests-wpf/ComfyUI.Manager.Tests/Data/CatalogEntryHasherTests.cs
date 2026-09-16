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
        // files/js_path/nodename_pattern/pip/preemptions/reference2/version —
        // 这些字段变,hash 不变
        // v1.0.0.x (2026-09-16) T43i.2.2-fix:apt_dependency/badges/last_update/nickname
        // 这 4 个键从 nodelist_entries 表删了 → 从 hash-skipped 列表也移除(它们即便出现在
        // RawMetadata 也不再影响 nodelist schema;但若未来 CatalogEntryHasher 又加回,
        // 它们仍是「不应进 hash」的元数据)。
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

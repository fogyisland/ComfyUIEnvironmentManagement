using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Data;

/// <summary>
/// v0.6.14: SHA256 of canonical JSON for per-entry hash diff. 仅包含 catalog
/// JSON 内容字段(package/author/title/description/reference 等)— 不包含
/// metadata 列或时间戳(metadata 改了不应触发 row 重写,否则死循环)。
/// </summary>
public static class CatalogEntryHasher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ComputeHash(CatalogEntry entry)
    {
        // SortedDictionary 自动按 key 字母序,JSON 序列化后 hash 稳定
        var canonical = new SortedDictionary<string, object?>
        {
            ["id"] = GetRaw(entry, "id"),
            ["name"] = entry.Package,
            ["author"] = GetRaw(entry, "author"),
            ["title"] = GetRaw(entry, "title"),
            ["description"] = GetRaw(entry, "description"),
            ["category"] = GetRaw(entry, "category"),
            ["reference"] = GetRaw(entry, "reference"),
            ["tags"] = GetRaw(entry, "tags"),
            ["install_type"] = GetRaw(entry, "install_type"),
        };
        var json = JsonSerializer.Serialize(canonical, JsonOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }

    private static object? GetRaw(CatalogEntry entry, string key)
    {
        if (entry.RawMetadata is null) return null;
        return entry.RawMetadata.TryGetValue(key, out var v) ? v : null;
    }
}

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Data;

/// <summary>
/// 永久缓存 <see cref="PyTorchVersion"/> 目录(完整多版本列表)。
/// 与单版本 <see cref="PyTorchVersionCache"/> 不同,本缓存
/// <b>没有 TTL</b>:一旦写入就永久有效,读取时不检查任何时间戳。
/// 文件名 <c>pytorch_catalog_cache.json</c>,与单版本缓存
/// (<c>pytorch_versions_cache.json</c>)分开,互不覆盖。
/// </summary>
public sealed class PyTorchVersionCatalogCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public const string FileName = "pytorch_catalog_cache.json";

    public PyTorchVersionCatalogCache(string appDataDir)
    {
        FilePath = Path.Combine(appDataDir, FileName);
    }

    public string FilePath { get; }

    public async Task<IReadOnlyList<PyTorchVersion>?> TryReadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(FilePath, ct);
            var versions = JsonSerializer.Deserialize<List<PyTorchVersion>>(json, JsonOptions);
            return versions;
        }
        catch
        {
            return null;
        }
    }

    public async Task WriteAsync(IReadOnlyList<PyTorchVersion> versions, CancellationToken ct = default)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(versions, JsonOptions);
            await File.WriteAllTextAsync(FilePath, json, ct);
        }
        catch
        {
        }
    }
}

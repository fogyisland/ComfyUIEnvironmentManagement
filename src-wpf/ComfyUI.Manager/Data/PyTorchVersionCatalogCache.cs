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
/// <remarks>
/// 非 sealed:<see cref="TryReadAsync"/> / <see cref="WriteAsync"/> 都
/// 标了 <c>virtual</c>,这样 <c>PyTorchVersionDirectoryTests</c> 可以在
/// 不写磁盘的前提下用 in-memory 子类验证编排逻辑。生产代码契约不变:
/// <list type="bullet">
/// <item><see cref="TryReadAsync"/>:文件不存在 / JSON 损坏 / 反序列化
///   失败 → <c>null</c>;反之返回 stable 列表(可能为空)。</item>
/// <item><see cref="WriteAsync"/>:写失败静默吞,调用方永远不期望
///   抛。</item>
/// </list>
/// </remarks>
public class PyTorchVersionCatalogCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public const string FileName = "pytorch_catalog_cache.json";

    public PyTorchVersionCatalogCache(string localDataDir)
    {
        FilePath = Path.Combine(localDataDir, FileName);
    }

    public string FilePath { get; }

    public virtual async Task<IReadOnlyList<PyTorchVersion>?> TryReadAsync(CancellationToken ct = default)
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

    public virtual async Task WriteAsync(IReadOnlyList<PyTorchVersion> versions, CancellationToken ct = default)
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

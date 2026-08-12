using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.13-B: GitHubCatalogMetadataService 的本地 24h TTL cache。
/// 文件:&lt;%APPDATA%/ComfyUI-Manager/catalog_metadata_cache.json&gt;,v1 schema:
/// <code>{ "version": 1, "entries": { "owner/repo": CachedMetadata, ... } }</code>
/// Atomic write via temp + rename。
/// </summary>
public sealed class MetadataCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public string FilePath { get; }

    public MetadataCache() : this(DefaultPath()) { }
    public MetadataCache(string filePath)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    public static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "ComfyUI-Manager", "catalog_metadata_cache.json");
    }

    /// <summary>
    /// 返回 entry 的 cached data,过期(&gt;24h)或缺失返回 null。
    /// 文件不存在或 version != 1 → 当 v1 创建(返回 null,下次 SaveAsync 写)。
    /// </summary>
    public async Task<CachedMetadata?> TryGetAsync(string repoKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(repoKey)) return null;
        if (!File.Exists(FilePath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(FilePath, ct).ConfigureAwait(false);
            var root = JsonSerializer.Deserialize<CacheRoot>(json, JsonOptions);
            if (root is null || root.Version != 1) return null;
            if (!root.Entries.TryGetValue(repoKey, out var data) || data is null) return null;
            // 24h TTL
            var age = DateTime.UtcNow - data.FetchedAt;
            if (age > TimeSpan.FromHours(24)) return null;
            return data;
        }
        catch
        {
            return null;  // 损坏文件 → 当 cache miss
        }
    }

    public async Task SaveAsync(string repoKey, CachedMetadata data, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(repoKey)) throw new ArgumentException(null, nameof(repoKey));
        var root = await LoadOrInitRootAsync(ct).ConfigureAwait(false);
        root.Entries[repoKey] = data;
        var json = JsonSerializer.Serialize(root, JsonOptions);
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tempPath = FilePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);
        File.Move(tempPath, FilePath, overwrite: true);
    }

    private async Task<CacheRoot> LoadOrInitRootAsync(CancellationToken ct)
    {
        if (File.Exists(FilePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(FilePath, ct).ConfigureAwait(false);
                var root = JsonSerializer.Deserialize<CacheRoot>(json, JsonOptions);
                if (root is not null && root.Version == 1) return root;
            }
            catch { /* fall through */ }
        }
        return new CacheRoot { Version = 1, Entries = new() };
    }

    private sealed class CacheRoot
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }
        [JsonPropertyName("entries")]
        public System.Collections.Generic.Dictionary<string, CachedMetadata> Entries { get; set; }
            = new();
    }
}

/// <summary>
/// v0.6.13-B: 一条 GitHub repo 的 cached metadata。
/// FetchedAt UTC, 24h TTL 由 <see cref="MetadataCache"/> 判定。
/// </summary>
public sealed record CachedMetadata(
    string? License,
    System.Collections.Generic.IReadOnlyList<string> Tags,
    int Stars,
    int Downloads,
    string? LastCommit,
    string? ReadmeMarkdown,
    string? LatestChangelog,
    bool Deprecated,
    System.Collections.Generic.IReadOnlyList<string> PythonCompat,
    System.Collections.Generic.IReadOnlyList<string> OsCompat,
    DateTime FetchedAt);
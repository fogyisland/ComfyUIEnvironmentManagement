using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x (2026-09-05) feat/nodelist-redesign:节点列表下载器。
///
/// 用户原话"默认的节点json文件放在目录下 nodeslist 目录下,
/// 这里面文件每次去
/// https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main/custom-node-list.json
/// 下面去拉取所有 custom-node-list.json 文件 到本地"。
///
/// 简化实现:只下载 1 个文件(默认仓库的 custom-node-list.json),
/// 不 clone 整个仓库。目标文件 = <nodelistDirectory>/nodeslist/custom-node-list.json
/// v1.0.0.x (2026-09-15) feat/nodelist-market-redesign (T43)+user:子目录命名 nodelist → nodeslist
/// (跟将来正式发布的 seed json 目录命名一致)。
/// </summary>
public sealed class NodelistDownloader
{
    public const string DefaultCustomNodeListUrl =
        "https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main/custom-node-list.json";

    /// <summary>子目录命名 — 跟发布版 seed json 目录一致(<paramref name="nodelistDirectory"/>/nodeslist/custom-node-list.json)。</summary>
    public const string SeedSubdirectoryName = "nodeslist";

    private readonly HttpClient _http;

    public NodelistDownloader(HttpClient http)
    {
        _http = http;
    }

    public sealed record DownloadResult(
        string FilePath,
        int SizeBytes,
        string Url,
        TimeSpan Elapsed);

    /// <summary>
    /// 下载默认仓库的 custom-node-list.json 到
    /// {nodelistDirectory}/nodeslist/custom-node-list.json
    /// </summary>
    public async Task<DownloadResult> DownloadDefaultAsync(
        string nodelistDirectory,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(nodelistDirectory))
            throw new ArgumentException("nodelistDirectory is empty", nameof(nodelistDirectory));

        var destDir = Path.Combine(nodelistDirectory, SeedSubdirectoryName);
        Directory.CreateDirectory(destDir);
        var destFile = Path.Combine(destDir, "custom-node-list.json");

        using var resp = await _http.GetAsync(DefaultCustomNodeListUrl, ct);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        await File.WriteAllBytesAsync(destFile, bytes, ct);

        sw.Stop();
        return new DownloadResult(destFile, bytes.Length, DefaultCustomNodeListUrl, sw.Elapsed);
    }
}

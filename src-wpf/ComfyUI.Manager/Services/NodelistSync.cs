using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x (2026-09-05) feat/nodelist-directory:自动同步节点列表
/// 扫描 envs/&lt;env&gt;/custom_nodes/ComfyUI-Manager/ 找所有
/// custom-node-list.json,挑最大那个拷到 NodelistDirectory/ 目录
/// (供后续 NodeListScanner 解析入库)。
///
/// 用户原话"能够自动检测所有 comfyui 环境中 node-list.json 文件,
/// 并且将最大的文件拷贝到 nodelists 目录"——
/// • 扫描 envs/&lt;env&gt;/custom_nodes/ComfyUI-Manager/{,node_db/&lt;dev|forked|legacy|new|tutorial&gt;}/custom-node-list.json
/// • 挑最大文件(custom_nodes 数量最多,JsonDocument 解析 entries.Length)
/// • 拷到 Settings.NodelistDirectory/ 目录
/// </summary>
public sealed class NodelistSync
{
    public sealed record SyncResult(
        int FilesScanned,
        int LargestFileEntries,
        string LargestFilePath,
        string DestinationPath,
        TimeSpan Elapsed);

    public sealed record SyncProgress(
        int FilesScanned,
        string CurrentFile,
        int CurrentEntries);

    public SyncResult Sync(
        string envsRoot,
        string destinationDirectory,
        IProgress<SyncProgress>? progress = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(envsRoot) || !Directory.Exists(envsRoot))
            return new SyncResult(0, 0, "", "", TimeSpan.Zero);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            return new SyncResult(0, 0, "", "", TimeSpan.Zero);

        // 1. 找所有 custom-node-list.json
        // envs/<env>/custom_nodes/ComfyUI-Manager/  + 可选 node_db/<dev|forked|legacy|new|tutorial>/
        var files = new List<string>();
        foreach (var comfuiMgr in Directory.EnumerateDirectories(
            envsRoot, "ComfyUI-Manager", SearchOption.AllDirectories))
        {
            // 顶层 custom-node-list.json
            var topJson = Path.Combine(comfuiMgr, "custom-node-list.json");
            if (File.Exists(topJson)) files.Add(topJson);
            // node_db/<分区>/custom-node-list.json
            var nodeDb = Path.Combine(comfuiMgr, "node_db");
            if (Directory.Exists(nodeDb))
            {
                foreach (var sub in Directory.EnumerateDirectories(nodeDb))
                {
                    var subJson = Path.Combine(sub, "custom-node-list.json");
                    if (File.Exists(subJson)) files.Add(subJson);
                }
            }
        }

        // 2. 解析每个文件 entries 数量,找最大
        string? largestFile = null;
        int largestEntries = -1;
        foreach (var f in files)
        {
            int entries = 0;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
                if (doc.RootElement.TryGetProperty("custom_nodes", out var arr) &&
                    arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    entries = arr.GetArrayLength();
                }
            }
            catch { /* 解析失败 = 0 entries */ }
            progress?.Report(new SyncProgress(files.IndexOf(f), f, entries));
            if (entries > largestEntries)
            {
                largestEntries = entries;
                largestFile = f;
            }
        }
        if (largestFile is null || largestEntries <= 0)
        {
            sw.Stop();
            return new SyncResult(files.Count, 0, "", "", sw.Elapsed);
        }

        // 3. 拷到 NodelistDirectory
        Directory.CreateDirectory(destinationDirectory);
        var destFile = Path.Combine(destinationDirectory, "custom-node-list.json");
        File.Copy(largestFile, destFile, overwrite: true);
        sw.Stop();
        return new SyncResult(
            files.Count,
            largestEntries,
            largestFile,
            destFile,
            sw.Elapsed);
    }

    public async Task<SyncResult> SyncAsync(
        string envsRoot,
        string destinationDirectory,
        IProgress<SyncProgress>? progress = null)
        => await Task.Run(() => Sync(envsRoot, destinationDirectory, progress));
}

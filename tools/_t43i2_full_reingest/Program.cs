using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Services;

Console.WriteLine("=== T43i.2 Nodelist 一次性 DB 重灌 ===");
Console.WriteLine("步骤:");
Console.WriteLine("  1. PRAGMA foreign_keys=OFF → DELETE entries + details → PRAGMA foreign_keys=ON");
Console.WriteLine("  2. 重新跑 IngestAsync(forceFull=true):raw JSON 24 字段 + GitHub API metadata 一起入");
Console.WriteLine("  3. verify 字段覆盖率(description/reference/install_type)");
Console.WriteLine();

var dbPath = @"D:\ToolDevelop\ComfyUI\release\staging\ComfyUI Manager\config\state.db";
var jsonFile = @"D:\ToolDevelop\ComfyUI\release\staging\ComfyUI Manager\nodelist\custom-node-list.json";

// Step 1: PRAGMA FK=OFF → DELETE entries + details → PRAGMA FK=ON
Console.WriteLine("[1/3] 清空 nodelist_entries + nodelist_details...");
using (var conn = new SqliteConnection($"Data Source={dbPath}"))
{
    conn.Open();
    using (var pragma = conn.CreateCommand())
    {
        // 关闭 FK 约束,避免 details cascade 路径复杂
        pragma.CommandText = "PRAGMA foreign_keys = OFF";
        pragma.ExecuteNonQuery();
    }
    using var tx = conn.BeginTransaction();
    using (var cmd = conn.CreateCommand())
    {
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM nodelist_entries";
        var n = cmd.ExecuteNonQuery();
        Console.WriteLine($"  DELETE FROM nodelist_entries: {n} rows");
    }
    using (var cmd = conn.CreateCommand())
    {
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM nodelist_details";
        var n = cmd.ExecuteNonQuery();
        Console.WriteLine($"  DELETE FROM nodelist_details: {n} rows");
    }
    tx.Commit();
    using (var pragma = conn.CreateCommand())
    {
        pragma.CommandText = "PRAGMA foreign_keys = ON";
        pragma.ExecuteNonQuery();
    }
}

// Step 2: re-ingest (forceFull=true)
Console.WriteLine();
Console.WriteLine("[2/3] 重新入库 raw JSON + GitHub API metadata...");
Console.WriteLine($"  json: {jsonFile}");

var factory = new SqliteConnectionFactory(dbPath);
var repo = new NodelistRepository(factory);
var sourceConfig = new NodelistSourceConfigRepository(factory);
var cfg = sourceConfig.Get();
var host = cfg.ServerUrl;
var token = cfg.ApiToken;
Console.WriteLine($"  host: {(string.IsNullOrEmpty(host) ? "(none, 走默认 github.com)" : host)}");
Console.WriteLine($"  token: {(string.IsNullOrEmpty(token) ? "(none, 匿名限流)" : $"{token[..Math.Min(8, token.Length)]}***")}");

// NodeRepoQueryService ctor 只收 HttpClient,token 走 FetchRepoMetadataAsync(host, apiKey, owner, repo)
// 由 IngestAsync 内部第 3 个参数 token 透传进去。
var http = new HttpClient();
var queryService = new NodeRepoQueryService(http);

var ing = new NodelistIngestor(repo, queryService, logger: null);

var totalSw = System.Diagnostics.Stopwatch.StartNew();
var progress = new Progress<NodelistIngestor.IngestEvent>(evt =>
{
    switch (evt.Kind)
    {
        case NodelistIngestor.IngestEventKind.Started:
            Console.WriteLine($"  ── 入库启动 — 共 {evt.Total} 条 entry ──");
            break;
        case NodelistIngestor.IngestEventKind.EntryUpserted:
            // 5939 条全打会刷屏;每 200 条打一次
            if (evt.Current % 200 == 0)
                Console.WriteLine($"  [新] entry 进度:{evt.Current}/{evt.Total}");
            break;
        case NodelistIngestor.IngestEventKind.EntrySkipped:
            if (evt.Current % 200 == 0)
                Console.WriteLine($"  [已存在] entry 进度:{evt.Current}/{evt.Total}");
            break;
        case NodelistIngestor.IngestEventKind.EntryFailed:
            Console.WriteLine($"  ✗ entry 失败:{evt.Author}/{evt.RepoName} — {evt.Message}");
            break;
        case NodelistIngestor.IngestEventKind.DetailFetched:
            if (evt.Current % 200 == 0)
                Console.WriteLine($"  详情进度:{evt.Current}/{evt.Total}");
            break;
        case NodelistIngestor.IngestEventKind.DetailFailed:
            Console.WriteLine($"  详情 ✗ {evt.Author}/{evt.RepoName} — {evt.Message}");
            break;
        case NodelistIngestor.IngestEventKind.Completed:
            var r = evt.Result!;
            Console.WriteLine($"  ── 完成 — 扫 {r.EntriesScanned},新增 {r.EntriesNew},跳过 {r.EntriesSkipped}," +
                              $"详情写入 {r.DetailsWritten}(失败 {r.DetailsFailed}),耗时 {r.Elapsed:hh\\:mm\\:ss} ──");
            break;
    }
});

var result = await ing.IngestAsync(
    jsonFile, host, token,
    forceFull: true, progress: progress, ct: CancellationToken.None);

totalSw.Stop();
Console.WriteLine();
Console.WriteLine($"  总耗时:{totalSw.Elapsed:hh\\:mm\\:ss}");

// Step 3: verify coverage
Console.WriteLine();
Console.WriteLine("[3/3] 字段覆盖率:");
using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
{
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT COUNT(*) AS total,
               SUM(CASE WHEN description IS NOT NULL THEN 1 ELSE 0 END) AS with_desc,
               SUM(CASE WHEN reference IS NOT NULL THEN 1 ELSE 0 END) AS with_reference,
               SUM(CASE WHEN install_type IS NOT NULL THEN 1 ELSE 0 END) AS with_install_type
          FROM nodelist_entries";
    using var rdr = cmd.ExecuteReader();
    if (rdr.Read())
    {
        Console.WriteLine($"  total              = {rdr["total"]}");
        Console.WriteLine($"  with_description   = {rdr["with_desc"]}");
        Console.WriteLine($"  with_reference     = {rdr["with_reference"]}");
        Console.WriteLine($"  with_install_type  = {rdr["with_install_type"]}");
    }

    Console.WriteLine();
    Console.WriteLine("--- 抽样 Dr.Lt.Data / ComfyUI-Manager ---");
    cmd.CommandText = @"SELECT author, repo_name, id, reference, description, install_type
                      FROM nodelist_entries
                      WHERE author = 'Dr.Lt.Data' AND repo_name = 'ComfyUI-Manager'";
    using var rdr2 = cmd.ExecuteReader();
    if (rdr2.Read())
    {
        for (int i = 0; i < rdr2.FieldCount; i++)
            Console.WriteLine($"  {rdr2.GetName(i),-15} = {(rdr2.IsDBNull(i) ? "<NULL>" : rdr2.GetValue(i).ToString())}");
    }
    else Console.WriteLine("  (not found)");
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Services;

Console.WriteLine("=== T43h force ingest runner ===");

var dbPath = @"D:\ToolDevelop\ComfyUI\release\staging\ComfyUI Manager\config\state.db";
var factory = new SqliteConnectionFactory(dbPath);
var repo = new NodelistRepository(factory);
var ing = new NodelistIngestor(repo, queryService: null!, logger: null);

var jsonFile = @"D:\ToolDevelop\ComfyUI\release\staging\ComfyUI Manager\nodelist\custom-node-list.json";

Console.WriteLine($"Pre-ingest stats:");
using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
{
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT COUNT(*) AS total,
               SUM(CASE WHEN install_type IS NOT NULL THEN 1 ELSE 0 END) AS with_install_type,
               SUM(CASE WHEN pip_json IS NOT NULL THEN 1 ELSE 0 END) AS with_pip
        FROM nodelist_entries";
    using var rdr = cmd.ExecuteReader();
    if (rdr.Read())
        Console.WriteLine($"  total={rdr["total"]} install_type={rdr["with_install_type"]} pip={rdr["with_pip"]}");
}

await ing.IngestAsync(jsonFile, "github.com", null,
    forceFull: true, progress: null, ct: CancellationToken.None);

Console.WriteLine();
Console.WriteLine($"Post-ingest stats:");
using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
{
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT COUNT(*) AS total,
               SUM(CASE WHEN install_type IS NOT NULL THEN 1 ELSE 0 END) AS with_install_type,
               SUM(CASE WHEN pip_json IS NOT NULL THEN 1 ELSE 0 END) AS with_pip,
               SUM(CASE WHEN tags_json IS NOT NULL THEN 1 ELSE 0 END) AS with_tags
        FROM nodelist_entries";
    using var rdr = cmd.ExecuteReader();
    if (rdr.Read())
        Console.WriteLine($"  total={rdr["total"]} install_type={rdr["with_install_type"]} pip={rdr["with_pip"]} tags={rdr["with_tags"]}");

    // sample ComfyUI-Manager entry
    cmd.CommandText = @"
        SELECT author, repo_name, id, install_type, reference, pip_json
        FROM nodelist_entries
        WHERE author = 'Dr.Lt.Data' AND repo_name = 'ComfyUI-Manager'";
    using var rdr2 = cmd.ExecuteReader();
    if (rdr2.Read())
    {
        Console.WriteLine();
        Console.WriteLine($"Sample Dr.Lt.Data / ComfyUI-Manager:");
        for (int i = 0; i < rdr2.FieldCount; i++)
            Console.WriteLine($"  {rdr2.GetName(i),-15} = {(rdr2.IsDBNull(i) ? "<NULL>" : rdr2.GetValue(i).ToString())}");
    }
}
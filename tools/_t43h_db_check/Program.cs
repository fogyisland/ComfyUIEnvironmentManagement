using System;
using Microsoft.Data.Sqlite;

var dbPath = args.Length > 0 ? args[0] : @"release/staging/ComfyUI Manager/config/state.db";
using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
conn.Open();

// 1) Schema check — v1.0.0.x (2026-09-16) T43i.2.2-fix:删 5 个 ≤0.017% 覆盖率列
//    (apt_dependency/dependencies_json/nickname/last_update/badges_json)→ 17 列。
//    跟 T43i.2.1-fix 删 raw_stars/raw_license 一脉相承,共删 7 列。
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "PRAGMA table_info(nodelist_entries)";
    using var rdr = cmd.ExecuteReader();
    int n = 0;
    Console.WriteLine("=== nodelist_entries columns ===");
    while (rdr.Read())
    {
        n++;
        Console.WriteLine($"  {rdr["name"]} ({rdr["type"]}){(rdr["notnull"].ToString() == "1" ? " NOT NULL" : "")}{(rdr["dflt_value"] is not DBNull ? " DEFAULT " + rdr["dflt_value"] : "")}");
    }
    Console.WriteLine($"Total: {n} columns (expected 17 = 5 original + description + 11 remaining raw JSON)");
}

// 2) Sample 1 entry with all remaining raw JSON columns populated
Console.WriteLine();
Console.WriteLine("=== Sample entry: 0-bill-0 / ComfyUI-BILL-Concept_Isolator-Captioner ===");
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = @"
        SELECT author, repo_name, id, install_type, reference, reference2,
               files_json, pip_json, preemptions_json, nodename_pattern,
               category, tags_json, js_path
        FROM nodelist_entries
        WHERE author = '0-bill-0'
        LIMIT 1";
    using var rdr = cmd.ExecuteReader();
    if (rdr.Read())
    {
        for (int i = 0; i < rdr.FieldCount; i++)
        {
            var name = rdr.GetName(i);
            var val = rdr.IsDBNull(i) ? "<NULL>" : rdr.GetValue(i).ToString();
            var truncated = val?.Length > 120 ? val.Substring(0, 120) + "..." : val;
            Console.WriteLine($"  {name,-25} = {truncated}");
        }
    }
    else Console.WriteLine("  (no 0-bill-0 entry found)");
}

// 3) Count entries + percentage of pip_json populated
Console.WriteLine();
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = @"
        SELECT COUNT(*) AS total,
               SUM(CASE WHEN pip_json IS NOT NULL THEN 1 ELSE 0 END) AS with_pip,
               SUM(CASE WHEN tags_json IS NOT NULL THEN 1 ELSE 0 END) AS with_tags,
               SUM(CASE WHEN install_type IS NOT NULL THEN 1 ELSE 0 END) AS with_install_type
        FROM nodelist_entries";
    using var rdr = cmd.ExecuteReader();
    if (rdr.Read())
    {
        Console.WriteLine($"=== Coverage stats ===");
        Console.WriteLine($"  total entries:        {rdr["total"]}");
        Console.WriteLine($"  with pip_json:        {rdr["with_pip"]}");
        Console.WriteLine($"  with tags_json:       {rdr["with_tags"]}");
        Console.WriteLine($"  with install_type:    {rdr["with_install_type"]}");
    }
}

// 4) Verify nodelist_details table has host + fetched_at + raw_json
Console.WriteLine();
Console.WriteLine("=== nodelist_details columns ===");
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "PRAGMA table_info(nodelist_details)";
    using var rdr = cmd.ExecuteReader();
    int n = 0;
    while (rdr.Read()) { n++; Console.WriteLine($"  {rdr["name"]} ({rdr["type"]})"); }
    Console.WriteLine($"Total: {n}");
}

Console.WriteLine();
Console.WriteLine("=== Sample detail row (host/fetched_at/raw_json presence) ===");
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = @"
        SELECT author, repo_name, host, fetched_at,
               LENGTH(raw_json) AS raw_json_len
        FROM nodelist_details
        WHERE author = '0-bill-0'
        LIMIT 1";
    using var rdr = cmd.ExecuteReader();
    if (rdr.Read())
    {
        for (int i = 0; i < rdr.FieldCount; i++)
            Console.WriteLine($"  {rdr.GetName(i),-20} = {(rdr.IsDBNull(i) ? "<NULL>" : rdr.GetValue(i).ToString())}");
    }
}
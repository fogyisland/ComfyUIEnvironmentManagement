using Microsoft.Data.Sqlite;

var dbPath = args.Length > 0
    ? args[0]
    : @"D:\ToolDevelop\ComfyUI\release\staging\ComfyUI Manager\config\state.db";
using var conn = new SqliteConnection($"Data Source={dbPath}");
conn.Open();

Console.WriteLine($"=== {dbPath} ===");
Console.WriteLine();

// 1. Schema
Console.WriteLine("--- nodelist_entries columns ---");
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "PRAGMA table_info(nodelist_entries)";
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        var name = r.GetString(1);
        var type = r.GetString(2);
        var notnull = r.GetInt32(3);
        Console.WriteLine($"  {name,-22} {type,-10} NOT NULL={notnull}");
    }
}

Console.WriteLine();
Console.WriteLine("--- row counts ---");
foreach (var t in new[] { "nodelist_entries", "nodelist_details" })
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT COUNT(*) FROM {t}";
    Console.WriteLine($"  {t} = {cmd.ExecuteScalar()}");
}

Console.WriteLine();
Console.WriteLine("--- description coverage ---");
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = @"SELECT COUNT(*) AS total,
                               SUM(CASE WHEN description IS NOT NULL THEN 1 ELSE 0 END) AS with_desc,
                               SUM(CASE WHEN reference IS NOT NULL THEN 1 ELSE 0 END) AS with_reference
                          FROM nodelist_entries";
    using var r = cmd.ExecuteReader();
    if (r.Read())
        Console.WriteLine($"  total={r["total"]} with_desc={r["with_desc"]} with_reference={r["with_reference"]}");
}

Console.WriteLine();
Console.WriteLine("--- sample row (ComfyUI-Manager) ---");
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = @"SELECT author, repo_name, id, reference, description, install_type
                      FROM nodelist_entries
                      WHERE author = 'Dr.Lt.Data' AND repo_name = 'ComfyUI-Manager'";
    using var r = cmd.ExecuteReader();
    if (r.Read())
    {
        for (int i = 0; i < r.FieldCount; i++)
            Console.WriteLine($"  {r.GetName(i),-15} = {(r.IsDBNull(i) ? "<NULL>" : r.GetValue(i).ToString())}");
    }
    else Console.WriteLine("  (not found)");
}
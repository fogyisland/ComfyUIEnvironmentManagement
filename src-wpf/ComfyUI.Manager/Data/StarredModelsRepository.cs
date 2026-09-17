using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// v1.0.0.x (2026-09-17) T47:Star 收藏 DAO — model_stars 表 CRUD。
/// source_path PK(Windows 绝对路径,大小写不敏感由 DB collation 处理)。
/// </summary>
public sealed class StarredModelsRepository
{
    private readonly SqliteConnectionFactory _factory;

    public StarredModelsRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public void Add(string sourcePath)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO model_stars (source_path) VALUES ($path)";
        cmd.Parameters.AddWithValue("$path", sourcePath);
        cmd.ExecuteNonQuery();
    }

    public void Remove(string sourcePath)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM model_stars WHERE source_path = $path";
        cmd.Parameters.AddWithValue("$path", sourcePath);
        cmd.ExecuteNonQuery();
    }

    public bool IsStarred(string sourcePath)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM model_stars WHERE source_path = $path LIMIT 1";
        cmd.Parameters.AddWithValue("$path", sourcePath);
        return cmd.ExecuteScalar() != null;
    }

    public IReadOnlyList<string> GetAll()
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT source_path FROM model_stars ORDER BY starred_at DESC";
        using var reader = cmd.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read()) paths.Add(reader.GetString(0));
        return paths;
    }
}
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// v1.0.0.x: 本地模型用户手动覆盖路径表 <c>local_model_overrides</c> 的 CRUD。
/// key = <see cref="ComfyUI.Manager.Models.DownloadedModel.SourceId"/>。
/// 用户在 LocalModelsView 改某张卡的「本地路径」后调 Upsert;Reloadasync 时批量读
/// 一次应用到 LocalModelCard.LocalPathOverride。
/// </summary>
public sealed class LocalModelOverridesRepository
{
    private readonly SqliteConnectionFactory _factory;

    public LocalModelOverridesRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// 一次性读所有 overrides(SourceId → override_path)。Reloadasync 时跟
    /// GroupToCards 的结果合并。空 DB → 空字典(不报错)。
    /// </summary>
    public Dictionary<string, string> LoadAll()
    {
        var dict = new Dictionary<string, string>();
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT source_id, override_path FROM local_model_overrides";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var path = reader.GetString(1);
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(path))
            {
                dict[id] = path;
            }
        }
        return dict;
    }

    /// <summary>
    /// 写入或更新一条覆盖(upsert semantics)。空 override_path = 删除该 SourceId 的覆盖。
    /// </summary>
    public void Upsert(string sourceId, string? overridePath)
    {
        if (string.IsNullOrEmpty(sourceId)) return;
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        if (string.IsNullOrEmpty(overridePath))
        {
            cmd.CommandText = "DELETE FROM local_model_overrides WHERE source_id = @id";
            cmd.Parameters.AddWithValue("@id", sourceId);
        }
        else
        {
            cmd.CommandText = @"
                INSERT INTO local_model_overrides (source_id, override_path, updated_at)
                VALUES (@id, @path, @ts)
                ON CONFLICT(source_id) DO UPDATE SET
                    override_path = @path,
                    updated_at = @ts";
            cmd.Parameters.AddWithValue("@id", sourceId);
            cmd.Parameters.AddWithValue("@path", overridePath);
            cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        }
        cmd.ExecuteNonQuery();
    }

    /// <summary>删除指定 SourceId 的覆盖(恢复 scanner 默认 FullPath)。</summary>
    public void Delete(string sourceId)
    {
        Upsert(sourceId, null);
    }
}
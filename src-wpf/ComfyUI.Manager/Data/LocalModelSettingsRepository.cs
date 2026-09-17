using System;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// v1.0.0.x (2026-09-17) T47:LocalModels UI 状态 K-V 存储 — model_settings 表。
/// 3 个 key:localmodels.view_mode / localmodels.stars_only / localmodels.search。
/// </summary>
public sealed class LocalModelSettingsRepository
{
    private readonly SqliteConnectionFactory _factory;

    public LocalModelSettingsRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public string? Get(string key)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM model_settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public string GetOrDefault(string key, string defaultValue)
    {
        return Get(key) ?? defaultValue;
    }

    public void Set(string key, string value)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO model_settings (key, value) VALUES ($k, $v)
                            ON CONFLICT(key) DO UPDATE SET value = $v, updated_at = datetime('now')";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
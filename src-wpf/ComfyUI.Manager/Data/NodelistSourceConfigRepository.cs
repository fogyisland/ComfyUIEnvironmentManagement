using System;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// v1.0.0.x: nodelist_source_config 单例行仓库。
///
/// 表 schema(id INTEGER PRIMARY KEY CHECK (id = 1),source/server_url/api_token/
/// refresh_versions/refresh_metadata/updated_at)在 SqliteConnectionFactory.InitSchema
/// 里 CREATE TABLE IF NOT EXISTS,SqliteConnectionFactory 同时 INSERT OR IGNORE 一行
/// sentinel(id=1)保证 Get() 永远能读到 1 行(空表返 default Config)。
///
/// 写入:SettingsViewModel.SaveCommand 在 _repo.Save(_settings) 之后调 Upsert —— 把
/// NodelistServerUrl/NodelistApiToken/Refresh* 镜像到 SQLite。失败 try/catch warn
/// 不 throw(.inf 已落盘,SQLite 镜像失败下次启动 Get() 返默认空 host/token,BG job /
/// Scanner 自动 skip,用户手动重保存即可)。
///
/// 读取:NodelistBackgroundJob / NodeListScanner / NodelistViewModel / MainViewModel /
/// SettingsViewModel.ScanNodeListAsync 全部从这里读 host + token(替代原来的
/// _settings.NodelistHostKind/NodelistHostToken/NodelistCustomHostUrl/NodelistCustomToken
/// 四字段)。
/// </summary>
public sealed class NodelistSourceConfigRepository
{
    private readonly SqliteConnectionFactory _factory;

    public NodelistSourceConfigRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public sealed record Config(
        string Source,         // "custom" — 列保留以备 future host kinds(GitHub 已删)
        string ServerUrl,
        string ApiToken,
        bool RefreshVersions,
        bool RefreshMetadata,
        string UpdatedAt);

    /// <summary>
    /// INSERT or UPDATE 单例行 id=1。updated_at 写入 UTC ISO 8601(由 System.Text.Json 用 'o')。
    /// null 入参当空字符串存。
    /// </summary>
    public void Upsert(string serverUrl, string apiToken, bool refreshVersions, bool refreshMetadata)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO nodelist_source_config
                (id, source, server_url, api_token, refresh_versions, refresh_metadata, updated_at)
            VALUES (1, 'custom', @url, @token, @rv, @rm, @ts)
            ON CONFLICT(id) DO UPDATE SET
                source         = excluded.source,
                server_url     = excluded.server_url,
                api_token      = excluded.api_token,
                refresh_versions  = excluded.refresh_versions,
                refresh_metadata  = excluded.refresh_metadata,
                updated_at     = excluded.updated_at";
        cmd.Parameters.AddWithValue("@url", serverUrl ?? "");
        cmd.Parameters.AddWithValue("@token", apiToken ?? "");
        cmd.Parameters.AddWithValue("@rv", refreshVersions ? 1 : 0);
        cmd.Parameters.AddWithValue("@rm", refreshMetadata ? 1 : 0);
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 读单例行。不存在返 default Config("custom", "", "", true, false, "")。
    /// sentinel INSERT OR IGNORE 保证正常情况下永远读到 1 行;只在异常删除行后才走 default。
    /// </summary>
    public Config Get()
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT source, server_url, api_token, refresh_versions, refresh_metadata, updated_at
                            FROM nodelist_source_config WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return new Config("custom", "", "", true, false, "");
        }
        return new Config(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3) != 0,
            reader.GetInt32(4) != 0,
            reader.GetString(5));
    }
}

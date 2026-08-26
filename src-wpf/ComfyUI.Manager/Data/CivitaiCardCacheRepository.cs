using System;
using System.Collections.Generic;
using System.Text.Json;
using ComfyUI.Manager.Models;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// v1.0.0.x: 本地模型「CivitAI 详情缓存」CRUD。
/// key = <see cref="DownloadedModel.SourceId"/>;value = JSON-serialized <see cref="CivitAiDetailDto"/>。
///
/// 写入时机:用户点 toolbar「🔎 CivitAI 查询」命中 picker 后(Modal dialog 内 pick candidate → write back)。
/// 读出时机:LocalModelsViewModel.GroupToCards / ReloadAsync 启动时 LoadAll → 把已查的卡片 hydrate
/// 到 LocalModelCard.MatchedDetail (MatchSource=UserQuery);非空的会盖掉 hash-match chain 自动结果
/// (用户在 toolbar 主动查询的优先级高于 scanner 自动 hash-match)。
///
/// detail_json 是 raw JSON string(不是 blob)以便运维时直接 SQLite shell 可读。System.Text.Json 默认
/// options(全 nullable, snake/camel 跟随全局 JsonOptions)对 CivitAiDetailDto 是 round-trip 安全的
/// (record positional + init-only + immutable collection 都被 STJ 正确反序列化)。
/// </summary>
public sealed class CivitaiCardCacheRepository
{
    private readonly SqliteConnectionFactory _factory;

    public CivitaiCardCacheRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// 一次性读所有缓存(SourceId → CivitAiDetailDto)。GroupToCards 时跟 scanner 结果合并。
    /// 空 DB / 空 source_id / JSON 损坏 → 跳过该行(不抛)。
    /// </summary>
    public Dictionary<string, CivitAiDetailDto> LoadAll()
    {
        var dict = new Dictionary<string, CivitAiDetailDto>(StringComparer.Ordinal);
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT source_id, detail_json FROM civitai_card_cache WHERE source_id <> ''";
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var sourceId = rdr.GetString(0);
            if (string.IsNullOrEmpty(sourceId)) continue;
            var json = rdr.GetString(1);
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                var dto = JsonSerializer.Deserialize<CivitAiDetailDto>(json);
                if (dto is null) continue;
                dict[sourceId] = dto;
            }
            catch (JsonException)
            {
                // 损坏的 JSON 行 → 跳过(不影响其他行)。让 reload 后的新结果自然覆盖。
            }
        }
        return dict;
    }

    /// <summary>
    /// Upsert 一条 (source_id → CivitAiDetailDto)。null/空 sourceId = no-op。
    /// 序列化失败抛(让 VM 走错误日志路径)。
    /// </summary>
    public void Upsert(string sourceId, CivitAiDetailDto detail)
    {
        if (string.IsNullOrEmpty(sourceId)) return;
        if (detail is null) throw new ArgumentNullException(nameof(detail));
        var json = JsonSerializer.Serialize(detail);
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO civitai_card_cache (source_id, detail_json, fetched_at)
            VALUES ($sid, $json, $at)
            ON CONFLICT(source_id) DO UPDATE SET
                detail_json = excluded.detail_json,
                fetched_at  = excluded.fetched_at";
        cmd.Parameters.AddWithValue("$sid", sourceId);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>删除一条 (用户手动清除某张卡的缓存,目前 VM 没暴露入口,留 API 给后续 ClearCacheCommand)。</summary>
    public void Delete(string sourceId)
    {
        if (string.IsNullOrEmpty(sourceId)) return;
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM civitai_card_cache WHERE source_id = $sid";
        cmd.Parameters.AddWithValue("$sid", sourceId);
        cmd.ExecuteNonQuery();
    }
}
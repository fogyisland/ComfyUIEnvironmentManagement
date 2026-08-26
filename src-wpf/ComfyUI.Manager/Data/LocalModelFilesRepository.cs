using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ComfyUI.Manager.Models;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// v1.0.0.x: 本地模型 scan 结果 per-file 缓存 (SQLite local_model_files)。
///
/// 写入时机:LocalModelsViewModel.ReloadAsync 跑完 scanner 后,把 raw DownloadedModel 列表
/// Upsert 到 DB(增量 diff 后只有 new + changed rows 入库,unchanged rows 不重写)。
///
/// 读出时机:LocalModelsViewModel.LoadFromDbAsync 把 DB 列表反序列化成 DownloadedModel,
/// 跟 civitai_card_cache(用户主动查询)合并 hydrate → GroupToCards 重建 card。
///
/// 用户原话「一次刷新就入库,后续不需要直接读,除非自己再次刷新」— 此 repo 是这条线的核心。
/// 增量 diff:ReloadyAsync 拿磁盘 list vs LoadAllPaths(),差集 = 新增 / 删除;
/// 同 path 的 file_mtime 变化 = 改动,要重算 hash + match 并覆写该行。
/// </summary>
public sealed class LocalModelFilesRepository
{
    private readonly SqliteConnectionFactory _factory;

    public LocalModelFilesRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// 全量读所有行 → DownloadedModel list。LoadFromDbAsync 用,带反序列化 matched_detail_json。
    /// 损坏的 JSON 行 → 跳过(matched_detail = null,后续 GroupToCards MatchedDetail 也 null,UI 仍可显示)。
    /// </summary>
    public List<DownloadedModel> LoadAll()
    {
        var list = new List<DownloadedModel>();
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT file_path, source_id, source_version_id, subfolder_name, file_name,
                   title, kind, source, hash, match_source, matched_detail_json,
                   preview_image_path, downloaded_at, file_mtime, scanned_at
            FROM local_model_files
            WHERE file_path <> ''";
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var filePath = rdr.GetString(0);
            if (string.IsNullOrEmpty(filePath)) continue;
            var sourceId = rdr.GetString(1);
            var kindStr = rdr.GetString(6);
            var matchedJson = rdr.IsDBNull(10) ? null : rdr.GetString(10);
            CivitAiDetailDto? matched = null;
            if (!string.IsNullOrWhiteSpace(matchedJson))
            {
                try
                {
                    matched = JsonSerializer.Deserialize<CivitAiDetailDto>(matchedJson);
                }
                catch (JsonException)
                {
                    // 损坏 JSON → 当作 null,UI 显示 status dot 灰。
                }
            }
            var hash = rdr.IsDBNull(8) ? null : rdr.GetString(8);
            var matchSource = rdr.IsDBNull(9) ? null : ParseMatchSource(rdr.GetString(9));
            var previewPath = rdr.IsDBNull(11) ? null : rdr.GetString(11);
            list.Add(new DownloadedModel
            {
                FullPath = filePath,
                SourceId = sourceId,
                SourceVersionId = rdr.GetString(2),
                SubfolderName = rdr.GetString(3),
                Title = rdr.GetString(5),
                Kind = ParseKind(kindStr),
                Source = rdr.GetString(7),
                Hash = hash,
                MatchedDetail = matched,
                MatchSource = matchSource,
                PreviewImagePath = previewPath,
                DownloadedAt = ParseDate(rdr.GetString(12)),
                // FileName 跟 FullPath basename 等价;RawScan 不存它,但 DownloadedModel 字段有。
                // 反序列化时设空字符串 — VM 实际只用 Title 显示。
            });
        }
        return list;
    }

    /// <summary>取所有已缓存的 file_path(用于增量 diff — 跟新 scan 的 file_path 集合比对)。
    /// 只返路径,不反序列化全行,避免每次 reload 都 deserialize 几千个 DownloadedModel。</summary>
    public HashSet<string> LoadAllPaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // Windows path case-insensitive
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_path FROM local_model_files WHERE file_path <> ''";
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var p = rdr.GetString(0);
            if (!string.IsNullOrEmpty(p)) set.Add(p);
        }
        return set;
    }

    /// <summary>取每行 (file_path → file_mtime ISO string)。增量 diff 用:同 path mtime 变 = 改动。</summary>
    public Dictionary<string, string> LoadAllMtimes()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_path, file_mtime FROM local_model_files WHERE file_path <> ''";
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var p = rdr.GetString(0);
            if (string.IsNullOrEmpty(p)) continue;
            dict[p] = rdr.GetString(1);
        }
        return dict;
    }

    /// <summary>
    /// Upsert 一行。RawScan 调 — 增量 diff 后 new + changed rows 都走这里。
    /// matched_detail_json 序列化失败抛(VM 走错误日志路径)。
    /// </summary>
    public void Upsert(DownloadedModel m, string fileMtimeIso)
    {
        if (m is null) throw new ArgumentNullException(nameof(m));
        if (string.IsNullOrEmpty(m.FullPath)) return;
        if (string.IsNullOrEmpty(fileMtimeIso)) return;
        string? matchedJson = null;
        if (m.MatchedDetail is not null)
        {
            matchedJson = JsonSerializer.Serialize(m.MatchedDetail);
        }
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO local_model_files (
                file_path, source_id, source_version_id, subfolder_name, file_name,
                title, kind, source, hash, match_source, matched_detail_json,
                preview_image_path, downloaded_at, file_mtime, scanned_at)
            VALUES (
                $path, $sid, $vid, $sub, $fname,
                $title, $kind, $src, $hash, $msrc, $mdetail,
                $prev, $dl, $mt, $at)
            ON CONFLICT(file_path) DO UPDATE SET
                source_id           = excluded.source_id,
                source_version_id   = excluded.source_version_id,
                subfolder_name      = excluded.subfolder_name,
                file_name           = excluded.file_name,
                title               = excluded.title,
                kind                = excluded.kind,
                source              = excluded.source,
                hash                = excluded.hash,
                match_source        = excluded.match_source,
                matched_detail_json = excluded.matched_detail_json,
                preview_image_path  = excluded.preview_image_path,
                downloaded_at       = excluded.downloaded_at,
                file_mtime          = excluded.file_mtime,
                scanned_at          = excluded.scanned_at";
        cmd.Parameters.AddWithValue("$path", m.FullPath);
        cmd.Parameters.AddWithValue("$sid", m.SourceId ?? "");
        cmd.Parameters.AddWithValue("$vid", m.SourceVersionId ?? "");
        cmd.Parameters.AddWithValue("$sub", m.SubfolderName ?? "");
        cmd.Parameters.AddWithValue("$fname", System.IO.Path.GetFileName(m.FullPath));
        cmd.Parameters.AddWithValue("$title", m.Title ?? "");
        cmd.Parameters.AddWithValue("$kind", m.Kind.ToString());
        cmd.Parameters.AddWithValue("$src", m.Source ?? "");
        cmd.Parameters.AddWithValue("$hash", (object?)m.Hash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$msrc", m.MatchSource?.ToString() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$mdetail", (object?)matchedJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$prev", (object?)m.PreviewImagePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dl", m.DownloadedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$mt", fileMtimeIso);
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    /// <summary>删除一行(磁盘文件消失,relay 增量 diff 时调)。</summary>
    public void Delete(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM local_model_files WHERE file_path = $p";
        cmd.Parameters.AddWithValue("$p", filePath);
        cmd.ExecuteNonQuery();
    }

    /// <summary>批量删除不在新扫描结果中的行 — 增量 diff 时调,把"磁盘已删"的文件清出 DB。
    /// 必须在 connection 内部 — 传 paths 集合一次性 DELETE WHERE NOT IN 比 N 次单删快得多。</summary>
    public int DeleteNotInPaths(ISet<string> currentPaths)
    {
        if (currentPaths is null) throw new ArgumentNullException(nameof(currentPaths));
        // 如果当前磁盘 list 为空 → 删全部(用户清空目录 / 切到空目录)。
        // 这里仍要删 — 不删的话下次 LoadFromDbAsync 还能读到旧 rows,跟磁盘不一致。
        if (currentPaths.Count == 0)
        {
            using var conn = _factory.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM local_model_files";
            return cmd.ExecuteNonQuery();
        }
        using var conn2 = _factory.Open();
        using var cmd2 = conn2.CreateCommand();
        // NOT IN 需要 parameterized list — IN($p0,$p1,...) 动态拼。
        var paramNames = new List<string>();
        var i = 0;
        foreach (var p in currentPaths)
        {
            var name = $"$p{i}";
            paramNames.Add(name);
            cmd2.Parameters.AddWithValue(name, p);
            i++;
        }
        cmd2.CommandText = $"DELETE FROM local_model_files WHERE file_path NOT IN ({string.Join(",", paramNames)})";
        return cmd2.ExecuteNonQuery();
    }

    /// <summary>清空整个表(供未来"重新全量扫描" / settings 改路径时的 nuke 入口用,当前 VM 不调)。</summary>
    public int Clear()
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM local_model_files";
        return cmd.ExecuteNonQuery();
    }

    private static ModelKind ParseKind(string s)
    {
        if (Enum.TryParse<ModelKind>(s, ignoreCase: true, out var k)) return k;
        return ModelKind.Checkpoint;  // fallback — 老 DB 漏类型字段时不让 UI 崩
    }

    private static MatchSource? ParseMatchSource(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (Enum.TryParse<MatchSource>(s, ignoreCase: true, out var ms)) return ms;
        return null;
    }

    private static DateTime ParseDate(string s)
    {
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var dt))
        {
            return dt;
        }
        return DateTime.MinValue;
    }
}
using System;
using System.IO;
using System.Text.Json;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Data;

/// <summary>
/// v0.6.16: 读取/写入 settings.json。路径由 <see cref="LocalDataPaths"/> 提供
/// (默认 &lt;projectRoot&gt;/.manager/settings.json;旧版 %APPDATA%/ComfyUI-Manager/settings.json
/// 由 <see cref="LocalDataMigrationService"/> 一次性迁过来)。
/// 绑定 <see cref="Settings"/> model,WPF UI / load / save 共享同一 shape。
/// </summary>
public class SettingsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    /// <summary>
    /// 生产 DI 入口 —— 接受 <see cref="LocalDataPaths"/> 提供路径。
    /// </summary>
    public SettingsRepository(LocalDataPaths paths)
    {
        _settingsPath = paths.SettingsFile;
    }

    /// <summary>
    /// 测试 seam —— 显式传入路径。生产代码走 LocalDataPaths ctor。
    /// </summary>
    public SettingsRepository(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public string SettingsPath => _settingsPath;

    public virtual Settings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new Settings();
        }

        var json = File.ReadAllText(_settingsPath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Settings();
        }

        // v0.6.15.4: 检测旧 schema 字段 (git_proxy_*) → 迁移到新 schema (http_proxy_*)
        // 并 Save 写回 (持久化迁移)。Pay-for-once: 第一次启动 v0.6.15.4 触发一次,
        // 后续启动走新 schema 没迁移开销。
        var (migratedJson, migrated) = TryMigrateOldGitProxyKeys(json);
        if (migrated)
        {
            // 写回新 schema (旧 key 删, 新 key 落)
            try
            {
                var dir = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_settingsPath, migratedJson);
            }
            catch
            {
                // 迁移失败不影响 Load 行为 (return migrated settings)
            }
            json = migratedJson;
        }

        var s = JsonSerializer.Deserialize<Settings>(json, JsonOptions)
            ?? new Settings();

        // v0.6.9 T2:G5 缺省 Dark;非法 theme_mode(老 settings.json 残留 "system"
        // 之外的不可识别值)normalize 到 "dark",避免下游 ParseThemeMode 失败。
        // "light"/"dark"/"system" 都是合法值,保留原样。
        if (s.ThemeMode != "light" && s.ThemeMode != "dark" && s.ThemeMode != "system")
        {
            s.ThemeMode = "dark";
        }

        // v0.6.22 T7+:per-source UseProxy 一键跟随全局代理。
        // 旧 v0.6.22 默认 false,用户开启全局代理但未触 per-source → AND 失败 → request 直连 →
        // Cloudflare 返 HTML 反爬页(2026-08-20 用户反馈"勾选了使用代理但模型市场没走代理")。
        // 一次性迁移:全局开 + 任意 per-source = false → 提升为 true(opt-out 须用户手动设回 false)。
        if (s.HttpProxyEnabled)
        {
            if (!s.ModelSourceCivitAiUseProxy) s.ModelSourceCivitAiUseProxy = true;
            if (!s.ModelSourceHuggingFaceUseProxy) s.ModelSourceHuggingFaceUseProxy = true;
        }
        return s;
    }

    /// <summary>
    /// v0.6.15.4: 检测 JSON 中是否有 <c>git_proxy_enabled</c> 字段,有则迁移到
    /// <c>http_proxy_*</c>。返回 (新 JSON, 是否迁移)。
    /// </summary>
    private static (string Json, bool Migrated) TryMigrateOldGitProxyKeys(string json)
    {
        if (string.IsNullOrEmpty(json)) return (json, false);
        if (!json.Contains("git_proxy_", StringComparison.Ordinal)) return (json, false);

        try
        {
            // 简化 path: 走 JsonNode (System.Text.Json.Nodes) 文档模型
            // 先读旧值,再删旧 key,再写新 key —— JsonNode indexer 返回 null
            // 表示 key 不存在,所以 partial / corrupt settings.json 不抛。
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();

            // 只在 git_proxy_enabled 存在时触发迁移 —— 避免 partial migration
            // (settings.json 残缺 / 误填了 url+port 但没 enabled 不动)
            if (node["git_proxy_enabled"] is null) return (json, false);

            var enabled = node["git_proxy_enabled"]?.GetValue<bool>() ?? false;
            var url = node["git_proxy_url"]?.GetValue<string>() ?? "";
            var port = node["git_proxy_port"]?.GetValue<int>() ?? 0;

            node.Remove("git_proxy_enabled");
            node.Remove("git_proxy_url");
            node.Remove("git_proxy_port");

            node["http_proxy_enabled"] = enabled;
            node["http_proxy_url"] = url;
            node["http_proxy_port"] = port;
            return (node.ToJsonString(JsonOptions), true);
        }
        catch
        {
            return (json, false);
        }
    }

    public virtual void Save(Settings s)
    {
        var dir = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(s, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }
}

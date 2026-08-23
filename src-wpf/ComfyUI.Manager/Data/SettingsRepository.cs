using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        // v0.6.22++:HttpProxyMode / ModelSourceProxyMode 枚举 → JSON 字符串(否则
        // 反序列化时未知 enum 字段会落到 0/Off,违反"默认 InheritSystem"语义)。
        // 老 JSON 数字格式(0/1/2)也能容错:JsonStringEnumConverter 默认接受数字。
        Converters = { new JsonStringEnumConverter() },
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

        return LoadInternal(json);
    }

    /// <summary>
    /// v1.0.0 (T12):Load 重载,把磁盘上的 raw JSON 文本也一并返出来,
    /// 让调用方能把 JSON 喂给 SettingsDefaults.Apply(s, projectRoot, rawJson)
    /// 触发老字段迁移(template_comfyui_dir 等)。file 不存在或空白 → 返 (new Settings(), null)。
    /// </summary>
    public virtual (Settings Settings, string? RawJson) LoadWithRawJson()
    {
        if (!File.Exists(_settingsPath))
        {
            return (new Settings(), null);
        }

        var json = File.ReadAllText(_settingsPath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return (new Settings(), null);
        }

        return (LoadInternal(json), json);
    }

    private Settings LoadInternal(string json)
    {
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

        // v0.6.22++:迁移 2-bool(http_proxy_enabled + http_proxy_use_system)→ HttpProxyMode enum。
        // 同时迁移 per-source use_proxy bool → ModelSourceProxyMode enum。首次启动 v0.6.22++ 时
        // 触发一次,后续启动走新 schema 没迁移开销。
        var (migratedJsonV22, migratedV22) = TryMigrateOldProxyBoolFields(json);
        if (migratedV22)
        {
            try
            {
                var dir = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_settingsPath, migratedJsonV22);
            }
            catch { }
            json = migratedJsonV22;
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

    /// <summary>
    /// v0.6.22++: 旧 settings.json 含 bool 字段(http_proxy_enabled / http_proxy_use_system +
    /// model_source_{civitai,huggingface}_use_proxy)迁移到新 enum 字段
    /// (http_proxy_mode / model_source_{civitai,huggingface}_proxy_mode)。
    ///
    /// 旧 bool 含义:
    /// - http_proxy_enabled=false → HttpProxyMode.Off
    /// - http_proxy_enabled=true + http_proxy_use_system=true → HttpProxyMode.InheritSystem
    /// - http_proxy_enabled=true + http_proxy_use_system=false → HttpProxyMode.Custom
    /// - model_source_*_use_proxy=true → ModelSourceProxyMode.InheritGlobal
    /// - model_source_*_use_proxy=false → ModelSourceProxyMode.Off
    /// </summary>
    private static (string Json, bool Migrated) TryMigrateOldProxyBoolFields(string json)
    {
        if (string.IsNullOrEmpty(json)) return (json, false);
        var hasOldGlobal = json.Contains("\"http_proxy_enabled\"", StringComparison.Ordinal)
                        || json.Contains("\"http_proxy_use_system\"", StringComparison.Ordinal);
        var hasOldCivit = json.Contains("\"model_source_civitai_use_proxy\"", StringComparison.Ordinal);
        var hasOldHf = json.Contains("\"model_source_huggingface_use_proxy\"", StringComparison.Ordinal);
        if (!hasOldGlobal && !hasOldCivit && !hasOldHf) return (json, false);

        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();

            if (hasOldGlobal)
            {
                var enabled = node["http_proxy_enabled"]?.GetValue<bool>() ?? false;
                var useSystem = node["http_proxy_use_system"]?.GetValue<bool>() ?? false;
                node.Remove("http_proxy_enabled");
                node.Remove("http_proxy_use_system");
                HttpProxyMode mode;
                if (!enabled) mode = HttpProxyMode.Off;
                else if (useSystem) mode = HttpProxyMode.InheritSystem;
                else mode = HttpProxyMode.Custom;
                node["http_proxy_mode"] = mode.ToString();
            }

            if (hasOldCivit)
            {
                var useProxy = node["model_source_civitai_use_proxy"]?.GetValue<bool>() ?? true;
                node.Remove("model_source_civitai_use_proxy");
                node["model_source_civitai_proxy_mode"] = (useProxy
                    ? ModelSourceProxyMode.InheritGlobal
                    : ModelSourceProxyMode.Off).ToString();
            }
            if (hasOldHf)
            {
                var useProxy = node["model_source_huggingface_use_proxy"]?.GetValue<bool>() ?? true;
                node.Remove("model_source_huggingface_use_proxy");
                node["model_source_huggingface_proxy_mode"] = (useProxy
                    ? ModelSourceProxyMode.InheritGlobal
                    : ModelSourceProxyMode.Off).ToString();
            }

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

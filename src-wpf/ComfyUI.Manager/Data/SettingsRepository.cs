using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.Inf;

namespace ComfyUI.Manager.Data;

/// <summary>
/// v0.6.16: 读取/写入 settings.json。路径由 <see cref="LocalDataPaths"/> 提供
/// (默认 &lt;projectRoot&gt;/.manager/settings.json;旧版 %APPDATA%/ComfyUI-Manager/settings.json
/// 由 <see cref="LocalDataMigrationService"/> 一次性迁过来)。
/// 绑定 <see cref="Settings"/> model,WPF UI / load / save 共享同一 shape。
///
/// v1.0.0.1 (settings-to-inf):持久化从 JSON 迁到 INF ——
///   - 主路径:&lt;projectRoot&gt;/config/settings.inf(由 <see cref="InfParser"/>/<see cref="InfWriter"/> + <see cref="InfSettingsSerializer"/> 处理)
///   - 兼容路径:&lt;projectRoot&gt;/.manager/settings.json(老用户数据)
///   - Load 优先 .inf,fallback 老 .json(自动写 .inf + 删 .json 一次性迁移)
///   - Save 只写 .inf
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

    private readonly string _settingsInfPath;
    private readonly string _legacyJsonPath;

    /// <summary>
    /// 生产 DI 入口 —— 接受 <see cref="LocalDataPaths"/> 提供 .inf + .json 路径。
    /// </summary>
    public SettingsRepository(LocalDataPaths paths)
    {
        _settingsInfPath = paths.SettingsInfFile;
        _legacyJsonPath = paths.SettingsFile;
    }

    /// <summary>
    /// 测试 seam —— 显式传入 .inf 路径。legacy .json 路径为 null 时 Load 永不 fallback。
    /// </summary>
    public SettingsRepository(string settingsInfPath)
        : this(settingsInfPath, legacyJsonPath: null)
    {
    }

    /// <summary>
    /// 测试 seam 重载 —— 显式传 .inf + 兼容 .json 路径,方便测迁移。
    /// </summary>
    public SettingsRepository(string settingsInfPath, string? legacyJsonPath)
    {
        if (string.IsNullOrEmpty(settingsInfPath))
            throw new ArgumentException("settingsInfPath 不能为空", nameof(settingsInfPath));
        _settingsInfPath = settingsInfPath;
        _legacyJsonPath = legacyJsonPath ?? string.Empty;
    }

    public string SettingsPath => _settingsInfPath;

    public virtual Settings Load()
    {
        // 1) 主 .inf 路径优先
        if (File.Exists(_settingsInfPath))
        {
            return LoadFromInf(_settingsInfPath);
        }

        // 2) fallback 老 .json:读 → 写 .inf → 删 .json 一次性迁移
        if (!string.IsNullOrEmpty(_legacyJsonPath) && File.Exists(_legacyJsonPath))
        {
            var s = LoadInternalJson(_legacyJsonPath);
            Save(s);
            TryDeleteLegacy();
            return s;
        }

        // 3) 都没有 → 默认 settings
        return new Settings();
    }

    /// <summary>
    /// v1.0.0 (T12):Load 重载,把磁盘上的 raw JSON 文本也一并返出来,
    /// 让调用方能把 JSON 喂给 SettingsDefaults.Apply(s, projectRoot, rawJson)
    /// 触发老字段迁移(template_comfyui_dir 等)。
    ///
    /// v1.0.0.1:从 .inf 路径读时 rawJson = null(无老字段要迁移);
    /// 从 .json legacy fallback 时 rawJson = .json 文本。
    /// </summary>
    public virtual (Settings Settings, string? RawJson) LoadWithRawJson()
    {
        if (File.Exists(_settingsInfPath))
        {
            return (LoadFromInf(_settingsInfPath), null);
        }

        if (!string.IsNullOrEmpty(_legacyJsonPath) && File.Exists(_legacyJsonPath))
        {
            var json = File.ReadAllText(_legacyJsonPath);
            var s = LoadInternalJson(_legacyJsonPath);
            Save(s);
            TryDeleteLegacy();
            return (s, string.IsNullOrWhiteSpace(json) ? null : json);
        }

        return (new Settings(), null);
    }

    private void TryDeleteLegacy()
    {
        try
        {
            if (!string.IsNullOrEmpty(_legacyJsonPath) && File.Exists(_legacyJsonPath))
            {
                File.Delete(_legacyJsonPath);
            }
        }
        catch
        {
            // 删不掉老 JSON 不影响功能 — 下次启动再 fallback 时 INF 已经存在会走主路径。
        }
    }

    private static Settings LoadFromInf(string path)
    {
        var dict = InfParser.ParseFile(path);
        var s = new Settings();
        InfSettingsSerializer.ApplyDictToSettings(s, dict);

        // v0.6.9 T2:G5 缺省 Dark;非法 theme_mode(老 settings.json 残留 "system"
        // 之外的不可识别值)normalize 到 "dark",避免下游 ParseThemeMode 失败。
        if (s.ThemeMode != "light" && s.ThemeMode != "dark" && s.ThemeMode != "system")
        {
            s.ThemeMode = "dark";
        }
        return s;
    }

    private static Settings LoadInternalJson(string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        if (string.IsNullOrWhiteSpace(json)) return new Settings();

        // v0.6.15.4: 检测旧 schema 字段 (git_proxy_*) → 迁移到新 schema (http_proxy_*)
        var (migratedJson, migrated) = TryMigrateOldGitProxyKeys(json);
        if (migrated)
        {
            json = migratedJson;
        }

        // v0.6.22++:迁移 2-bool(http_proxy_enabled + http_proxy_use_system)→ HttpProxyMode enum。
        var (migratedJsonV22, migratedV22) = TryMigrateOldProxyBoolFields(json);
        if (migratedV22)
        {
            json = migratedJsonV22;
        }

        var s = JsonSerializer.Deserialize<Settings>(json, JsonOptions)
            ?? new Settings();

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
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
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
    /// model_source_{civitai,huggingface}_use_proxy)迁移到新 enum 字段。
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
        var dict = InfSettingsSerializer.SerializeToDict(s);
        InfWriter.Write(_settingsInfPath, dict, new[]
        {
            "settings.inf — main user config",
            "Located at <projectRoot>/config/settings.inf",
            "Simple fields: direct key=value. Complex fields (List/Dict): JSON-encoded value.",
        });
    }
}
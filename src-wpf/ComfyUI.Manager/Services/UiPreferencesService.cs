using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.Inf;

namespace ComfyUI.Manager.Services;

/// <summary>
/// UI 偏好持久化:<c>&lt;projectRoot&gt;/config/ui-preferences.inf</c>(v1.0.0.1 settings-to-inf)。
///
/// v0.6.5.21:首次引入,JSON 格式。<see cref="UiPreferences"/> 字段全简单(8 字段),
/// 跟 INF key=value 格式天然契合 —— 不需要 JSON-encode 复杂值。
///
/// v1.0.0.1:持久化从 JSON 迁到 INF ——
///   - 主路径:&lt;projectRoot&gt;/config/ui-preferences.inf
///   - 兼容路径:&lt;projectRoot&gt;/config/ui-preferences.json(老用户数据)
///   - LoadFromFile 优先 .inf,fallback 老 .json(自动写 .inf + 删 .json 一次性迁移)
///
/// 加载失败静默回退 <see cref="UiPreferences"/> 默认值,只走 <see cref="AppLogger"/> 记 ERROR(G17);
/// 加载成功触发 <see cref="Loaded"/> 事件(订阅者:MainWindow应用 Window 尺寸 / MainViewModel 切 LastViewName)。
/// </summary>
public class UiPreferencesService
{
    private readonly AppLogger? _logger;

    public string DefaultPath { get; }

    /// <summary>
    /// 生产入口 —— 接受 <see cref="LocalDataPaths"/> 提供 .inf + 兼容 .json 路径。
    /// DefaultPath 指向 .inf。
    /// </summary>
    public UiPreferencesService(LocalDataPaths paths, AppLogger? logger = null)
    {
        _logger = logger;
        DefaultPath = paths.UiPreferencesInfFile;
        _legacyJsonPath = Path.Combine(paths.ConfigDirectory, "ui-preferences.json");
    }

    /// <summary>
    /// 测试 seam —— 显式传 .inf 路径。legacy .json 路径默认 null(无迁移)。
    /// </summary>
    public UiPreferencesService(string infPath, AppLogger? logger = null)
    {
        _logger = logger;
        DefaultPath = infPath;
        _legacyJsonPath = null;
    }

    /// <summary>兼容老 JSON 路径(从 ctor 注入)。null = 无 legacy 兼容。</summary>
    private readonly string? _legacyJsonPath;

    /// <summary>加载完成后触发(订阅者从 <see cref="UiPreferences"/> 读字段应用)。</summary>
    public event EventHandler<UiPreferences>? Loaded;

    /// <summary>
    /// 从 <paramref name="path"/> 加载。失败(文件不存在 / INF 损坏 / 字段缺失)→ 静默
    /// 回退 <c>new UiPreferences()</c>,触发 <see cref="Loaded"/>(让订阅者照常启动)。
    ///
    /// v1.0.0.1:如果 <paramref name="path"/>(.inf)不存在,且 legacy .json 存在 → 一次性
    /// 读 .json → 写 .inf → 删 .json 迁移。
    /// </summary>
    public UiPreferences LoadFromFile(string path)
    {
        UiPreferences prefs;
        try
        {
            if (File.Exists(path))
            {
                prefs = LoadFromInf(path);
            }
            else if (_legacyJsonPath is not null && File.Exists(_legacyJsonPath))
            {
                // Legacy fallback:读老 JSON → 转 UiPreferences → 写 .inf → 删老 .json
                var json = File.ReadAllText(_legacyJsonPath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    prefs = new UiPreferences();
                }
                else
                {
                    prefs = System.Text.Json.JsonSerializer.Deserialize<UiPreferences>(json, JsonOpts)
                        ?? new UiPreferences();
                }
                SaveToFile(path, prefs);
                TryDeleteLegacy();
            }
            else
            {
                prefs = new UiPreferences();
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("ui-preferences", $"加载失败 path={path}", ex);
            prefs = new UiPreferences();
        }
        Loaded?.Invoke(this, prefs);
        return prefs;
    }

    private void TryDeleteLegacy()
    {
        if (_legacyJsonPath is null) return;
        try
        {
            if (File.Exists(_legacyJsonPath)) File.Delete(_legacyJsonPath);
        }
        catch
        {
            // 删不掉不影响功能 — 下次启动 .inf 存在会走主路径。
        }
    }

    private static UiPreferences LoadFromInf(string path)
    {
        var dict = InfParser.ParseFile(path);
        var prefs = new UiPreferences();

        if (dict.TryGetValue("window_width", out var w) && double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var wd))
            prefs.WindowWidth = wd;
        if (dict.TryGetValue("window_height", out var h) && double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out var ht))
            prefs.WindowHeight = ht;
        if (dict.TryGetValue("window_left", out var l) && double.TryParse(l, NumberStyles.Float, CultureInfo.InvariantCulture, out var lf))
            prefs.WindowLeft = lf;
        if (dict.TryGetValue("window_top", out var t) && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var tf))
            prefs.WindowTop = tf;
        if (dict.TryGetValue("window_maximized", out var m) && bool.TryParse(m, out var mb))
            prefs.WindowMaximized = mb;
        if (dict.TryGetValue("sidebar_visible", out var sv) && bool.TryParse(sv, out var svb))
            prefs.SidebarVisible = svb;
        if (dict.TryGetValue("last_selected_env_id", out var lid))
            prefs.LastSelectedEnvId = string.IsNullOrEmpty(lid) ? null : lid;
        if (dict.TryGetValue("last_view_name", out var lvn))
            prefs.LastViewName = string.IsNullOrEmpty(lvn) ? null : lvn;

        return prefs;
    }

    /// <summary>写 prefs 到 <paramref name="path"/>(父目录不存在则创建,G20)。失败只 log。</summary>
    public void SaveToFile(string path, UiPreferences prefs)
    {
        try
        {
            var entries = new Dictionary<string, string>
            {
                ["window_width"] = prefs.WindowWidth?.ToString(CultureInfo.InvariantCulture) ?? "",
                ["window_height"] = prefs.WindowHeight?.ToString(CultureInfo.InvariantCulture) ?? "",
                ["window_left"] = prefs.WindowLeft?.ToString(CultureInfo.InvariantCulture) ?? "",
                ["window_top"] = prefs.WindowTop?.ToString(CultureInfo.InvariantCulture) ?? "",
                ["window_maximized"] = prefs.WindowMaximized.ToString(),
                ["sidebar_visible"] = prefs.SidebarVisible.ToString(),
                ["last_selected_env_id"] = prefs.LastSelectedEnvId ?? "",
                ["last_view_name"] = prefs.LastViewName ?? "",
            };

            InfWriter.Write(path, entries, new[]
            {
                "ui-preferences.inf — UI state (window, sidebar, last view)",
                "Located at <projectRoot>/config/ui-preferences.inf",
            });
        }
        catch (Exception ex)
        {
            _logger?.Error("ui-preferences", $"保存失败 path={path}", ex);
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}
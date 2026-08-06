using System;
using System.IO;
using System.Text.Json;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// UI 偏好持久化:<c>&lt;projectRoot&gt;/config/ui-preferences.json</c>(G6/G20)。
/// 加载失败静默回退 <see cref="UiPreferences"/> 默认值,只走 <see cref="AppLogger"/> 记 ERROR(G17);
/// 加载成功触发 <see cref="Loaded"/> 事件(订阅者:MainWindow 应用 Window 尺寸 / MainViewModel 切 LastViewName)。
/// </summary>
public class UiPreferencesService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly AppLogger? _logger;

    public string DefaultPath { get; }

    public UiPreferencesService(string projectRoot, AppLogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new ArgumentException("projectRoot 不能为空", nameof(projectRoot));
        _logger = logger;
        DefaultPath = Path.Combine(projectRoot, "config", "ui-preferences.json");
    }

    /// <summary>加载完成后触发(订阅者从 <see cref="UiPreferences"/> 读字段应用)。</summary>
    public event EventHandler<UiPreferences>? Loaded;

    /// <summary>
    /// 从 <paramref name="path"/> 加载。失败(文件不存在 / JSON 损坏 / 字段缺失)→ 静默
    /// 回退 <c>new UiPreferences()</c>,触发 <see cref="Loaded"/>(让订阅者照常启动)。
    /// </summary>
    public UiPreferences LoadFromFile(string path)
    {
        UiPreferences prefs;
        try
        {
            if (!File.Exists(path))
            {
                prefs = new UiPreferences();
            }
            else
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    prefs = new UiPreferences();
                }
                else
                {
                    prefs = JsonSerializer.Deserialize<UiPreferences>(json, JsonOpts)
                        ?? new UiPreferences();
                }
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

    /// <summary>写 prefs 到 <paramref name="path"/>(父目录不存在则创建,G20)。失败只 log。</summary>
    public void SaveToFile(string path, UiPreferences prefs)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(prefs, JsonOpts);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger?.Error("ui-preferences", $"保存失败 path={path}", ex);
        }
    }
}

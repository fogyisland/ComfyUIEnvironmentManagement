using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// v0.6.7.2 (config-editing feature):ComfyUI 启动前写
/// <c>&lt;comfyui-root&gt;/user/default/comfy.settings.json</c> 的 <c>Comfy.Locale</c> 字段。
///
/// ComfyUI 自身不接 --lang CLI 参数 —— UI 语言只能通过修改这个 json(launcher
/// 推荐路径)或运行期 POST /settings/Comfy.Locale API 切。前端加载完会用这个
/// locale 决定显示哪国语言。
///
/// 写入策略 = "保留其它字段,只改 Comfy.Locale" —— 反序列化成
/// <c>Dictionary&lt;string, JsonElement&gt;</c> 后 set 单字段再 roundtrip 序列化,
/// ComfyUI 自己写过的 Comfy.ColorPalette / 其它 key 不会被冲掉。
/// </summary>
public sealed class ComfySettingsWriter
{
    /// <summary>
    /// ComfyUI 官方支持的 UI locale code。其它 code 也接受(原样写入),但 ComfyUI
    /// 不识别就 fallback 英文。
    /// </summary>
    public static readonly IReadOnlyList<string> KnownLocales = new[]
    {
        "zh", "en", "ja", "ko", "ru", "fr", "es",
    };

    /// <summary>
    /// 把 <paramref name="locale"/> 写到
    /// <c>&lt;comfyui-root&gt;/user/default/comfy.settings.json</c> 的
    /// <c>Comfy.Locale</c> 字段。其它已有字段保留。文件 / 目录不存在会自动创建。
    /// </summary>
    /// <param name="comfyuiRoot">ComfyUI 安装根(main.py 所在目录)。</param>
    /// <param name="locale">locale code,如 "zh"、"en"、""。空字符串不写文件(no-op)。</param>
    public void WriteLocale(string comfyuiRoot, string locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return;
        if (string.IsNullOrWhiteSpace(comfyuiRoot))
            throw new ArgumentException("comfyuiRoot 不能为空", nameof(comfyuiRoot));

        var dir = Path.Combine(comfyuiRoot, "user", "default");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "comfy.settings.json");

        // 读取已有内容(若有),作为 Dictionary 保留所有 key
        Dictionary<string, JsonElement> settings;
        if (File.Exists(path))
        {
            try
            {
                var existing = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(existing))
                {
                    settings = new();
                }
                else
                {
                    settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existing)
                        ?? new();
                }
            }
            catch (JsonException)
            {
                // 文件损坏 → 不覆盖,直接写新的(Dictionary 已是空)
                settings = new();
            }
        }
        else
        {
            settings = new();
        }

        settings["Comfy.Locale"] = JsonSerializer.SerializeToElement(locale);

        var json = JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0 sidebar.inf 解析器。
///
/// 文件格式(宽松):
///   - key=value 或 key:value(分隔符任一)
///   - 大小写无关
///   - 前后空白忽略
///   - 空行 / `#` 开头行 = 注释,跳过
///   - 未知 key / 无法解析的行 → 跳过,通过 ParseAndCollectWarnings 输出警告
///
/// 缺省的 key 不出现在 dict 里 — 调用方 ManagerSidebarConfig.IsEnabled 用
/// "missing → 默认 true" 兜底,符合用户期望"没写的=启用"。
/// </summary>
public static class SidebarInfParser
{
    public static IReadOnlyDictionary<MainSection, bool> Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new Dictionary<MainSection, bool>(0);
        using var sr = new StringReader(text);
        return Parse(sr);
    }

    public static IReadOnlyDictionary<MainSection, bool> Parse(TextReader reader)
    {
        var result = new Dictionary<MainSection, bool>();
        if (reader is null) return result;

        string? line;
        int lineNo = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                continue;

            // 找第一个 = 或 :
            int sep = -1;
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (trimmed[i] == '=' || trimmed[i] == ':')
                {
                    sep = i;
                    break;
                }
            }
            if (sep <= 0)
                continue; // 无分隔符或空 key

            var key = trimmed.Substring(0, sep).Trim();
            var rawVal = trimmed.Substring(sep + 1).Trim();
            if (!TryParseBool(rawVal, out var enabled))
                continue;

            if (!Enum.TryParse<MainSection>(key, ignoreCase: true, out var section))
                continue; // 未知 key — 静默丢弃(警告通过 ParseAndCollectWarnings 拿)

            result[section] = enabled;
        }
        return result;
    }

    public static IReadOnlyDictionary<MainSection, bool> ParseAndCollectWarnings(
        string text, out IReadOnlyList<string> warnings)
    {
        var warnList = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            warnings = warnList;
            return new Dictionary<MainSection, bool>(0);
        }
        using var sr = new StringReader(text);

        var result = new Dictionary<MainSection, bool>();
        string? line;
        int lineNo = 0;
        while ((line = sr.ReadLine()) is not null)
        {
            lineNo++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                continue;

            int sep = -1;
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (trimmed[i] == '=' || trimmed[i] == ':')
                {
                    sep = i;
                    break;
                }
            }
            if (sep <= 0)
            {
                warnList.Add($"line {lineNo}: no '=' or ':' separator");
                continue;
            }

            var key = trimmed.Substring(0, sep).Trim();
            var rawVal = trimmed.Substring(sep + 1).Trim();
            if (!TryParseBool(rawVal, out var enabled))
            {
                warnList.Add($"line {lineNo}: value '{rawVal}' not 0/1");
                continue;
            }

            if (!Enum.TryParse<MainSection>(key, ignoreCase: true, out var section))
            {
                warnList.Add($"line {lineNo}: unknown section '{key}'");
                continue;
            }

            result[section] = enabled;
        }

        warnings = warnList;
        return result;
    }

    private static bool TryParseBool(string raw, out bool enabled)
    {
        // 接受:1/0/true/false/yes/no/on/off(大小写无关);常见 ini 写法
        switch (raw.ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                enabled = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
            case "":
                enabled = false;
                return true;
            default:
                enabled = false;
                return false;
        }
    }
}

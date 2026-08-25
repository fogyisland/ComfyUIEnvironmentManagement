using System;
using System.Collections.Generic;
using System.IO;

namespace ComfyUI.Manager.Services.Inf;

/// <summary>
/// 通用 INF 格式解析器。设计目标:settings / UI 偏好 / base-env-profiles 等
/// 用户配置文件统一存 .inf,跟现有 <c>config/sidebar.inf</c> 同风格。
///
/// 格式(宽松,跟 <see cref="SidebarInfParser"/> 对齐):
/// <code>
///   # 注释行 / 空行 跳过
///   key = value          # ' = ' 或 ':' 任一分隔符
///   KEY = value           # 大小写无关,统一归一化为 lowercase key
///   query_sources = [{"name":"..."}]   # 复杂值(List/Dict) JSON-encode 到单值
/// </code>
///
/// 缺省行为:未知行 / 无法解析的行 → 静默跳过(警告通过 <see cref="ParseAndCollectWarnings"/> 收集)。
/// 重复 key → last-wins(覆盖前值,跟常见 INI 解析器一致)。
/// </summary>
public static class InfParser
{
    /// <summary>解析 <paramref name="text"/> 为扁平 dict(string key→raw value,trim 空白)。
    /// key 归一化为 lowercase。空 / null 输入返空 dict。</summary>
    public static IReadOnlyDictionary<string, string> Parse(string? text)
    {
        ParseAndCollectWarnings(text, out var result, out _);
        return result;
    }

    /// <summary>从 <paramref name="path"/> 读文本 + Parse。文件不存在抛 <see cref="FileNotFoundException"/>。</summary>
    public static IReadOnlyDictionary<string, string> ParseFile(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("path 不能为空", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException($"INF file not found: {path}", path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>解析 + 收集警告(无分隔符 / 无法识别的行)。生产代码用 <see cref="Parse"/>。</summary>
    public static IReadOnlyDictionary<string, string> ParseAndCollectWarnings(
        string? text,
        out IReadOnlyDictionary<string, string> result,
        out IReadOnlyList<string> warnings)
    {
        var mutableResult = new Dictionary<string, string>(StringComparer.Ordinal);
        result = mutableResult;
        warnings = Array.Empty<string>();

        if (string.IsNullOrEmpty(text))
            return result;

        var warnList = new List<string>();

        using var sr = new StringReader(text);
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

            if (key.Length == 0)
            {
                warnList.Add($"line {lineNo}: empty key");
                continue;
            }

            // 大小写无关:归一化为 lowercase。string comparer Ordinal 已经能做查找匹配,
            // 但输出 key 统一 lowercase 让调用方 lookup 时不用关心原始大小写。
            var normalizedKey = key.ToLowerInvariant();
            mutableResult[normalizedKey] = rawVal;
        }

        if (warnList.Count > 0) warnings = warnList;
        return result;
    }
}
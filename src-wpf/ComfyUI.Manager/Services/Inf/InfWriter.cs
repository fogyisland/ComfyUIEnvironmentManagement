using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ComfyUI.Manager.Services.Inf;

/// <summary>
/// 通用 INF 格式写入器。跟 <see cref="InfParser"/> 配对使用。
///
/// 输出格式:
/// <code>
///   # &lt;header&gt;
///   # &lt;sub-header&gt;
///
///   key1 = value1
///   key2 = value2
/// </code>
///
/// entries 按 key 排序输出 → git diff 友好(顺序稳定,无关改动只动一行)。
/// 复杂值(List/Dict 等)调用方应预先 JSON-encode 成 string,作为 entry value 写入。
/// </summary>
public static class InfWriter
{
    /// <summary>把 <paramref name="entries"/> 序列化为 INF 文本。</summary>
    /// <param name="headerLines">写在文件顶部的注释行(每个一行,以 '# ' 开头)。null/空 → 不写 header。</param>
    /// <param name="entries">扁平 key→raw value dict。key 假定已 normalize(lowercase,跟 parser 一致);value 不 trim,parser 会 trim。</param>
    public static string ToText(
        IEnumerable<KeyValuePair<string, string>> entries,
        IEnumerable<string>? headerLines = null)
    {
        var sb = new System.Text.StringBuilder();

        if (headerLines is not null)
        {
            foreach (var line in headerLines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                sb.Append("# ");
                sb.AppendLine(line);
            }
            if (sb.Length > 0) sb.AppendLine(); // header 后空一行
        }

        // 排序输出 → 顺序稳定。空 entries 直接返 header。
        foreach (var kvp in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            sb.Append(kvp.Key);
            sb.Append(" = ");
            sb.AppendLine(kvp.Value);
        }

        return sb.ToString();
    }

    /// <summary>把 <paramref name="entries"/> 写到 <paramref name="path"/>。父目录不存在自动创建。</summary>
    public static void Write(
        string path,
        IEnumerable<KeyValuePair<string, string>> entries,
        IEnumerable<string>? headerLines = null)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("path 不能为空", nameof(path));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var text = ToText(entries, headerLines);
        File.WriteAllText(path, text);
    }
}
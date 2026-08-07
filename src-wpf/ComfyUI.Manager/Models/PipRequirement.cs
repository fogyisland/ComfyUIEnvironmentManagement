using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v0.6.7.4: 一个 catalog pip requirement 项。
/// 简化 PEP 440:丢弃 prerelease / epoch / url / extras,只支持 spec 字段
/// (`>= / &lt;= / &gt; / &lt; / == / != / ~=`)。
/// Name = 原始(trim),NormalizedName = lowercase + underscore→dash + dot→dash。
/// Specifier = specifier 子串原样(逗号分隔 AND 关系)。
/// </summary>
public sealed record PipRequirement(string Name, string? Specifier)
{
    public string NormalizedName => Name.Trim().ToLowerInvariant()
        .Replace('_', '-').Replace('.', '-');

    public static IReadOnlyList<PipRequirement> ParseList(IEnumerable<string?> raw)
    {
        var list = new List<PipRequirement>();
        foreach (var s in raw)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            var trimmed = s.Trim();
            var specIdx = FindSpecifierIndex(trimmed);
            if (specIdx < 0)
                list.Add(new PipRequirement(trimmed, null));
            else
                list.Add(new PipRequirement(trimmed[..specIdx], trimmed[specIdx..]));
        }
        return list;
    }

    private static int FindSpecifierIndex(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '>' or '<' or '!' or '=' or '~')
            {
                // 三字符 === 优先
                if (c == '=' && i + 2 < s.Length && s[i + 1] == '=' && s[i + 2] == '=')
                    return i;
                // 双字符 >= <= == !=
                if (i + 1 < s.Length && s[i + 1] == '=')
                    return i;
                // 单字符 > < ! ~(无 =)
                if (c is '>' or '<')
                    return i;
            }
        }
        return -1;
    }
}

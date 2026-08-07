using System;
using System.Linq;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.7.4: 让 catalog requirement(pip 字段)可以跟"已安装版本"比对。
/// 简化 PEP 440:支持 `&gt;= / &lt;= / &gt; / &lt; / == / != / ~=` + 逗号分隔 AND。
/// prerelease(a1/b2)丢弃,不解析 epoch / url / extras。
/// 失败模式(G8):null / 空 / 不可解析版本 返回 false,不抛。
/// </summary>
public static class PipRequirementMatcher
{
    public static bool IsSatisfiedBy(PipRequirement req, string? installedVersion)
    {
        if (string.IsNullOrEmpty(installedVersion)) return false;
        if (req.Specifier is null) return true;
        foreach (var single in req.Specifier.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!SingleMatches(installedVersion, single)) return false;
        }
        return true;
    }

    private static bool SingleMatches(string installed, string single)
    {
        string op = "";
        string ver = single;
        for (int i = 0; i < single.Length; i++)
        {
            if (single[i] is '>' or '<' or '!' or '=' or '~')
            {
                int opLen = 1;
                if (i + 1 < single.Length && single[i + 1] == '=') opLen = 2;
                if (i + 2 < single.Length && single[i] == '=' && single[i + 1] == '=' && single[i + 2] == '=')
                    opLen = 3;
                op = single[..(i + opLen)];
                ver = single[(i + opLen)..];
                break;
            }
        }
        if (!Version.TryParse(NormalizeVersion(ver), out var want)) return false;
        if (!Version.TryParse(NormalizeVersion(installed), out var have)) return false;
        var cmp = have.CompareTo(want);
        return op switch
        {
            ">=" => cmp >= 0,
            "<=" => cmp <= 0,
            ">"  => cmp > 0,
            "<"  => cmp < 0,
            "==" => cmp == 0,
            "!=" => cmp != 0,
            "~=" => TildeEqualsSatisfied(have, want),
            _    => false,
        };
    }

    private static string NormalizeVersion(string v)
    {
        // "1.0" → "1.0.0"; "1.0.0a1" → "1.0.0"(丢 prerelease)
        var dash = v.IndexOfAny(new[] { 'a', 'b', 'r', 'p', '-' });
        var clean = dash >= 0 ? v[..dash] : v;
        var parts = clean.Split('.');
        while (parts.Length < 3) parts = parts.Append("0").ToArray();
        return string.Join('.', parts.Take(3));
    }

    // PEP 440 compatible release: ~=X.Y.Z means >=X.Y.Z, ==X.Y.*
    // For >=X.Y (no patch), means >=X.Y, <(X+1).
    private static bool TildeEqualsSatisfied(Version have, Version want)
    {
        if (have.Major != want.Major) return false;
        // 3-part (X.Y.Z): any 1.Y.* >= Z satisfies
        if (have.Minor == want.Minor)
            return have.CompareTo(want) >= 0;
        return false;
    }
}

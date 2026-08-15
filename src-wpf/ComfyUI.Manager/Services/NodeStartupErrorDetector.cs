using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.15.7:扫描 ComfyUI 启动期 stdout/stderr,识别 custom node 加载失败的行。
///
/// 匹配模式(覆盖 ComfyUI server.py 的 node 加载 + Python ImportError 三种形态):
/// - <c>Failed to import module 'X'</c>  — ComfyUI 自家 server 输出
/// - <c>ImportError: No module named 'X'</c>  — Python 2/3
/// - <c>ModuleNotFoundError: No module named 'X'</c>  — Python 3.6+
/// - <c>Error loading X</c>  — 兜底
///
/// 输出按 PackageName 去重(同一个包两次报错合并成一条,保留第一次出现的 ErrorMessage)。
///
/// Stateless,线程安全 — 可作 singleton 复用。
/// </summary>
public class NodeStartupErrorDetector
{
    private static readonly Regex[] Patterns = new[]
    {
        new Regex(@"Failed to import module ['""]([^'""]+)['""]",
                  RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"ImportError:\s*(?:No module named ['""]([^'""]+)['""]|cannot import name)",
                  RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"ModuleNotFoundError:\s*No module named ['""]([^'""]+)['""]",
                  RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"Error loading ([A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*)",
                  RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    public virtual IReadOnlyList<NodeStartupError> Parse(IEnumerable<string> lines)
    {
        if (lines is null) return System.Array.Empty<NodeStartupError>();
        var seen = new Dictionary<string, NodeStartupError>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in lines)
        {
            if (string.IsNullOrEmpty(rawLine)) continue;
            foreach (var pattern in Patterns)
            {
                var match = pattern.Match(rawLine);
                if (!match.Success) continue;
                // Group 1 = package name (for patterns that have it). Error loading always has it.
                // ImportError / ModuleNotFoundError / FailedToImport 都 Group 1 = package。
                // ImportError with "cannot import name" 没有 group 1 → skip。
                if (match.Groups.Count < 2 || string.IsNullOrEmpty(match.Groups[1].Value))
                {
                    continue;
                }
                var packageName = match.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(packageName)) continue;
                if (seen.ContainsKey(packageName)) break;  // dedup,first wins
                seen[packageName] = new NodeStartupError(packageName, rawLine.Trim());
                break;  // 一行只算一条错误
            }
        }
        return seen.Values.ToList();
    }
}

public sealed record NodeStartupError(string PackageName, string ErrorMessage);
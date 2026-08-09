using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ComfyUI.Manager.Search;

/// <summary>
/// v0.6.9 T6:内存搜索索引 + 5 档评分。
/// <para>
/// 用法:<see cref="GlobalSearchService"/> 在 BuildAsync 一次性灌入所有 entry,
/// 然后 T7 拿 <see cref="SearchIndex"/> 反复 <see cref="Query"/>(G7 键入走内存)。
/// </para>
/// <para>
/// 索引不变量:Add 一旦完成,SearchIndex 即视为 immutable — Query 永远不修改 _entries。
/// T7 持有后不要再调 Add。
/// </para>
/// </summary>
public sealed class SearchIndex
{
    /// <summary>硬上限 — 防止异常数据(env × 100 nodes 几千)把索引撑爆。Add 时静默截断。</summary>
    public const int MaxEntries = 1000;

    private readonly List<SearchEntry> _entries = new();

    /// <summary>当前索引中已收录的 entry 数(可能小于实际 Add 调用次数 — 上限截断后)。</summary>
    public int Count => _entries.Count;

    /// <summary>单条 entry overload。</summary>
    public void Add(SearchEntry entry) => Add(new[] { entry });

    /// <summary>批量灌入。索引到达 <see cref="MaxEntries"/> 后后续条目静默丢弃。</summary>
    public void Add(IEnumerable<SearchEntry> entries)
    {
        foreach (var e in entries)
        {
            if (_entries.Count >= MaxEntries) break;
            _entries.Add(e);
        }
    }

    /// <summary>
    /// 评分查询,返 top <paramref name="maxResults"/>(默认 20)。
    /// <para>
    /// 空 query 返空数组(让调用方决定怎么 fallback — 不让"输入框空"撑出满屏)。
    /// </para>
    /// <para>同步、纯函数、可重入(因为不修改 _entries)。</para>
    /// </summary>
    public IReadOnlyList<SearchResult> Query(string query, int maxResults = 20)
    {
        var q = Normalize(query);
        if (q.Length == 0) return Array.Empty<SearchResult>();
        var qTokens = Tokenize(q);

        var scored = new List<SearchResult>(_entries.Count);
        foreach (var e in _entries)
        {
            var score = ComputeScore(e, qTokens, q);
            if (score > 0)
                scored.Add(new SearchResult(e, score, KindPriority(e.Kind)));
        }

        scored.Sort((a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            if (c != 0) return c;
            c = a.KindPriority.CompareTo(b.KindPriority);
            if (c != 0) return c;
            return a.Entry.DisplayName.Length.CompareTo(b.Entry.DisplayName.Length);
        });

        if (scored.Count > maxResults)
            scored = scored.Take(maxResults).ToList();
        return scored;
    }

    // -- 评分核心 -----------------------------------------------------------

    /// <summary>
    /// 5 档评分,取最高命中档(单调 Max):
    ///   1. exact match       DisplayName normalized == query normalized → 100
    ///   2. token prefix      任一 name token 以 query 起             → 80
    ///   3. any-token prefix  任一 name token 以任一 q token 起        → 60
    ///   4. substring         normalized name Contains query           → 40
    ///   5. subsequence       query 字符按序出现在 name(可跳字符)     → 20
    /// </summary>
    private static int ComputeScore(SearchEntry e, string[] qTokens, string q)
    {
        var nameTokens = e.NormalizedTokens;
        // Name 整体 normalized 形式 — DisplayName 经同一 Normalize 走一遍,
        // 避免 entry 内留的 normalized form 因实现细节差异导致 substring miss。
        var name = ConcatTokens(nameTokens);
        // 注:DisplayName 经 Normalize 后再用 ' ' 拼起来 — 跟 brief §4.6 一致。

        // 1. exact
        if (name == q) return 100;

        int best = 0;

        // 2. token prefix(name 任一 token 以 q 整起)
        foreach (var t in nameTokens)
        {
            if (t.Length > 0 && t.StartsWith(q, StringComparison.Ordinal))
            {
                best = Math.Max(best, 80);
                break;
            }
        }

        // 3. any-token prefix(name 任一 token 以 qTokens 任一 token 起)
        if (best < 60)
        {
            foreach (var nt in nameTokens)
            {
                foreach (var qt in qTokens)
                {
                    if (nt.Length > 0 && qt.Length > 0 && nt.StartsWith(qt, StringComparison.Ordinal))
                    {
                        best = Math.Max(best, 60);
                        break;
                    }
                }
                if (best >= 60) break;
            }
        }

        // 4. substring
        if (name.Contains(q, StringComparison.Ordinal))
            best = Math.Max(best, 40);

        // 5. subsequence
        if (best < 20 && IsSubsequence(q, name))
            best = Math.Max(best, 20);

        return best;
    }

    /// <summary>
    /// needle 字符按序出现在 haystack(允许中间夹其他字符)。
    /// 单字符 needle 永远 true(只要 needle 在 haystack 出现)。
    /// </summary>
    private static bool IsSubsequence(string needle, string haystack)
    {
        if (needle.Length == 0) return true;
        if (haystack.Length < needle.Length) return false;
        int i = 0;
        foreach (var c in haystack)
        {
            if (c == needle[i])
            {
                i++;
                if (i == needle.Length) return true;
            }
        }
        return false;
    }

    private static string ConcatTokens(string[] tokens)
    {
        if (tokens.Length == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < tokens.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(tokens[i]);
        }
        return sb.ToString();
    }

    // -- Kind priority(tie-break,数字小 = 优先)----------------------------

    /// <summary>
    /// Kind 优先级:Command 最优先(用户最常用的快捷入口),
    /// 然后 SettingsSection,然后 Environment,最后 Node(数量最多,放最末)。
    /// </summary>
    internal static int KindPriority(TargetKind k) => k switch
    {
        TargetKind.Command => 1,
        TargetKind.SettingsSection => 2,
        TargetKind.Environment => 3,
        TargetKind.Node => 4,
        _ => 99,
    };

    // -- Normalize / Tokenize(暴露 internal 供 test 直接调用)------------

    /// <summary>
    /// Normalize:小写化 + 替换分隔符为空格。
    /// <para>
    /// 关键细节:ASCII letter 跟非 ASCII letter 之间强制插入空格分隔符 —
    /// 避免"python解释器"被当成单 token。CJK 字符 char.IsLetterOrDigit==true,
    /// 但通过 ASCII↔CJK 边界检测,我们让"python解释器"切成 ["python","解释器"]。
    /// </para>
    /// <para>
    /// 空白 / 下划线 / 连字符 / 其他标点 → 替换为单空格(再 trim)。
    /// </para>
    /// </summary>
    internal static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 4);
        // 初始 lastWasAsciiLetter=true 让开头的 ASCII 不触发边界;
        // 用 lastEmittedSpace 跟踪上一字符是不是空格,避免连续空格。
        bool lastWasAsciiLetter = true;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
            {
                bool isAscii = c < 128;
                if (sb.Length > 0 && isAscii != lastWasAsciiLetter)
                {
                    // ASCII ↔ non-ASCII 边界:插入空格作 token 分隔
                    sb.Append(' ');
                }
                sb.Append(char.ToLowerInvariant(c));
                lastWasAsciiLetter = isAscii;
            }
            else
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != ' ')
                    sb.Append(' ');
                lastWasAsciiLetter = true; // 重置,让下一个 ASCII 不触发边界
            }
        }
        // 去除尾部空格
        while (sb.Length > 0 && sb[sb.Length - 1] == ' ')
            sb.Length--;
        return sb.ToString();
    }

    /// <summary>
    /// 把 normalized 字符串按空格切成 token 数组。
    /// </summary>
    internal static string[] Tokenize(string normalized)
    {
        if (normalized.Length == 0) return Array.Empty<string>();
        var tokens = new List<string>();
        int start = 0;
        for (int i = 0; i < normalized.Length; i++)
        {
            if (normalized[i] == ' ')
            {
                if (i > start)
                    tokens.Add(normalized.Substring(start, i - start));
                start = i + 1;
            }
        }
        if (start < normalized.Length)
            tokens.Add(normalized.Substring(start));
        return tokens.ToArray();
    }

    /// <summary>
    /// 一站式 helper:对原始字符串走 Normalize + Tokenize。
    /// Add SearchEntry 前用这个生成 NormalizedTokens,保证跟 Query 内部用同一套切词规则。
    /// </summary>
    internal static string[] TokenizeRaw(string raw) => Tokenize(Normalize(raw));
}

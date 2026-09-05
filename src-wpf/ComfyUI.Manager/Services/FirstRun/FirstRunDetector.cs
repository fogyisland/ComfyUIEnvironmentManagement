using System;
using System.IO;

namespace ComfyUI.Manager.Services.FirstRun;

/// <summary>
/// v1.0.0.x T41:改用 INI 文件(不依赖隐藏 sentinel) + 简化 <c>executed</c> 单字段控制
/// 首次运行。文件 = <c>config/firstrun.inf</c> 路径(可读 + 可改 + user 控制),
/// 不是早期隐藏 .first-run-complete sentinel file。
///
/// 协议:
/// - 文件缺 → 首次运行(true)
/// - <c>executed=0</c> → 已完成,跳过 wizard(false)
/// - <c>executed=1</c>(或任何非 0 值)→ 强制重新跑 wizard(true)
/// - 文件存在但读不出 <c>executed</c> → 默认按首次运行(true)
/// </summary>
public static class FirstRunDetector
{
    public const string FirstRunInfName = "firstrun.inf";
    public const string ExecutedKey = "executed";
    public const string ExecutedFalse = "0";

    /// <summary>
    /// v1.0.0.x T41:解析 <paramref name="appDataDir"/>/<paramref name="configDirName"/>/firstrun.inf
    /// 单 key <c>executed</c>= 0/1(其它值按已跑过判断)。
    /// 不 try-catch swallow — 之前 catch (Exception) { return true; } 让真 IO 错掩盖 parser 真实行为。
    /// </summary>
    public static bool IsFirstRun(string appDataDir, string configDirName = "config")
    {
        var inf = Path.Combine(appDataDir, configDirName, FirstRunInfName);
        if (!File.Exists(inf)) return true;
        foreach (var line in File.ReadAllLines(inf))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#") || string.IsNullOrEmpty(trimmed)) continue;
            var idx = trimmed.IndexOf('=');
            if (idx <= 0) continue;
            var key = trimmed.Substring(0, idx).Trim();
            var val = trimmed.Substring(idx + 1).Trim();
            if (string.Equals(key, ExecutedKey, StringComparison.Ordinal))
            {
                return !string.Equals(val, ExecutedFalse, StringComparison.Ordinal);
            }
        }
        // 文件存在但没 executed key → 默认按首次跑
        return true;
    }

    /// <summary>
    /// v1.0.0.x T41:写 <c>executed=0</c> 到 <paramref name="appDataDir"/>/<paramref name="configDirName"/>/firstrun.inf,
    /// 同时清理老的 <c>.first-run-complete</c> sentinel(避免双状态)。
    /// </summary>
    public static void MarkComplete(string appDataDir, string configDirName = "config")
    {
        var configDir = Path.Combine(appDataDir, configDirName);
        Directory.CreateDirectory(configDir);
        var inf = Path.Combine(configDir, FirstRunInfName);
        // v1.0.0.x T41:clean header + executed key 0 = 已完成
        File.WriteAllText(inf,
            "# ComfyUI Manager first-run marker\n" +
            "# executed=0 -> wizard skipped (already ran)\n" +
            "# executed=1 -> wizard shown (force re-run)\n" +
            "# delete this file to re-trigger wizard on next launch\n" +
            $"{ExecutedKey}={ExecutedFalse}\n");

        // 清理 v0.6 老的 .first-run-complete sentinel(避免文件扩散)
        var oldSentinel = Path.Combine(appDataDir, ".first-run-complete");
        if (File.Exists(oldSentinel))
        {
            try { File.Delete(oldSentinel); } catch { }
        }
    }

    /// <summary>
    /// v1.0.0.x T41:把 executed 字段设成 1 强制下次启动再跑 wizard
    /// (用户在 firstrun.inf 里手动改也行,这是便捷 API)。
    /// </summary>
    public static void ResetToForceRun(string appDataDir, string configDirName = "config")
    {
        var configDir = Path.Combine(appDataDir, configDirName);
        Directory.CreateDirectory(configDir);
        var inf = Path.Combine(configDir, FirstRunInfName);
        File.WriteAllText(inf,
            "# ComfyUI Manager first-run marker\n" +
            "# executed=1 -> wizard shown (force re-run)\n" +
            $"{ExecutedKey}=1\n");
    }
}

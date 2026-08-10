using System.Collections.Generic;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.11++:把 <see cref="Settings.PipMirror"/> (string) + <see cref="Settings.PipMirrorCustomUrl"/>
/// 解析成实际 pip 参数。string → enum 走 explicit map(因为 settings 持久化用
/// snake_case "tsinghua_tuna" 而 enum 是 PascalCase "TsinghuaTuna",不是
/// 1-to-1 case-insensitive match),无效字符串回退 <see cref="PipMirrorKind.Official"/>,
/// 让未来 enum 加新值前已存在的 settings.json 不会崩(G3)。
/// </summary>
public static class PipMirrorResolver
{
    public const string TsinghuaTunaUrl = "https://pypi.tuna.tsinghua.edu.cn/simple";
    public const string AliyunUrl = "https://mirrors.aliyun.com/pypi/simple/";
    public const string USTCUrl = "https://pypi.mirrors.ustc.edu.cn/simple/";

    /// <summary>
    /// 把 settings JSON 字符串(用户视角,snake_case)翻译成 <see cref="PipMirrorKind"/>。
    /// 未知字符串 → Official(向后兼容)。
    /// </summary>
    public static PipMirrorKind ParseKind(string value) => value switch
    {
        "official" => PipMirrorKind.Official,
        "tsinghua_tuna" => PipMirrorKind.TsinghuaTuna,
        "aliyun" => PipMirrorKind.Aliyun,
        "ustc" => PipMirrorKind.USTC,
        "custom" => PipMirrorKind.Custom,
        _ => PipMirrorKind.Official,
    };

    /// <summary>
    /// 根据 <see cref="Settings.PipMirror"/> 解析出 PyPI index URL。
    /// 返回 <c>null</c> 表示"走官方"(不传 <c>--index-url</c>)。
    /// </summary>
    public static string? ResolveIndexUrl(Settings settings)
    {
        if (settings is null) return null;

        var kind = ParseKind(settings.PipMirror);

        return kind switch
        {
            PipMirrorKind.Official => null,
            PipMirrorKind.TsinghuaTuna => TsinghuaTunaUrl,
            PipMirrorKind.Aliyun => AliyunUrl,
            PipMirrorKind.USTC => USTCUrl,
            PipMirrorKind.Custom => string.IsNullOrWhiteSpace(settings.PipMirrorCustomUrl)
                ? null
                : settings.PipMirrorCustomUrl.Trim(),
            _ => null,
        };
    }

    /// <summary>
    /// 把 mirror URL 包装成 pip 参数列表(<c>--index-url &lt;url&gt;</c>)。
    /// <see cref="ResolveIndexUrl"/> 返 <c>null</c> → 返空列表(caller 直接 append)。
    /// </summary>
    public static IReadOnlyList<string> BuildPipArgs(Settings settings)
    {
        var url = ResolveIndexUrl(settings);
        if (url is null) return System.Array.Empty<string>();
        return new[] { "--index-url", url };
    }
}


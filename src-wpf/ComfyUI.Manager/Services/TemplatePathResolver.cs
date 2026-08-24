using System;
using System.IO;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x: 解析 <see cref="TemplateConfig.LocalSourceDir"/> 的相对路径为绝对路径。
///
/// 背景:用户反馈 "下载后的目录必须和设置中的目录一致"。settings.json 存的是相对路径
/// (如 <c>ComfyUI</c>、<c>envTemplates/ComfyUI</c>)做便携 —— 跨机器 clone git 仓库后
/// settings 仍可读。但运行时 <see cref="Environment.CurrentDirectory"/> 可能不同:
/// <list type="bullet">
///   <item>Start Menu / Explorer 双击 → CWD = %USERPROFILE% 或 %WINDIR%</item>
///   <item>Visual Studio F5 → CWD = bin/Debug/net8.0-windows</item>
///   <item>Release exe 双击 → CWD = exe 所在目录</item>
/// </list>
/// 三种场景下同一个 settings.json 解析到不同目录 → git clone 写到 settings 之外的目录,
/// 违反用户的一致性要求。
///
/// 解法(两层):
/// <list type="number">
///   <item>主:用 <see cref="Settings.SystemTemplateLibraryDir"/> 作相对路径的父目录。
///         用户在 Settings 页设的 "系统模板库目录" 就是这个意图 —— 它是用户期望的
///         模板存放根。把 <c>local_source_dir = "ComfyUI"</c> 解析为
///         <c>&lt;system_template_library_dir&gt;/ComfyUI</c>。</item>
///   <item>回退:若 <see cref="Settings.SystemTemplateLibraryDir"/> 为空(用户没配或老
///         settings.json 没这个字段),锚定到 <see cref="AppContext.BaseDirectory"/>
///         (= exe 所在目录,跨启动方式稳定),保留 v1.0.0 早期实现的兜底行为。</item>
/// </list>
/// 绝对路径(用户主动填的)在两条分支都原样返回,允许用户指向任意位置(外部盘等)。
///
/// 调用方:
/// <list type="bullet">
///   <item><see cref="TemplateSourceUpdater.CloneAsync"/> / <c>UpdateAsync</c> —
///         git clone 之前 resolve,保证 clone target = settings 解析结果</item>
///   <item><see cref="EnvCreatorService.CreateAsync"/> — 复制源码之前 resolve</item>
/// </list>
/// </summary>
public static class TemplatePathResolver
{
    /// <summary>
    /// 把 <paramref name="localSourceDir"/> 解析为绝对路径。空串原样返回;
    /// 已经是绝对路径原样返回;相对路径锚定到 <paramref name="basePath"/>(默认
    /// <see cref="AppContext.BaseDirectory"/>)后 <see cref="Path.GetFullPath"/>
    /// 标准化(处理 <c>.</c> / <c>..</c>)。
    ///
    /// 调用方通常传 <see cref="Settings.SystemTemplateLibraryDir"/> 作 basePath:
    /// 用户设了非空值时,所有模板都克隆到该目录下;为空时回退到 BaseDirectory。
    /// </summary>
    public static string Resolve(string? localSourceDir, string? basePath = null)
    {
        if (string.IsNullOrWhiteSpace(localSourceDir)) return localSourceDir ?? "";
        if (Path.IsPathRooted(localSourceDir)) return localSourceDir;
        var anchor = string.IsNullOrWhiteSpace(basePath)
            ? AppContext.BaseDirectory
            : basePath;
        return Path.GetFullPath(Path.Combine(anchor, localSourceDir));
    }
}

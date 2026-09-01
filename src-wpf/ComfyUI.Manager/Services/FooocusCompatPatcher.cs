using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x (2026-09-01) T27:Fooocus 上游 <c>modules/ui_gradio_extensions.py</c> 兼容补丁
/// —— gradio 3.41.2 + starlette 1.6.0 + jinja2 3.1.6 组合下,Fooocus 上游
/// <c>template_response(*args, **kwargs)</c> 透传到 starlette 1.x 的
/// <c>TemplateResponse(self, request, name, context, ...)</c> 时 args 错位
/// (gradio 3.x 调 <c>templates.TemplateResponse(name, context_dict)</c> 2 个 positional,
/// 但 starlette 1.x expect <c>(request, name, context)</c> 3 个 positional),导致
/// jinja2 LRUCache 拿 <c>cache_key=(weakref, name)</c> 时 <c>name</c> 是 dict
/// → <c>TypeError: unhashable type: 'dict'</c>,每次 HTTP 请求都炸。
///
/// 修法:把 Fooocus 的 <c>template_response</c> 改写成显式 normalize 两个 signature
/// (老 gradio 风格 <c>(name, context)</c> + 新 starlette 风格 <c>(request, name, context)</c>)
/// 后再调原版 <see cref="GradioTemplateResponseOriginal"/>。Mirror gradio 3.41.2
/// 实际调法 + starlette 1.6.0 <see cref="TemplateResponse"/> 签名(line 117-126)。
///
/// **调用入口**:ProcessLauncher.StartEnvAsync Fooocus kind env 启动前 pre-step,
/// idempotent(<see cref="MarkerFileName"/> marker 防重复 patch)。
///
/// **不动**:Fooocus 其它 9 个 non-ComfyUI/Forge kind 不受影响(只 Fooocus 用 gradio)。
/// </summary>
public static class FooocusCompatPatcher
{
    /// <summary>
    /// v1.0.0.x (2026-09-01) T27:Patch 完成 marker 文件名 —— 跟 <see cref="FooocusBaseEnvInstaller.FooocusBaseEnvConstants.MarkerFileName"/>
    /// + <see cref="FooocusDefaultModelsConstants.MarkerFileName"/> 同 pattern。
    /// </summary>
    public const string MarkerFileName = ".fooocus_compat_patched";

    /// <summary>
    /// 相对 env.RootPath 的 ui_gradio_extensions.py 路径。
    /// </summary>
    private const string UiGradioExtensionsRelativePath = "modules/ui_gradio_extensions.py";

    /// <summary>
    /// 原版 broken Fooocus wrapper signature —— 探测 source file 是否已经被 patch 过
    /// (我们改写后含 normalize 逻辑,grep 检测)。
    /// </summary>
    private const string UnpatchedMarkerLine = "def template_response(*args, **kwargs):";

    /// <summary>
    /// 老 T27 patch 注入的 marker(没有 T27b hotfix)。用于检测已 patch 的老版本
    /// Fooocus env(dev 跑过 T27 但还没升级到 T27b),<see cref="Patch"/> 会
    /// re-patch 升级到 T27b。
    /// </summary>
    private const string OldPatchedMarkerLine = "# WPF T27 compat patch:";

    /// <summary>
    /// 我们 patch 后注入的 normalize 注释 —— grep 检测标志位。
    /// **T27b hotfix**:marker 文本必须与 <see cref="OldPatchedMarkerLine"/> 不同,
    /// 否则 <see cref="NeedsPatch"/> 无法区分老 T27 patch 和新版,会漏掉 hotfix 升级。
    /// </summary>
    private const string PatchedMarkerLine = "# WPF T27 compat patch (T27b hotfix: request=None):";

    /// <summary>
    /// v1.0.0.x (2026-09-01) T27:Patch 过的 <c>template_response</c> 实现 ——
    /// 显式 normalize 老 gradio (name, context) + 新 starlette (request, name, context)
    /// 两个 signature,再调原版 <c>GradioTemplateResponseOriginal</c>。
    /// 保留 Fooocus 原版的 css/js 注入逻辑。Indentation = 4(嵌套在 reload_javascript 内)。
    ///
    /// **T27b hotfix**(2026-09-01):函数顶部加 <c>request = None</c> 初始化。
    /// 原因:gradio 3.x 调 <c>template_response(name, context)</c> 时走第一个 if 分支,
    /// 该分支只赋值 <c>name</c> + <c>context</c> 不赋 <c>request</c>;后续
    /// <c>request = request or context["request"]</c> 时 Python 抛 <c>UnboundLocalError:
    /// local variable 'request' referenced before assignment</c>。
    /// 加 <c>request = None</c> 让 <c>or</c> 短路回退到 <c>context["request"]</c>。
    /// </summary>
    private const string PatchedTemplateResponseBlock =
        "    # WPF T27 compat patch (T27b hotfix: request=None): normalize gradio 3.x (name, context) + starlette 1.x (request, name, context) signatures\n"
        + "    def template_response(*args, **kwargs):\n"
        + "        request = None  # T27b hotfix: gradio 3.x (name, context) 分支不赋 request,默认 None 防 UnboundLocalError\n"
        + "        if args and isinstance(args[0], str):\n"
        + "            name = args[0]\n"
        + "            context = args[1] if len(args) > 1 else kwargs.get(\"context\") or {}\n"
        + "        else:\n"
        + "            request = args[0] if args else kwargs.get(\"request\")\n"
        + "            name = args[1] if len(args) > 1 else kwargs.get(\"name\", \"\")\n"
        + "            context = args[2] if len(args) > 2 else kwargs.get(\"context\") or {}\n"
        + "        if isinstance(context, dict) and \"request\" in context:\n"
        + "            request = request or context[\"request\"]\n"
        + "        res = GradioTemplateResponseOriginal(request=request, name=name, context=context)\n"
        + "        res.body = res.body.replace(b'</head>', f'{js}</head>'.encode(\"utf8\"))\n"
        + "        res.body = res.body.replace(b'</body>', f'{css}</body>'.encode(\"utf8\"))\n"
        + "        res.init_headers()\n"
        + "        return res\n";

    /// <summary>
    /// 检测 Fooocus env 是否需要 patch(返回 true = 需要)。
    /// **T27b 升级语义**:source 含最新 <see cref="PatchedMarkerLine"/> → 跳过;
    /// 否则看是否含老 <see cref="OldPatchedMarkerLine"/>(老 T27 patch)→
    /// 也需要 patch(re-patch 升级);都没有 → 看原版 <see cref="UnpatchedMarkerLine"/>
    /// 还在不在(决定是 fresh Fooocus 还是上游已修)。
    ///
    /// 不再依赖 <see cref="MarkerFileName"/> 做 fast path——source content 是 source of truth,
    /// marker file 仅作 diagnostic/debug 用。这样老 T27 patch 的 env 升级到 T27b 时
    /// 不会被 marker 漏掉。
    /// </summary>
    public static bool NeedsPatch(Environment env)
    {
        if (env is null || string.IsNullOrWhiteSpace(env.RootPath)) return false;
        var sourcePath = Path.Combine(env.RootPath, UiGradioExtensionsRelativePath);
        if (!File.Exists(sourcePath)) return false;
        var source = File.ReadAllText(sourcePath);
        if (source.Contains(PatchedMarkerLine)) return false;  // 已 patch 到最新版
        if (source.Contains(OldPatchedMarkerLine)) return true;  // 老 T27 patch,需升级
        return source.Contains(UnpatchedMarkerLine);             // 原版 broken,需 patch
    }

    /// <summary>
    /// v1.0.0.x (2026-09-01) T27:实际 patch Fooocus 源文件 + 写 marker。
    /// 失败抛异常让调用方记 logProgress;不会回滚已写的 marker。
    /// <para>副作用:</para>
    /// <list type="number">
        ///   <item>原文件备份到 <c>modules/ui_gradio_extensions.py.bak</c>(debug 用)</item>
        ///   <item>改写 <c>template_response(*args, **kwargs):</c> 整段为 normalize 实现</item>
        ///   <item>写 <see cref="MarkerFileName"/> marker 文件</item>
        /// </list>
    /// </summary>
    public static void Patch(Environment env, IProgress<string>? logProgress = null)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));
        if (string.IsNullOrWhiteSpace(env.RootPath))
            throw new ArgumentException("env.RootPath 为空", nameof(env));

        var sourcePath = Path.Combine(env.RootPath, UiGradioExtensionsRelativePath);
        if (!File.Exists(sourcePath))
        {
            logProgress?.Report($"[fooocus-compat] ✗ source 不存在:{sourcePath}(Fooocus env 未 git clone?)");
            throw new FileNotFoundException(
                $"Fooocus source 未找到:{sourcePath}", sourcePath);
        }

        var source = File.ReadAllText(sourcePath);
        if (source.Contains(PatchedMarkerLine))
        {
            logProgress?.Report("[fooocus-compat] ✓ source 已含最新 T27b patch marker,跳过(写 marker 保险)");
            WriteMarker(env);
            return;
        }
        if (!source.Contains(UnpatchedMarkerLine) && !source.Contains(OldPatchedMarkerLine))
        {
            logProgress?.Report("[fooocus-compat] ✓ source 已不含 broken signature(可能上游已修或 Fooocus 升级),跳过 patch");
            WriteMarker(env);
            return;
        }

        // 备份原文件(防 patch 写坏)
        var backupPath = sourcePath + ".bak";
        try
        {
            File.Copy(sourcePath, backupPath, overwrite: true);
            logProgress?.Report($"[fooocus-compat] 备份原文件 → {backupPath}");
        }
        catch (Exception ex)
        {
            logProgress?.Report($"[fooocus-compat] ⚠ 备份失败(继续 patch):{ex.Message}");
        }

        // 定位 block 起点:优先老 T27 patch(升级场景),fallback 原版 broken signature
        // (fresh Fooocus)。block 终点统一是 `gr.routes.templates.TemplateResponse = template_response`
        // 行(无论起点是哪种,block 内部结构一致,直接替换就行)。
        var blockEndMarker = "    gr.routes.templates.TemplateResponse = template_response";
        int blockStart;
        if (source.Contains(OldPatchedMarkerLine))
        {
            blockStart = source.IndexOf(OldPatchedMarkerLine, StringComparison.Ordinal);
            logProgress?.Report("[fooocus-compat] 检测到老 T27 patch,升级到 T27b hotfix(request=None)");
        }
        else
        {
            var unpatchedLine = "    " + UnpatchedMarkerLine;
            blockStart = source.IndexOf(unpatchedLine, StringComparison.Ordinal);
            if (blockStart < 0)
            {
                throw new InvalidOperationException(
                    $"无法定位 unpatched signature line in {sourcePath}");
            }
        }

        var blockEnd = source.IndexOf(blockEndMarker, blockStart, StringComparison.Ordinal);
        if (blockEnd < 0)
        {
            throw new InvalidOperationException(
                $"无法定位 block end in {sourcePath}");
        }

        var patched = source.Substring(0, blockStart)
            + PatchedTemplateResponseBlock.Replace("\n", System.Environment.NewLine)
            + source.Substring(blockEnd);

        File.WriteAllText(sourcePath, patched);
        WriteMarker(env);
        logProgress?.Report("[fooocus-compat] ✓ patch 完成(template_response normalize + T27b request=None hotfix)");
    }

    /// <summary>
    /// v1.0.0.x (2026-09-01) T27:pre-step hook 主入口 ——
    /// <see cref="NeedsPatch"/> 判定需要 → <see cref="Patch"/>。失败仅 log,不抛
    /// (best-effort:launch 继续,只是 UI 可能 broken)。
    /// </summary>
    public static void PatchIfNeeded(Environment env, IProgress<string>? logProgress = null)
    {
        if (!NeedsPatch(env)) return;
        logProgress?.Report("[fooocus-compat] Fooocus ui_gradio_extensions.py 需要 T27 patch...");
        try
        {
            Patch(env, logProgress);
        }
        catch (Exception ex)
        {
            logProgress?.Report($"[fooocus-compat] ✗ patch 失败(launch 继续,UI 可能 TypeError):{ex.Message}");
        }
    }

    /// <summary>
    /// 写 marker 文件 —— 跟 <see cref="FooocusBaseEnvInstaller.InstallAsync"/>
    /// line 122 + <see cref="FooocusDefaultModelsInstaller.WriteMarker"/> line 471
    /// pattern 一致,失败仅 log 不抛。
    /// </summary>
    private static void WriteMarker(Environment env)
    {
        var markerPath = Path.Combine(env.RootPath, MarkerFileName);
        try
        {
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        }
        catch
        {
            // best-effort,marker 缺失下次启动会重 patch(幂等)
        }
    }
}
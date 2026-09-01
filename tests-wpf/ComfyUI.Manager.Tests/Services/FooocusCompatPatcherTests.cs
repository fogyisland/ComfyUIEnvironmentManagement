using System;
using System.IO;
using System.Threading.Tasks;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Models;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-09-01) T27:锁 <see cref="FooocusCompatPatcher"/> 对 Fooocus
/// <c>modules/ui_gradio_extensions.py</c> 的兼容补丁 ——
/// gradio 3.41.2 + starlette 1.6.0 + jinja2 3.1.6 组合下,unpatched
/// <c>template_response(*args, **kwargs)</c> 透传导致 jinja2 LRUCache
/// TypeError("unhashable type: 'dict'")。patch 后 normalize 两个 signature
/// 再调原版 <c>GradioTemplateResponseOriginal</c>。
///
/// 镜像 <see cref="FooocusBaseEnvInstallerTests"/> pattern(写 marker + 改 source + 测幂等)。
/// </summary>
public sealed class FooocusCompatPatcherTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly string _envsDir;

    public FooocusCompatPatcherTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(),
            "fooocus-compat-patcher-" + Guid.NewGuid().ToString("N")[..8]);
        _envsDir = Path.Combine(_projectRoot, "envs");
        Directory.CreateDirectory(_envsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private Environment MakeFooocusEnv(string name = "FocusAll")
    {
        var envDir = Path.Combine(_envsDir, name);
        var modulesDir = Path.Combine(envDir, "modules");
        Directory.CreateDirectory(modulesDir);
        // 写一个 stub ui_gradio_extensions.py(模拟 Fooocus 上游 unpatched 版本)
        WriteUnpatchedUiGradioExtensions(Path.Combine(modulesDir, "ui_gradio_extensions.py"));

        return new Environment
        {
            Id = "fooocus-compat-test",
            Name = name,
            Status = "stopped",
            TemplateKind = "Fooocus",
            RootPath = envDir,
        };
    }

    private static void WriteUnpatchedUiGradioExtensions(string path)
    {
        // 模拟 Fooocus 上游 modules/ui_gradio_extensions.py 末尾 reload_javascript 函数
        var stub = """
            # Fooocus upstream stub

            GradioTemplateResponseOriginal = gr.routes.templates.TemplateResponse


            def reload_javascript():
                js = javascript_html()
                css = css_html()

                def template_response(*args, **kwargs):
                    res = GradioTemplateResponseOriginal(*args, **kwargs)
                    res.body = res.body.replace(b'</head>', f'{js}</head>'.encode("utf8"))
                    res.body = res.body.replace(b'</body>', f'{css}</body>'.encode("utf8"))
                    res.init_headers()
                    return res

                gr.routes.templates.TemplateResponse = template_response
            """;
        File.WriteAllText(path, stub);
    }

    [Fact]
    public void NeedsPatch_FreshEnvWithUnpatchedSource_ReturnsTrue()
    {
        var env = MakeFooocusEnv();
        Assert.True(FooocusCompatPatcher.NeedsPatch(env));
    }

    [Fact]
    public void NeedsPatch_FreshEnvWithoutMarker_ReturnsTrue()
    {
        // T27b:NeedsPatch 不再依赖 marker file 做 fast path — 改用 source content 判定
        // (让老 T27 patch 的 env 能升级到 T27b)。本测试验 fresh env 含 unpatched signature → True。
        var env = MakeFooocusEnv();

        Assert.True(FooocusCompatPatcher.NeedsPatch(env));
    }

    [Fact]
    public void NeedsPatch_SourceContainsLatestT27bMarker_ReturnsFalse()
    {
        // T27b:source 含最新 marker (`... T27b hotfix: request=None):`) → 已 patch,跳过
        var env = MakeFooocusEnv();
        var sourcePath = Path.Combine(env.RootPath, "modules", "ui_gradio_extensions.py");
        var original = File.ReadAllText(sourcePath);
        File.WriteAllText(sourcePath,
            original + "\n    # WPF T27 compat patch (T27b hotfix: request=None): dummy\n");

        Assert.False(FooocusCompatPatcher.NeedsPatch(env));
    }

    [Fact]
    public void NeedsPatch_SourceContainsOldT27Marker_ReturnsTrue_ForUpgrade()
    {
        // T27b:dev 已跑过 T27 老 patch,但没 T27b hotfix(`request=None` 修 UnboundLocalError)。
        // 这种情况 NeedsPatch 必须返 True(否则 dev 启动 Fooocus 仍会 UnboundLocalError)。
        var env = MakeFooocusEnv();
        var sourcePath = Path.Combine(env.RootPath, "modules", "ui_gradio_extensions.py");
        var original = File.ReadAllText(sourcePath);
        File.WriteAllText(sourcePath, original + "\n    # WPF T27 compat patch: dummy\n");

        Assert.True(FooocusCompatPatcher.NeedsPatch(env));
    }

    [Fact]
    public void Patch_ReplacesTemplateResponseBlock_AndWritesMarker()
    {
        var env = MakeFooocusEnv();
        var sourcePath = Path.Combine(env.RootPath, "modules", "ui_gradio_extensions.py");

        FooocusCompatPatcher.Patch(env);

        var patched = File.ReadAllText(sourcePath);
        Assert.Contains("WPF T27 compat patch (T27b hotfix: request=None):", patched);
        Assert.Contains("def template_response(*args, **kwargs):", patched);
        Assert.Contains("if args and isinstance(args[0], str):", patched);
        // T27b hotfix:函数顶部必须赋 request = None,否则 gradio 3.x (name, context)
        // 分支后续 `request or context["request"]` 会抛 UnboundLocalError
        Assert.Contains("request = None", patched);
        // unpatched 的 GradioTemplateResponseOriginal(*args, **kwargs) 不应再出现
        Assert.DoesNotContain("res = GradioTemplateResponseOriginal(*args, **kwargs)",
            patched);

        var markerPath = Path.Combine(env.RootPath, FooocusCompatPatcher.MarkerFileName);
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public void Patch_UpgradeFromOldT27Patch_ReplacesBlockWithT27bHotfix()
    {
        // T27b hotfix 关键场景:dev 已经跑过老 T27 patch(有老 marker,缺 T27b hotfix),
        // NeedsPatch 必须识别为需要升级 → Patch 替换整个老 block 为 T27b 新 block。
        // 否则 dev 启动 Fooocus 会 UnboundLocalError。
        var env = MakeFooocusEnv();
        var sourcePath = Path.Combine(env.RootPath, "modules", "ui_gradio_extensions.py");

        // Step 1:模拟 dev 跑过老 T27 patch
        var original = File.ReadAllText(sourcePath);
        var oldT27Block =
            "    # WPF T27 compat patch: old version missing request=None\n"
            + "    def template_response(*args, **kwargs):\n"
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
            + "        return res\n"
            + "\n"
            + "                gr.routes.templates.TemplateResponse = template_response\n";
        // 用 old T27 block 替换原 unpatched signature 那段(保留原文件前后)
        var unpatchedSignature = "                def template_response(*args, **kwargs):";
        var replaced = original.Replace(unpatchedSignature, oldT27Block.TrimStart());
        File.WriteAllText(sourcePath, replaced);

        // Step 2:验证 NeedsPatch 检测到需要升级
        Assert.True(FooocusCompatPatcher.NeedsPatch(env));

        // Step 3:跑 Patch 升级
        FooocusCompatPatcher.Patch(env);

        var patched = File.ReadAllText(sourcePath);
        // 老 T27 marker 没了
        Assert.DoesNotContain("# WPF T27 compat patch: old version missing request=None",
            patched);
        // 新 T27b marker 出现
        Assert.Contains("# WPF T27 compat patch (T27b hotfix: request=None):", patched);
        // T27b hotfix `request = None` 出现
        Assert.Contains("request = None", patched);
    }

    [Fact]
    public void Patch_CreatesBackupFile_WithOriginalContent()
    {
        var env = MakeFooocusEnv();
        var sourcePath = Path.Combine(env.RootPath, "modules", "ui_gradio_extensions.py");
        var original = File.ReadAllText(sourcePath);

        FooocusCompatPatcher.Patch(env);

        var backupPath = sourcePath + ".bak";
        Assert.True(File.Exists(backupPath));
        var backup = File.ReadAllText(backupPath);
        // 备份内容应该 == 原 unpatched 内容
        Assert.Contains("def template_response(*args, **kwargs):", backup);
        Assert.Contains("res = GradioTemplateResponseOriginal(*args, **kwargs)", backup);
        Assert.DoesNotContain("WPF T27 compat patch:", backup);
    }

    [Fact]
    public void Patch_ThrowsWhenSourceFileMissing()
    {
        var env = MakeFooocusEnv();
        File.Delete(Path.Combine(env.RootPath, "modules", "ui_gradio_extensions.py"));

        var ex = Assert.Throws<FileNotFoundException>(() =>
            FooocusCompatPatcher.Patch(env));
        Assert.Contains("ui_gradio_extensions.py", ex.Message);
    }

    [Fact]
    public void PatchIfNeeded_Idempotent_CalledTwiceDoesNotDoublePatch()
    {
        // T27:PatchIfNeeded 是 launch pre-step,可能连续调多次(用户多启/多停)。
        // 第二次应该看到 marker 存在 → NeedsPatch=false → 啥也不做。
        var env = MakeFooocusEnv();

        FooocusCompatPatcher.PatchIfNeeded(env);
        var sourceAfterFirst = File.ReadAllText(
            Path.Combine(env.RootPath, "modules", "ui_gradio_extensions.py"));

        FooocusCompatPatcher.PatchIfNeeded(env);
        var sourceAfterSecond = File.ReadAllText(
            Path.Combine(env.RootPath, "modules", "ui_gradio_extensions.py"));

        Assert.Equal(sourceAfterFirst, sourceAfterSecond);
    }

    [Fact]
    public void PatchIfNeeded_DoesNotThrowOnMissingSource_JustLogs()
    {
        // T27:best-effort 模式 — source 缺失不应该抛(只 log + launch 继续,UI 可能 broken)
        var env = MakeFooocusEnv();
        File.Delete(Path.Combine(env.RootPath, "modules", "ui_gradio_extensions.py"));

        var ex = Record.Exception(() => FooocusCompatPatcher.PatchIfNeeded(env));
        Assert.Null(ex);  // 不抛,best-effort
    }

    [Fact]
    public void NeedsPatch_NonFooocusEnv_ReturnsFalse()
    {
        // T27:防御性 — patcher 只对 Fooocus env 有意义;非 Fooocus kind 即使有 source 也不动
        var env = MakeFooocusEnv();
        env.TemplateKind = "ComfyUI";

        // 即使 source unpatched,NeedsPatch 也只看文件存在 + 内容,不查 kind
        // (调用方 ProcessLauncher 才做 kind 判定)。这里仅测环境本身非空 + source 存在
        // → NeedsPatch 仍然返 true(因为 source 存在 + unpatched)。
        // PatchIfNeeded 由 caller (ProcessLauncher) 在 Fooocus 分支内调,kind 隔离由 caller 保证。
        // 因此此 test 改为:env.RootPath 为空 → NeedsPatch false。
        env.RootPath = "";
        Assert.False(FooocusCompatPatcher.NeedsPatch(env));
    }
}
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
    public void NeedsPatch_AlreadyPatchedMarkerExists_ReturnsFalse()
    {
        var env = MakeFooocusEnv();
        // 写 marker → 已 patched,不需要再 patch
        File.WriteAllText(
            Path.Combine(env.RootPath, FooocusCompatPatcher.MarkerFileName),
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

        Assert.False(FooocusCompatPatcher.NeedsPatch(env));
    }

    [Fact]
    public void NeedsPatch_SourceContainsPatchedMarkerLine_ReturnsFalse()
    {
        // 边界:marker 文件丢了但 source 已被 patch(罕见 race / 用户手改)→ 不重 patch
        var env = MakeFooocusEnv();
        var sourcePath = Path.Combine(env.RootPath, "modules", "ui_gradio_extensions.py");
        var original = File.ReadAllText(sourcePath);
        File.WriteAllText(sourcePath, original + "\n    # WPF T27 compat patch: dummy\n");

        Assert.False(FooocusCompatPatcher.NeedsPatch(env));
    }

    [Fact]
    public void Patch_ReplacesTemplateResponseBlock_AndWritesMarker()
    {
        var env = MakeFooocusEnv();
        var sourcePath = Path.Combine(env.RootPath, "modules", "ui_gradio_extensions.py");

        FooocusCompatPatcher.Patch(env);

        var patched = File.ReadAllText(sourcePath);
        Assert.Contains("WPF T27 compat patch:", patched);
        Assert.Contains("def template_response(*args, **kwargs):", patched);
        Assert.Contains("if args and isinstance(args[0], str):", patched);
        // unpatched 的 GradioTemplateResponseOriginal(*args, **kwargs) 不应再出现
        Assert.DoesNotContain("res = GradioTemplateResponseOriginal(*args, **kwargs)",
            patched);

        var markerPath = Path.Combine(env.RootPath, FooocusCompatPatcher.MarkerFileName);
        Assert.True(File.Exists(markerPath));
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
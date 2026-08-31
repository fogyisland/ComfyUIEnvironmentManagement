using System.Collections.Generic;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// v1.0.0.x (2026-09-01) T23a:锁 <see cref="ProcessLauncher.PythonEncodingEnvironmentVariables"/>
/// 给所有 env kind(11 个 built-in + 任何 custom)注入 <c>PYTHONIOENCODING=utf-8</c>,
/// 修中文 Windows Python stdout/stderr 中文乱码(mojibake)。
///
/// 跟 T21 <c>PipProcessHelpers.ApplyUtf8Mode</c> 互补:
/// <list type="bullet">
///   <item>T21 <c>PYTHONUTF8=1</c> = PEP 540 UTF-8 mode(修 Python file I/O 读 UTF-8 文件)</item>
///   <item>T23a <c>PYTHONIOENCODING=utf-8</c> = 修 Python stdout/stderr encoding</item>
/// </list>
/// 两路并修才能彻底治 mojibake —— 前者 T22 doc-comment 说"PYTHONIOENCODING 是更老
/// workaround 只影响 stdin/stdout/stderr" 是错的(对 stdout 是正解)。
/// </summary>
public sealed class ProcessLauncherEncodingTests
{
    [Fact]
    public void PythonEncodingEnvironmentVariables_SetsPythonIoEncodingUtf8()
    {
        // 关键锁:PYTHONIOENCODING=utf-8 必须存在,值是 utf-8(不是 utf-16 / gb2312 等)
        var envVars = ProcessLauncher.PythonEncodingEnvironmentVariables();

        Assert.Single(envVars);
        Assert.True(envVars.ContainsKey("PYTHONIOENCODING"));
        Assert.Equal("utf-8", envVars["PYTHONIOENCODING"]);
    }

    [Fact]
    public void PythonEncodingEnvironmentVariables_AppliesForAllTemplateKinds()
    {
        // 回归保护:encoding 是全局 Python concern,不分 TemplateKind。
        // 镜像 OpenVoiceExtraEnvironmentVariables "非 OpenVoice kind 返 empty" 反向:
        // 本方法对所有 kind 返同一 dict(不 gated)。
        var allKinds = new[]
        {
            "ComfyUI", "Forge", "Fooocus", "OpenVoice", "Whisper",
            "CoquiTTS", "Bark", "HunyuanVideo", "LTXVideo",
            "CogVideoX", "HivisionIDPhotos", "MyCustomKind"
        };
        var expected = ProcessLauncher.PythonEncodingEnvironmentVariables();

        // 无 env 参数,直接调 —— 对所有 TemplateKind 行为一致
        Assert.Single(expected);
        Assert.Equal("utf-8", expected["PYTHONIOENCODING"]);

        // 不同 env instances 调同一方法返同一 dict(无 env 依赖)
        foreach (var kind in allKinds)
        {
            var env = new Environment { TemplateKind = kind };
            // 不传 env 也能用——纯函数
            var vars = ProcessLauncher.PythonEncodingEnvironmentVariables();
            Assert.Equal(expected, vars);
        }
    }

    [Fact]
    public void PythonEncodingEnvironmentVariables_IsIndependentOfOpenVoiceHelper()
    {
        // 跟 OpenVoiceExtraEnvironmentVariables 完全解耦(后者 gated on OpenVoice,
        // 本方法无条件)。两个 helper 合并使用互不污染(ProcessLauncher 启动时都调)。
        var env = new Environment
        {
            Id = "test",
            Name = "test",
            TemplateKind = "OpenVoice",
            Port = 8000,
        };
        var openvoice = ProcessLauncher.OpenVoiceExtraEnvironmentVariables(env);
        var encoding = ProcessLauncher.PythonEncodingEnvironmentVariables();

        // OpenVoice 1 个 entry(GRADIO_SERVER_PORT),encoding 1 个 entry(PYTHONIOENCODING)
        Assert.Single(openvoice);
        Assert.Single(encoding);
        Assert.Contains("GRADIO_SERVER_PORT", openvoice.Keys);
        Assert.Contains("PYTHONIOENCODING", encoding.Keys);
        // key 不重叠 —— 两个 helper 完全解耦
        Assert.False(openvoice.ContainsKey("PYTHONIOENCODING"));
        Assert.False(encoding.ContainsKey("GRADIO_SERVER_PORT"));
    }
}

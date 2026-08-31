using System;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// v1.0.0.x (2026-08-31): 锁 <see cref="ProcessLauncher.OpenVoiceExtraEnvironmentVariables"/>
/// 给 OpenVoice kind 注入 <c>GRADIO_SERVER_PORT</c> env var,Gradio <c>demo.launch()</c>
/// 自动读 — 零 upstream 改动路径(不动 ENVTemplate/OpenVoice/openvoice/openvoice_app.py)。
/// </summary>
public sealed class ProcessLauncherOpenVoiceTests
{
    private static Environment MakeEnv(string kind, int? port)
    {
        // 最小 env 子集 — helper 只读 TemplateKind + Port
        return new Environment
        {
            Id = "test-env-id",
            Name = "test",
            TemplateKind = kind,
            Port = port,
            RootPath = "D:/proj/envs/test",
        };
    }

    [Fact]
    public void OpenVoiceExtraEnvironmentVariables_OpenVoiceKind_SetsGradioServerPort()
    {
        var env = MakeEnv("OpenVoice", 8000);
        var extras = ProcessLauncher.OpenVoiceExtraEnvironmentVariables(env);

        Assert.Single(extras);
        Assert.True(extras.ContainsKey("GRADIO_SERVER_PORT"));
        Assert.Equal("8000", extras["GRADIO_SERVER_PORT"]);
    }

    [Fact]
    public void OpenVoiceExtraEnvironmentVariables_OpenVoiceKind_CustomPort_PropagatesValue()
    {
        var env = MakeEnv("OpenVoice", 17865);
        var extras = ProcessLauncher.OpenVoiceExtraEnvironmentVariables(env);

        Assert.Equal("17865", extras["GRADIO_SERVER_PORT"]);
    }

    [Fact]
    public void OpenVoiceExtraEnvironmentVariables_OpenVoiceKind_NullPort_FallsBackTo8000()
    {
        // 镜像 BuildStartCommand line 878:env.Port == null 兜底 "8000"
        var env = MakeEnv("OpenVoice", null);
        var extras = ProcessLauncher.OpenVoiceExtraEnvironmentVariables(env);

        Assert.Equal("8000", extras["GRADIO_SERVER_PORT"]);
    }

    [Fact]
    public void OpenVoiceExtraEnvironmentVariables_NonOpenVoiceKind_EmptyExtras()
    {
        // 镜像 ForgeExtraEnvironmentVariables 行为 — 只 OpenVoice kind 才注入
        var env = MakeEnv("ComfyUI", 8000);
        var extras = ProcessLauncher.OpenVoiceExtraEnvironmentVariables(env);

        Assert.Empty(extras);
    }

    [Fact]
    public void OpenVoiceExtraEnvironmentVariables_ForgeKind_EmptyExtras()
    {
        // Forge kind 由 ForgeExtraEnvironmentVariables 单独管(SD_WEBUI_RESTARTING),
        // OpenVoice helper 不污染 — 两个 helper 完全解耦。
        var env = MakeEnv("Forge", 7860);
        var extras = ProcessLauncher.OpenVoiceExtraEnvironmentVariables(env);

        Assert.Empty(extras);
    }
}
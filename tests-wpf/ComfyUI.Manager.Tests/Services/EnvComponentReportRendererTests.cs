using System;
using System.Collections.Generic;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// EnvComponentReportRenderer 纯函数测试:验证 HTML 输出结构 + 转义 + 6 阶段渲染。
/// 不依赖 WPF / subprocess,可独立运行。
/// </summary>
public sealed class EnvComponentReportRendererTests
{
    [Fact]
    public void Render_MinimalReport_ProducesValidHtml()
    {
        var report = new EnvComponentReport
        {
            EnvName = "test-env",
            GeneratedAtUtc = new DateTime(2026, 8, 7, 10, 0, 0, DateTimeKind.Utc),
            AppVersion = "0.6.7.0",
            Required = new BedSpec
            {
                ProfileId = "pytorch-2.4.1-cu118-stable",
                TorchVersion = "2.4.1",
                CudaVersion = "cu118",
                CudaLabel = "CUDA 11.8",
                Channel = "stable",
                Packages = new[] { "torch==2.4.1", "torchvision", "torchaudio", "xformers" },
                BedStatus = "done",
            },
            KeyPackages = new[]
            {
                new ActualKeyPackage
                {
                    PackageName = "torch",
                    RequiredVersion = "torch==2.4.1",
                    ActualVersion = "2.4.1",
                    Status = KeyPackageMatchStatus.Match,
                },
            },
        };

        var html = EnvComponentReportRenderer.Render(report);

        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("test-env", html);
        Assert.Contains("pytorch-2.4.1-cu118-stable", html);
        Assert.Contains("✓ MATCH", html);
        Assert.Contains("2.4.1", html);
        Assert.Contains("CUDA 11.8", html);
    }

    [Fact]
    public void Render_RequiredNull_ShowsSkipNotice()
    {
        var report = new EnvComponentReport
        {
            EnvName = "bare-env",
            GeneratedAtUtc = DateTime.UtcNow,
            AppVersion = "0.6.7.0",
            Required = null,
        };

        var html = EnvComponentReportRenderer.Render(report);

        Assert.Contains("阶段 1", html);
        Assert.Contains("跳过对比", html);
        Assert.Contains("BedProfileId", html);
    }

    [Fact]
    public void Render_KeyPackageMismatch_ShowsRedBadge()
    {
        var report = new EnvComponentReport
        {
            EnvName = "mismatch-env",
            GeneratedAtUtc = DateTime.UtcNow,
            AppVersion = "0.6.7.0",
            Required = new BedSpec
            {
                ProfileId = "p1",
                TorchVersion = "2.4.1",
                CudaVersion = "cu118",
                CudaLabel = "CUDA 11.8",
                Channel = "stable",
                Packages = new[] { "torch" },
            },
            KeyPackages = new[]
            {
                new ActualKeyPackage
                {
                    PackageName = "torch",
                    RequiredVersion = "torch==2.4.1",
                    ActualVersion = "2.1.0",
                    Status = KeyPackageMatchStatus.Mismatch,
                },
            },
        };

        var html = EnvComponentReportRenderer.Render(report);

        Assert.Contains("MISMATCH", html);
        Assert.Contains("mismatch", html);
        Assert.Contains("2.1.0", html);
        Assert.Contains("2.4.1", html);
    }

    [Fact]
    public void Render_EscapesAngleBracketsInPaths()
    {
        var report = new EnvComponentReport
        {
            EnvName = "<script>alert(1)</script>",
            GeneratedAtUtc = DateTime.UtcNow,
            AppVersion = "0.6.7.0",
            Metadata = new EnvMetadata
            {
                RootPath = "C:\\fake\\<>path",
                Status = "stopped",
            },
        };

        var html = EnvComponentReportRenderer.Render(report);

        // 不应有未转义的 <script>
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        // 应有转义后的 <script>
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        // path 中的 < > 应被转义
        Assert.Contains("&lt;&gt;path", html);
        Assert.DoesNotContain("C:\\fake\\<>path", html);
    }

    [Fact]
    public void Render_GitNotARepository_ShowsMissingBadge()
    {
        var report = new EnvComponentReport
        {
            EnvName = "no-git-env",
            GeneratedAtUtc = DateTime.UtcNow,
            AppVersion = "0.6.7.0",
            ComfyuiStatus = new GitTargetStatus
            {
                DisplayName = "ComfyUI 源码",
                Path = "C:\\fake\\comfyui",
                State = GitTargetState.NotARepository,
                ErrorMessage = "fatal: not a git repository",
            },
        };

        var html = EnvComponentReportRenderer.Render(report);

        Assert.Contains("不是 git 仓库", html);
        Assert.Contains("not a git repository", html);
        Assert.Contains("阶段 4", html);
        Assert.Contains("ComfyUI 源码状态", html);
    }

    // --- v1.0.0.x (2026-08-29):section heading 按 ComfyuiStatus.DisplayName 派生 ---

    [Theory]
    [InlineData("ComfyUI 源码", "ComfyUI 源码状态")]
    [InlineData("Forge 源码", "Forge 源码状态")]
    [InlineData("OpenVoice 源码", "OpenVoice 源码状态")]
    [InlineData("HunyuanVideo 源码", "HunyuanVideo 源码状态")]
    public void Render_Section4Heading_ReflectsDisplayName(
        string displayName, string expectedHeading)
    {
        // v1.0.0.x:Renderer 不再 hardcode "ComfyUI 源码状态" — 用 ComfyuiStatus.DisplayName
        // 拼出 heading。这样 Builder 按 env.TemplateKind 派生 DisplayName 之后,
        // 标题自动跟随(ComfyUI 兼容、Forge 等多模板正确)。
        var report = new EnvComponentReport
        {
            EnvName = "test",
            GeneratedAtUtc = new DateTime(2026, 8, 7, 10, 0, 0, DateTimeKind.Utc),
            AppVersion = "0.6.7.0",
            ComfyuiStatus = new GitTargetStatus
            {
                DisplayName = displayName,
                Path = "C:\\fake\\source",
                State = GitTargetState.Ok,
                CommitHash = "abc1234",
                Branch = "main",
                LastCommitTimeUtc = DateTime.UtcNow,
            },
        };

        var html = EnvComponentReportRenderer.Render(report);

        Assert.Contains($"阶段 4 — {expectedHeading}", html);
    }
}

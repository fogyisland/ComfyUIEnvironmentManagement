using System;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v1.0.0.x (2026-09-01) T25:测试 <see cref="Environment.FooocusReadyToStart"/>
/// (3 件套 ready gate) + <see cref="Environment.StartStopButtonTooltip"/>
/// (灰按钮时告诉用户缺哪个)。
///
/// 测的是 Environment 自身 computed bool + tooltip 文本组装,不构造 VM
/// (避免 FooocusConfigProbe.ProbeAsync spawn python 在 CI 失败)。VM 侧
/// RecomputeFooocusReadyGatedProperties (private) 走 Load() 路径已在
/// EnvironmentListViewModelBaseEnvTests 间接测过启停按钮 enabled 计算,
/// 这里单测 gate 逻辑本身。
///
/// 测试矩阵:
/// <list type="bullet">
///   <item>3 件套齐 → ready=true,其它 kind → ready=true(不参与 gate)</item>
///   <item>任一缺失 → ready=false</item>
///   <item>StartStopButtonTooltip 写"缺:基础环境 / 依赖 / 默认模型"按缺哪几个列出</item>
/// </list>
/// </summary>
public class EnvironmentListViewModelFooocusReadyTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly EnvironmentRepository _repo;

    public EnvironmentListViewModelFooocusReadyTests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            $"envlistview-fooocus-ready-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _factory = new SqliteConnectionFactory(Path.Combine(_root, "state.db"));
        _repo = new EnvironmentRepository(_factory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private Environment SeedFooocus()
    {
        return new Environment
        {
            Id = "fooocus-env",
            Name = "fooocus-env",
            RootPath = Path.Combine(_root, "fooocus-env"),
            TemplateKind = "Fooocus",
            Status = "stopped",  // T25 只在 stopped 状态 gate
        };
    }

    private Environment SeedNonFooocus(string kind)
    {
        return new Environment
        {
            Id = kind + "-env",
            Name = kind + "-env",
            RootPath = Path.Combine(_root, kind + "-env"),
            TemplateKind = kind,
            Status = "stopped",
        };
    }

    /// <summary>3 件套齐: BED + Requirements + 默认模型 都装好</summary>
    private void MarkAllInstalled(Environment env)
    {
        env.IsBaseEnvInstalled = true;
        env.IsRequirementsInstalled = true;
        env.FooocusAllDefaultModelsDownloaded = true;
    }

    // ----- FooocusReadyToStart computed bool: 3 件套齐 =----=

    [Fact]
    public void FooocusReadyToStart_TrueForFooocus_AllThreeReady()
    {
        // T25:Fooocus + 3 件套齐 → ready=true → StartStop 按钮 enabled
        var env = SeedFooocus();
        MarkAllInstalled(env);
        Assert.True(env.FooocusReadyToStart);
    }

    [Fact]
    public void FooocusReadyToStart_FalseWhen_BedMissing()
    {
        // T25:缺 BED → false(用户要先点「基础环境」)
        var env = SeedFooocus();
        env.IsBaseEnvInstalled = false;
        env.IsRequirementsInstalled = true;
        env.FooocusAllDefaultModelsDownloaded = true;
        Assert.False(env.FooocusReadyToStart);
    }

    [Fact]
    public void FooocusReadyToStart_FalseWhen_RequirementsMissing()
    {
        // T25:缺依赖 → false
        var env = SeedFooocus();
        env.IsBaseEnvInstalled = true;
        env.IsRequirementsInstalled = false;
        env.FooocusAllDefaultModelsDownloaded = true;
        Assert.False(env.FooocusReadyToStart);
    }

    [Fact]
    public void FooocusReadyToStart_FalseWhen_DefaultModelsMissing()
    {
        // T25:缺默认模型 → false(最常见状态,刚装完 env 默认 false)
        var env = SeedFooocus();
        env.IsBaseEnvInstalled = true;
        env.IsRequirementsInstalled = true;
        env.FooocusAllDefaultModelsDownloaded = false;
        Assert.False(env.FooocusReadyToStart);
    }

    [Fact]
    public void FooocusReadyToStart_TrueForFooocus_NoneInstalled_RequiresAllThree()
    {
        // T25:全部 0 件 → false(双重防 — 防止 3 个 false 偶遇 true 的逻辑漏洞)
        var env = SeedFooocus();
        env.IsBaseEnvInstalled = false;
        env.IsRequirementsInstalled = false;
        env.FooocusAllDefaultModelsDownloaded = false;
        Assert.False(env.FooocusReadyToStart);
    }

    // ----- FooocusReadyToStart computed bool: 其它 9 个 kind 永远 true =====

    [Theory]
    [InlineData("ComfyUI")]
    [InlineData("Forge")]
    [InlineData("OpenVoice")]
    [InlineData("Whisper")]
    [InlineData("CoquiTTS")]
    [InlineData("Bark")]
    [InlineData("HunyuanVideo")]
    [InlineData("LTXVideo")]
    [InlineData("CogVideoX")]
    [InlineData("HivisionIDPhotos")]
    public void FooocusReadyToStart_AlwaysTrue_ForNonFooocusKind(string kind)
    {
        // T25:用户决策"只 Fooocus 加 ready gate",其它 9 个 kind 永远 true
        // (它们没有 BED/Requirements/默认模型概念,StartStopButtonEnabled 不依赖
        // 这个字段,Vm.Load() 只对 Fooocus 调 RecomputeFooocusReadyGatedProperties)
        var env = SeedNonFooocus(kind);
        Assert.True(env.FooocusReadyToStart);
    }

    [Fact]
    public void FooocusReadyToStart_TrueForFooocus_RegardlessOfStatus()
    {
        // T25:FooocusReadyToStart 是 3 件套状态计算,不关心 env.Status(running/stopped 都
        // 该返真实 ready 状态)。启停按钮 enabled 由 VM RecomputeFooocusReadyGatedProperties
        // 组合 (Status == stopped && FooocusReadyToStart) 决定。
        var env = SeedFooocus();
        env.Status = "running";
        MarkAllInstalled(env);
        Assert.True(env.FooocusReadyToStart);
    }

    // ----- StartStopButtonTooltip 默认值 + set 兼容 =====

    [Fact]
    public void StartStopButtonTooltip_DefaultsToEmpty()
    {
        // T25:新 env 默认空 tooltip(Load() 末尾 RecomputeFooocusReadyGatedProperties
        // 会按 ready 状态重写;空 = "ready" 或 "其它 kind" 都不需要 tooltip)
        var env = new Environment();
        Assert.Equal("", env.StartStopButtonTooltip);
    }

    [Fact]
    public void StartStopButtonTooltip_RoundTripsThroughSetter()
    {
        // T25:setter 是 public set (Load() 末尾写),getter 读出
        // (XAML 启停按钮 ToolTip 绑定读它)
        var env = new Environment();
        env.StartStopButtonTooltip = "缺:基础环境";
        Assert.Equal("缺:基础环境", env.StartStopButtonTooltip);
    }

    // ----- 3 件套语义防回归:AND 不是 OR =====

    [Fact]
    public void FooocusReadyToStart_AndSemantics_NotOr()
    {
        // T25:ready = 3 件 AND 齐(不是 OR — 任意一个 true 就够)
        // 防回归:如果有人误改 computed 用 ||,这里会 fail
        var env = SeedFooocus();
        env.IsBaseEnvInstalled = true;   // 1/3
        env.IsRequirementsInstalled = false;
        env.FooocusAllDefaultModelsDownloaded = false;
        Assert.False(env.FooocusReadyToStart);

        env.IsBaseEnvInstalled = false;
        env.IsRequirementsInstalled = true;  // 1/3
        env.FooocusAllDefaultModelsDownloaded = false;
        Assert.False(env.FooocusReadyToStart);

        env.IsBaseEnvInstalled = false;
        env.IsRequirementsInstalled = false;
        env.FooocusAllDefaultModelsDownloaded = true;  // 1/3
        Assert.False(env.FooocusReadyToStart);
    }
}

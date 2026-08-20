using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// v0.6.22++:ModelSourceProxyDecision 决策助手测试 — 3×3 矩阵
/// (global Mode × source Mode)。
/// 默认 = InheritSystem + InheritGlobal(企业 VPN 用户开箱即用)。
/// </summary>
public class ModelSourceProxyDecisionTests
{
    private static Settings MakeSettings(HttpProxyMode global, string url = "10.0.0.1", int port = 8888)
        => new Settings
        {
            HttpProxyMode = global,
            HttpProxyUrl = url,
            HttpProxyPort = port,
        };

    [Fact]
    public void GlobalOff_SourceOff_ReturnsNull()
    {
        var s = MakeSettings(HttpProxyMode.Off);
        var result = ModelSourceProxyDecision.Resolve(HttpProxyMode.Off, ModelSourceProxyMode.Off, s);
        Assert.Null(result);
    }

    [Fact]
    public void GlobalOff_SourceInheritGlobal_ReturnsNull()
    {
        var s = MakeSettings(HttpProxyMode.Off);
        var result = ModelSourceProxyDecision.Resolve(HttpProxyMode.Off, ModelSourceProxyMode.InheritGlobal, s);
        Assert.Null(result);
    }

    [Fact]
    public void GlobalOff_SourceAlwaysOn_ReturnsEnabledButWillBeDisabledByFrom()
    {
        // AlwaysOn + GlobalOff:HttpProxyConfig.From → Disabled(因为 HttpProxyMode.Off)。
        // 决策语义:AlwaysOn 跟随全局配置走;全局 Off → 无 proxy。
        var s = MakeSettings(HttpProxyMode.Off);
        var result = ModelSourceProxyDecision.Resolve(HttpProxyMode.Off, ModelSourceProxyMode.AlwaysOn, s);
        // 不为 null(AlwaysOn 走 HttpProxyConfig.From 返回 Disabled 实例)。
        Assert.NotNull(result);
        Assert.False(result!.Enabled);
    }

    [Fact]
    public void GlobalInheritSystem_SourceOff_ReturnsNull()
    {
        var s = MakeSettings(HttpProxyMode.InheritSystem);
        var result = ModelSourceProxyDecision.Resolve(HttpProxyMode.InheritSystem, ModelSourceProxyMode.Off, s);
        Assert.Null(result);
    }

    [Fact]
    public void GlobalInheritSystem_SourceInheritGlobal_ReturnsEnabledUseSystemTrue()
    {
        var s = MakeSettings(HttpProxyMode.InheritSystem);
        var result = ModelSourceProxyDecision.Resolve(HttpProxyMode.InheritSystem, ModelSourceProxyMode.InheritGlobal, s);
        Assert.NotNull(result);
        Assert.True(result!.Enabled);
        Assert.True(result.UseSystemProxy);
    }

    [Fact]
    public void GlobalInheritSystem_SourceAlwaysOn_ReturnsEnabledUseSystemTrue()
    {
        var s = MakeSettings(HttpProxyMode.InheritSystem);
        var result = ModelSourceProxyDecision.Resolve(HttpProxyMode.InheritSystem, ModelSourceProxyMode.AlwaysOn, s);
        Assert.NotNull(result);
        Assert.True(result!.Enabled);
        Assert.True(result.UseSystemProxy);
    }

    [Fact]
    public void GlobalCustom_SourceOff_ReturnsNull()
    {
        var s = MakeSettings(HttpProxyMode.Custom);
        var result = ModelSourceProxyDecision.Resolve(HttpProxyMode.Custom, ModelSourceProxyMode.Off, s);
        Assert.Null(result);
    }

    [Fact]
    public void GlobalCustom_SourceInheritGlobal_ReturnsEnabledWithUrlAndPort()
    {
        var s = MakeSettings(HttpProxyMode.Custom, url: "10.0.0.1", port: 8888);
        var result = ModelSourceProxyDecision.Resolve(HttpProxyMode.Custom, ModelSourceProxyMode.InheritGlobal, s);
        Assert.NotNull(result);
        Assert.True(result!.Enabled);
        Assert.False(result.UseSystemProxy);
        Assert.Equal("10.0.0.1", result.Url);
        Assert.Equal(8888, result.Port);
    }

    [Fact]
    public void GlobalCustom_SourceAlwaysOn_ReturnsEnabledWithUrlAndPort()
    {
        var s = MakeSettings(HttpProxyMode.Custom, url: "127.0.0.1", port: 7890);
        var result = ModelSourceProxyDecision.Resolve(HttpProxyMode.Custom, ModelSourceProxyMode.AlwaysOn, s);
        Assert.NotNull(result);
        Assert.True(result!.Enabled);
        Assert.Equal("127.0.0.1", result.Url);
        Assert.Equal(7890, result.Port);
    }
}
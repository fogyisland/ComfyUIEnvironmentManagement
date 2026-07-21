using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public sealed class BaseEnvConfigTests
{
    [Fact]
    public void Defaults_AreCu118StableFourPackages()
    {
        var c = new BaseEnvConfig();
        Assert.Equal("cu118", c.CudaVersion);
        Assert.Equal("stable", c.TorchChannel);
        Assert.Equal(new[] { "torch", "torchaudio", "torchvision", "xformers" }, c.Packages);
        Assert.Equal("", c.ExtraArgs);
        Assert.Equal("", c.CustomPipArgs);
    }

    [Fact]
    public void BuildPipArgs_CustomPipArgs_ReturnsSplitVerbatim()
    {
        var c = new BaseEnvConfig { CustomPipArgs = "install torch --pre  -f /wheels" };
        Assert.Equal(
            new[] { "install", "torch", "--pre", "-f", "/wheels" },
            c.BuildPipArgs());
    }

    [Fact]
    public void BuildPipArgs_CustomPipArgs_EmptyFallsThroughToStructured()
    {
        var c = new BaseEnvConfig { CustomPipArgs = "   " };
        var args = c.BuildPipArgs();
        Assert.Contains("install", args);
        Assert.Contains("torch", args);
    }

    [Fact]
    public void BuildPipArgs_StableCu118_BuildsIndexUrl()
    {
        var c = new BaseEnvConfig();  // defaults: cu118, stable
        var args = c.BuildPipArgs();
        Assert.Equal("install", args[0]);
        Assert.Contains("torch", args);
        Assert.Contains("torchaudio", args);
        Assert.Contains("torchvision", args);
        Assert.Contains("xformers", args);
        Assert.DoesNotContain("--pre", args);
        var idx = args.ToList().IndexOf("--index-url");
        Assert.True(idx >= 0);
        Assert.Equal("https://download.pytorch.org/whl/cu118", args[idx + 1]);
    }

    [Fact]
    public void BuildPipArgs_Nightly_AppendsPreFlag()
    {
        var c = new BaseEnvConfig { TorchChannel = "nightly" };
        var args = c.BuildPipArgs();
        Assert.Contains("--pre", args);
        // nightly 仍带 index-url(CUDA 决定,与 channel 无关)
        Assert.Contains("--index-url", args);
    }

    [Fact]
    public void BuildPipArgs_Cpu_NoIndexUrl()
    {
        var c = new BaseEnvConfig { CudaVersion = "cpu" };
        var args = c.BuildPipArgs();
        Assert.DoesNotContain("--index-url", args);
        Assert.DoesNotContain("https://download.pytorch.org", args);
    }

    [Fact]
    public void BuildPipArgs_Cuda121_SwapsIndex()
    {
        var c = new BaseEnvConfig { CudaVersion = "cu121" };
        var args = c.BuildPipArgs();
        var idx = args.ToList().IndexOf("--index-url");
        Assert.True(idx >= 0);
        Assert.Equal("https://download.pytorch.org/whl/cu121", args[idx + 1]);
    }

    [Fact]
    public void BuildPipArgs_ExtraArgs_AppendedAtEnd()
    {
        var c = new BaseEnvConfig { ExtraArgs = "--user --no-cache" };
        var args = c.BuildPipArgs();
        var tail = args.SkipWhile(a => a != "--user").Take(2).ToArray();
        Assert.Equal(new[] { "--user", "--no-cache" }, tail);
    }

    [Fact]
    public void BuildPipArgs_PackagesOrderPreserved()
    {
        var c = new BaseEnvConfig
        {
            Packages = new List<string> { "torch", "xformers" },
        };
        var args = c.BuildPipArgs();
        Assert.Equal("install", args[0]);
        Assert.Equal("torch", args[1]);
        Assert.Equal("xformers", args[2]);
    }

    [Fact]
    public void Clone_ReturnsIndependentDeepCopy()
    {
        var c = new BaseEnvConfig { ExtraArgs = "--user" };
        var copy = c.Clone();
        copy.Packages.Add("foo");
        copy.ExtraArgs = "--changed";
        Assert.Equal(4, c.Packages.Count);  // original untouched
        Assert.Equal("--user", c.ExtraArgs);
        Assert.Equal("--changed", copy.ExtraArgs);
    }
}
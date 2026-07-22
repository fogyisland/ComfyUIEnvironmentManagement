using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public sealed class BaseEnvProfileTests
{
    [Fact]
    public void Defaults_AreSensibleForFreshProfile()
    {
        var p = new BaseEnvProfile();
        Assert.Equal("", p.Id);
        Assert.Equal("", p.Name);
        Assert.Equal("", p.Description);
        Assert.Equal("", p.TorchVersion);
        Assert.Equal("cu118", p.CudaVersion);
        Assert.Equal("stable", p.Channel);
        Assert.Equal(new[] { "torch", "torchaudio", "torchvision", "xformers" }, p.Packages);
        Assert.Equal("", p.ExtraArgs);
    }

    [Fact]
    public void BuildPipArgs_StableCu118_BuildsIndexUrl()
    {
        var p = new BaseEnvProfile();  // defaults: cu118, stable
        var args = p.BuildPipArgs();
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
        var p = new BaseEnvProfile { Channel = "nightly" };
        var args = p.BuildPipArgs();
        Assert.Contains("--pre", args);
        // nightly 仍带 index-url(CUDA 决定,与 channel 无关)
        Assert.Contains("--index-url", args);
    }

    [Fact]
    public void BuildPipArgs_Cpu_NoIndexUrl()
    {
        var p = new BaseEnvProfile { CudaVersion = "cpu" };
        var args = p.BuildPipArgs();
        Assert.DoesNotContain("--index-url", args);
        Assert.DoesNotContain("https://download.pytorch.org", args);
    }

    [Fact]
    public void BuildPipArgs_Cuda121_SwapsIndex()
    {
        var p = new BaseEnvProfile { CudaVersion = "cu121" };
        var args = p.BuildPipArgs();
        var idx = args.ToList().IndexOf("--index-url");
        Assert.True(idx >= 0);
        Assert.Equal("https://download.pytorch.org/whl/cu121", args[idx + 1]);
    }

    [Fact]
    public void BuildPipArgs_Cuda124_SwapsIndex()
    {
        var p = new BaseEnvProfile { CudaVersion = "cu124" };
        var args = p.BuildPipArgs();
        var idx = args.ToList().IndexOf("--index-url");
        Assert.True(idx >= 0);
        Assert.Equal("https://download.pytorch.org/whl/cu124", args[idx + 1]);
    }

    [Fact]
    public void BuildPipArgs_ExtraArgs_AppendedAtEnd()
    {
        var p = new BaseEnvProfile { ExtraArgs = "--user --no-cache" };
        var args = p.BuildPipArgs();
        var tail = args.SkipWhile(a => a != "--user").Take(2).ToArray();
        Assert.Equal(new[] { "--user", "--no-cache" }, tail);
    }

    [Fact]
    public void BuildPipArgs_PackagesOrderPreserved()
    {
        var p = new BaseEnvProfile
        {
            Packages = new List<string> { "torch", "xformers" },
        };
        var args = p.BuildPipArgs();
        Assert.Equal("install", args[0]);
        Assert.Equal("torch", args[1]);
        Assert.Equal("xformers", args[2]);
    }

    [Fact]
    public void BuildPipArgs_DoesNotBranchOnCustomPipArgs()
    {
        // BaseEnvProfile has no CustomPipArgs — BuildPipArgs always builds structured.
        // Set ExtraArgs to something that looks like CustomPipArgs to prove no branch.
        var p = new BaseEnvProfile { ExtraArgs = "install torch --pre" };
        var args = p.BuildPipArgs();
        // install must be the first token (from structured build), not from ExtraArgs
        Assert.Equal("install", args[0]);
        // ExtraArgs "install" is appended later as a literal token → should appear twice
        int installCount = 0;
        foreach (var a in args) if (a == "install") installCount++;
        Assert.Equal(2, installCount);
    }

    [Fact]
    public void Clone_ReturnsIndependentDeepCopy()
    {
        var p = new BaseEnvProfile
        {
            Id = "torch-stable-cu118",
            Name = "Torch Stable CUDA 11.8",
            Description = "desc",
            TorchVersion = "2.1.0",
            CudaVersion = "cu118",
            Channel = "stable",
            ExtraArgs = "--user",
        };
        var copy = p.Clone();
        copy.Packages.Add("foo");
        copy.ExtraArgs = "--changed";
        copy.Name = "renamed";

        Assert.Equal(4, p.Packages.Count);  // original untouched
        Assert.Equal("--user", p.ExtraArgs);
        Assert.Equal("--changed", copy.ExtraArgs);
        Assert.Equal("Torch Stable CUDA 11.8", p.Name);
        Assert.Equal("renamed", copy.Name);
        Assert.NotSame(p.Packages, copy.Packages);
    }

    [Fact]
    public void Clone_CopiesAllScalarFields()
    {
        var p = new BaseEnvProfile
        {
            Id = "id1",
            Name = "n",
            Description = "d",
            TorchVersion = "2.3.0",
            CudaVersion = "cu124",
            Channel = "nightly",
            ExtraArgs = "--user",
        };
        var copy = p.Clone();
        Assert.Equal("id1", copy.Id);
        Assert.Equal("n", copy.Name);
        Assert.Equal("d", copy.Description);
        Assert.Equal("2.3.0", copy.TorchVersion);
        Assert.Equal("cu124", copy.CudaVersion);
        Assert.Equal("nightly", copy.Channel);
        Assert.Equal("--user", copy.ExtraArgs);
    }
}
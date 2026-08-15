using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class NodeStartupErrorDetectorTests
{
    private readonly NodeStartupErrorDetector _detector = new();

    [Fact]
    public void Parse_FailedToImportLine_ExtractsPackageName()
    {
        var lines = new[] {
            "ComfyUI: starting server",
            "Failed to import module 'comfyui-impact-pack'",
            "Traceback (most recent call last):",
        };
        var errors = _detector.Parse(lines);
        Assert.Single(errors);
        Assert.Equal("comfyui-impact-pack", errors[0].PackageName);
        Assert.Contains("Failed to import module 'comfyui-impact-pack'", errors[0].ErrorMessage);
    }

    [Fact]
    public void Parse_ImportErrorLine_ExtractsModuleName()
    {
        var lines = new[] {
            "ImportError: No module named 'openai'",
        };
        var errors = _detector.Parse(lines);
        Assert.Single(errors);
        Assert.Equal("openai", errors[0].PackageName);
    }

    [Fact]
    public void Parse_ModuleNotFoundErrorLine_ExtractsModuleName()
    {
        var lines = new[] {
            "ModuleNotFoundError: No module named 'tqdm'",
        };
        var errors = _detector.Parse(lines);
        Assert.Single(errors);
        Assert.Equal("tqdm", errors[0].PackageName);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmptyResult()
    {
        var errors = _detector.Parse(new string[0]);
        Assert.Empty(errors);
    }

    [Fact]
    public void Parse_MultipleFailedPackages_ReturnsAllDeduplicated()
    {
        var lines = new[] {
            "Failed to import module 'comfyui-impact-pack'",
            "ModuleNotFoundError: No module named 'openai'",
            "Failed to import module 'comfyui-impact-pack'",   // second occurrence
            "ImportError: No module named 'tqdm'",
        };
        var errors = _detector.Parse(lines);
        Assert.Equal(3, errors.Count);  // dedup by package name
        Assert.Contains(errors, e => e.PackageName == "comfyui-impact-pack");
        Assert.Contains(errors, e => e.PackageName == "openai");
        Assert.Contains(errors, e => e.PackageName == "tqdm");
    }
}
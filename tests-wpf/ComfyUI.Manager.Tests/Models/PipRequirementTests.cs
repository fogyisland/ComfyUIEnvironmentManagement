using System.Linq;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public class PipRequirementTests
{
    [Fact]
    public void ParseList_Empty_ReturnsEmpty()
    {
        var result = PipRequirement.ParseList(System.Array.Empty<string>());
        Assert.Empty(result);
    }

    [Fact]
    public void ParseList_BareName_NoSpecifier()
    {
        var result = PipRequirement.ParseList(new[] { "huggingface-hub" });
        Assert.Single(result);
        Assert.Equal("huggingface-hub", result[0].Name);
        Assert.Null(result[0].Specifier);
    }

    [Fact]
    public void ParseList_WithSpecifier_SplitsCorrectly()
    {
        var result = PipRequirement.ParseList(new[] { "numpy>=1.24.0" });
        Assert.Single(result);
        Assert.Equal("numpy", result[0].Name);
        Assert.Equal(">=1.24.0", result[0].Specifier);
    }

    [Fact]
    public void ParseList_MultiSpecifier_PreservesComma()
    {
        var result = PipRequirement.ParseList(new[] { "requests>=1.0,<2.0" });
        Assert.Single(result);
        Assert.Equal("requests", result[0].Name);
        Assert.Equal(">=1.0,<2.0", result[0].Specifier);
    }

    [Fact]
    public void ParseList_NormalizesName_LowercaseUnderscoresToDashes()
    {
        var req = PipRequirement.ParseList(new[] { "Some_PKG" }).Single();
        Assert.Equal("some-pkg", req.NormalizedName);
    }

    [Fact]
    public void ParseList_SkipsEmptyAndWhitespace()
    {
        var result = PipRequirement.ParseList(new[] { "", "  ", "torch" });
        Assert.Single(result);
        Assert.Equal("torch", result[0].Name);
    }
}

using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class PipRequirementMatcherTests
{
    [Fact]
    public void IsSatisfiedBy_NoSpecifier_AlwaysTrue()
    {
        var req = new PipRequirement("torch", null);
        Assert.True(PipRequirementMatcher.IsSatisfiedBy(req, "2.0.0"));
        Assert.True(PipRequirementMatcher.IsSatisfiedBy(req, "99.99.99"));
    }

    [Fact]
    public void IsSatisfiedBy_GEQ_Passes_And_Fails()
    {
        var req = new PipRequirement("numpy", ">=1.20");
        Assert.True(PipRequirementMatcher.IsSatisfiedBy(req, "1.24.0"));
        Assert.True(PipRequirementMatcher.IsSatisfiedBy(req, "1.20.0"));
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, "1.19.99"));
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, "0.9.0"));
    }

    [Fact]
    public void IsSatisfiedBy_EQ_Passes_And_Fails()
    {
        var req = new PipRequirement("gradio", "==4.19.0");
        Assert.True(PipRequirementMatcher.IsSatisfiedBy(req, "4.19.0"));
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, "4.19.1"));
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, "4.18.0"));
    }

    [Fact]
    public void IsSatisfiedBy_Range_AndSemantics()
    {
        var req = new PipRequirement("urllib3", ">=1.0,<2.0");
        Assert.True(PipRequirementMatcher.IsSatisfiedBy(req, "1.5.0"));
        Assert.True(PipRequirementMatcher.IsSatisfiedBy(req, "1.99.99"));
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, "2.0.0"));
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, "0.9.0"));
    }

    [Fact]
    public void IsSatisfiedBy_NullVersion_ReturnsFalse_NoThrow()
    {
        var req = new PipRequirement("torch", ">=2.0");
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, null));
    }

    [Fact]
    public void IsSatisfiedBy_EmptyVersion_ReturnsFalse()
    {
        var req = new PipRequirement("torch", ">=2.0");
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, ""));
    }

    [Fact]
    public void IsSatisfiedBy_UnparseableVersion_ReturnsFalse()
    {
        var req = new PipRequirement("torch", ">=2.0");
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, "not-a-version"));
    }

    [Fact]
    public void IsSatisfiedBy_CompatibleRelease_TildeEquals()
    {
        var req = new PipRequirement("numpy", "~=1.4.2");
        Assert.True(PipRequirementMatcher.IsSatisfiedBy(req, "1.4.2"));
        Assert.True(PipRequirementMatcher.IsSatisfiedBy(req, "1.4.5"));
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, "1.5.0"));
        Assert.False(PipRequirementMatcher.IsSatisfiedBy(req, "1.4.1"));
    }
}

using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class NodeUrlResolverTests
{
    [Fact]
    public void Resolve_NodeTemplate_Substitutes()
    {
        var result = NodeUrlResolver.Resolve(
            "https://github.com/comfyanonymous/{node}", "ComfyUI-IPAdapter-Flux");
        Assert.Equal("https://github.com/comfyanonymous/ComfyUI-IPAdapter-Flux", result);
    }

    [Fact]
    public void Resolve_NoTemplate_ReturnsOriginal()
    {
        // 用户可以填一个不带 {node} 的固定 URL
        var url = "https://github.com/foo/SpecificRepo";
        var result = NodeUrlResolver.Resolve(url, "any-node");
        Assert.Equal(url, result);
    }

    [Fact]
    public void Resolve_EmptyUrl_ReturnsEmpty()
    {
        var result = NodeUrlResolver.Resolve("", "any-node");
        Assert.Equal("", result);
    }

    [Fact]
    public void Resolve_WhitespaceUrl_ReturnsWhitespace()
    {
        var result = NodeUrlResolver.Resolve("   ", "any-node");
        Assert.Equal("   ", result);
    }

    [Fact]
    public void Resolve_MultipleNodeOccurrences_AllSubstituted()
    {
        // 防御性:用户写了多次 {node} 时全部替换
        var result = NodeUrlResolver.Resolve(
            "https://mirror/{node}/extra/{node}", "my-node");
        Assert.Equal("https://mirror/my-node/extra/my-node", result);
    }
}
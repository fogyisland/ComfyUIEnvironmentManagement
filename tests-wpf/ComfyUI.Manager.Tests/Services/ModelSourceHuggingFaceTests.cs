using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services.ModelSources;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelSourceHuggingFaceTests
{
    [Fact]
    public async Task SearchAsync_AnyQuery_ReturnsEmpty()
    {
        var source = new HuggingFaceModelSource();
        var entries = await source.SearchAsync("anything", 50, CancellationToken.None);
        Assert.Empty(entries);
    }

    [Fact]
    public void IsEnabled_DefaultsFalse()
    {
        var source = new HuggingFaceModelSource();
        Assert.False(source.IsEnabled);
    }
}

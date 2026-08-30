using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public sealed class ModelsMissingExceptionTests
{
    [Fact]
    public void Ctor_StoresFields()
    {
        var missing = new List<string> { "/a/b/transformer.safetensors", "/a/b/vae.safetensors" };
        var ex = new ModelsMissingException(
            "缺少 LTX-2 模型文件",
            missing,
            "https://huggingface.co/Lightricks/LTX-2.5",
            "hf download Lightricks/LTX-2.5 --local-dir models/ltx-2.5");

        Assert.Equal("缺少 LTX-2 模型文件", ex.Message);
        Assert.Equal(2, ex.MissingPaths.Count);
        Assert.Equal("/a/b/transformer.safetensors", ex.MissingPaths[0]);
        Assert.Equal("https://huggingface.co/Lightricks/LTX-2.5", ex.HuggingFaceRepoUrl);
        Assert.Contains("hf download", ex.DownloadCommand);
    }

    [Fact]
    public void MissingPaths_IsReadOnly()
    {
        var ex = new ModelsMissingException("msg", new List<string>(), "url", "cmd");
        Assert.IsAssignableFrom<IReadOnlyList<string>>(ex.MissingPaths);
    }

    [Fact]
    public void MissingPaths_Empty_StillConstructable()
    {
        var ex = new ModelsMissingException("msg", new List<string>(), "url", "cmd");
        Assert.Empty(ex.MissingPaths);
    }
}

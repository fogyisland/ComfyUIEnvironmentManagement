using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public sealed class PipMirrorResolverTests
{
    private static Settings S(string mirror, string customUrl = "")
        => new Settings { PipMirror = mirror, PipMirrorCustomUrl = customUrl };

    [Fact]
    public void ResolveIndexUrl_Official_ReturnsNull()
    {
        Assert.Null(PipMirrorResolver.ResolveIndexUrl(S("official")));
    }

    [Fact]
    public void ResolveIndexUrl_TsinghuaTuna_ReturnsTunaUrl()
    {
        Assert.Equal("https://pypi.tuna.tsinghua.edu.cn/simple",
            PipMirrorResolver.ResolveIndexUrl(S("tsinghua_tuna")));
    }

    [Fact]
    public void ResolveIndexUrl_Aliyun_ReturnsAliyunUrl()
    {
        Assert.Equal("https://mirrors.aliyun.com/pypi/simple/",
            PipMirrorResolver.ResolveIndexUrl(S("aliyun")));
    }

    [Fact]
    public void ResolveIndexUrl_USTC_ReturnsUstcUrl()
    {
        Assert.Equal("https://pypi.mirrors.ustc.edu.cn/simple/",
            PipMirrorResolver.ResolveIndexUrl(S("ustc")));
    }

    [Fact]
    public void ResolveIndexUrl_CustomWithUrl_ReturnsTrimmedUrl()
    {
        Assert.Equal("https://pypi.doubanio.com/simple",
            PipMirrorResolver.ResolveIndexUrl(S("custom", "  https://pypi.doubanio.com/simple  ")));
    }

    [Fact]
    public void ResolveIndexUrl_CustomWithEmptyUrl_ReturnsNull()
    {
        // 选了 custom 但 URL 没填 → 视为未设,走官方(不传 --index-url)
        Assert.Null(PipMirrorResolver.ResolveIndexUrl(S("custom", "")));
    }

    [Fact]
    public void ResolveIndexUrl_GarbageValue_ReturnsNull()
    {
        // 未来加新 enum 值前用户可能手改 settings.json 写成无效字符串 → 回退官方
        Assert.Null(PipMirrorResolver.ResolveIndexUrl(S("not_a_real_mirror")));
    }

    [Fact]
    public void BuildPipArgs_Official_IsEmpty()
    {
        var args = PipMirrorResolver.BuildPipArgs(S("official"));
        Assert.Empty(args);
    }

    [Fact]
    public void BuildPipArgs_TsinghuaTuna_IsTwoElements()
    {
        var args = PipMirrorResolver.BuildPipArgs(S("tsinghua_tuna"));
        Assert.Equal(2, args.Count);
        Assert.Equal("--index-url", args[0]);
        Assert.Equal("https://pypi.tuna.tsinghua.edu.cn/simple", args[1]);
    }
}

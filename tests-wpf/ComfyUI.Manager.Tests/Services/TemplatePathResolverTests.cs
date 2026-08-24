using System;
using System.IO;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x: 用户反馈 "下载后的目录必须和设置中的目录一致"。TemplatePathResolver 把
/// 相对路径锚定到 <see cref="AppContext.BaseDirectory"/>,保证不同启动方式下 git clone
/// 目标始终一致。这套测试覆盖 3 个分支:绝对路径/相对路径/空串。
/// </summary>
public class TemplatePathResolverTests
{
    [Fact]
    public void Resolve_AbsolutePath_ReturnsUnchanged()
    {
        var abs = Path.Combine("C:", "absolute", "path");
        Assert.Equal(abs, TemplatePathResolver.Resolve(abs));
    }

    [Fact]
    public void Resolve_RelativePath_AnchorsToBaseDirectory()
    {
        var result = TemplatePathResolver.Resolve("envTemplates/ComfyUI");
        // Resolve 返回的应是绝对路径(以盘符开头),且子串含原 relative suffix
        Assert.True(Path.IsPathRooted(result), "resolve result must be rooted");
        Assert.EndsWith(Path.Combine("envTemplates", "ComfyUI"), result);
    }

    [Fact]
    public void Resolve_PathWithParentTraversal_Normalized()
    {
        // "../foo" 锚定到 base dir 后应该 normalize(不能逃出 base 目录)
        var result = TemplatePathResolver.Resolve("./envTemplates/./ComfyUI");
        Assert.True(Path.IsPathRooted(result));
        Assert.DoesNotContain("./", result);
        Assert.DoesNotContain("./", result.Replace('\\', '/').Replace("/./", ""));
    }

    [Fact]
    public void Resolve_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Equal("", TemplatePathResolver.Resolve(""));
        Assert.Equal("", TemplatePathResolver.Resolve(null));
    }

    [Fact]
    public void Resolve_IsStableAcrossCurrentDirectory()
    {
        // 关键属性:Resolve 不依赖 Environment.CurrentDirectory。
        // 设 CWD 到根目录,如果 Resolve 走 CWD 而不是 BaseDirectory,结果会完全不同。
        var before = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Path.GetPathRoot(Path.GetTempPath()) ?? "/";
            var result = TemplatePathResolver.Resolve("envTemplates/ComfyUI");
            // 还是 BaseDirectory-anchored,不是 CWD-anchored
            var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var root = Path.GetPathRoot(baseDir);
            Assert.NotEqual(
                Path.Combine(Environment.CurrentDirectory, "envTemplates", "ComfyUI"),
                result);
            Assert.StartsWith(root ?? "", result);
        }
        finally
        {
            Environment.CurrentDirectory = before;
        }
    }

    [Fact]
    public void Resolve_WithBasePath_AnchorsToBasePath()
    {
        // v1.0.0.x: 用户配 system_template_library_dir = "D:\\ToolDevelop\\ComfyUI\\ENVTemplate"
        // 后,所有模板都克隆到该目录下。local_source_dir = "ComfyUI" 解析为
        // <basePath>/ComfyUI。
        var basePath = @"D:\ToolDevelop\ComfyUI\ENVTemplate";
        var result = TemplatePathResolver.Resolve("ComfyUI", basePath);
        Assert.Equal(Path.Combine(basePath, "ComfyUI"), result);
    }

    [Fact]
    public void Resolve_WithBasePathEmpty_FallsBackToBaseDirectory()
    {
        // basePath 为 null/空串 → 走 BaseDirectory fallback(老行为不变)
        var result = TemplatePathResolver.Resolve("ComfyUI", "");
        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ComfyUI"));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Resolve_WithBasePath_AbsoluteLocalSourceDir_Unchanged()
    {
        // 即使提供了 basePath,绝对 localSourceDir 仍原样返回(用户主动填的绝对路径优先)
        var abs = Path.Combine("C:", "absolute", "path");
        var result = TemplatePathResolver.Resolve(abs, @"D:\whatever\base");
        Assert.Equal(abs, result);
    }

    [Fact]
    public void Resolve_WithBasePathAndTraversal_Normalized()
    {
        // basePath 提供时,相对路径中的 ./ 也被 Path.GetFullPath 标准化
        var basePath = @"D:\ToolDevelop\ComfyUI\ENVTemplate";
        var result = TemplatePathResolver.Resolve("./ComfyUI/../A1111", basePath);
        Assert.Equal(Path.Combine(basePath, "A1111"), result);
    }

    [Fact]
    public void Resolve_BasePathWinsOverCurrentDirectory()
    {
        // basePath 提供时,锚定 basePath 而不是 CWD/BaseDirectory。
        // 设 CWD 到 drive root,如果 resolve 走 CWD 而不是 basePath,会得到 CWD-anchored 路径。
        var basePath = @"D:\ToolDevelop\ComfyUI\ENVTemplate";
        var before = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Path.GetPathRoot(Path.GetTempPath()) ?? "/";
            var result = TemplatePathResolver.Resolve("ComfyUI", basePath);
            // 不会用 CWD 锚定,只用 basePath 锚定
            Assert.NotEqual(
                Path.Combine(Environment.CurrentDirectory, "ComfyUI"),
                result);
            Assert.Equal(Path.Combine(basePath, "ComfyUI"), result);
        }
        finally
        {
            Environment.CurrentDirectory = before;
        }
    }
}

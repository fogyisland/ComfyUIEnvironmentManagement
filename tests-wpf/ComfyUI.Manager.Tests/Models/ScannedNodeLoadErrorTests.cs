using System.Collections.Generic;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

/// <summary>
/// v1.0.0.x: ScannedNode.HasLoadError / LoadErrorDisplay 派生属性测试 —
/// 纯 model 测试,不需要 DB。覆盖 ScanMeta 三态(无 key / key 空 / key 有值)。
/// </summary>
public class ScannedNodeLoadErrorTests
{
    [Fact]
    public void HasLoadError_NoScanMeta_ReturnsFalse()
    {
        var n = new ScannedNode { Package = "ComfyUI" };
        Assert.False(n.HasLoadError);
        Assert.Equal("", n.LoadErrorDisplay);
    }

    [Fact]
    public void HasLoadError_EmptyScanMeta_ReturnsFalse()
    {
        var n = new ScannedNode { Package = "ComfyUI", ScanMeta = new Dictionary<string, string>() };
        Assert.False(n.HasLoadError);
        Assert.Equal("", n.LoadErrorDisplay);
    }

    [Fact]
    public void HasLoadError_NoLoadErrorKey_ReturnsFalse()
    {
        var n = new ScannedNode
        {
            Package = "ComfyUI",
            ScanMeta = new Dictionary<string, string> { { "branch", "main" } },
        };
        Assert.False(n.HasLoadError);
        Assert.Equal("", n.LoadErrorDisplay);
    }

    [Fact]
    public void HasLoadError_LoadErrorEmptyValue_ReturnsFalse()
    {
        // ScanMeta["load_error"] = "" 视作"无错误" — 写入了 key 但 ProcessLauncher
        // 不应该写空字符串,这里锁住"空 == 无"的语义,避免 UI 误报。
        var n = new ScannedNode
        {
            Package = "ComfyUI",
            ScanMeta = new Dictionary<string, string> { { "load_error", "" } },
        };
        Assert.False(n.HasLoadError);
        Assert.Equal("", n.LoadErrorDisplay);
    }

    [Fact]
    public void HasLoadError_LoadErrorPresent_ReturnsTrueAndExposesMessage()
    {
        const string msg = "Failed to import module 'ComfyUI' for instance 'ComfyUI': No module named 'ComfyUI'";
        var n = new ScannedNode
        {
            Package = "ComfyUI",
            ScanMeta = new Dictionary<string, string> { { "load_error", msg } },
        };
        Assert.True(n.HasLoadError);
        Assert.Equal(msg, n.LoadErrorDisplay);
    }

    [Fact]
    public void HasLoadError_LoadErrorPresent_WithOtherKeys_StillTrue()
    {
        // 真实场景:ScanMeta 里同时有 branch / disk_size / load_error 等多个 key。
        var n = new ScannedNode
        {
            Package = "ComfyUI",
            ScanMeta = new Dictionary<string, string>
            {
                { "branch", "main" },
                { "disk_size", "1024" },
                { "load_error", "ModuleNotFoundError: No module named 'torch'" },
            },
        };
        Assert.True(n.HasLoadError);
        Assert.Contains("ModuleNotFoundError", n.LoadErrorDisplay);
    }
}
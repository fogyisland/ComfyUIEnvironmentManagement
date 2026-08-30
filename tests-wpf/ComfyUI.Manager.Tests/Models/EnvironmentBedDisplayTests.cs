using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public class EnvironmentBedDisplayTests
{
    [Theory]
    [InlineData(null, null, null, "✗ 未装")]
    [InlineData("done", "pytorch-2.5.0-cu121-stable", null, "✓ pytorch-2.5.0-cu121-stable")]
    [InlineData("failed", "pytorch-2.5.0-cu121-stable", "pip 退出码 1", "❌ pytorch-2.5.0-cu121-stable (pip 退出码 1)")]
    [InlineData("installing", null, null, "⏳ 装中")]
    public void BedDisplay_FormatsCorrectly(string? bedStatus, string? bedProfileId, string? reason, string expected)
    {
        var env = new Environment { BedStatus = bedStatus, BedProfileId = bedProfileId, BedFailedReason = reason };
        Assert.Equal(expected, env.BedDisplay);
    }

    // v1.0.0.x (2026-08-30):BedDisplay "done" 分支优先 InstalledTorchVersion —
    // BED marker 在盘上但 BedProfileId 未回填的 state desync 老 env,
    // 不能只显示 "✓ " 空内容,要显示实际 torch 版本。

    [Fact]
    public void BedDisplay_Done_InstalledTorchVersion_PrefersActualTorchVersion()
    {
        var env = new Environment
        {
            BedStatus = "done",
            BedProfileId = null,                  // state desync 老 env
            InstalledTorchVersion = "2.4.0+cu121", // 实际部署版本
        };
        Assert.Equal("✓ torch 2.4.0+cu121", env.BedDisplay);
    }

    [Fact]
    public void BedDisplay_Done_TorchVersionBeatsBedProfileId()
    {
        // 优先级跟 BedDisplayId 一致:实际版本 > 持久化记录
        var env = new Environment
        {
            BedStatus = "done",
            BedProfileId = "pytorch-2.5.0-cu121-stable", // SQLite 字段
            InstalledTorchVersion = "2.4.0+cu118",       // 实际装的(stale profile id)
        };
        Assert.Equal("✓ torch 2.4.0+cu118", env.BedDisplay);
    }

    [Fact]
    public void BedDisplay_Done_NoTorchVersion_FallsBackToBedProfileId()
    {
        var env = new Environment
        {
            BedStatus = "done",
            BedProfileId = "pytorch-2.5.0-cu121-stable",
            InstalledTorchVersion = null,
        };
        Assert.Equal("✓ pytorch-2.5.0-cu121-stable", env.BedDisplay);
    }

    [Fact]
    public void BedDisplay_Done_NeitherTorchVersionOrProfileId_EmptyCheckmark()
    {
        // 极端 edge case:done 但两边都没值(可能 venv torch 没装 marker 是手写伪造)— 返 "✓ "
        // 让用户至少知道 BED 状态标记是 done
        var env = new Environment
        {
            BedStatus = "done",
            BedProfileId = null,
            InstalledTorchVersion = null,
        };
        Assert.Equal("✓ ", env.BedDisplay);
    }
}

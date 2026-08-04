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
}

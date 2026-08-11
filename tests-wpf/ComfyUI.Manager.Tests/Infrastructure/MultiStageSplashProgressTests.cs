using ComfyUI.Manager.Infrastructure;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

public class MultiStageSplashProgressTests
{
    [Fact]
    public void Report_WeightedSum_ComputesTotalPercent()
    {
        var p = new MultiStageSplashProgress();
        p.Report(Stage.Init, 100);          // 100% of 25% = 25
        p.Report(Stage.LoadDatabase, 100);  // 100% of 25% = 25 → cumulative 50
        p.Report(Stage.LoadTheme, 100);     // 100% of 25% = 25 → cumulative 75
        Assert.Equal(75, p.TotalPercent);
    }

    [Fact]
    public void Report_ClampToValidRange()
    {
        var p = new MultiStageSplashProgress();
        p.Report(Stage.Init, 150);          // over → clamp 100
        Assert.Equal(25, p.TotalPercent);
        p.Report(Stage.Init, -50);          // under → clamp 0 (re-init)
        Assert.Equal(0, p.TotalPercent);
    }

    [Fact]
    public void Report_OutOfOrderStage_DoesNotRegress()
    {
        var p = new MultiStageSplashProgress();
        p.Report(Stage.Ready, 100);         // 100% of 25% = 25 (out of order is OK, no regression)
        p.Report(Stage.Init, 100);          // 100% of 25% = 25 → cumulative 50
        Assert.Equal(50, p.TotalPercent);
    }

    [Fact]
    public void Report_FiresEventOnChange()
    {
        var p = new MultiStageSplashProgress();
        Stage? firedStage = null;
        int? firedPercent = null;
        p.StageChanged += (s, pct) => { firedStage = s; firedPercent = pct; };
        p.Report(Stage.Init, 50);
        Assert.Equal(Stage.Init, firedStage);
        Assert.Equal(50, firedPercent);
    }
}

using System;
using System.Windows;
using System.Windows.Media;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.9 T1:ThemeService.Apply 原子替换 palette 槽位的 5 路径测试。
/// ResourceDictionary pack URI 加载需要 STA(项目惯例见 StaFact.cs),
/// 所以测试体包进 StaFact.RunOnSTA。
/// </summary>
public class ThemeServiceTests
{
    private const string LightPalettePath = "/ComfyUI.Manager;component/Themes/Palette.Light.xaml";
    private const string DarkPalettePath = "/ComfyUI.Manager;component/Themes/Palette.Dark.xaml";

    private static ResourceDictionary NewAppResources()
    {
        var rd = new ResourceDictionary();
        rd.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(LightPalettePath, UriKind.Relative)
        });
        return rd;
    }

    [Fact]
    public void Apply_Dark_ReplacesMergedDictToPaletteDark()
    {
        StaFact.RunOnSTA(() =>
        {
            var appResources = NewAppResources();
            var svc = new ThemeService(appResources);
            svc.Apply(ThemeMode.Dark);
            // 验证:PrimaryBrush 颜色是 Dark #BB86FC
            var brush = (SolidColorBrush)appResources["PrimaryBrush"];
            Assert.Equal(Color.FromRgb(0xBB, 0x86, 0xFC), brush.Color);
        });
    }

    [Fact]
    public void Apply_Light_ReplacesMergedDictToPaletteLight()
    {
        StaFact.RunOnSTA(() =>
        {
            var appResources = NewAppResources();
            var svc = new ThemeService(appResources);
            svc.Apply(ThemeMode.Dark);
            svc.Apply(ThemeMode.Light);
            var brush = (SolidColorBrush)appResources["PrimaryBrush"];
            Assert.Equal(Color.FromRgb(0x67, 0x50, 0xA4), brush.Color);
        });
    }

    [Fact]
    public void Apply_FollowSystem_ResolvesToSystemTheme()
    {
        StaFact.RunOnSTA(() =>
        {
            var appResources = NewAppResources();
            var svc = new ThemeService(appResources);
            svc.Apply(ThemeMode.FollowSystem);
            // FollowSystem 必须落定到 Light 或 Dark 之一(不应该是无效状态)
            Assert.Contains(svc.Current, new[] { ThemeMode.Light, ThemeMode.Dark });
        });
    }

    [Fact]
    public void Apply_InvalidValue_FallsBackToDark()
    {
        StaFact.RunOnSTA(() =>
        {
            var appResources = NewAppResources();
            var svc = new ThemeService(appResources);
            // 模拟无效值 —— cast 一个无效 int
            svc.Apply((ThemeMode)999);
            Assert.Equal(ThemeMode.Dark, svc.Current);
        });
    }

    [Fact]
    public void Applied_Event_FiresAfterApply()
    {
        StaFact.RunOnSTA(() =>
        {
            var appResources = NewAppResources();
            var svc = new ThemeService(appResources);
            ThemeMode? fired = null;
            svc.Applied += (_, m) => fired = m;
            svc.Apply(ThemeMode.Light);
            Assert.Equal(ThemeMode.Light, fired);
        });
    }
}

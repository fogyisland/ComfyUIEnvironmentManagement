using System;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

/// <summary>
/// v0.6.11+ dashboard/splash polish T3:STA-thread headless load 验证 Dashboard 新 XAML
/// (hero 区 96×96 icon + 4 卡片 + 「下载地址」区块)解析不抛 XamlParseException。
///
/// 跟 v0.6.9.2 MaterialTextBox / v0.6.10.2 EnvironmentListView 同款守护 —— Setter 里
/// StaticResource 解析失败、converter 未注册、pack URI 写错,都只在真正 load 时才炸。
/// </summary>
public class DashboardViewLoadTests
{
    [Fact]
    public void DashboardView_DarkTheme_LoadsWithoutException()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);
                var v = new DashboardView();
                v.Measure(new Size(1100, 900));
                v.Arrange(new Rect(0, 0, 1100, 900));
                v.UpdateLayout();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught is not null)
        {
            throw new Exception(
                $"DashboardView load failed: {caught.GetType().FullName}: {caught.Message}",
                caught);
        }
    }

    /// <summary>
    /// Hero 区图标 URI 必须真的能解析。
    ///
    /// 踩坑记录(同 SplashWindowLoadTests):asset/** 在 csproj 里是 &lt;None&gt; +
    /// CopyToOutputDirectory(松散文件),不是编译进 assembly 的 &lt;Resource&gt;,
    /// 所以 pack://application:,,,/asset/icon.png 一定抛 IOException 找不到资源;
    /// 必须用 pack://siteoforigin:,,,/asset/icon.png。而 Image.Source 解析失败走
    /// ImageFailed 事件静默 —— XAML load 测试本身抓不到,只能像这样单独解析 URI。
    /// </summary>
    [Fact]
    public void DashboardHeroIcon_SiteOfOriginUri_Resolves()
    {
        Exception? caught = null;
        var size = "";

        var thread = new Thread(() =>
        {
            try
            {
                // pack: scheme 必须先由 WPF 注册(Application / PackUriHelper 静态初始化),
                // 否则 new Uri("pack://...") 直接抛 UriFormatException: Invalid port specified。
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);

                var bmp = new BitmapImage(
                    new Uri("pack://siteoforigin:,,,/asset/icon.png", UriKind.Absolute));
                size = $"{bmp.PixelWidth}x{bmp.PixelHeight}";
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(caught);
        Assert.NotEqual("0x0", size);
    }
}

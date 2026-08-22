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
    /// 踩坑记录(同 SplashWindowLoadTests):assets/** 在 csproj 里是 &lt;None&gt; +
    /// CopyToOutputDirectory(松散文件),不是编译进 assembly 的 &lt;Resource&gt;,
    /// 所以 pack://application:,,,/assets/icon.png 一定抛 IOException 找不到资源;
    /// 必须用 pack://siteoforigin:,,,/assets/icon.png。而 Image.Source 解析失败走
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
                    new Uri("pack://siteoforigin:,,,/assets/icon.png", UriKind.Absolute));
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

    /// <summary>
    /// v0.6.11+ dashboard/splash polish T5:回归守护 T3 新增的两块 XAML 表面 ——
    /// 顶部 hero 行(icon + 标题 + 版本 + GitHub 数据条)和底部「📥 下载地址」区块。
    ///
    /// 跟上面的 <see cref="DashboardView_DarkTheme_LoadsWithoutException"/> 结构相似但意图不同:
    /// 那个是 T3 落地时对「整个 DashboardView 能不能 load」的通用守护;这个专门盯住
    /// hero + 下载地址 —— 它们是 T3 引入的新 XAML,含 pack://siteoforigin: 图片、
    /// 新 Border 卡片样式、以及 GitHub 版本/离线徽章的 converter 绑定,任何一处
    /// StaticResource / converter / pack URI 写错都只在真实 load + layout 时才炸,
    /// 编译期完全看不出来。名字点明覆盖面,后续改 hero 或下载地址时能直接定位到这条。
    ///
    /// 尺寸用 1100×900(跟上面一致):新的 hero + 卡片 + 下载地址三段式布局在 800×600
    /// 下会被裁掉下半部分,measure/arrange 走不到下载地址那段。
    /// </summary>
    [Fact]
    public void DashboardView_HeroAndDownloadAddress_DoesNotThrow()
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
                $"DashboardView hero+download layout load failed: {caught.GetType().FullName}: {caught.Message}",
                caught);
        }
    }
}

using System;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

/// <summary>
/// v0.6.11+ dashboard/splash polish:STA-thread headless load 验证 Splash 新 XAML
/// (asset/ComfyUI.png + 4-row ProgressBar + no Border bg)解析不抛 XamlParseException。
/// </summary>
public class SplashWindowLoadTests
{
    [Fact]
    public void SplashWindow_NewMultiStageLayout_DoesNotThrow()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);
                var vm = new SplashViewModel(
                    title: "ComfyUI 多环境管理系统",
                    tagline: "智能管理 ComfyUI 环境、节点、依赖",
                    version: AppVersionInfo.Current);
                var v = new SplashWindow(vm);
                v.Measure(new Size(900, 540));
                v.Arrange(new Rect(0, 0, 900, 540));
                v.UpdateLayout();

                // 4 stage report 后重新 layout — 确认 ProgressBar 的
                // StageRowsPercent[i] 索引绑定不抛(错的 index 绑定只会静默 fallback,
                // 但 Value 类型不匹配 / 索引器缺失会在 UpdateLayout 期间炸)。
                vm.ReportStageProgress(Stage.Init, 100);
                vm.ReportStageProgress(Stage.LoadDatabase, 100);
                vm.ReportStageProgress(Stage.LoadTheme, 100);
                vm.ReportStageProgress(Stage.Ready, 100);
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
                $"SplashWindow multi-stage layout load failed: {caught.GetType().FullName}: {caught.Message}",
                caught);
        }
    }

    /// <summary>
    /// 背景图 URI 必须真的能解析 — v0.6.11+ 去掉了 Border 背景色兜底
    /// (用户原话"背景就是一张图片"),URI 错了就是全黑/全透明 splash,
    /// 而 XAML 解析本身不会抛(WPF Image.Source 失败走 ImageFailed 事件静默)。
    ///
    /// 踩坑记录:asset/** 在 csproj 里是 &lt;None&gt; + CopyToOutputDirectory
    /// (松散文件),不是编译进 assembly 的 &lt;Resource&gt;,所以
    /// pack://application:,,,/asset/x.png 一定抛 IOException 找不到资源;
    /// 必须用 pack://siteoforigin:,,,/asset/x.png。v0.6.8 的 splash.png
    /// 一直是坏的,只是被 Border 背景色 + 静默 ImageFailed 盖住了没人发现。
    /// </summary>
    [Fact]
    public void SplashBackgroundImage_SiteOfOriginUri_Resolves()
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
                    new Uri("pack://siteoforigin:,,,/asset/ComfyUI.png", UriKind.Absolute));
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

using System;
using System.IO;
using System.Threading;
using System.Windows;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

/// <summary>
/// 诊断用:headless 加载 CatalogView,捕获 XAML 解析异常。
/// v0.6.11+ Catalog polish:4 区重做(顶部 toolbar / 列表 / 磁贴 / 详情面板)后,
/// Theme.xaml 新加 styles(segmented control / pill badge / version combobox / card container)
/// 任何 Setter StaticResource 解析失败会在 STA load 抛 XamlParseException。
/// 跟 v0.6.9.2 MaterialButton / v0.6.9.2 MaterialTextBox / v0.6.10.2 EnvironmentListView
/// 同款根因,headless 抓得到。
/// </summary>
public class CatalogViewLoadTests
{
    [Fact]
    public void CatalogView_DarkTheme_LoadsWithoutException()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);
                var v = new CatalogView();
                v.Measure(new Size(900, 700));
                v.Arrange(new Rect(0, 0, 900, 700));
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
                $"CatalogView Dark load failed: {caught.GetType().FullName}: {caught.Message}\n" +
                $"--- InnerException ---\n{caught.InnerException}\n" +
                $"--- StackTrace ---\n{caught.StackTrace}",
                caught);
        }
    }

    [Fact]
    public void CatalogView_LightTheme_LoadsWithoutException()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Light);
                var v = new CatalogView();
                v.Measure(new Size(900, 700));
                v.Arrange(new Rect(0, 0, 900, 700));
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
                $"CatalogView Light load failed: {caught.GetType().FullName}: {caught.Message}\n" +
                $"--- InnerException ---\n{caught.InnerException}\n" +
                $"--- StackTrace ---\n{caught.StackTrace}",
                caught);
        }
    }
}
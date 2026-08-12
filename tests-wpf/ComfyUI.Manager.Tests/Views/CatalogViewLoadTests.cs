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

    [Fact]
    public void CatalogView_LatestVersionBinding_RendersWithoutException()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);

                // 构造一个 ListBox 引用 Theme.xaml 里的 CatalogRowCardTemplate +
                // CatalogCardItemContainerStyle,2 个 entry:1 个 LatestVersion="v0.6.7",
                // 1 个 LatestVersion=null(走 TargetNullValue → "latest: —")
                var app = System.Windows.Application.Current;
                var template = (System.Windows.DataTemplate)app!.Resources["CatalogRowCardTemplate"];
                var containerStyle = (System.Windows.Style)app.Resources["CatalogCardItemContainerStyle"];

                var listBox = new System.Windows.Controls.ListBox
                {
                    ItemTemplate = template,
                    ItemContainerStyle = containerStyle,
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new System.Windows.Thickness(0),
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
                };

                var entries = new System.Collections.Generic.List<ComfyUI.Manager.Models.CatalogEntry>
                {
                    new()
                    {
                        Id = "node-1",
                        Package = "pkg-with-latest",
                        Author = "Alice",
                        Description = "Has version",
                        LatestVersion = "v0.6.7",
                    },
                    new()
                    {
                        Id = "node-2",
                        Package = "pkg-no-latest",
                        Author = "Bob",
                        Description = "No version",
                        LatestVersion = null,
                    },
                };
                listBox.ItemsSource = entries;

                listBox.Measure(new System.Windows.Size(900, 700));
                listBox.Arrange(new System.Windows.Rect(0, 0, 900, 700));
                listBox.UpdateLayout();
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
                $"CatalogView LatestVersion binding render failed: {caught.GetType().FullName}: {caught.Message}\n" +
                $"--- InnerException ---\n{caught.InnerException}\n" +
                $"--- StackTrace ---\n{caught.StackTrace}",
                caught);
        }
    }
}

using System;
using System.IO;
using System.Linq;
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
        string? p1Actual = null;
        string? p2Actual = null;
        string? p3Actual = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);

                // 构造一个 ListBox 引用 Theme.xaml 里的 CatalogRowCardTemplate +
                // CatalogCardItemContainerStyle,3 个 entry 覆盖所有 branch:
                //   p1 LatestVersion="v0.6.7" → "latest: v0.6.7"
                //   p2 LatestVersion=null      → "latest: —" (TargetNullValue)
                //   p3 LatestVersion=""        → "latest: —" (DataTrigger;spec §1)
                var app = System.Windows.Application.Current;
                var template = (System.Windows.DataTemplate)app!.Resources["CatalogRowCardTemplate"];

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
                    new()
                    {
                        Id = "node-3",
                        Package = "pkg-empty-latest",
                        Author = "Carol",
                        Description = "Empty string",
                        LatestVersion = "",
                    },
                };

                // 用 ContentPresenter + DataTemplate 直接 load 每个 entry(绕过 ListBox 虚拟化)
                var presenters = new System.Windows.Controls.ContentPresenter[entries.Count];
                for (int i = 0; i < entries.Count; i++)
                {
                    presenters[i] = new System.Windows.Controls.ContentPresenter
                    {
                        Content = entries[i],
                        ContentTemplate = template,
                        Width = 880,
                    };
                }
                var stack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
                foreach (var cp in presenters) stack.Children.Add(cp);

                stack.Measure(new System.Windows.Size(900, 700));
                stack.Arrange(new System.Windows.Rect(0, 0, 900, 700));
                stack.UpdateLayout();

                // 遍历每个 ContentPresenter 的可视树,找 Grid Row=3 的 latest TextBlock
                for (int i = 0; i < presenters.Length; i++)
                {
                    var grid = FindVisualChildren<System.Windows.Controls.Grid>(presenters[i])
                        .FirstOrDefault(g => g.RowDefinitions.Count == 4);
                    Assert.NotNull(grid);
                    var latestTextBlock = FindVisualChildren<System.Windows.Controls.TextBlock>(grid!)
                        .FirstOrDefault(tb => tb.Text != null && tb.Text.StartsWith("latest:", StringComparison.Ordinal));
                    Assert.NotNull(latestTextBlock);
                    if (i == 0) p1Actual = latestTextBlock!.Text;
                    else if (i == 1) p2Actual = latestTextBlock!.Text;
                    else p3Actual = latestTextBlock!.Text;
                }
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

        // 断言:3 个 entry 的 latest: TextBlock.Text 各自匹配 spec
        Assert.Equal("latest: v0.6.7", p1Actual);
        Assert.Equal("latest: —", p2Actual);
        Assert.Equal("latest: —", p3Actual);
    }

    private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject parent)
        where T : System.Windows.DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var grand in FindVisualChildren<T>(child))
            {
                yield return grand;
            }
        }
    }
}

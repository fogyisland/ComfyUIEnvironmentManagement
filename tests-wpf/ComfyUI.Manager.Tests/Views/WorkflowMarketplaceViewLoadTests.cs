using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

/// <summary>
/// v0.6.19 T9:STA-thread headless load 验证 WorkflowMarketplaceView XAML 解析不抛
/// XamlParseException(任何 Theme.xaml 漏注册 converter / Setter DynamicResource 写错 /
/// pack URI 错都只在真正 load 时炸)。follow LocalNodeListViewLoadTests 模式 +
/// StaFact.RunOnSTA(走 Light palette 默认值)。
/// </summary>
public class WorkflowMarketplaceViewLoadTests
{
    [Fact]
    public void Constructor_LoadsXaml_NoException()
    {
        StaFact.RunOnSTA(() =>
        {
            var view = new WorkflowMarketplaceView();
            Assert.NotNull(view);
        });
    }

    [Fact]
    public void Constructor_DarkTheme_LoadsXaml_NoException()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);
                var v = new WorkflowMarketplaceView();
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
                $"WorkflowMarketplaceView load failed: {caught.GetType().FullName}: {caught.Message}",
                caught);
        }
    }

    [Fact]
    public void Constructor_WithVm_LoadsXaml_NoException()
    {
        var vm = MakeVm();
        StaFact.RunOnSTA(() =>
        {
            var view = new WorkflowMarketplaceView { DataContext = vm };
            Assert.NotNull(view);
        });
    }

    private static WorkflowMarketplaceViewModel MakeVm()
    {
        var settings = new Settings { WorkflowsDirectory = Path.GetTempPath() };
        var marketplace = new StubMarketplace();
        var downloader = new WorkflowDownloader(new HttpClient(), logger: null);
        var scanner = new WorkflowFilesystemScanner(logger: null);
        return new WorkflowMarketplaceViewModel(
            settings, marketplace, downloader, scanner, logger: null);
    }

    private sealed class StubMarketplace : WorkflowMarketplaceService
    {
        public StubMarketplace() : base(Array.Empty<IWorkflowSource>()) { }
        public override Task<IReadOnlyList<WorkflowEntry>> LoadAllAsync(
            string query, int maxResultsPerSource, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkflowEntry>>(Array.Empty<WorkflowEntry>());
    }
}
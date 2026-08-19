using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Services.ModelSources;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

/// <summary>
/// v0.6.20 T8:STA-thread headless load 验证 ModelMarketplaceView XAML 解析不抛
/// XamlParseException(任何 Theme.xaml 漏注册 converter / Setter DynamicResource 写错 /
/// pack URI 错都只在真正 load 时炸)。follow WorkflowMarketplaceViewLoadTests 模式 +
/// StaFact.RunOnSTA(走 Light palette 默认值,WpfTestResources 统一管理 单例 Application)。
///
/// 3 个测试:Empty / WithModels / WithSelectedVersions — 3 个不同 binding 触发路径,
/// 任何 converter error / DataTrigger 拼错都会正好在这 3 个不同 DataContext 形态炸。
/// </summary>
public class ModelMarketplaceViewLoadTests
{
    [Fact]
    public void Load_EmptyVm_DoesNotThrow()
    {
        StaFact.RunOnSTA(() =>
        {
            var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
            var view = new ModelMarketplaceView { DataContext = vm };
            Assert.NotNull(view);
        });
    }

    [Fact]
    public void Load_WithModels_DoesNotThrow()
    {
        StaFact.RunOnSTA(() =>
        {
            var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
            vm.Models.Add(new ModelEntry
            {
                Source = ModelSourceKind.CivitAi,
                SourceId = "1",
                Title = "TestModel",
                Kind = ModelKind.Checkpoint,
                NsfwKind = ModelNsfwKind.SFW,
                Versions = new List<ModelVersionEntry>().AsReadOnly(),
            });
            var view = new ModelMarketplaceView { DataContext = vm };
            Assert.NotNull(view);
        });
    }

    [Fact]
    public void Load_WithSelectedVersions_DoesNotThrow()
    {
        StaFact.RunOnSTA(() =>
        {
            var entry = new ModelEntry
            {
                Source = ModelSourceKind.CivitAi,
                SourceId = "1",
                Title = "T",
                Kind = ModelKind.Checkpoint,
                NsfwKind = ModelNsfwKind.SFW,
                Versions = new List<ModelVersionEntry>().AsReadOnly(),
            };
            var v = new ModelVersionEntry
            {
                SourceVersionId = "v1",
                Name = "1.0",
                PrimaryDownloadUrl = "https://example.invalid",
                SizeBytes = 1,
                Files = new List<ModelFile>().AsReadOnly(),
                Parent = entry,
            };
            var vm = new ModelMarketplaceViewModel(null!, null!, null!, null!, null);
            vm.SelectedVersions.Add(v);
            var view = new ModelMarketplaceView { DataContext = vm };
            Assert.NotNull(view);
        });
    }
}

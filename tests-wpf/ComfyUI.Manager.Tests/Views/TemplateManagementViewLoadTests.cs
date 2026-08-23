using System.IO;
using System.Windows;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views.TemplateManagement;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

/// <summary>
/// v1.0.0 T9:TemplateManagementView XAML STA-thread headless load test.
/// WPF UserControl construction requires STA; wrap in <see cref="StaFact.RunOnSTA"/>
/// like other view-load tests (SettingsViewLoadTests / LocalNodeListViewLoadTests).
/// Any XAML compile error / missing theme resource / binding parse failure throws
/// XamlParseException in the ctor.
/// </summary>
public class TemplateManagementViewLoadTests
{
    [Fact]
    public void View_Loads_WithTemplateManagementViewModel()
    {
        StaFact.RunOnSTA(() =>
        {
            var vm = new TemplateManagementViewModel(
                new ComfyUI.Manager.Models.Settings(),
                editTemplateFactory: null,
                updater: null);
            var view = new TemplateManagementView { DataContext = vm };
            // XAML load is implicit in ctor; if XAML has compile errors, the test won't run
            Assert.NotNull(view);
        });
    }
}

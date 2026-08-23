using System.Threading;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views.TemplateManagement;
using Xunit;

namespace ComfyUI.Manager.Tests.Views.TemplateManagement;

/// <summary>
/// v1.0.0 T14: STA-thread headless XAML load test for EditTemplateDialog.
/// Validates the new SourceKind ComboBox + conditional Local/GitHub fields markup
/// compiles and loads without throwing XamlParseException.
/// WPF Window construction requires STA; wrap in <see cref="StaFact.RunOnSTA"/>
/// like other view-load tests (TemplateManagementViewLoadTests / SettingsViewLoadTests).
/// </summary>
public class EditTemplateDialogLoadTests
{
    [Fact]
    public void Load_WithDefaults_DoesNotThrow()
    {
        StaFact.RunOnSTA(() =>
        {
            var s = new Settings();
            var vm = new EditTemplateDialogViewModel(s, null) { Mode = EditTemplateDialogMode.Add };
            var dlg = new EditTemplateDialog { DataContext = vm };
            // If XAML compiles & loads, this passes. Window.Show not invoked.
            Assert.NotNull(dlg);
        });
    }
}
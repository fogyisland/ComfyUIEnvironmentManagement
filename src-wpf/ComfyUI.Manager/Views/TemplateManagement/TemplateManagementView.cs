// TODO T9: Replace stub with real UserControl in T9. T9 lands the XAML card-list
// layout bound to TemplateManagementViewModel.Templates / IsBuiltIn +
// AddCommand / EditCommand / DeleteCommand / UpdateSourceCommand. T8 only needs
// the type to exist so MainViewModel.ShowTemplateManagement can compile and
// present a non-null CurrentView for the 9th sidebar entry.

using System.Windows.Controls;

namespace ComfyUI.Manager.Views.TemplateManagement;

public class TemplateManagementView : UserControl
{
    public TemplateManagementView() { }
}

using System.Windows;
using System.Windows.Controls;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

/// <summary>
/// v1.0.0 T11:CivitAI lookup dialog。modal Window。
/// LoadAsync 触发点 = Window.Loaded event(同 EditTemplateDialog EditTemplateDialog_Loaded)。
/// Picker ListBox SelectionChanged 调 vm.SelectCandidateAsync → 切 Detail state。
/// Dialog 关 = 用户点关闭(任何 state 都可用)。
/// </summary>
public partial class LocalModelCivitAiDialog : Window
{
    public LocalModelCivitAiDialog()
    {
        InitializeComponent();
        Loaded += LocalModelCivitAiDialog_Loaded;
    }

    private async void LocalModelCivitAiDialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is LocalModelCivitAiDialogViewModel vm)
        {
            await vm.LoadAsync();
        }
    }

    private async void CandidatesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is Models.CivitAiCandidate c
            && DataContext is LocalModelCivitAiDialogViewModel vm)
        {
            await vm.SelectCandidateAsync(c);
        }
    }

    private void OnBackToPicker(object sender, RoutedEventArgs e)
    {
        if (DataContext is LocalModelCivitAiDialogViewModel vm)
        {
            vm.BackToPicker();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

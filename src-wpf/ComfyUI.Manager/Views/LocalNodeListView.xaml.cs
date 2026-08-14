using System.Windows.Controls;

namespace ComfyUI.Manager.Views;

public partial class LocalNodeListView : UserControl
{
    public LocalNodeListView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.LocalNodeListViewModel vm)
            {
                // 首次进入自动 refresh(RelayCommand.Execute 返回 void,fire-and-forget)
                vm.RefreshCommand.Execute(null);
            }
        };
    }
}
using System.Windows.Controls;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public sealed partial class LocalModelsView : UserControl
{
    public LocalModelsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// v1.0.0 T2:kind chip radio button click handler — RadioButton.Checked 是用户点击触发的源,
    /// 把 sender.Tag (KindChip) 写回 VM.ActiveChip。XAML 的 IsChecked OneWay binding 把 VM 状态
    /// 反射回 RadioButton(选中态高亮),但用户输入走这里 → setter 触发 ApplyFilter。
    /// </summary>
    private void KindChip_Checked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is KindChip chip && DataContext is LocalModelsViewModel vm)
        {
            vm.ActiveChip = chip;
        }
    }
}

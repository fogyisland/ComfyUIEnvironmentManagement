using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Controls;

public partial class SpotlightSearchBox : UserControl
{
    public SpotlightSearchBox()
    {
        InitializeComponent();
    }

    /// <summary>取得当前绑定的 VM。DataContext 没绑时为 null。</summary>
    public SpotlightSearchViewModel? Vm => DataContext as SpotlightSearchViewModel;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        switch (e.Key)
        {
            case Key.Escape:
                Vm.CloseCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter:
                Vm.EnterCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down:
                Vm.DownCommand.Execute(null);
                // ListBox 默认接管 Up/Down navigation(同 WPF 标准行为)。
                // 这里不 e.Handled = true,让 ListBox 也拿到事件调整高亮行。
                break;
            case Key.Up:
                Vm.UpCommand.Execute(null);
                break;
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null) return;
        Vm.EnterCommand.Execute(null);
    }
}
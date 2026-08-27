using System.Windows;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

/// <summary>
/// v1.0.0.x #594:启动期路径错位确认弹窗 view。
/// canonical pattern — InstallDialog.xaml.cs:15:
///   DataContext = vm; vm.CloseRequested += () =&gt; Close();
/// Owner 不设(Application.Current.MainWindow 在此处还是 Splash,设 Owner 会冲突)。
/// </summary>
public partial class PathMigrationConfirmDialog : Window
{
    public PathMigrationConfirmDialog(PathMigrationConfirmViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += () => Close();
    }
}
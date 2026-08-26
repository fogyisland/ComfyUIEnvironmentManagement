using System.Windows;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class ComfyUICoursesWindow : Window
{
    public ComfyUICoursesWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 弹非模态 "ComfyUI 课程" 独立窗口 — v1.0.0。
    /// Owner 通常 <c>Application.Current.MainWindow</c>(从主菜单 "ComfyUI 课程" 顶级 dropdown 触发)。
    /// 不需要 projectRoot — 4 个课程名从 resx 加载,无外部文件依赖。
    /// </summary>
    public static void Show(Window owner)
    {
        var vm = new ComfyUICoursesViewModel();
        var dlg = new ComfyUICoursesWindow
        {
            Owner = owner,
            DataContext = vm,
        };
        vm.RequestClose += (_, _) => dlg.Close();
        dlg.Show();
    }
}

using System;
using System.Globalization;
using System.Resources;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// ComfyUI 课程窗口的 VM — v1.0.0。
///
/// 把 v0.6.15.5 起堆在 <see cref="AboutDialogViewModel"/> 里的 4 个课程链接抽到独立对话框:
/// <see cref="CoursesHeader"/> + <see cref="Course51CTO"/> / <see cref="CourseShenYeCG"/> /
/// <see cref="CourseYihuu"/> / <see cref="CourseUdemy"/>。
///
/// AboutDialog 瘦身只保留系统级信息(版本/描述/授权/仓库/问题反馈),这些课程链接
/// 现在对应主菜单 "ComfyUI 课程" 顶级下拉,与 AboutDialog 完全脱离 — spec 重构
/// "把关于拆成 4 个不同下拉框"。
///
/// 当前课程列表只展示文字标签(无 URL)— v0.6.15.5 决定 "先文字后续再加 Hyperlink",
/// v1.0.0 仍未实装链接,继续沿用纯展示,等 spec 加 URL 后再升级为 <see cref="System.Windows.Documents.Hyperlink"/>。
/// </summary>
public sealed class ComfyUICoursesViewModel : ViewModelBase
{
    private static readonly ResourceManager StringsResources = new(
        "ComfyUI.Manager.Resources.Strings",
        typeof(ComfyUICoursesViewModel).Assembly);

    private static string GetString(string key) =>
        StringsResources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public ComfyUICoursesViewModel()
    {
        // Title/Hint 走硬编码中文,匹配 工具/about 周围硬编码菜单风格(见 MainWindow.xaml);
        // 课程 4 项名字(51CTO/深夜CG/一呼/Udemy)从 resx 来 — resx 里早已存在
        // About_CoursesHeader/About_Course_* 5 个 key,搬过来共享即可。
        Title = "ComfyUI 课程";
        Hint = "以下是作者主讲的 ComfyUI 系列课程,持续更新中。";
        CoursesHeader = GetString("About_CoursesHeader");
        Course51CTO = GetString("About_Course_51CTO");
        CourseShenYeCG = GetString("About_Course_ShenYeCG");
        CourseYihuu = GetString("About_Course_Yihuu");
        CourseUdemy = GetString("About_Course_Udemy");
        CloseCommand = new RelayCommand(_ => Close());
    }

    public string Title { get; }
    public string Hint { get; }
    public string CoursesHeader { get; }
    public string Course51CTO { get; }
    public string CourseShenYeCG { get; }
    public string CourseYihuu { get; }
    public string CourseUdemy { get; }
    public RelayCommand CloseCommand { get; }

    public event EventHandler? RequestClose;

    public void Close() => RequestClose?.Invoke(this, EventArgs.Empty);
}

using System;
using System.Globalization;
using System.Reflection;
using System.Resources;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// "关于系统" 对话框的 VM — v1.0.0 拆分后只剩系统级信息。
///
/// 历史(v0.6.5.21 hotfix + v0.6.15.5 + 此次):
/// - AboutDialog 改非模态(Show 而非 ShowDialog)— 用户扫码时主窗口仍可操作;
/// - 二维码 / 课程拆到独立对话框:
///   - <see cref="DonateQrViewModel"/> — 赞助二维码(从主菜单 "赞助作者" 顶级 dropdown 触发);
///   - <see cref="ComfyUIGroupQrViewModel"/> — 微信技术组二维码(从主菜单 "ComfyUI 群组" 顶级 dropdown 触发);
///   - <see cref="ComfyUICoursesViewModel"/> — 4 个课程链接(从主菜单 "ComfyUI 课程" 顶级 dropdown 触发)。
/// - 此 VM 删掉原 OpenDonateQr/OpenComfyUIGroup 命令 + 4 个课程字段,只剩:
///   版本 / 标题 / 描述 / 授权 / 仓库 / 问题反馈 / 关闭;
/// - 路径常量 <see cref="DonateImageFileName"/> / <see cref="DonateImageSubdirectory"/> 仍
///   保留 — 它们是全局路径契约,DonateQrViewModel 跟此处都引用,任何一处改名另一边跟;
/// - <see cref="GroupImageFileName"/> 删掉(职责已迁 ComfyUIGroupQrViewModel 那里自己定义)。
/// </summary>
public sealed class AboutDialogViewModel : ViewModelBase
{
    public const string RepositoryUrlValue = "https://github.com/fogyisland/ComfyUIEnvironmentManagement";
    public const string IssuesUrlValue = RepositoryUrlValue + "/issues";
    public const string LicenseTextValue = "MIT";
    // v1.0.0:目录重构 — asset/ → assets/(复数,仓库一致命名)。
    // 微信支付收款码图片路径:projectRoot/assets/receiveMark.jpg。
    public const string DonateImageFileName = "receiveMark.jpg";
    public const string DonateImageSubdirectory = "assets";

    // csproj 用 <Resource Include="Resources\Strings*.resx"> + MSBuild:_GenerateResxSource,
    // 该 generator 只把 .resx 编进二进制资源,不会生成 strong-typed Strings 类。
    // 这里走 ResourceManager 显式拿值以满足 G15(走 resx),根命名空间 + 默认 culture。
    private static readonly ResourceManager StringsResources = new(
        "ComfyUI.Manager.Resources.Strings",
        typeof(AboutDialogViewModel).Assembly);

    private static string GetString(string key) =>
        StringsResources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public AboutDialogViewModel(string projectRoot)
    {
        _ = projectRoot;   // v1.0.0:分离后此 VM 不再直接读 assets,参数保留兼容旧 ctor 调用。
        Version = (Assembly.GetExecutingAssembly().GetName().Version?.ToString()) ?? "0.0.0";
        Title = GetString("About_Title");
        Description = GetString("About_Description");
        LicenseText = LicenseTextValue;
        RepositoryUrl = RepositoryUrlValue;
        IssuesUrl = IssuesUrlValue;
        CloseCommand = new RelayCommand(_ => Close());
    }

    public string Version { get; }
    public string Title { get; }
    public string Description { get; }
    public string LicenseText { get; }
    public string RepositoryUrl { get; }
    public string IssuesUrl { get; }
    public RelayCommand CloseCommand { get; }

    /// <summary>View code-behind 订阅 → 调 <c>Close()</c>。</summary>
    public event EventHandler? RequestClose;

    public void Close() => RequestClose?.Invoke(this, EventArgs.Empty);
}

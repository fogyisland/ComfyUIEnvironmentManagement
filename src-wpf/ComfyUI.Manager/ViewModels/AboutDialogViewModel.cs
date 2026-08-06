using System;
using System.Globalization;
using System.Reflection;
using System.Resources;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// About 对话框的 VM — v0.6.5.21 + hotfix。堆叠顶~下:标题 + 版本 + 描述 + 授权 + 仓库 / 问题
/// 链接 + "查看赞助二维码"按钮(→ 弹独立 DonateQrWindow 窗口)。
///
/// v0.6.5.21 hotfix 改造:
/// - AboutDialog 改非模态(Show 而非 ShowDialog)— 用户扫码时主窗口仍可操作;
/// - 二维码拆到独立 <see cref="DonateQrViewModel"/> 窗口 — AboutDialog 里只放按钮;
/// - 路径常量 <see cref="DonateImageFileName"/> / <see cref="DonateImageSubdirectory"/> 公开
///   共享给 <see cref="DonateQrViewModel"/>(单源 — 改一处两处都跟);
/// - <see cref="HasDonateImage"/> / <see cref="DonateImagePath"/> / <see cref="DonatePlaceholder"/>
///   / <see cref="CreateDonateImage"/> 全部移走(由 DonateQrViewModel 持有)。
/// </summary>
public sealed class AboutDialogViewModel : ViewModelBase
{
    public const string RepositoryUrlValue = "https://github.com/fogyisland/ComfyUIEnvironmentManagement";
    public const string IssuesUrlValue = RepositoryUrlValue + "/issues";
    public const string LicenseTextValue = "MIT";
    // v0.6.5.21 hotfix:用户桌面 `asset/receiveMark.jpg`(单数)就是微信支付收款码。
    // v0.6.5.21 T9 创建的 `assets/`(复数)是占位,留作未来其他 asset 用,与 donate 无关。
    public const string DonateImageFileName = "receiveMark.jpg";
    public const string DonateImageSubdirectory = "asset";

    // csproj 用 <Resource Include="Resources\Strings*.resx"> + MSBuild:_GenerateResxSource,
    // 该 generator 只把 .resx 编进二进制资源,不会生成 strong-typed Strings 类。
    // 这里走 ResourceManager 显式拿值以满足 G15(走 resx),根命名空间 + 默认 culture。
    private static readonly ResourceManager StringsResources = new(
        "ComfyUI.Manager.Resources.Strings",
        typeof(AboutDialogViewModel).Assembly);

    private static string GetString(string key) =>
        StringsResources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    private readonly string _projectRoot;

    public AboutDialogViewModel(string projectRoot)
    {
        _projectRoot = projectRoot;
        Version = (Assembly.GetExecutingAssembly().GetName().Version?.ToString()) ?? "0.0.0";
        Title = GetString("About_Title");
        Description = GetString("About_Description");
        LicenseText = LicenseTextValue;
        RepositoryUrl = RepositoryUrlValue;
        IssuesUrl = IssuesUrlValue;
        OpenDonateQrButtonText = GetString("About_OpenDonateButton");
        OpenDonateQrCommand = new RelayCommand(_ => OpenDonateQr());
        CloseCommand = new RelayCommand(_ => Close());
    }

    public string Version { get; }
    public string Title { get; }
    public string Description { get; }
    public string LicenseText { get; }
    public string RepositoryUrl { get; }
    public string IssuesUrl { get; }
    public string OpenDonateQrButtonText { get; }
    public RelayCommand OpenDonateQrCommand { get; }
    public RelayCommand CloseCommand { get; }

    private void OpenDonateQr() => OpenDonateQrRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>View code-behind 订阅 → 调 <c>Close()</c>。</summary>
    public event EventHandler? RequestClose;

    /// <summary>View code-behind 订阅 → 弹 DonateQrWindow 独立窗口。</summary>
    public event EventHandler? OpenDonateQrRequested;

    public void Close() => RequestClose?.Invoke(this, EventArgs.Empty);
}

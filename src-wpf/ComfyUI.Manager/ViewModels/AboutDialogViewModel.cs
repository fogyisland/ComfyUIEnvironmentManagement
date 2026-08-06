using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Windows.Media.Imaging;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// About 对话框的 VM — v0.6.5.21。堆叠顶~下:标题 + 版本 + 描述 + 授权 + 仓库 / 问题
/// 链接 + 二维码(<c>&lt;projectRoot&gt;/assets/wechat-donate.png</c>)。
///
/// 二维码加载策略(G10/G11/G19):
/// - <see cref="HasDonateImage"/> 是同步 bool(<c>File.Exists</c>),XAML 用它切换 Image vs 占位;
/// - <see cref="CreateDonateImage"/> 在 UI 线程同步创建 <see cref="BitmapImage"/>(不是异步),
///   View code-behind 在 <c>Loaded</c> 事件里调一次,缺位返 null。
/// </summary>
public sealed class AboutDialogViewModel : ViewModelBase
{
    public const string RepositoryUrlValue = "https://github.com/fogyisland/ComfyUIEnvironmentManagement";
    public const string IssuesUrlValue = RepositoryUrlValue + "/issues";
    public const string LicenseTextValue = "MIT";
    public const string DonateImageFileName = "wechat-donate.png";

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
        DonateImagePath = Path.Combine(projectRoot, "assets", DonateImageFileName);
        HasDonateImage = File.Exists(DonateImagePath);
        DonatePlaceholder = GetString("About_DonatePlaceholder");
        CloseCommand = new RelayCommand(_ => Close());
    }

    public string Version { get; }
    public string Title { get; }
    public string Description { get; }
    public string LicenseText { get; }
    public string RepositoryUrl { get; }
    public string IssuesUrl { get; }
    public string DonateImagePath { get; }
    public bool HasDonateImage { get; }
    public string DonatePlaceholder { get; }
    public RelayCommand CloseCommand { get; }

    /// <summary>UI 线程同步创建 <see cref="BitmapSource"/>;缺位返 null。View code-behind 调。</summary>
    public BitmapSource? CreateDonateImage()
    {
        if (!HasDonateImage) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;     // 同步加载到内存
            img.CreateOptions = BitmapCreateOptions.None;
            img.UriSource = new Uri(DonateImagePath, UriKind.Absolute);
            img.EndInit();
            img.Freeze();   // 跨线程安全
            return img;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>View code-behind 订阅 → 调 <c>Close()</c>。</summary>
    public event EventHandler? RequestClose;

    public void Close() => RequestClose?.Invoke(this, EventArgs.Empty);
}

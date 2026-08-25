using System;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Windows.Media.Imaging;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// ComfyUI 技术组(微信群)二维码独立窗口的 VM — v1.0.0。
/// 跟 <see cref="AboutDialogViewModel"/> 拆开:AboutDialog 改非模态 + 这里 Show 一个独立非模态窗口,
/// 用户扫码时主窗口仍可操作,AboutDialog 也可继续保留。
///
/// 二维码加载模式跟 <see cref="DonateQrViewModel"/> 一致:
/// - <see cref="HasGroupImage"/> 同步 bool;
/// - <see cref="CreateGroupImage"/> UI 线程同步创建,View code-behind 在 Loaded 里调。
/// </summary>
public sealed class ComfyUIGroupQrViewModel : ViewModelBase
{
    // v1.0.0 ComfyUI 技术组微信群二维码:assets/wechatgroup.png
    public const string GroupImageFileName = "wechatgroup.png";
    public const string GroupImageSubdirectory = "assets";

    private static readonly ResourceManager StringsResources = new(
        "ComfyUI.Manager.Resources.Strings",
        typeof(ComfyUIGroupQrViewModel).Assembly);

    private static string GetString(string key) =>
        StringsResources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    private readonly string _projectRoot;

    public ComfyUIGroupQrViewModel(string projectRoot)
    {
        _projectRoot = projectRoot;
        Title = GetString("ComfyUIGroup_Title");
        Hint = GetString("ComfyUIGroup_Hint");
        GroupImagePath = Path.Combine(projectRoot, GroupImageSubdirectory, GroupImageFileName);
        HasGroupImage = File.Exists(GroupImagePath);
        PlaceholderText = GetString("ComfyUIGroup_Placeholder");
        CloseCommand = new RelayCommand(_ => Close());
    }

    public string Title { get; }
    public string Hint { get; }
    public string GroupImagePath { get; }
    public bool HasGroupImage { get; }
    public string PlaceholderText { get; }
    public RelayCommand CloseCommand { get; }

    /// <summary>UI 线程同步创建 <see cref="BitmapSource"/>;缺位返 null。View code-behind 调。</summary>
    public BitmapSource? CreateGroupImage()
    {
        if (!HasGroupImage) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.None;
            img.UriSource = new Uri(GroupImagePath, UriKind.Absolute);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }

    public event EventHandler? RequestClose;

    public void Close() => RequestClose?.Invoke(this, EventArgs.Empty);
}

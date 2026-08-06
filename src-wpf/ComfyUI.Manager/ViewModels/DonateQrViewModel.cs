using System;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Windows.Media.Imaging;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// 赞助二维码独立窗口的 VM — v0.6.5.21 hotfix。
/// 跟 AboutDialog 拆开:AboutDialog 改非模态 + 这里 Show 一个独立非模态窗口,
/// 用户扫码时主窗口仍可操作,AboutDialog 也可继续保留。
///
/// 二维码加载模式跟 <see cref="AboutDialogViewModel.CreateDonateImage"/> 一致:
/// - <see cref="HasDonateImage"/> 同步 bool;
/// - <see cref="CreateDonateImage"/> UI 线程同步创建,View code-behind 在 Loaded 里调。
/// </summary>
public sealed class DonateQrViewModel : ViewModelBase
{
    // 跟 AboutDialogViewModel 共用字符串/常量(单源 — 路径来源唯一)
    public const string DonateImageFileName = AboutDialogViewModel.DonateImageFileName;
    public const string DonateImageSubdirectory = AboutDialogViewModel.DonateImageSubdirectory;

    private static readonly ResourceManager StringsResources = new(
        "ComfyUI.Manager.Resources.Strings",
        typeof(DonateQrViewModel).Assembly);

    private static string GetString(string key) =>
        StringsResources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    private readonly string _projectRoot;

    public DonateQrViewModel(string projectRoot)
    {
        _projectRoot = projectRoot;
        Title = GetString("Donate_Title");
        Hint = GetString("Donate_Hint");
        DonateImagePath = Path.Combine(projectRoot, DonateImageSubdirectory, DonateImageFileName);
        HasDonateImage = File.Exists(DonateImagePath);
        PlaceholderText = GetString("Donate_Placeholder");
        CloseCommand = new RelayCommand(_ => Close());
    }

    public string Title { get; }
    public string Hint { get; }
    public string DonateImagePath { get; }
    public bool HasDonateImage { get; }
    public string PlaceholderText { get; }
    public RelayCommand CloseCommand { get; }

    /// <summary>UI 线程同步创建 <see cref="BitmapSource"/>;缺位返 null。View code-behind 调。</summary>
    public BitmapSource? CreateDonateImage()
    {
        if (!HasDonateImage) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.None;
            img.UriSource = new Uri(DonateImagePath, UriKind.Absolute);
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

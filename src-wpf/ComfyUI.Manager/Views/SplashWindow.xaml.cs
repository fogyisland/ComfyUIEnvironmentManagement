using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class SplashWindow : Window
{
    private readonly SplashViewModel _vm;

    public SplashWindow(SplashViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        // v0.6.11+ dashboard/splash polish:4 阶段进度通知 hook
        _vm.StageProgressChanged += OnStageProgressChanged;
        // 用户手动 Alt+F4 关闭时也 raise,让 App/FadeCompleted handler 收到通知
        Closed += (_, _) => _vm.RaiseFadeCompleted();

        // v0.6.8 G6: Image 加载失败静默 → Image 控件空,文本兜底显示
        // 用 ImageFailed 事件捕获失败原因(典型:assets/ComfyUI.png 缺失或格式损坏)
        Loaded += OnLoadedSubscribeImageFailed;
    }

    private void OnLoadedSubscribeImageFailed(object sender, RoutedEventArgs e)
    {
        // 找 XAML 里第一个 Image(主图)订阅 ImageFailed
        var image = FindFirstImage(this);
        if (image is not null) image.ImageFailed += OnMainImageFailed;
    }

    private static Image? FindFirstImage(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Image img) return img;
            var nested = FindFirstImage(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void OnMainImageFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        // 无 AppLogger 注入(本 Window 不依赖 App.xaml.cs 的 _logger 字段),
        // 用 Debug.WriteLine 兜底 — 用户查不到日志,但视觉上仅显示文本,影响小。
        System.Diagnostics.Debug.WriteLine(
            $"splash image failed: {e.ErrorException?.Message ?? "unknown"}");
    }

    /// <summary>
    /// v0.6.11+ dashboard/splash polish:阶段进度推进通知。
    /// ProgressBar / TextBlock 的 Value 已经由 VM 的
    /// RaisePropertyChanged(StageRowsPercent) 直接驱动,这里不需要额外动作 —
    /// 保留空 hook 供后续加逐格动画 / 音效用。
    /// </summary>
    private void OnStageProgressChanged()
    {
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SplashViewModel.IsFading) && _vm.IsFading)
        {
            var storyboard = (Storyboard)Resources["FadeOutStoryboard"];
            storyboard.Begin(this);
        }
    }

    private void OnFadeOutCompleted(object? sender, System.EventArgs e)
    {
        // Storyboard 跑完 800ms → 调 vm raise → Close → Closed 事件再 raise 一次
        // (但 vm.RaiseFadeCompleted() 有 _disposed 幂等闭锁防双 invoke)
        _vm.RaiseFadeCompleted();
        Close();
    }
}
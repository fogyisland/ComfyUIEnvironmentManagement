// v0.6.9.3 T4:主题切换按钮宿主。绑 IThemeService(从 App.ThemeService 取,或测试
// 注入 ThemeServiceForTest)。
// click handler 计算 next mode(Light↔Dark)调 Apply,不等 WPF ToggleButton 自动
// 翻 IsChecked — SunMoonIconButton 的 IsChecked 完全由本类显式 set,跟
// ThemeService.Current == Light 同步;订阅 Applied event 反向 sync。
using System.Globalization;
using System.Resources;
using System.Windows;
using System.Windows.Controls;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.Controls;

public partial class ThemeToggleButton : UserControl
{
    private static readonly ResourceManager StringsResources = new(
        "ComfyUI.Manager.Resources.Strings",
        typeof(ThemeToggleButton).Assembly);

    private IThemeService? _subscribed;

    /// <summary>
    /// v0.6.9.3 T4:测试 seam。生产路径 OnLoaded 取 <c>App.ThemeService</c>;
    /// 测试在 new ThemeToggleButton() 后 set 此属性,OnLoaded 优先用它。
    /// null → 走生产路径。
    /// </summary>
    internal IThemeService? ThemeServiceForTest { get; set; }

    public ThemeToggleButton()
    {
        InitializeComponent();

        // Subscribe ToggleButton.Click(ButtonBase.Click 是 bubbling routed event)。
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // Loaded 后 SunMoon 已构建,SubscribeClick() 安全。
        SunMoon.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
            new RoutedEventHandler(OnSunMoonClick));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 找 ThemeService(只在 Loaded 后取 — 应用在 Loaded 时保证 ThemeService 已赋值)
        var svc = ThemeServiceForTest ?? (Application.Current as App)?.ThemeService;
        if (svc is null) return;

        // 初始 IsChecked 镜像 svc.Current == Light
        SyncIsChecked(svc.Current);

        // 订阅 Applied 后续反向同步
        _subscribed = svc;
        _subscribed.Applied += OnThemeApplied;

        // 初始 tooltip
        UpdateTooltip(svc.Current);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed is not null)
        {
            _subscribed.Applied -= OnThemeApplied;
            _subscribed = null;
        }
    }

    private void OnThemeApplied(object? sender, ThemeMode mode)
    {
        SyncIsChecked(mode);
        UpdateTooltip(mode);
    }

    private void SyncIsChecked(ThemeMode mode)
    {
        SunMoon.IsChecked = mode == ThemeMode.Light;
    }

    private void UpdateTooltip(ThemeMode mode)
    {
        SunMoon.ToolTip = mode == ThemeMode.Light
            ? StringsResources.GetString("ThemeToggle_Tooltip_Light", CultureInfo.CurrentUICulture)
              ?? "当前为浅色 — 点击切换到深色"
            : StringsResources.GetString("ThemeToggle_Tooltip_Dark", CultureInfo.CurrentUICulture)
              ?? "当前为深色 — 点击切换到浅色";
    }

    private void OnSunMoonClick(object sender, RoutedEventArgs e)
    {
        var svc = ThemeServiceForTest ?? (Application.Current as App)?.ThemeService;
        if (svc is null) return;

        // 当前是 Light → Apply(Dark),反之亦然。IsChecked 此时已被 ToggleButton 翻过,
        // 不能用 IsChecked 算 next mode — 用 svc.Current 直接拿权威值。
        var next = svc.Current == ThemeMode.Light ? ThemeMode.Dark : ThemeMode.Light;
        svc.Apply(next);
    }
}
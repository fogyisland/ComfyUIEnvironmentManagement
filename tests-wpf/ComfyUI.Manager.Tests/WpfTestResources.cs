using System;
using System.Threading;
using System.Windows;

namespace ComfyUI.Manager.Tests;

/// <summary>
/// v0.6.9.3 final-review fix:统一的 WPF Application + 资源字典初始化 helper。
///
/// 三个 load test 文件(SettingsViewLoadTests / GearIconButtonLoadTests /
/// ThemeToggleButtonTests)在 v0.6.9.3 final review 前各自维护私有 <c>_resourcesLoaded</c>
/// + lock + new Application() 逻辑;xunit2 并行跑多个 STA 测试 class 时两个 helper
/// thread 同时进 <c>Application.Current is null</c> 分支、两次 <c>new Application()</c>,
/// 第二个抛 <c>InvalidOperationException: 不能在同一 AppDomain 中创建多个
/// System.Windows.Application 实例</c>。测试结果:23 PASS / 3 FAIL(分支专项),
/// 全套 761 PASS / 5 FAIL / 1 SKIP 多出 3 个新 FAIL。
///
/// <see cref="StaFact"/> 也有同款初始化逻辑(走 Interlocked 单例 + 加载 Palette.Light),
/// 但三个 load test 没用 StaFact → 各写一套 → race。
///
/// 修法:抽出本 helper,所有 load test 走同一入口;StaFact 内部也委托给本 helper
/// (保留 Light palette 默认值,跟 v0.6.9.3 之前行为完全一致)。
///
/// 用法:
/// <code>
/// WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);
/// </code>
///
/// 注意:必须在 STA thread 内调用(因为 <c>Application.Current</c> 的 STA 模型跟
/// runner 线程的 MTA 不兼容)。所有 load test 走 <see cref="StaFact.RunOnSTA"/>
/// 或自己 spin STA thread(本 helper 在调用方控制的 STA thread 内首次被调)。
/// </summary>
public static class WpfTestResources
{
    public enum PaletteVariant
    {
        Light,
        Dark,
    }

    private const string ThemeUri = "/ComfyUI.Manager;component/Resources/Theme.xaml";
    private const string LightPaletteUri = "/ComfyUI.Manager;component/Themes/Palette.Light.xaml";
    private const string DarkPaletteUri = "/ComfyUI.Manager;component/Themes/Palette.Dark.xaml";

    // 三段独立 flag — 互不阻塞,Light/Dark 可同时加载(测试套件混合跑)。
    // 每个 flag 单独 lock,因为 Application.Current 的 new Application() 一次,
    // palette 槽位可分别添加。
    private static int _appReady;
    private static int _themeReady;
    private static int _lightReady;
    private static int _darkReady;
    private static readonly object _gate = new();

    /// <summary>
    /// 确保 <see cref="Application.Current"/> 存在且 Theme.xaml + 指定 palette
    /// 已合并进 <see cref="Application.Resources"/>。<see cref="StaFact.RunOnSTA"/>
    /// 走 default(<see cref="PaletteVariant.Light"/>) 保持 v0.6.9.3 之前行为。
    /// </summary>
    public static void EnsureLoaded(PaletteVariant variant = PaletteVariant.Light)
    {
        EnsureApplication();
        EnsureTheme();
        EnsurePalette(variant);
    }

    private static void EnsureApplication()
    {
        if (Interlocked.CompareExchange(ref _appReady, 1, 0) != 0) return;
        lock (_gate)
        {
            if (Application.Current is null)
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            }
            // 重新标记,即使本线程不是第一个进入也确保 flag 已置 1
            Interlocked.Exchange(ref _appReady, 1);
        }
    }

    private static void EnsureTheme()
    {
        if (Interlocked.CompareExchange(ref _themeReady, 1, 0) != 0) return;
        lock (_gate)
        {
            if (HasMergedDictEndingWith("Theme.xaml"))
            {
                Interlocked.Exchange(ref _themeReady, 1);
                return;
            }
            TryAddMergedDict(ThemeUri);
            Interlocked.Exchange(ref _themeReady, 1);
        }
    }

    private static void EnsurePalette(PaletteVariant variant)
    {
        var flag = variant == PaletteVariant.Light ? ref _lightReady : ref _darkReady;
        var fileName = variant == PaletteVariant.Light ? "Palette.Light.xaml" : "Palette.Dark.xaml";
        var uri = variant == PaletteVariant.Light ? LightPaletteUri : DarkPaletteUri;

        if (Interlocked.CompareExchange(ref flag, 1, 0) != 0) return;
        lock (_gate)
        {
            if (HasMergedDictEndingWith(fileName))
            {
                Interlocked.Exchange(ref flag, 1);
                return;
            }
            TryAddMergedDict(uri);
            Interlocked.Exchange(ref flag, 1);
        }
    }

    private static bool HasMergedDictEndingWith(string suffix)
    {
        if (Application.Current?.Resources.MergedDictionaries is not { } merged) return false;
        foreach (var d in merged)
        {
            if (d.Source is { } src && src.ToString().EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static void TryAddMergedDict(string packUri)
    {
        // 失败兜底 — 测试失败由 assert 接管(同 StaFact 既有行为)。
        try
        {
            Application.Current?.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(packUri, UriKind.Relative),
            });
        }
        catch
        {
            // 静默 — pack URI 加载失败通常意味着 production 代码那边也有问题,
            // 此处抛只会让 race 相关的根因分析更复杂。
        }
    }
}
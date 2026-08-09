using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using Xunit;

namespace ComfyUI.Manager.Tests;

/// <summary>
/// v0.6.6 T2:STA 跑测试 helper。
/// xunit 2.x 不内置 STA 支持。<see cref="RunOnSTA(System.Action)"/> 把 WPF 元素构造
/// marshaling 到 STA helper thread,主 runner 线程 Join 等结果;异常 marshal 回去 throw。
/// 测试环境下手动把 <c>Resources/Theme.xaml</c> 合并到 <see cref="Application.Resources"/>,
/// 这样裸 <c>new MyView()</c> 能解析到 <c>ZeroCountToVisibility</c> 等 converter;
/// 生产环境走 App.xaml 的合并,不会进此分支。
///
/// <para>
/// 用法:[Fact] test 方法 body 里调 <see cref="RunOnSTA(System.Action)"/>,在
/// 该 action 内构造 <see cref="System.Windows.Controls.UserControl"/> 或
/// <see cref="System.Windows.Window"/> 并断言。
/// </para>
/// </summary>
public static class StaFact
{
    private static int _themeLoaded;

    /// <summary>
    /// 在 STA thread 上跑 <paramref name="action"/>,并确保 <c>Resources/Theme.xaml</c>
    /// 已合并到 <see cref="Application.Resources"/>(缺省 Application.Current 在
    /// 测试下为 null,需要 new 一个占位)。
    /// </summary>
    public static void RunOnSTA(Action action)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));

        Exception? caught = null;
        var t = new Thread(() =>
        {
            try
            {
                EnsureAppAndThemeResources();
                action();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (caught is not null) throw caught;
    }

    private static void EnsureAppAndThemeResources()
    {
        if (Interlocked.CompareExchange(ref _themeLoaded, 1, 0) != 0) return;

        if (Application.Current is null)
        {
            // 占位 Application,只为让 ResourceDictionary 可以 merge。
            new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }

        var already = false;
        if (Application.Current?.Resources.MergedDictionaries is { } merged)
        {
            foreach (var d in merged)
            {
                if (d.Source is { } src && src.ToString().EndsWith("Theme.xaml", StringComparison.OrdinalIgnoreCase))
                {
                    already = true; break;
                }
            }
        }
        if (already) return;

        // Resources/Theme.xaml 是 Resource(SatelliteResource),可走 pack URI。
        var themeUri = new Uri("/ComfyUI.Manager;component/Resources/Theme.xaml", UriKind.Relative);
        try
        {
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = themeUri });
        }
        catch
        {
            // 失败兜底:尝试同源 alt pack URI。不抛 — 测试失败由 assert 接管。
        }

        // v0.6.9 T1:Theme.xaml 删了 8 colors + 8 brushes,搬到 Themes/Palette.*.xaml。
        // STA 测试只 merge Theme.xaml 不够,view XAML 仍 StaticResource PrimaryBrush / BackgroundBrush /
        // SurfaceBrush 等 → 必须再 merge 一个 palette dict(默认 Light 跟现状对齐,
        // T2 加 ThemeService 后再考虑让 StaFact 也支持按测试切主题)。
        var paletteAdded = false;
        if (Application.Current?.Resources.MergedDictionaries is { } mergedDicts)
        {
            foreach (var d in mergedDicts)
            {
                if (d.Source is { } src && src.ToString().EndsWith(
                        "Palette.Light.xaml", StringComparison.OrdinalIgnoreCase))
                {
                    paletteAdded = true; break;
                }
            }
        }
        if (!paletteAdded)
        {
            var paletteUri = new Uri("/ComfyUI.Manager;component/Themes/Palette.Light.xaml", UriKind.Relative);
            try
            {
                Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = paletteUri });
            }
            catch
            {
                // 失败兜底 — 测试失败由 assert 接管。
            }
        }
    }
}

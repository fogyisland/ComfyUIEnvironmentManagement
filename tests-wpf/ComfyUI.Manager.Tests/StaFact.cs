using System;
using System.Threading;
using System.Windows;
using Xunit;

namespace ComfyUI.Manager.Tests;

/// <summary>
/// v0.6.6 T2:STA 跑测试 helper。
/// xunit 2.x 不内置 STA 支持。<see cref="RunOnSTA(System.Action)"/> 把 WPF 元素构造
/// marshaling 到 STA helper thread,主 runner 线程 Join 等结果;异常 marshal 回去 throw。
///
/// 资源加载委托给 <see cref="WpfTestResources.EnsureLoaded"/> —— 所有 load test
/// 共享同一个 Application 单例,避免 v0.6.9.3 final review 报告的 race:
/// 多个测试 class 各自 new Application 第二次抛 InvalidOperationException。
/// 默认走 Light palette 跟 v0.6.9.3 之前的行为一致;SettingsView / GearIconButton
/// 的 Dark 测试自行调用 <see cref="WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark)"/>。
///
/// <para>
/// 用法:[Fact] test 方法 body 里调 <see cref="RunOnSTA(System.Action)"/>,在
/// 该 action 内构造 <see cref="System.Windows.Controls.UserControl"/> 或
/// <see cref="System.Windows.Window"/> 并断言。
/// </para>
/// </summary>
public static class StaFact
{
    /// <summary>
    /// 在 STA thread 上跑 <paramref name="action"/>,并确保
    /// <c>Resources/Theme.xaml</c> + <c>Themes/Palette.Light.xaml</c> 已合并到
    /// <see cref="Application.Resources"/>(缺省 Application.Current 在测试下为
    /// null,需要 new 一个占位)。
    /// </summary>
    public static void RunOnSTA(Action action)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));

        Exception? caught = null;
        var t = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Light);
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
}
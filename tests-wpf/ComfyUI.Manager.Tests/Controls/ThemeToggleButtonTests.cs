// v0.6.9.3 T4:验证 ThemeToggleButton + SunMoonIconButton 主题切换链路。
// 用 FakeThemeService 注入避免依赖 Application.Current + 真实 palette dict。
// 所有断言在 STA 线程内完成(STA-created WPF 元素跨线程访问抛 VerifyAccess)。
//
// v0.6.9.3 final-review fix:统一走 WpfTestResources 加载 Theme + Palette.Light
// (STA helper 内部 new Application 单例,避免 race 抛 InvalidOperationException)。
using System;
using System.Windows;
using ComfyUI.Manager.Controls;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Controls;

/// <summary>
/// 极简 IThemeService — 记录 Apply 调用、暴露可控 Current + Applied event。
/// </summary>
internal sealed class FakeThemeService : IThemeService
{
    public ThemeMode Current { get; private set; } = ThemeMode.Dark;
    public System.Collections.Generic.List<ThemeMode> AppliedCalls { get; } = new();

    // v0.6.9.3 final-review fix:CS0067 — interface 要求声明 event,即使 fake
    // 不触发也保留(跟 Production ThemeService 同款 event 签名,MainWindow 接
    // cross-fade overlay 时订阅 Production 的 ThemeChanging;fake 不暴露信号
    // 给 listener 是 by design,无关测试断言)。
#pragma warning disable CS0067 // event 永远不被本 fake 触发,接口合约要求保留
    public event EventHandler<ThemeMode>? ThemeChanging;
#pragma warning restore CS0067

    public event EventHandler<ThemeMode>? Applied;

    public void Apply(ThemeMode mode)
    {
        Current = mode;
        AppliedCalls.Add(mode);
        Applied?.Invoke(this, mode);
    }
}

public class ThemeToggleButtonTests
{
    [Fact]
    public void Initial_IsChecked_Mirrors_Current_Light()
    {
        var fake = new FakeThemeService();
        fake.Apply(ThemeMode.Light);  // 启动后 Current 落定到 Light

        RunOnSTA(fake, button =>
        {
            Assert.True(button.IsCheckedSunMoon());
            return true;
        });
    }

    [Fact]
    public void Initial_IsChecked_Mirrors_Current_Dark()
    {
        var fake = new FakeThemeService { /* Current = Dark by default */ };

        RunOnSTA(fake, button =>
        {
            Assert.False(button.IsCheckedSunMoon());
            return true;
        });
    }

    [Fact]
    public void Click_When_Light_Fires_Apply_Dark()
    {
        var fake = new FakeThemeService();
        fake.Apply(ThemeMode.Light);

        RunOnSTA(fake, button =>
        {
            fake.AppliedCalls.Clear();
            button.RaiseSunMoonClick();

            Assert.Equal(new[] { ThemeMode.Dark }, fake.AppliedCalls);
            return true;
        });
    }

    [Fact]
    public void Click_When_Dark_Fires_Apply_Light()
    {
        var fake = new FakeThemeService { /* Current = Dark */ };

        RunOnSTA(fake, button =>
        {
            fake.AppliedCalls.Clear();
            button.RaiseSunMoonClick();

            Assert.Equal(new[] { ThemeMode.Light }, fake.AppliedCalls);
            return true;
        });
    }

    [Fact]
    public void Applied_Event_From_ThemeService_Syncs_IsChecked()
    {
        var fake = new FakeThemeService { /* Current = Dark */ };

        RunOnSTA(fake, button =>
        {
            Assert.False(button.IsCheckedSunMoon());

            fake.Apply(ThemeMode.Light);
            Assert.True(button.IsCheckedSunMoon());

            fake.Apply(ThemeMode.Dark);
            Assert.False(button.IsCheckedSunMoon());
            return true;
        });
    }

    [Fact]
    public void Click_Twice_Toggles_Back_To_Original_Mode()
    {
        var fake = new FakeThemeService { /* Dark */ };
        RunOnSTA(fake, button =>
        {
            Assert.False(button.IsCheckedSunMoon());

            button.RaiseSunMoonClick();  // → Light
            Assert.True(button.IsCheckedSunMoon());

            button.RaiseSunMoonClick();  // → Dark
            Assert.False(button.IsCheckedSunMoon());

            Assert.Equal(new[] { ThemeMode.Light, ThemeMode.Dark }, fake.AppliedCalls);
            return true;
        });
    }

    // ---- helpers ----

    private static void RunOnSTA(IThemeService fake, Func<ThemeToggleButtonOnSTA, bool> action)
    {
        Exception? caught = null;

        var t = new System.Threading.Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Light);

                // Wrap 在 Window 内 — Loaded 事件需要控件进 visual tree 才 fire,
                // Measure/Arrange alone 不触发 Loaded。
                var btn = new ThemeToggleButton
                {
                    ThemeServiceForTest = fake,
                };
                var window = new Window
                {
                    Content = btn,
                    Width = 100,
                    Height = 100,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000,
                };
                window.Show();
                window.UpdateLayout();
                // Pump dispatcher 让 Loaded event 触发
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    new System.Action(() => { }),
                    System.Windows.Threading.DispatcherPriority.Background);

                var wrapper = new ThemeToggleButtonOnSTA(btn);
                action(wrapper);

                window.Close();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
        t.Join();

        if (caught is not null) throw caught;
    }
}

/// <summary>
/// STA-context 操作 ThemeToggleButton 的 helper(wrap FindName + 触发 click)。
/// 必须在 STA 线程上实例化并使用。
/// </summary>
internal sealed class ThemeToggleButtonOnSTA
{
    private readonly ThemeToggleButton _btn;
    private readonly SunMoonIconButton _sunMoon;
    private readonly System.Windows.Controls.Primitives.ToggleButton _toggle;

    public ThemeToggleButtonOnSTA(ThemeToggleButton btn)
    {
        _btn = btn;
        _sunMoon = (SunMoonIconButton)btn.FindName("SunMoon")!;
        _toggle = (System.Windows.Controls.Primitives.ToggleButton)_sunMoon.FindName("Toggle")!;
    }

    public bool IsCheckedSunMoon() => _sunMoon.IsChecked;

    public void RaiseSunMoonClick()
    {
        // 触发 ToggleButton.Click(RoutedEventArgs)— ToggleButton 自动翻 IsChecked。
        // ThemeToggleButton 已 AddHandler(ButtonBase.ClickEvent, OnSunMoonClick),
        // 本方法只 raise event,bubbling 让 handler 接管。
        _toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }
}
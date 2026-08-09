// v0.6.9.3 T4:验证 ThemeToggleButton + SunMoonIconButton 主题切换链路。
// 用 FakeThemeService 注入避免依赖 Application.Current + 真实 palette dict。
// 所有断言在 STA 线程内完成(STA-created WPF 元素跨线程访问抛 VerifyAccess)。
using System;
using System.Collections.Generic;
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
    public List<ThemeMode> AppliedCalls { get; } = new();

    public event EventHandler<ThemeMode>? ThemeChanging;
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
                EnsureTestResources();

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

    private static void EnsureTestResources()
    {
        if (Application.Current is null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }

        var alreadyHasTheme = false;
        foreach (var d in Application.Current.Resources.MergedDictionaries)
        {
            if (d.Source is { } src && src.ToString().EndsWith("Theme.xaml", StringComparison.OrdinalIgnoreCase))
            {
                alreadyHasTheme = true;
                break;
            }
        }
        if (alreadyHasTheme) return;

        var theme = new ResourceDictionary
        {
            Source = new Uri(
                "/ComfyUI.Manager;component/Resources/Theme.xaml",
                UriKind.Relative)
        };
        var palette = new ResourceDictionary
        {
            Source = new Uri(
                "/ComfyUI.Manager;component/Themes/Palette.Light.xaml",
                UriKind.Relative)
        };
        Application.Current.Resources.MergedDictionaries.Add(theme);
        Application.Current.Resources.MergedDictionaries.Add(palette);
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
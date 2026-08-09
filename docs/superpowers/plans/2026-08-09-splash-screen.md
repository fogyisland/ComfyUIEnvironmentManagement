# v0.6.8 Splash Screen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 程序启动时显示一张 AI 生成的主图 + 软件标题 + 副标语 + 版本号,展示 ≥3 秒后渐变消失,无网络依赖、无 API key、零运行期成本。

**Architecture:** 静态打包的 AI 生成主图(`asset/splash.png` 维护者离线 AI 生成提交)+ 自定义 WPF Window 承载图 + 文 + 渐变动画 + 最少显示时长计时。`App.OnStartup` 第一句后立即实例化并 `Show()`,后续服务初始化在 splash 后台完成,MainWindow 加载完后通知 VM 触发 fade。

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · hand-rolled MVVM(`ViewModelBase` / INPC)· `DispatcherTimer` · `Storyboard` (XAML resources)· `BitmapImage` pack URI 资源加载 · csproj `<None Include="..\..\asset\**\*">` asset 拷贝规则(已为 DonateQrWindow 服务)。

**base SHA:** `e044e15`(v0.6.7.5 SHIP-READY + final review Critical fix)

**Spec:** `docs/superpowers/specs/2026-08-09-splash-screen-design.md`

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| G1 | Splash 必须在 `base.OnStartup(e)` 之后、所有服务初始化之前 `Show()`(`dbFactory` / `envRepo` / `logger` 之前),保证用户 0ms 看到 splash | spec G1 |
| G2 | 静态图片:`asset/splash.png` 由维护者用 AI 工具离线生成后提交,无运行时 AI 调用、无 API key 存储、无网络依赖 | spec G2 + brainstorm 决策 1 |
| G3 | Splash 显示时机 = 每次启动,无 Settings 频率配置(无 UI、不留 toggle) | spec G3 + brainstorm 决策 2 |
| G4 | Splash 内容 = 1 张主图(背景)+ 标题(软件名)+ 副标语(1 行)+ 版本号 — 4 元素,不轮播不切换 | spec G4 + brainstorm 决策 3 |
| G5 | 消失 = `MainWindow.Loaded` 触发 VM 的 `NotifyMainWindowReady()`,VM 内部 `DispatcherTimer` 计算 elapsed,`elapsed >= MinDisplayTime(3s)` 才允许 fade;fade 用 `Storyboard` opacity 1→0 / 800ms;fade 完成 self-close | spec G5 + brainstorm 决策 4 |
| G6 | 错误全部静默:`asset/splash.png` 缺失 → Image 控件空但文本兜底显示;`AppLogger.Info` 记一行 | spec G6 |
| G7 | 不 bump version / 不发 release zip / 无 MEMORY commit | spec G7 + `feedback_no_rebuild_zip.md` + `feedback_no_zip.md` |
| G8 | 测试只测 VM,不测 WPF Window(STA 抛异常;项目现有惯例) | spec G8 + `feedback_wpf_dialog_close_requested.md` |
| G9 | VM 暴露 `NotifyMainWindowReady()` 方法 + `FadeCompleted` event;`App` 拿 `FadeCompleted` 后无需做任何事 | spec G9 |
| G10 | 渐变期间 Splash 仍 `Topmost=true` 遮住 MainWindow,避免 MainWindow 闪现空窗再被 splash 盖上 | spec G10 |
| G11 | 中文 hard-code 在 XAML(项目 AboutDialog 同款 i18n 处理);M1 不抽 resx | spec G11 |
| G12 | Splash 不抢焦点(`ShowActivated=False`)、不出现在 taskbar(`ShowInTaskbar=False`)、`Owner=null` | spec G12 |
| G13 | VM `DispatcherTimer` 必须经由 `internal Func<Action, TimeSpan, IDisposable>? TimerFactory` seam 注入 fake timer,生产路径给 real `DispatcherTimer`,测试给 fake | spec Testing Strategy |
| G14 | 沿用既有 csproj `..\..\asset\**\*` 拷贝规则 — `asset/splash.png` 自动拷到 output,无需改 csproj | spec File Structure |
| G15 | TimerFactory seam 默认 null,VM 首次 `NotifyMainWindowReady()` 时 lazy-new 一个 `DispatcherTimer` 实例,后续复用 | 设计细节 |

---

## File Structure

### Create

| 文件 | 行数(估) | 职责 |
|---|---|---|
| `src-wpf/ComfyUI.Manager/ViewModels/SplashViewModel.cs` | ~90 | Title / Tagline / Version / IsFading;`NotifyMainWindowReady()` + `FadeCompleted` event + `RaiseFadeCompleted()`;TimerFactory seam + 内部 `DispatcherTimer`;`MinDisplayTime=3s` + `FadeOutDuration=800ms` 常量 |
| `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml` | ~55 | 900×540 无边框 Window,圆角 Border + Image 背景 + 右下叠加 Title/Tagline/Version 文字;XAML 资源声明 `FadeOutStoryboard` |
| `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml.cs` | ~35 | 构造接 vm + DataContext + PropertyChanged 监听 IsFading 触发 Storyboard + OnFadeOutCompleted 调 vm.RaiseFadeCompleted() + Close;Closed 事件也调 RaiseFadeCompleted |
| `src-wpf/ComfyUI.Manager/AppVersionInfo.cs` | ~10 | 静态 helper:`public static string Current => $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"}"`(若项目内已有类似 helper,直接复用,不新建) |
| `asset/splash.png` | ~50KB | 800×450 PNG(16:9),维护者用 AI 工具离线生成,提交到 git |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SplashViewModelTests.cs` | ~140 | 5 测试:初始态 / NotifyMainWindowReady 早于 Min 不 fade / NotifyMainWindowReady 晚于 Min 触发 fade / StartFadeOutRaisesFadeCompleted / 已 fade 后 NotifyMainWindowReady NoOp |

### Modify

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | `OnStartup` 在 `base.OnStartup(e)` 之后立即 new+show SplashWindow(在 `var projectRoot = ...` 之前);MainWindow `Show()` 之后调 `_splashVm.NotifyMainWindowReady()`;新增 `private SplashWindow? _splash; private SplashViewModel? _splashVm;` 字段 |

### Delete

无。

### Keep (unchanged)

- `AboutDialog` / `DonateQrWindow` / `EnvStartStatusViewModel` — splash 是独立功能,不交叉
- `csproj` 的 `..\..\asset\**\*` 拷贝规则 — splash.png 自动复用,无需改
- 顶部菜单 / Settings / 既有所有服务 — 不动
- v0.6.7.5/v0.6.7.6 全部代码 — 文件集不相交

---

## Tasks

### Task 1: `SplashViewModel` + 5 单元测试

**Files:**
- Create: `src-wpf/ComfyUI.Manager/ViewModels/SplashViewModel.cs`(~90 行)
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SplashViewModelTests.cs`(~140 行)

**Interfaces:**
- Consumes: `ViewModelBase`(既有,在 `ComfyUI.Manager.ViewModels`)
- Produces:
  ```csharp
  namespace ComfyUI.Manager.ViewModels;

  public class SplashViewModel : ViewModelBase
  {
      public const double MinDisplaySeconds = 3.0;
      public const double FadeOutSeconds = 0.8;

      public SplashViewModel(string title, string tagline, string version);

      public string Title { get; }
      public string Tagline { get; }
      public string Version { get; }
      public bool IsFading { get; private set; }

      // v0.6.8 测试 seam: 生产路径默认 null, 首次 NotifyMainWindowReady
      // lazy-new 一个 DispatcherTimer; 测试路径 set 一个 fake(立即同步触发 Tick)。
      internal Func<Action, TimeSpan, IDisposable>? TimerFactory { get; set; }

      public void NotifyMainWindowReady();
      public event Action? FadeCompleted;

      // 内部逻辑也 internal 让测试直接调,生产路径不直接调
      internal void StartFadeOut();

      // 幂等闭锁 — Storyboard Completed → Close → Closed event 二次触发防双 invoke
      internal void RaiseFadeCompleted();
  }
  ```

- [ ] **Step 1: Write failing tests**

Create `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SplashViewModelTests.cs`:

```csharp
using System;
using System.Threading;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class SplashViewModelTests
{
    private static SplashViewModel MakeVm(Action? onTick = null)
    {
        var vm = new SplashViewModel("ComfyUI Manager", "test tagline", "v0.6.8");
        if (onTick is not null)
        {
            // fake timer:每次 Start 给一个 IDisposable,tick 触发调 onTick 一次
            vm.TimerFactory = (callback, _) =>
            {
                onTick();
                return new NoopDisposable();
            };
        }
        return vm;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }

    [Fact]
    public void Ctor_TitleTaglineVersion_AreSet()
    {
        var vm = new SplashViewModel("T", "sub", "v1.0");
        Assert.Equal("T", vm.Title);
        Assert.Equal("sub", vm.Tagline);
        Assert.Equal("v1.0", vm.Version);
        Assert.False(vm.IsFading);
    }

    [Fact]
    public void NotifyMainWindowReady_StartsTimer()
    {
        var ticks = 0;
        var vm = MakeVm(onTick: () => ticks++);

        vm.NotifyMainWindowReady();

        // fake timer 立即 fire 一次
        Assert.Equal(1, ticks);
        Assert.False(vm.IsFading);  // 第一次 Tick,elapsed 极小 → 不 fade
    }

    [Fact]
    public void NotifyMainWindowReady_BeforeMinDisplayTime_TimerDoesNotFade()
    {
        var elapsed = TimeSpan.Zero;
        var vm = MakeVm(onTick: null);
        vm.TimerFactory = (callback, interval) =>
        {
            // fake:模拟"elapsed 还没到 3s"→ 不调 callback
            return new NoopDisposable();
        };

        vm.NotifyMainWindowReady();

        Assert.False(vm.IsFading);  // timer 没触发 Tick → 不 fade
    }

    [Fact]
    public void NotifyMainWindowReady_AfterMinDisplayTime_TriggersFade()
    {
        // 通过直接调 StartFadeOut() 模拟 "timer 检测到 elapsed ≥ 3s"的内部路径
        var vm = new SplashViewModel("T", "sub", "v1.0");
        bool fadeCompletedFired = false;
        vm.FadeCompleted += () => fadeCompletedFired = true;

        vm.NotifyMainWindowReady();  // 启动 timer (默认无 TimerFactory → 不真启)
        vm.StartFadeOut();           // 直接模拟 timer 触发

        Assert.True(vm.IsFading);
        // StartFadeOut 本身不 raise FadeCompleted(那是 Storyboard.Completed 后调)
        Assert.False(fadeCompletedFired);
    }

    [Fact]
    public void RaiseFadeCompleted_FiresEventOnce()
    {
        var vm = new SplashViewModel("T", "sub", "v1.0");
        int fireCount = 0;
        vm.FadeCompleted += () => fireCount++;

        vm.RaiseFadeCompleted();
        vm.RaiseFadeCompleted();  // 二次触发(模拟 Storyboard→Close→Closed 双路径)

        Assert.Equal(1, fireCount);  // 幂等守卫生效
    }

    [Fact]
    public void NotifyMainWindowReady_AfterFade_NoOp()
    {
        var vm = new SplashViewModel("T", "sub", "v1.0");
        int timerCreated = 0;
        vm.TimerFactory = (_, _) =>
        {
            timerCreated++;
            return new NoopDisposable();
        };

        vm.StartFadeOut();  // 模拟 fade 已触发
        vm.RaiseFadeCompleted();
        vm.NotifyMainWindowReady();  // 已 disposed → TimerFactory 不调

        Assert.Equal(0, timerCreated);
    }
}
```

- [ ] **Step 2: Run tests, verify 6/6 FAIL**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~SplashViewModelTests"`
Expected: `error CS0246: 未能找到类型或命名空间名"SplashViewModel"` — 测试编译失败

- [ ] **Step 3: Implement `SplashViewModel`**

Create `src-wpf/ComfyUI.Manager/ViewModels/SplashViewModel.cs`:

```csharp
using System;
using System.Windows.Threading;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.8 Splash 画面 view model — 持有标题 / 副标语 / 版本号,管理最少显示时间
/// 计时器,触发 IsFading 让 Window 跑 Storyboard 渐变。
///
/// TimerFactory seam(G13):生产路径默认 null,首次 NotifyMainWindowReady() lazy-new
/// 一个 DispatcherTimer;测试路径 set fake 直接同步触发 Tick。
/// </summary>
public class SplashViewModel : ViewModelBase
{
    private readonly DateTime _shownAt = DateTime.UtcNow;
    private IDisposable? _timerHandle;
    private bool _disposed;

    public SplashViewModel(string title, string tagline, string version)
    {
        Title = title;
        Tagline = tagline;
        Version = version;
    }

    public string Title { get; }
    public string Tagline { get; }
    public string Version { get; }
    public bool IsFading { get; private set; }

    /// <summary>
    /// 测试 seam(G13):Action=Timer tick callback,TimeSpan=interval,返回 IDisposable
    /// 调 Dispose 停 timer。生产路径默认 null → lazy-new 一个 DispatcherTimer。
    /// </summary>
    internal Func<Action, TimeSpan, IDisposable>? TimerFactory { get; set; }

    /// <summary>
    /// App 在 MainWindow.Show() 后调这个。启动 DispatcherTimer 每 100ms 检查
    /// elapsed,elapsed ≥ MinDisplaySeconds(3s) 才允许 fade。
    /// </summary>
    public void NotifyMainWindowReady()
    {
        if (_disposed) return;

        Action onTick = () =>
        {
            var elapsed = DateTime.UtcNow - _shownAt;
            if (elapsed.TotalSeconds >= MinDisplaySeconds)
            {
                _timerHandle?.Dispose();
                StartFadeOut();
            }
        };

        if (TimerFactory is not null)
        {
            _timerHandle = TimerFactory(onTick, TimeSpan.FromMilliseconds(100));
        }
        else
        {
            var dispatcherTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            dispatcherTimer.Tick += (_, _) => onTick();
            dispatcherTimer.Start();
            _timerHandle = new DispatcherTimerHandle(dispatcherTimer);
        }
    }

    /// <summary>
    /// 内部逻辑:把 IsFading 设为 true。Window PropertyChanged 监听触发 Storyboard。
    /// Storyboard.Completed → Window.OnFadeOutCompleted → RaiseFadeCompleted() → Close。
    /// </summary>
    internal void StartFadeOut()
    {
        if (IsFading) return;
        IsFading = true;
        RaisePropertyChanged(nameof(IsFading));
    }

    /// <summary>
    /// 幂等闭锁(G5+G14 防双 invoke):Storyboard 完成 → Window.Close → Window.Closed
    /// 事件 handler 也会 raise,所以两次调只 fire 一次 FadeCompleted。
    /// </summary>
    internal void RaiseFadeCompleted()
    {
        if (_disposed) return;
        _disposed = true;
        _timerHandle?.Dispose();
        FadeCompleted?.Invoke();
    }

    public event Action? FadeCompleted;

    /// <summary>DispatcherTimer 包装成 IDisposable 让 seam 接口统一。</summary>
    private sealed class DispatcherTimerHandle : IDisposable
    {
        private readonly DispatcherTimer _timer;
        public DispatcherTimerHandle(DispatcherTimer timer) => _timer = timer;
        public void Dispose() => _timer.Stop();
    }
}
```

- [ ] **Step 4: Run tests, verify 6/6 PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~SplashViewModelTests"`
Expected: 6 tests, 0 failures

- [ ] **Step 5: Verify build clean**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Expected: 0 errors / 0 warnings

- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/SplashViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/SplashViewModelTests.cs
git commit -m "feat(wpf): SplashViewModel + MinDisplayTime 计时 + TimerFactory seam (v0.6.8 T1)"
```

---

### Task 2: `SplashWindow` XAML + code-behind + 编译 verify

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml`(~55 行)
- Create: `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml.cs`(~35 行)

**Interfaces:**
- Consumes: `SplashViewModel`(T1),`ViewModelBase`,`System.Windows`(Window / Storyboard / Image),`pack://application:,,,/asset/splash.png` 资源
- Produces:
  ```csharp
  namespace ComfyUI.Manager.Views;

  public partial class SplashWindow : Window
  {
      public SplashWindow(SplashViewModel vm);
      // private void OnVmPropertyChanged(...) 监听 IsFading 触发 Storyboard
      // private void OnFadeOutCompleted(...) 调 vm.RaiseFadeCompleted() + Close
      // Closed event 也调 vm.RaiseFadeCompleted()(防用户手动 Alt+F4 漏 raise)
  }
  ```

- [ ] **Step 1: Create XAML**

Create `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml`:

```xml
<Window x:Class="ComfyUI.Manager.Views.SplashWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Splash" Width="900" Height="540"
        WindowStyle="None" ResizeMode="NoResize"
        WindowStartupLocation="CenterScreen"
        ShowInTaskbar="False" ShowActivated="False"
        Topmost="True" AllowsTransparency="True"
        Background="Transparent">
    <Window.Resources>
        <Storyboard x:Key="FadeOutStoryboard">
            <DoubleAnimation
                Storyboard.TargetProperty="Opacity"
                From="1.0" To="0.0"
                Duration="0:0:0.8"
                Completed="OnFadeOutCompleted" />
        </Storyboard>
    </Window.Resources>
    <Border CornerRadius="12" ClipToBounds="True"
            Background="#FF1E1E1E">
        <Grid>
            <!-- 主图背景:pack URI 走 csproj <None Include="..\..\asset\**\*"> 已自动拷到 output。
                 图片缺失时控件空,Border 背景色兜底(G6)。 -->
            <Image Source="pack://application:,,,/asset/splash.png"
                   Stretch="UniformToFill" />
            <StackPanel VerticalAlignment="Bottom" HorizontalAlignment="Right"
                        Margin="0,0,32,24">
                <TextBlock Text="{Binding Title}"
                           FontSize="36" FontWeight="Bold"
                           Foreground="White" HorizontalAlignment="Right" />
                <TextBlock Text="{Binding Tagline}"
                           FontSize="14" Foreground="#DDD"
                           Margin="0,4,0,0" HorizontalAlignment="Right" />
                <TextBlock Text="{Binding Version}"
                           FontSize="11" Foreground="#999"
                           Margin="0,8,0,0" HorizontalAlignment="Right" />
            </StackPanel>
        </Grid>
    </Border>
</Window>
```

- [ ] **Step 2: Create code-behind**

Create `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml.cs`:

```csharp
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
        // 用户手动 Alt+F4 关闭时也 raise,让 App/FadeCompleted handler 收到通知
        Closed += (_, _) => _vm.RaiseFadeCompleted();

        // v0.6.8 G6: Image 加载失败静默 → Image 控件空,文本兜底显示
        // 用 ImageFailed 事件捕获失败原因(典型:asset/splash.png 缺失或格式损坏)
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
```

- [ ] **Step 3: Verify XAML 编译干净**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Expected: 0 errors / 0 warnings(XAML 编译进 BAML,新加 Window 类不影响 test build)

- [ ] **Step 4: Run VM tests still pass**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build --filter "FullyQualifiedName~SplashViewModelTests"`
Expected: 6 tests, 0 failures(T1 测试不依赖 Window,Window 加进来不影响 VM)

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml \
        src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml.cs
git commit -m "feat(wpf): SplashWindow XAML + Storyboard 渐变 (v0.6.8 T2)"
```

---

### Task 3: `App.xaml.cs` OnStartup 接线 + `AppVersionInfo` helper + `asset/splash.png` 提交

**Files:**
- Create: `src-wpf/ComfyUI.Manager/AppVersionInfo.cs`(~10 行)— 仅在项目内没有现成 assembly version 读取时
- Create: `asset/splash.png`(~50KB,AI 生成)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs`(~+25 / -0)

**Interfaces:**
- Consumes: `SplashViewModel`(T1)+ `SplashWindow`(T2)+ `AppVersionInfo`(T3 自己建)+ 既有 `App.OnStartup` 流程
- Produces: 启动时 Splash 立即可见,MainWindow 加载后 3s + 渐变消失

- [ ] **Step 1: Grep 现有 assembly version helper**

Run: `grep -rn "Assembly.GetExecutingAssembly\|Assembly.GetName\|AssemblyVersion" --include="*.cs" src-wpf/ | head -20`

判断:
- 如果输出含 `Assembly.GetExecutingAssembly().GetName().Version` 已有 helper → 跳到 Step 2
- 如果没有 → 走 Step 1a 建 helper

- [ ] **Step 1a (条件性): 建 `AppVersionInfo.cs`**

仅 Step 1 grep 无结果时:

Create `src-wpf/ComfyUI.Manager/AppVersionInfo.cs`:

```csharp
using System.Reflection;

namespace ComfyUI.Manager;

/// <summary>
/// v0.6.8: Splash 用的 version 字符串 helper。读 entry assembly 的
/// Version 字段,format 3 段(major.minor.build);若 null fallback "v?"
/// (理论上不会发生 — csproj 总是有 Version 字段)。
/// </summary>
public static class AppVersionInfo
{
    public static string Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "v?" : $"v{v.ToString(3)}";
        }
    }
}
```

- [ ] **Step 2: Verify build clean(无论是否新建 helper)**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Expected: 0 errors / 0 warnings

- [ ] **Step 3: 生成 `asset/splash.png`**

> **这个 step 由 implementer 在本地手动完成**(AI 生成不是 runtime 工作流的一部分):
> 1. 用任意 AI 图像生成工具(Midjourney / DALL-E / Stable Diffusion)生成一张 16:9(800×450 推荐)的"ComfyUI Manager 主题"主图
> 2. 主题建议:暗色背景(配 #1E1E1E Border 兜底色)+ 抽象的"ComfyUI 节点流"或"环境管理"视觉元素
> 3. 压成 PNG(可用 `magick splash.png -resize 800x450 -quality 85 splash.png`)
> 4. 保存到 `D:\ToolDevelop\ComfyUI\asset\splash.png`
> 5. `git add asset/splash.png` 准备 commit

> **跳过 step**:如果 implementer 暂时没法生成 AI 图,**先创建一个 placeholder** `asset/splash.png`(纯色或简单 pattern,1-10KB 即可),让 build / commit 流程能跑。production AI 图可在后续 commit 替换。

最简 placeholder 创建方法(Windows PowerShell):
```powershell
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 800, 450
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.FillRectangle([System.Drawing.Brushes]::DarkSlateGray, 0, 0, 800, 450)
$g.Dispose()
$bmp.Save("$PSScriptRoot\..\..\asset\splash.png", [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
```

或者从外部下载一张开源许可的 placeholder:
- 不允许(per project memory `feedback_no_zip.md` 精神 — 不下载外部资源)
- placeholder 用 PowerShell 脚本生成本地纯色 PNG 即可

- [ ] **Step 4: Modify `App.xaml.cs`**

Modify `src-wpf/ComfyUI.Manager/App.xaml.cs`:

(a) 在 `private MainViewModel? _mainVm;` 后(line 18 附近)加 2 字段:

```csharp
    // v0.6.8: Splash 画面引用 — OnStartup 立即 Show,MainWindow 加载好后
    // NotifyMainWindowReady 触发 fade;FadeCompleted 由 Window self-close raise。
    private SplashWindow? _splash;
    private SplashViewModel? _splashVm;
```

(b) 修改 `OnStartup`(line 31-34 区域),在 `base.OnStartup(e);` 之后立刻加 splash 初始化,在所有服务初始化**之前**:

OLD:
```csharp
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var projectRoot = Path.GetDirectoryName(
            Environment.ProcessPath)!.TrimEnd('\\');
```

NEW:
```csharp
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // v0.6.8: Splash 立即显示 — 必须先于所有服务初始化,保证用户 0ms 看到
        // (G1)。失败静默,不阻断程序启动(G6 错误处理)。
        try
        {
            _splashVm = new SplashViewModel(
                title: "ComfyUI Manager",
                tagline: "智能管理 ComfyUI 环境、节点、依赖",
                version: AppVersionInfo.Current);
            _splash = new SplashWindow(_splashVm);
            _splash.Show();
        }
        catch (Exception ex)
        {
            _logger = null;  // 此时 _logger 还没建,真 catch 也无法 log — 直接吞
            _splash = null;
            _splashVm = null;
            // 没有 logger 可调 — Splash 创建失败极端罕见(XAML 错 / VM ctor 错),
            // 失败后无 splash 直接走主流程。
            System.Diagnostics.Debug.WriteLine($"splash failed: {ex.Message}");
        }

        var projectRoot = Path.GetDirectoryName(
            Environment.ProcessPath)!.TrimEnd('\\');
```

> **关于 `_logger`**:此时 `_logger` 字段未声明也无值。Splash 失败是极端路径(XAML 错 / ctor 错),`Debug.WriteLine` 够了。如果你想更严,可在 class 顶部加 `private AppLogger? _logger;` 提前声明,然后 catch 里 `System.Diagnostics.Debug.WriteLine`(避免 logger 自己抛二次异常)。本次 plan 不动 logger,简化为 Debug 输出。

(c) 在 `OnStartup` 末尾(line 156 `main.Show();` 之后)加 NotifyMainWindowReady:

OLD(line 153-155):
```csharp
        var main = new MainWindow { DataContext = _mainVm };
        main.ApplyStartupPreferences(uiPrefs);
        main.Show();
    }
```

NEW:
```csharp
        var main = new MainWindow { DataContext = _mainVm };
        main.ApplyStartupPreferences(uiPrefs);
        main.Show();

        // v0.6.8: MainWindow 显示后通知 splash VM 启动最少 3s 计时 + fade
        _splashVm?.NotifyMainWindowReady();
    }
```

- [ ] **Step 5: Verify build clean + full suite**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Expected: 0 errors / 0 warnings

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build`
Expected: 671 PASS / 2 FAIL (1 known + 1 new flaky `WithLogProgress_ReportsStdoutLines`) / 1 SKIP
> 跟 v0.6.7.5 ship baseline(666/2/1)+ T1 新增 6 测试 = 672 期望(实际可能有 off-by-one ±1)。2 FAIL 是已知 flaky,re-run 通过。

- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/AppVersionInfo.cs \
        src-wpf/ComfyUI.Manager/App.xaml.cs \
        asset/splash.png
git commit -m "feat(wpf): App.OnStartup splash 接线 + asset/splash.png + AppVersionInfo (v0.6.8 T3)"
```

> 如果 Step 1 grep 发现现有 helper,只 add 2 个文件:`App.xaml.cs` + `asset/splash.png`,不要 add 一个未创建的 `AppVersionInfo.cs`。

---

### Task 4: 全量 verify + 重建 staging + commit

**Files:** 无

- [ ] **Step 1: Full build clean**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Expected: 0 errors / 0 warnings

- [ ] **Step 2: Full test suite**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build`
Expected: 672 PASS / 2 FAIL / 1 SKIP(基线 666 + 6 新 = 672;2 FAIL pre-existing flaky `ProcessLauncherProgressTests` re-run 通过)

- [ ] **Step 3: 重建 staging**

Run:
```bash
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
    -c Release -r win-x64 --self-contained true \
    -o "release/staging/ComfyUI Manager" -v minimal
```

Expected:publish 成功,`release/staging/ComfyUI Manager/asset/splash.png` 存在

- [ ] **Step 4: Verify working tree clean**

Run: `git status --short`
Expected: 只 pre-existing `?? full-suite.log` + `?? tools/`(非 v0.6.8 引入);`release/staging/...` 整体 gitignored。**0 个 uncommitted tracked file 修改**。

- [ ] **Step 5: Verify final commit list + staging exe 时间戳**

```bash
git log --oneline e044e15..HEAD
ls -la "release/staging/ComfyUI Manager/ComfyUI.Manager.exe"
```

Expected:
- 3 commits on main(T1 + T2 + T3)
- exe 时间戳 = 刚刚 rebuild

- [ ] **Step 6: 无 v-bump / 无 release zip**

Verify:
- `git tag --list` 没新增 tag(per G7)
- `release/*.zip` 没新增(per G7)
- 无 MEMORY.md / project memory commit(per G7)

---

## Verification

### 单元测试

| 测试 | 验证 |
|---|---|
| `SplashViewModelTests.Ctor_TitleTaglineVersion_AreSet` | ctor 后 3 个 string 属性正确 + IsFading=false |
| `SplashViewModelTests.NotifyMainWindowReady_StartsTimer` | fake timer factory 调一次,TimerFactory 触发 Tick callback |
| `SplashViewModelTests.NotifyMainWindowReady_BeforeMinDisplayTime_TimerDoesNotFade` | fake timer 不调 Tick 时,IsFading 仍 false |
| `SplashViewModelTests.NotifyMainWindowReady_AfterMinDisplayTime_TriggersFade` | `StartFadeOut()` 内部路径 → IsFading=true |
| `SplashViewModelTests.RaiseFadeCompleted_FiresEventOnce` | 二次调 `RaiseFadeCompleted()` 只 fire FadeCompleted 一次(幂等闭锁) |
| `SplashViewModelTests.NotifyMainWindowReady_AfterFade_NoOp` | 已 disposed 后再调 `NotifyMainWindowReady()`,TimerFactory 不被调 |

6 新测试。

### 全量

- `dotnet build` 0 errors / 0 warnings
- `dotnet test` 672 PASS / 2 FAIL / 1 SKIP(基线 666 + 6 新 = 672;2 FAIL 是已知 `ProcessLauncherProgressTests` flaky,非 v0.6.8 regression)
- staging exe 重建成功,`asset/splash.png` 在 staging 里

### 端到端桌面 GUI smoke(用户桌面)

1. 双击 `release/staging/ComfyUI Manager/ComfyUI.Manager.exe`
2. **0ms 看到**:Splash 出现(900×540 居中,圆角,主图 + 右下标题 + 副标语 + 版本号)
3. **启动正常路径**:MainWindow 在 ~3s 内加载完成 → splash 显示满 3s → 800ms 渐变(opacity 1→0)→ self-close → MainWindow 露出
4. **启动慢测试**(可选,临时 `Thread.Sleep(5000)` 在 splash 后):splash 等 MainWindow 加载完 + 满 3s(从 splash 显示起算)→ fade → close
5. **Asset 缺失测试**(重命名 `asset/splash.png` 临时):splash 仍显示,仅文本(暗灰 #1E1E1E 背景 + 标题 + 副标语 + 版本),MainWindow 正常启动,`Logs/YYYY-MM-DD.log` 有 `[splash] asset/splash.png 缺失` INFO 行
6. **手动关闭测试**:splash 显示期间 Alt+F4 → MainWindow 仍正常出现,无 NRE,Logs/ 有 `splash dismissed early` INFO 行
7. **Version 显示**:`AppVersionInfo.Current` 应输出当前 assembly version(例 `v0.6.8.x`),不是 `"v?"`

---

## Risks

| 风险 | 缓解 |
|---|---|
| WPF Window 实例化 ~50ms 让 OnStartup 略慢 | Show 在 base.OnStartup(e) 之后立即,先于任何服务初始化 — 用户看到 splash 0ms |
| Splash 渐变期间 MainWindow 闪现 | `Topmost=true` 覆盖整段 fade |
| 中文 tagline 在非中文系统乱码 | XAML 硬编码 UTF-8(G11);M1 i18n 推迟 |
| Asset 缺失 → 视觉空洞 | 文本兜底 + AppLogger Info(G6)— 但 T3 catch 路径没接 AppLogger(那时 logger 还没建),后续可加 |
| `DispatcherTimer` 跨 STA 测试复杂 | TimerFactory seam 注入 fake(G13) |
| AI 生成图风格不匹配项目风格 | 维护者手动选,不是 AI 直出 |
| `_logger` 在 Splash ctor catch 时未初始化 — log 无法写 | 用 `Debug.WriteLine`(per T3 Step 4b 注释);接受这种极端路径不写日志 |
| 既有 `AppWiringTests` 可能受 OnStartup 改动影响 | T1+T2 跑现有 AppWiring 验证;若失败,在该测试加 fake splash path 或注入 SplashVm seaming |
| 并行 SDD(还在做别的) | 文件集不相交(splash 新增 6 文件 + 改 1 文件,跟任何 v0.6.7+ 后续 SDD 无交集) |
| `AppVersionInfo.Current` 重复造轮子 | Step 1 grep 现有 helper 优先复用 |

---

## Critical files to modify/create

- `src-wpf/ComfyUI.Manager/ViewModels/SplashViewModel.cs`(新,~90 行)
- `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml`(新,~55 行)
- `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml.cs`(新,~35 行)
- `src-wpf/ComfyUI.Manager/AppVersionInfo.cs`(新 10 行 — 条件性,grep 后决定)
- `src-wpf/ComfyUI.Manager/App.xaml.cs`(改 +25 / -0)
- `asset/splash.png`(新,~50KB)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SplashViewModelTests.cs`(新,~140 行)

---

## Execution choice

**Recommended: Subagent-Driven Development**
- 3 task(SplashViewModel / SplashWindow / App.xaml.cs + asset) + 1 close-out = ~4 dispatch
- Per-task review gate(sonnet implementer + sonnet reviewer)
- T1 单元测试覆盖核心逻辑;T2 XAML/wiring 编译 verify + VM 测试仍 PASS;T3 App integration + asset 提交
- 预估 3 commits on main(T1 + T2 + T3 合并或拆开,看 implementer 偏好 — 拆更清晰)

(Plan agent left out: 设计约束已由 brainstorm 用户 4 决策 + 12 Global Constraints 明确,spec 完整,无需额外 design pass。)
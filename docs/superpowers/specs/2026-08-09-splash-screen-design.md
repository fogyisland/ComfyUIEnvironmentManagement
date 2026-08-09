# v0.6.8 Splash Screen 设计 spec

> **For agentic workers:** This is a design spec, not a plan. After user review/approval, invoke `superpowers:writing-plans` to produce a task-by-task implementation plan.

**Goal:** 程序启动时显示一张 AI 生成的主图 + 软件标题 + 副标语 + 版本号,展示 ≥3 秒后渐变消失,无网络依赖、无 API key、零运行期成本。

**Architecture:** 静态打包的 AI 生成主图(维护者在 dev/release 用 AI 工具离线生成,提交 `asset/splash.png`)+ 自定义 WPF Window(`SplashWindow`)承载图 + 文 + 渐变动画 + 最少显示时长计时。`App.OnStartup` 第一句后立刻实例化并 `Show()`,所有后续服务初始化在 splash 显示期间后台完成,MainWindow 加载完成后通知 VM 触发 fade。组件模式跟现有 `AboutDialog` / `DonateQrWindow` / `EnvStartStatusViewModel` 一致(自定义 Window + ViewModelBase + DispatcherTimer + Storyboard)。

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · hand-rolled MVVM(`ViewModelBase` / `RelayCommand` / INPC)· `DispatcherTimer` · `Storyboard`(XAML resources)· `BitmapImage` 静态资源加载 · 现有 csproj `<None Include="..\..\asset\**\*">` asset 拷贝规则(已为 DonateQrWindow 服务)。

## Context

ComfyUI Manager 至今没有任何启动画面 — 用户双击 exe 后看到一段空白,然后突然出现 MainWindow。空白期间如果服务初始化慢(SQLite 锁、git 探测、网络 catalog 拉取)用户会以为程序卡死。

用户反馈:"为当前的程序添加 Splash 界面,可以调用 AI 生成图片来获取当前的软件的特性和特定生成必要的 Splash 用来吸引用户"。

**用户已确认的 4 个关键决策**(per brainstorming):
1. **图片来源 = 静态打包**:维护者用 AI 工具离线生成主图,提交 `asset/splash.png`,运行时无网络/无 API key/无启动延迟
2. **触发时机 = 每次启动**:`App.OnStartup` 第一句后立即显示,无 Settings 频率配置(YAGNI)
3. **内容形态 = 单图 + 标题 + 副标语 + 版本号**:不轮播、不切换标语、不加 Skip 按钮
4. **消失方式 = MainWindow 加载好 + 最少 3s + 800ms 渐变**:`DispatcherTimer` 在 VM 内计算 elapsed,`Storyboard` 触发 opacity 1→0

**base SHA:** `e044e15`(v0.6.7.5 SHIP-READY + final review Critical fix)

**已有相关代码(参考模式,不直接复用)**:
- `Views/DonateQrWindow.xaml` + `.xaml.cs` — 自定义 Window 显示静态图,`pack://application:,,,/asset/receiveMark.jpg` 资源路径,`ShowInTaskbar` / `ShowActivated` 模式可参考
- `Views/AboutDialog.xaml` — Window + 多行文字 + MaterialButton 风格参考
- `ViewModels/EnvStartStatusViewModel.cs` — `IProgress<string>` + 计时 + 阶段切换模式(可比对 IProgress vs DispatcherTimer 用法)
- `App.xaml.cs:OnStartup` — 启动序列插入点,Splash 必须在 `base.OnStartup(e)` 之后、`dbFactory` 构造之前立即 Show
- `csproj` 已有 `<None Include="..\..\asset\**\*"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>` 规则,dontate 那块已用,加 `asset/splash.png` 自动拷到 output/staging

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| G1 | Splash 必须在 `base.OnStartup(e)` 之后、所有服务初始化之前 `Show()`(`dbFactory` / `envRepo` / `logger` 之前),保证用户 0ms 看到 splash | 用户"启动立刻看到"诉求 |
| G2 | 静态图片:`asset/splash.png` 由维护者用 AI 工具离线生成后提交,无运行时 AI 调用、无 API key 存储、无网络依赖 | brainstorm 决策 1 |
| G3 | Splash 显示时机 = 每次启动,无 Settings 频率配置(无 UI、不留 toggle) | brainstorm 决策 2 |
| G4 | Splash 内容 = 1 张主图(背景)+ 标题(软件名)+ 副标语(1 行)+ 版本号 — 4 元素,不轮播不切换 | brainstorm 决策 3 |
| G5 | 消失 = `MainWindow.Loaded` 触发 VM 的 `MainWindowReady` 事件,VM 内部 `DispatcherTimer` 计算 elapsed,`elapsed >= MinDisplayTime(3s)` 才允许 fade;fade 用 `Storyboard` opacity 1→0 / 800ms;fade 完成 self-close | brainstorm 决策 4 |
| G6 | 错误全部静默:`asset/splash.png` 缺失 → Image 控件空但文本兜底显示;`AppLogger.Info` 记一行 | 项目 v0.6.5.13 AppLogger 哲学 |
| G7 | 不 bump version / 不发 release zip / 无 MEMORY commit(无 version bump 时) | 项目 `feedback_no_rebuild_zip.md` + `feedback_no_zip.md` |
| G8 | 测试只测 VM,不测 WPF Window(STA 抛异常;项目现有惯例) | 项目 `feedback_wpf_dialog_close_requested.md` 跟 `EnvStartStatusViewModelDispatchTests` 模式 |
| G9 | VM 暴露 `MainWindowReady()` 方法 + `FadeCompleted` event;`App` 拿 `FadeCompleted` 后无需做任何事(Window 已 self-close),但保留 hook 给未来 telemetry | 设计解耦 |
| G10 | 渐变期间 Splash 仍 `Topmost=true` 遮住 MainWindow,避免 MainWindow 闪现空窗再被 splash 盖上 | UX 细节 |
| G11 | 中文 hard-code 在 XAML(项目 AboutDialog 同款 i18n 处理);未来要 i18n 再抽 resx,M1 不动 | 项目 i18n 现状 |
| G12 | Splash 不抢焦点(`ShowActivated=False`)、不出现在 taskbar(`ShowInTaskbar=False`)、`Owner=null`(避免被 MainWindow 遮挡) | WPF UX 习惯 |

---

## File Structure

### Create

| 文件 | 行数(估) | 职责 |
|---|---|---|
| `src-wpf/ComfyUI.Manager/ViewModels/SplashViewModel.cs` | ~80 | Title / Tagline / Version / IsVisible / IsFading 属性;`MainWindowReady()` 方法;`FadeCompleted` event;内部 `DispatcherTimer` 计算 elapsed |
| `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml` | ~50 | 900×540 无边框 Window,圆角 Border,`asset/splash.png` 背景 Image,右下叠加 Title / Tagline / Version TextBlock,XAML 资源声明 `FadeOut` Storyboard |
| `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml.cs` | ~30 | 构造接 `SplashViewModel`,设 `DataContext` + `Owner=null` + `ShowActivated=False` + `ShowInTaskbar=False` + `Topmost=true` + `WindowStyle=None` + `AllowsTransparency=True` |
| `asset/splash.png` | ~50KB | AI 生成的 800×450(16:9)PNG,带 alpha 通道,提交到 git |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SplashViewModelTests.cs` | ~120 | 5 测试:初始态 / MainWindowReady 早于 Min 不 fade / MainWindowReady 晚于 Min 触发 fade / FadeCompleted 事件 / 已关闭后 Notify 忽略 |

### Modify

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | `OnStartup` 在 `base.OnStartup(e)` 之后立即 `var splashVm = new SplashViewModel(...); var splash = new SplashWindow(splashVm); splash.Show();`;MainWindow `Show()` 之后调 `splashVm.NotifyMainWindowReady()`;SplashWindow 引用字段 `_splash` / `_splashVm` 留着给未来 telemetry/测试;不需在 `OnExit` 做额外事(Window self-close) |

### Delete

无。

### Keep (unchanged)

- `AboutDialog` / `DonateQrWindow` / `EnvStartStatusViewModel` — splash 是独立功能,不交叉
- `csproj` 的 `..\..\asset\**\*` 拷贝规则 — splash.png 自动复用,无需改
- 顶部菜单 / Settings / 既有所有服务 — 不动
- v0.6.7.5/v0.6.7.6 全部代码 — 文件集不相交

---

## Components

### 1. `SplashViewModel`(继承 `ViewModelBase`)

```csharp
public class SplashViewModel : ViewModelBase
{
    private static readonly TimeSpan MinDisplayTime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(800);

    private readonly DateTime _shownAt = DateTime.UtcNow;
    private DispatcherTimer? _readyTimer;
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

    /// <summary>App 在 MainWindow.Show() 后调这个。</summary>
    public void NotifyMainWindowReady()
    {
        if (_disposed) return;
        _readyTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _readyTimer.Tick += (_, _) => CheckElapsed();
        _readyTimer.Start();
    }

    private void CheckElapsed()
    {
        var elapsed = DateTime.UtcNow - _shownAt;
        if (elapsed >= MinDisplayTime)
        {
            _readyTimer?.Stop();
            StartFadeOut();
        }
    }

    private void StartFadeOut()
    {
        IsFading = true;
        OnPropertyChanged(nameof(IsFading));
        // Window 监听 IsFading property changed → 触发 XAML Storyboard
        // Storyboard Completed → Window self-close + 触发 FadeCompleted event
    }

    public event Action? FadeCompleted;

    internal void RaiseFadeCompleted()
    {
        if (_disposed) return;
        _disposed = true;
        _readyTimer?.Stop();
        FadeCompleted?.Invoke();
    }
}
```

**设计要点:**
- `NotifyMainWindowReady` 只在 `MainWindow.Show()` 之后调,VM 不知道 MainWindow 存在(解耦)
- `DispatcherTimer` 100ms 检查 elapsed(比 timer 直接等 3s 灵活 — 若启动慢于 3s,MainWindow 加载后 timer 还在跑直到 elapsed ≥ 3s)
- `IsFading` 是 OneWayToSource 模式 — VM set → Window XAML Storyboard 触发 → Storyboard Completed → Window 调 `vm.RaiseFadeCompleted()` → Window self-close
- `RaiseFadeCompleted` 用 `_disposed` 幂等守卫,防 Storyboard 完成 → Close → `Closed` event 二次触发(防 event handler 双 invoke)

### 2. `SplashWindow.xaml`(核心 markup 示意)

```xaml
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

### 3. `SplashWindow.xaml.cs`(code-behind)

```csharp
public partial class SplashWindow : Window
{
    private readonly SplashViewModel _vm;

    public SplashWindow(SplashViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) => _vm.RaiseFadeCompleted();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SplashViewModel.IsFading) && _vm.IsFading)
        {
            var storyboard = (Storyboard)Resources["FadeOutStoryboard"];
            storyboard.Begin(this);
        }
    }

    private void OnFadeOutCompleted(object? sender, EventArgs e)
    {
        _vm.RaiseFadeCompleted();
        Close();
    }
}
```

### 4. `App.xaml.cs` 改动

OLD(`OnStartup` line 32-34):
```csharp
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);
    var projectRoot = ...
```

NEW:
```csharp
private SplashWindow? _splash;
private SplashViewModel? _splashVm;

protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);

    // v0.6.8: Splash 立即显示 — 必须先于所有服务初始化,保证用户 0ms 看到
    _splashVm = new SplashViewModel(
        title: "ComfyUI Manager",
        tagline: "智能管理 ComfyUI 环境、节点、依赖",
        version: AppVersionInfo.Current);
    _splash = new SplashWindow(_splashVm);
    _splash.Show();

    var projectRoot = ...
    // ... 既有全部初始化 ...
    var main = new MainWindow { DataContext = _mainVm };
    main.ApplyStartupPreferences(uiPrefs);
    main.Show();
    _splashVm.NotifyMainWindowReady();   // 触发 VM timer + fade-when-elapsed
}
```

> **AppVersionInfo.Current** — 由 implementer grep 现有 codebase 决定:优先复用项目里既有的 assembly version 读取 helper(若有);若无,新增 1 个 5 行静态类 `src-wpf/ComfyUI.Manager/AppVersionInfo.cs`,暴露 `public static string Current => $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"}"` —— fallback `"v?"` 防 null version 抛。

---

## Data Flow

```
                            ┌──────────────────────┐
                            │ asset/splash.png     │
                            │ (csproj 自动拷 output)│
                            └──────────┬───────────┘
                                       │ pack://application:,,,/asset/splash.png
                                       ▼
┌────────────┐  new+Show   ┌──────────────────────┐         ┌────────────────────┐
│ App        │────────────→│ SplashWindow         │         │ SplashViewModel    │
│ OnStartup  │             │ - DataContext = vm   │ binds→  │ - Title/Tagline/Ver│
│ (line 32)  │             │ - IsFading → Storybd │←────────│ - NotifyMainWindow │
│            │             │ - Storyboard.Complete│         │   Ready()          │
│            │             │   → Close()          │         │ - IsFading setter  │
│            │             │                      │         │ - FadeCompleted    │
└─────┬──────┘             └──────────┬───────────┘         └────────┬───────────┘
      │                               │                              │
      │  (后续服务初始化 ~50-500ms)     │                              │
      │  dbFactory / envRepo / ...     │                              │
      │  _mainVm / MainWindow.Show()   │                              │
      │                               │                              │
      │  splashVm.NotifyMainWindowReady() ──────────────────────────→│
      │                               │                              │
      │                               │       ┌── DispatcherTimer(100ms)
      │                               │       │  CheckElapsed()
      │                               │       │  elapsed >= 3s?
      │                               │       ▼
      │                               │  IsFading = true ────property changed────→ Window 触发 FadeOutStoryboard
      │                               │                                              │
      │                               │       Storyboard Completed (800ms) ──────────→ Window.Close()
      │                               │                                              │
      │                               │                              FadeCompleted ←─┘
      │                               │                              (App 可选 log)
```

---

## Error Handling

| 场景 | 检测 | 处理 |
|---|---|---|
| `asset/splash.png` 文件缺失 | `BitmapImage` 加载抛 `FileNotFoundException` 在 ctor | Window 仍显示,Image 控件空(透明 Window 看不见),Title/Tagline/Version 文本兜底;`AppLogger.Info("splash", "asset/splash.png 缺失,仅显示文本")` |
| Splash png 文件格式损坏 | `BitmapImage` 加载抛 `NotSupportedException` | 同上,文本兜底 + log |
| `AppVersionInfo.Current` 抛异常 | ctor catch | fallback `"v?"`,不让 splash 创建失败 |
| MainWindow 加载耗时 > 30s(磁盘极慢) | timer 自然跑,N 秒后才 fade | 用户看到的 splash 久一点 — 但已保证最少 3s,符合预期;YAGNI 不加超时 |
| 用户在 splash 显示期间 Alt+F4 | `Window.Closed` 事件触发 | VM `RaiseFadeCompleted()` 清理 timer,后续 `NotifyMainWindowReady()` 被 `_disposed` 拦掉 |
| 用户在 splash 显示期间点 MainWindow | MainWindow 被 splash 遮(`Topmost=true`),点不到 | 无影响(G10) |
| Splash ctor 抛异常(VM null / XAML 错) | `try/catch` 包裹 `Show()` 调用 | catch 后 `AppLogger.Error`,继续正常启动(无 splash),不阻断程序 |
| 双启动(用户连点 2 次 exe) | OS-level,第二个进程 WPF 启动会失败或被拦 | YAGNI 不加 mutex(项目无此约束) |

---

## Testing Strategy(5 测试,纯 VM)

| 测试 | 验证 |
|---|---|
| `SplashVm_InitialState_TitleTaglineVersionSet` | ctor 后 3 个 string 属性正确 |
| `SplashVm_NotifyMainWindowReady_BeforeMinDisplayTime_DoesNotFade` | `NotifyMainWindowReady` 立刻调,IsFading 仍 false(FakeTimer 不前进) |
| `SplashVm_NotifyMainWindowReady_AfterMinDisplayTime_TriggersFade` | FakeTimer 前进到 ≥3s,CheckElapsed 把 IsFading 设为 true |
| `SplashVm_FadeOut_RaisesFadeCompletedEvent` | `StartFadeOut()` 直接调(测内部路径),`FadeCompleted` event fire |
| `SplashVm_NotifyMainWindowReadyAfterFade_NoOp` | 已 fade 后再 Notify,无副作用(无 NRE) |

**测试基础设施**:
- `DispatcherTimer` 在非 STA thread 实例化抛 — VM 抽象出 `internal Func<Action, TimeSpan, IDisposable>? _createTimer` seam 注入 fake timer:生产路径给 `DispatcherTimer`,测试给 fake 直接调 `Tick` callback
- 项目既有 `EnvStartStatusViewModelDispatchTests` 有 `TestSynchronizationContext` helper 可参考,但本测试只需要 fake timer 不需要真 DispatcherSynchronizationContext
- seam 设计:`internal Func<Action, TimeSpan, IDisposable>? TimerFactory { get; set; }`(默认 null → 生产路径 lazy-new 一个 DispatcherTimer;测试 path set 一个 fake)

**不测**:
- WPF Window(WPF Window 在非 STA thread 实例化抛 `InvalidOperationException`,项目现有惯例不测)
- Storyboard 渐变(XAML 视觉,无快照测试基建)
- `App.OnStartup` 集成路径(已有 `AppWiringTests` 模式可借鉴,但 YAGNI 不加 — splash 改 OnStartup 太微)

---

## Verification

### 单元测试

```
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```

期望:**671 PASS / 2 FAIL / 1 SKIP**(基线 666 + 5 新)
- 2 FAIL = pre-existing flaky `ProcessLauncherProgressTests`(已知,非 regression)
- 1 SKIP = `LiveFetch_RealGitHub_StoresTags`

### Build

```
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

期望:0 errors / 0 warnings(新增的 XAML 编译干净)

### Staging rebuild

```
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
    -c Release -r win-x64 --self-contained true \
    -o "release/staging/ComfyUI Manager" -v minimal
```

期望:`release/staging/ComfyUI Manager/asset/splash.png` 存在且 ~50KB

### 端到端桌面 GUI smoke(用户桌面)

1. 双击 `release/staging/ComfyUI Manager/ComfyUI.Manager.exe`
2. **0ms 看到**:splash 出现,标题"ComfyUI Manager" + 副标语 + 版本号 + 主图
3. **启动慢测试**:把 splash 后面塞一个 `Thread.Sleep(5000)`(临时)→ splash 仍显示 ≥3s(MainWindow 加载完 + 满 3s 才 fade)
4. **快速启动测试**:删 `Thread.Sleep` → splash 显示约 3s 后渐变 800ms 消失 → MainWindow 露出
5. **Asset 缺失测试**:重命名 `asset/splash.png` 临时测 → splash 仍显示,仅文本,MainWindow 正常启动
6. **手动关闭测试**:splash 显示期间 Alt+F4 → MainWindow 仍正常出现,无 NRE,Logs/ 有 "splash dismissed early" INFO 行
7. **检查 Logs/**:每次启动写 `splash-shown elapsed=Nms disposed=N` INFO 行(G9 telemetry hook)

---

## Risks

| 风险 | 缓解 |
|---|---|
| WPF Window 实例化 ~50ms 让 OnStartup 略慢 | Show 在 base.OnStartup(e) 之后立即,先于任何服务初始化 — 用户看到 splash 0ms,服务在 splash 后台跑 |
| Splash 渐变期间 MainWindow 闪现 | `Topmost=true` 覆盖整段 fade(MainWindow 在 splash 下 Show,被遮,渐变后才露出) |
| 中文 tagline 在非中文系统乱码 | XAML 硬编码 UTF-8,跟 AboutDialog 一致;M1 i18n 推迟(G11) |
| MinDisplayTime = 3s 用户嫌长 | 后续可放 Settings 加 `SplashMinDisplaySeconds`(YAGNI 不在 v0.6.8 做) |
| Asset 缺失 → 视觉空洞 | 文本兜底 + AppLogger Info(G6) |
| `DispatcherTimer` 跨 STA 测试复杂 | 抽 `_createTimer` `internal` seam 注入 FakeTimer |
| AI 生成图风格不匹配项目风格 | 维护者手动选,不是 AI 直出;YAGNI 不加 AI 风格约束 |
| Splash png 大小影响 staging 体积 | AI 生成建议 ≤200KB(PNG + 800×450);后续维护可重压 |
| 并行 SDD 干扰 | 文件集不相交(splash 新增 5 文件 + 改 1 文件,跟任何 v0.6.7+ 后续 SDD 无交集) |
| `AppVersionInfo.Current` 重复造轮子 | 先 grep `Assembly.GetName().Version` / `AssemblyInfo.cs`;有现成复用,无则新增 1 个 5 行类 |

---

## Out of Scope(不做)

- ❌ 运行时 AI 调用生成图(用户已选静态打包)
- ❌ Settings 频率配置(用户已选每次启动)
- ❌ 多图轮播 / 标语切换(用户已选单图 + 单标语)
- ❌ Skip 按钮 / ESC 手动关闭(用户已选纯自动)
- ❌ Splash 显示设置改 Settings UI(G12 项目 i18n / config 推到下轮)
- ❌ i18n 抽 resx(G11 推迟)
- ❌ Splash 加载状态("加载数据库..." / "初始化 git...")(G5 不要求;YAGNI)
- ❌ Splash 期间禁用 Alt+F4 / 锁屏防误关(用户接受手动关闭)
- ❌ Splash 显示期间 spinner / 进度条(无明确阶段,加 spinner 反而误导)
- ❌ 改 AboutDialog / DonateQrWindow / 顶部菜单(Splash 是独立 feature)

---

## Critical files to modify/create

- `src-wpf/ComfyUI.Manager/ViewModels/SplashViewModel.cs`(新,~80 行)
- `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml`(新,~50 行)
- `src-wpf/ComfyUI.Manager/Views/SplashWindow.xaml.cs`(新,~30 行)
- `asset/splash.png`(新,~50KB,维护者 AI 生成)
- `src-wpf/ComfyUI.Manager/App.xaml.cs`(改 +25 / -0)
- `src-wpf/ComfyUI.Manager/AppVersionInfo.cs`(新 5 行,若没有现成 version helper)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SplashViewModelTests.cs`(新 ~120 行)

---

## Implementation Phases Preview

(写作 plan 时拆;此处仅预览)

- **T1** SplashViewModel + 5 测试
- **T2** SplashWindow XAML + code-behind
- **T3** App.xaml.cs OnStartup 接线 + asset/splash.png 提交 + AppVersionInfo helper(若需要)
- **T4** 全量 verify + 重建 staging + commit

预估 4 commits on main,无 v-bump,无 release zip。
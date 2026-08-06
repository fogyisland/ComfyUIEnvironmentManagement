# v0.6.5.21 Top Menu Bar + About Dialog 设计

> **For agentic workers:** 不实现。等 user 批准 spec 后用 `superpowers:writing-plans` 写 plan。

## Goal

在 `MainWindow` 顶部加传统 WPF `Menu` 条(文件 / 设置 / 关于 三个顶级菜单),菜单项
触发各功能入口(打开项目文件夹、退出、显示 About 对话框含微信赞助二维码等);
侧栏的 5 个导航按钮仍保留(用户原话:并存,菜单是快捷入口,不是替代)。
新加"文件 > 保存/加载环境"实现 UI 偏好(窗口大小、位置、侧栏状态、最近选中 env)
的导入/导出。

## User stories(用户原话)

1. "为当前程序添加菜单,菜单包含 文件-设置-关于"
2. "我说的菜单栏是程序的顶部菜单,例如文件菜单提供三个功能,保存环境,加载环境,退出"
3. "加载环境是加载当前的各类环境中的配置项,不保存实际的业务项目,例如当前窗口的大小和可定制项目"
4. "在关于最后提供微信二维码提供小额捐款赞助"
5. "顶部菜单要怎么放?" → "侧栏+菜单并存"
6. "设置:在菜单加快捷入口" → "设置"菜单项,点入设置页
7. "赞助二维码怎么提供?" → "硬编码资源" + "从项目根路径 assets 取" → `assets/wechat-donate.png`
8. "顶部菜单要怎么放?" → "要 Alt+单字母助记键"(Alt+F 文件, Alt+S 设置, Alt+H 关于)
9. "关于对话框要多高?" → 堆叠顶~下
10. "UI 偏好要存在哪里?" → 项目根下的 `config/`

## Architecture

### 1. 顶部菜单条(在 `MainWindow.xaml` 现有 Grid Row 0 上方加新 Row)

```
<Window>
  <Grid>
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto" />   <!-- 新:菜单条 -->
      <RowDefinition Height="*" />     <!-- 现有 Row 0(原侧栏 + Content) -->
      <RowDefinition Height="Auto" />   <!-- 现有 Row 1:ErrorBanner -->
    </Grid.RowDefinitions>
    <Menu Grid.Row="0">
      <MenuItem Header="_文件(F)">
        <MenuItem Header="_保存环境..." Command="{Binding SaveUiPreferencesCommand}" />
        <MenuItem Header="_加载环境..." Command="{Binding LoadUiPreferencesCommand}" />
        <Separator />
        <MenuItem Header="打开项目文件夹" Command="{Binding OpenProjectFolderCommand}" />
        <MenuItem Header="查看日志目录"   Command="{Binding OpenLogFolderCommand}" />
        <Separator />
        <MenuItem Header="_退出"           Command="{Binding ExitAppCommand}" />
      </MenuItem>
      <MenuItem Header="_设置(S)">
        <MenuItem Header="设置..." Command="{Binding ShowSettingsCommand}" />
      </MenuItem>
      <MenuItem Header="_关于(H)">
        <MenuItem Header="关于 ComfyUI Manager..." Command="{Binding ShowAboutCommand}" />
      </MenuItem>
    </Menu>
    <!-- 现有内容(侧栏 + ContentControl + ErrorBanner)整体下移 1 个 Row -->
  </Grid>
</Window>
```

> 助记符:`_F`、`_S`、`_H` 在 Header 字符串里加下划线,Alt+F / Alt+S / Alt+H
> 触发打开。子项的 `_保存环境`、`_加载环境`、`_退出` 同样加下划线。
> 顶部右侧的"(_F)"等字符由 WPF 自动渲染(用户没明确要隐藏)。

### 2. `MainViewModel` 新增 6 个 Command(顶菜单 wire)

```csharp
public RelayCommand SaveUiPreferencesCommand { get; }
public RelayCommand LoadUiPreferencesCommand { get; }
public RelayCommand OpenProjectFolderCommand { get; }
public RelayCommand OpenLogFolderCommand { get; }
public RelayCommand ExitAppCommand { get; }
public RelayCommand ShowAboutCommand { get; }
```

`ShowSettingsCommand` 已有(侧栏"设置"按钮),复用 — 菜单项直接绑到它。

### 3. UI 偏好(文件 > 保存/加载环境)

新建 `Models/UiPreferences.cs`:

```csharp
public class UiPreferences
{
    public double? WindowWidth  { get; set; }
    public double? WindowHeight { get; set; }
    public double? WindowLeft   { get; set; }
    public double? WindowTop    { get; set; }
    public bool   WindowMaximized { get; set; }
    public bool   SidebarVisible  { get; set; } = true;
    public string? LastSelectedEnvId { get; set; }
    public string? LastViewName { get; set; }  // "Environments" / "Catalog" / ...
}
```

新建 `Services/UiPreferencesService.cs`:

- `void SaveToFile(string path, UiPreferences prefs)`
- `UiPreferences LoadFromFile(string path)`(读不到 / JSON 解析失败 → 静默回退默认值)
- `string DefaultPath` 静态属性,固定为 `<projectRoot>/config/ui-preferences.json`(projectRoot = `MainViewModel._projectRoot` 复用)
- `event EventHandler<UiPreferences>? Loaded` — 加载完成后通知订阅者应用

`App.OnStartup` 在 `SettingsDefaults.Apply` 之后,首次 `LoadFromFile(DefaultPath)`,
把 `WindowWidth/Height/Left/Top/Maximized` 应用到 `MainWindow`(用 `Loaded` 事件;
启动时 MainWindow 还没 AddToWindowCollection? — 走 `Window.SourceInitialized` 或
`App.MainWindow` 强类型 cast)。

`MainViewModel` 持有 `UiPreferencesService` 实例。`SaveUiPreferencesCommand` 用
`Microsoft.Win32.SaveFileDialog` 选路径(默认 `config/ui-preferences.json`,
filter `*.json`),`LoadUiPreferencesCommand` 用 `OpenFileDialog` 选文件,`Loaded`
事件里:调 `_mainWindow` 应用尺寸/最大化、`MainViewModel.CurrentViewName` = `LastViewName` →
派发 `ShowXxxCommand`(`LastSelectedEnvId` 应用走 `EnvListVM.Selected = envRepo.Get(id)`)。

### 4. About 对话框(`Views/AboutDialog.xaml` + `AboutDialog.xaml.cs`)

- 新 View+ViewModel:`ViewModels/AboutDialogViewModel.cs` 只持有 `Version` / `RepositoryUrl` / `LicenseText` / `DonateImagePath` 几个属性,`Show` 静态方法弹模态(`Window.ShowDialog`)。
- 布局(堆叠顶~下,窗口 360×420):
  ```
  ┌─────────────────────────────────────┐
  │ ComfyUI Manager              1.0    │   标题 + 版本号(粗体 24pt)
  │                                     │
  │ 一站式 ComfyUI 环境管理工具          │   简短描述
  │                                     │
  │ 授权: MIT                            │
  │ 仓库: github.com/fogyisland/...      │   Hyperlink
  │ 问题反馈: ...                        │   Hyperlink
  │ ─────────────────────────────────── │
  │ 扫码赞助(微信)                      │   12pt 灰字
  │       [wechat-donate.png]            │   <Image Width=180 Height=180/>
  │ 感谢你的支持 ❤                       │   11pt 灰字
  │                                     │
  │              [关闭]                  │
  └─────────────────────────────────────┘
  ```
- 资源路径:`assets/wechat-donate.png`(`projectRoot/assets/`)— 用 `pack://application:,,,/`
  找不到(那是引用程序集资源),走运行时 `new BitmapImage(new Uri(path, UriKind.Absolute))`。
  缺图时(`FileNotFoundException`)只显示"二维码未配置"占位 + 仍然显示关闭按钮。
- 快捷键:`Esc` 关闭,`F1` 打开 GitHub 链接,`Ctrl+C` 复制版本号(非必须,可省)。

### 5. OpenProjectFolder / OpenLogFolder

- `OpenProjectFolderCommand`:用 `Process.Start("explorer.exe", projectRoot)` 打开项目根。
- `OpenLogFolderCommand`:用 `Process.Start("explorer.exe", Path.Combine(projectRoot, "Logs"))`,
  目录不存在时先 `Directory.CreateDirectory`(跟 `AppLogger` 启动时一致)。

### 6. Exit

`ExitAppCommand`:`Application.Current.Shutdown()`。模态对话框关闭后调用,UI 退出
(等同关窗)。

## File structure

### Create

| 文件 | 行(估) | 职责 |
|---|---|---|
| `src-wpf/ComfyUI.Manager/Models/UiPreferences.cs` | ~30 | 偏好 DTO,JSON 序列化 |
| `src-wpf/ComfyUI.Manager/Services/UiPreferencesService.cs` | ~120 | Save/Load + DefaultPath + Loaded 事件 |
| `src-wpf/ComfyUI.Manager/ViewModels/AboutDialogViewModel.cs` | ~60 | Version / Repository / License / DonateImagePath |
| `src-wpf/ComfyUI.Manager/Views/AboutDialog.xaml` + `.xaml.cs` | ~120 | 堆叠布局 + Image + Hyperlink + Close 按钮 |
| `tests-wpf/.../Services/UiPreferencesServiceTests.cs` | ~180 | Save→Load round-trip / 缺文件 / JSON 损坏 / null 字段保留 / DefaultPath 走 config/ |
| `tests-wpf/.../ViewModels/AboutDialogViewModelTests.cs` | ~80 | 默认版本号 / 资源缺位时占位 / RepositoryUrl 正确 |
| `assets/wechat-donate.png` | 0 | 用户原话:项目根 assets/,先建空 placeholder,等用户提供真实 png 后替换 |

### Modify

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/MainWindow.xaml` | Grid 新 Row 0 = Auto 放 `<Menu>`;现有 Row 0/1 顺移到 Row 1/2 |
| `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` | 加 6 个 RelayCommand + ctor 注入 `UiPreferencesService`;`Loaded` 事件订阅 → 应用 Window 尺寸 / 切到 LastViewName |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | `new UiPreferencesService()`;在 `SettingsDefaults.Apply` 之后读 `DefaultPath` → 调 `WindowApplyTo(Loaded)` 之前 |
| `src-wpf/ComfyUI.Manager/MainWindow.xaml.cs` | `SourceInitialized` → 应用 UiPreferences WindowWidth/Height/Left/Top/WindowMaximized;`Closing` 写一份 preferences 回 `config/ui-preferences.json`(用 `_uiPreferencesService` 实例) |
| `src-wpf/ComfyUI.Manager/Views/AboutDialog.xaml.cs` | `Show(Window owner)` 静态,设 Owner + `ShowDialog` |
| `src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx` | 6 个新 string(menu labels,About 标题/版本提示/赞助文字) |
| `tests-wpf/.../ViewModels/MainViewModelMenuTests.cs` | 6 个新测试:每个 command `CanExecute=true`、command delegate 不抛 |

### Delete

无。

## Global constraints

| # | Constraint | Source |
|---|---|---|
| G1 | 顶部 `<Menu>` 控件 + 3 个 `<MenuItem>`,放在 `MainWindow` Grid Row 0(新加),不挤压侧栏(侧栏仍在,只是下移 1 Row) | user 选 "侧栏+菜单并存" |
| G2 | 助记符:Header 字符串里加下划线 `_*`,WPF 自动渲染 + Alt+字母触发 | user 选 Alt+单字母 |
| G3 | 文件菜单 5 项:保存环境 / 加载环境 / 打开项目文件夹 / 查看日志目录 / 退出;中间用 `<Separator />` 分组 | user 原话 + 4 个快入口 |
| G4 | 设置菜单只 1 项:设置... (复用 `ShowSettingsCommand`) | user 选 "设置"菜单项,点入设置页 |
| G5 | 关于菜单只 1 项:关于 ComfyUI Manager... (弹模态 AboutDialog) | user 选 关于 |
| G6 | UI 偏好文件路径:`<projectRoot>/config/ui-preferences.json`,`projectRoot` 取 `MainViewModel._projectRoot`(跟 `MainViewModel` 现有逻辑一致) | user 选 项目根下的 config/ |
| G7 | UI 偏好 Save/Load 弹 `Microsoft.Win32.SaveFileDialog` / `OpenFileDialog`,filter `*.json`,默认文件名/目录 = `config/ui-preferences.json` | 设计 + 跟现有 CatalogEntryPickerDialog 一致 |
| G8 | 启动时(Loading 窗口 Loaded 事件或 `Window.SourceInitialized` 之前)读一次 UiPreferences,应用到 MainWindow(尺寸/位置/最大化);关闭时(`Closing` 事件)写回 | 常见模式 |
| G9 | AboutDialog 用 `Window.ShowDialog`(模态),Owner = `Application.Current.MainWindow`;按 Esc 关闭 | 标准 WPF 模态 |
| G10 | 微信赞助二维码路径:`<projectRoot>/assets/wechat-donate.png`,在 `AboutDialogViewModel` ctor 里读 `File.Exists`;缺图时显示"二维码未配置,请联系作者"文字 + 关闭按钮仍工作 | user 选 硬编码资源 + 项目根 assets/ |
| G11 | 资源加载走 `new BitmapImage(new Uri(absPath, UriKind.Absolute))`(不是 pack URI,因为 png 在项目根不嵌进 exe) | 运行时加载 |
| G12 | 不 bump version / 不发 release zip / 无 ledger commit | per v0.6.5.6 hotfix 偏好 |
| G13 | 现有侧栏 6 个按钮(环境/节点目录/基础环境/设置/批量更新/系统状态)保留,内容不重复;菜单的"设置"项等价于侧栏"设置"按钮(都走 `ShowSettingsCommand`) | user 选 并存 |
| G14 | 退出命令 = `Application.Current.Shutdown()`(等同用户关窗);不弹"是否保存"确认(本程序无未保存业务数据,UI 偏好自动保存) | 标准模式 |
| G15 | 资源字符串走 `Strings.zh-CN.resx` 现有 6 个新 key(menu labels + About 文案);不在 XAML 写硬编码中文(老代码例外,本 plan 加的按 resx 走) | 现有 i18n 风格 |
| G16 | AboutDialog 的 RepositoryUrl / LicenseText 硬编码在 `AboutDialogViewModel` ctor 里,不走 `Settings`(用户原话"快捷链接"指向 fogyisland/ComfyUIEnvironmentManagement) | 设计 |
| G17 | UiPreferences JSON 字段缺失 / null 时 LoadFromFile 返回 `new UiPreferences()` 默认值,主程序照常启动(降级路径,跟 settings.json 一样) | 设计 |
| G18 | 测试不依赖 git/WPF STA;UiPreferencesService 用临时目录构造;AboutDialogViewModel 不实例化 BitmapImage(路径字段即可) | 项目风格 |

## Out of scope(本次不做)

- i18n 其它语种(只有 zh-CN)
- AboutDialog 富文本(markdown / xaml 流文档)— 简单 TextBlock 即可
- 系统托盘 / 全局快捷键 — 不在本次
- 主题切换菜单项 — Settings 已有 Theme 设置,菜单不放重复入口
- 加载环境时的"合并"模式 — 始终整文件覆盖(用户原话"加载环境"是切换配置)
- 捐赠金额档位 / 微信收款码 — 单一二维码图片

## Risks + tradeoffs

| 风险 | 缓解 |
|---|---|
| `MainWindow` 加新 Row 后,现有 Row 0(侧栏+内容)位置变化 → ContentControl Grid 绑定失效 | MainViewModel.CurrentView 类型不变,只 Grid.Row 改变;XAML 改 3 处 Row 索引 |
| Alt+单字母助记符跟现有 TextBox 冲突(用户在 TextBox 按 Alt+F 也会展开文件菜单) | 标准 WPF 行为,用户已习惯(Visual Studio / Office 同样);不刻意绕开 |
| 启动时读 ui-preferences.json 失败 → 崩溃 | LoadFromFile 静默回退默认值 + try/catch 包整段 |
| 关闭时写 preferences 失败 → 静默(用户原话没要求弹错) | try/catch 整段,失败只写 log(沿用 AppLogger) |
| 微信二维码 png 缺位 → AboutDialog 异常 | ViewModel ctor 提早 `File.Exists` 检查 → 切到占位;XAML 用 `Visibility` 切换 Image vs 占位 TextBlock |
| BitmapImage 异步加载卡住 UI | 用 `BitmapCacheOption.OnLoad` + `BitmapCreateOptions.None`,等加载完再显示;缺图 catch → 占位 |
| 用户保存到不存在的目录 | SaveFileDialog 弹错后被 framework 接住,不写文件;后续读回 default 即可 |
| 多显示器位置 — Last 坐标可能在不存在的屏幕上 | `SystemParameters.VirtualScreenWidth/Height` 检查越界 → 退化到 (100,100) |
| UI 偏好 JSON 字段被用户手动改坏 | LoadFromFile try/catch 全包 + LogResult("ui-preferences", "failed", ex.Message) |

## Open questions

无 — 全部已澄清。

## Self-review

- [x] 范围:单 plan 可独立完成(顶部 Menu + 1 个 Dialog + 1 个 DTO + 1 个 Service)
- [x] 占位符:无 TBD/TODO
- [x] 一致性:Architecture 跟 User stories 1:1 对应(3 个顶级菜单 = 1/2/3;保存/加载 = 3;微信二维码 = 4;并存 = 5;设置等价 = 6;硬编码资源 = 7;Alt 助记 = 8;堆叠布局 = 9;config/ = 10)
- [x] 二义性:G6 明确写 projectRoot 来源;G10 明确缺位占位;G17 明确降级路径

## Verification(写 plan 时再细化)

- 单元测试:`dotnet test tests-wpf/.../ComfyUI.Manager.Tests/` → 期望 +N(暂估 8-10)
- 端到端:用户 desktop 跑 staging → 看到顶部菜单 → Alt+F 展开 → 选"退出" → 应用关闭
- 边界:删除 `config/ui-preferences.json` 重启 → 应用启动正常,UI 用默认尺寸
- 边界:把 `wechat-donate.png` 改名 → AboutDialog 显示"二维码未配置"占位,不崩

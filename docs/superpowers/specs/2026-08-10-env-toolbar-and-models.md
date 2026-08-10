# env-list 两行按钮 + 组件报告 Chrome Fallback + 设置-全局默认 Models 路径

## 背景

用户桌面验证 v0.6.9.3 (status bar + 齿轮 + 太阳/月亮) 时反馈 3 个独立问题:

1. **env-list 操作列单行 10 按钮挤,看不清**:启动/停止/装依赖/卸依赖/卸基础环境/查日志/打开浏览器/装节点/组件报告/删除 全部塞一行,列宽 560px,文本略长就挤;用户想要按"安装/卸载 链路"vs"调试/删除 链路"分两行。

2. **组件报告按钮走的不是 Chrome**:`DefaultOpenReportFile` 直接 `Process.Start(UseShellExecute=true)` 走默认浏览器,没尝试 Chrome;但 `OpenBrowser`(打开 ComfyUI 页面)已实现 Chrome 优先 + 默认浏览器 fallback(v0.6.7.2)。

3. **设置里"模型路径"用户没看到入口**:v0.6.7.3 加过"共享 Models 目录"字段 + 浏览按钮,但这是"共享"语义(所有 env junction 到同一目录);用户原话要的是"全局默认"(env-create 默认填的路径,每个 env 仍可独立覆盖)。

3 个改动相对独立(改 3 个 view/XAML + 1 个 Settings 字段),1 个 spec 一起实现。

---

## 目标

- env-list 操作列改两行按钮(每行 5 个),按"安装/卸载"vs"调试/删除"分组
- 组件报告按钮跟"打开浏览器"按钮行为一致:Chrome 优先 → 默认浏览器 fallback → 错误提示
- 设置新增"全局默认 Models 目录"字段,env-create 时作为默认值;已存在"共享 Models 目录"字段保留(共享 = 跨 env 共享,默认 = 每个 env 默认值,两者独立)

---

## 架构

### 1. env-list 两行按钮

- `Views/EnvironmentListView.xaml` 操作列 DataGridTemplateColumn 改 `CellTemplate`:
  - 原:横向 StackPanel 10 个 Button
  - 新:垂直 StackPanel 含 2 个横向 WrapPanel
    - Row1:启动 / 停止 / 装依赖 / 卸载依赖 / 卸载基础环境 (安装卸载链路)
    - Row2:查看日志 / 打开浏览器 / 安装节点 / 组件报告 / 删除 (调试删除链路)
- 列 Width 从 560 → 580(留 WrapPanel 余量),DataGrid 行高自动 grow(原固定 → 不强制固定)
- 按钮顺序保持现状(用户已确认 Row1/Row2 的 5+5 映射)

### 2. 组件报告 Chrome fallback

- 抽取 `Services/BrowserLauncher` 静态 helper:
  ```csharp
  public static class BrowserLauncher
  {
      public static void OpenWithChromeFallback(string path);
      private static string? ResolveChromePath(); // 复用 EnvironmentListViewModel 现有 3 个候选路径
  }
  ```
- `EnvironmentListViewModel.DefaultOpenReportFile` 改调 `BrowserLauncher.OpenWithChromeFallback(path)`
- `EnvironmentListViewModel.OpenBrowser` 也调 `BrowserLauncher.OpenWithChromeFallback` (消除重复)
- 行为:
  1. 优先 Chrome.exe(3 个候选:ProgramFiles / ProgramFiles(x86) / LOCALAPPDATA\Google\Chrome\Application)
  2. Chrome 不存在或启动失败 → `Process.Start(UseShellExecute=true)` 走默认浏览器
  3. 默认浏览器启动也失败 → `ErrorBanner.Add` 显示 "打开报告失败:..." (沿用 ErrorSeverity.Warn,不抛 MessageBox)

### 3. 设置全局默认 Models 路径

- `Models/Settings.cs` 新增字段:
  ```csharp
  [JsonPropertyName("default_models_directory")]
  public string DefaultModelsDirectory { get; set; } = "";
  ```
  默认值空字符串 = 不设置全局默认(env-create 仍用项目根 `<projectRoot>/models` 作 fallback)。
- `SettingsDefaults.Apply` 不动这个字段(不主动写默认值,用户显式填)。
- `Views/SettingsView.xaml` 新增一行(放在"共享 Models 目录"之上,作为更常用的入口):
  ```xml
  <TextBlock Text="全局默认 Models 目录(留空 = 用项目根/models 作默认)" Margin="0,8,0,4" />
  <DockPanel Margin="0,2,0,0">
      <Button DockPanel.Dock="Right" Content="浏览..." Click="BrowseDefaultModelsDirectory"
              Style="{StaticResource MaterialButton}" Margin="4,0,0,0" />
      <TextBox Text="{Binding DefaultModelsDirectory, UpdateSourceTrigger=PropertyChanged}"
               Style="{StaticResource MaterialTextBox}" />
  </DockPanel>
  ```
- `Views/SettingsView.xaml.cs` 新增 `BrowseDefaultModelsDirectory` Click handler(同 `BrowseSharedModelsDirectory` 模式)
- `ViewModels/SettingsViewModel.cs` 新增 `DefaultModelsDirectory` property + 双向绑定逻辑(同 `SharedModelsDirectory` 模式)
- `Services/EnvCreatorService.cs` 在创建 env 时,如果 `_settings.DefaultModelsDirectory` 非空 → 用作 `<env-root>/models` 的初始路径(写入 `Environment.ModelsDirectory` 字段,如果有);空 → fallback `<projectRoot>/models`(沿用现有逻辑)
- **不动** `SharedModelsDirectory` 字段、不动 junction 逻辑(那是 v0.6.7.3 的事);两字段语义独立:
  - `DefaultModelsDirectory` = 每个 env 默认值(可被 env 自身覆盖)
  - `SharedModelsDirectory` = 跨 env 共享(junction 同步)

---

## 关键文件

**新增:**
- `src-wpf/ComfyUI.Manager/Services/BrowserLauncher.cs`(~50 行)

**修改:**
- `src-wpf/ComfyUI.Manager/Models/Settings.cs`(+1 字段)
- `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`(+1 行 + 浏览按钮)
- `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs`(+1 Click handler)
- `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`(+1 property)
- `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml`(操作列改两 WrapPanel)
- `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`(`DefaultOpenReportFile` + `OpenBrowser` 改调 `BrowserLauncher`)
- `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs`(env-create 用 `DefaultModelsDirectory` 作默认路径)

**测试新增:**
- `tests-wpf/.../Services/BrowserLauncherTests.cs`(~4 测试:Chrome 优先 / Chrome 失败回退 / Chrome + 默认浏览器都失败 throw / 无 URL 不调)
- `tests-wpf/.../Models/SettingsDefaultModelsDirectoryTests.cs`(~2 测试:字段存在 + 默认空字符串)
- `tests-wpf/.../ViewModels/SettingsViewModelDefaultModelsDirectoryTests.cs`(~2 测试:property 绑定 / 浏览按钮 override)

---

## 数据流

```
用户点「组件报告」按钮
   ↓
EnvironmentListViewModel.ReportComponentsExecuteWrapper(env)
   ↓
build HTML → 写 <projectRoot>/reports/env-{name}-{ts}.html
   ↓
BrowserLauncher.OpenWithChromeFallback(path)
   ├─ ResolveChromePath() → 找到 chrome.exe
   │  └─ Process.Start(chrome, path) → Chrome 打开
   ├─ ResolveChromePath() → null (Chrome 未装)
   │  └─ Process.Start(UseShellExecute=true) → 默认浏览器打开
   └─ 都失败 → ErrorBanner.Add Warn
```

```
用户创建新 env
   ↓
EnvCreatorService.CreateAsync(name, ...)
   ├─ if (settings.DefaultModelsDirectory != "")
   │     env.ModelsDirectory = settings.DefaultModelsDirectory
   ├─ else
   │     env.ModelsDirectory = "<projectRoot>/models" (现有 fallback)
   └─ if (settings.SharedModelsDirectory != "")
        junction env.ComfyuiSource/models → settings.SharedModelsDirectory
```

---

## 边界

- 不动现有 `Settings.SharedModelsDirectory` 字段(保留 junction 共享机制)
- 不动 `SharedModelsDirectory` 的 env-create junction 逻辑(v0.6.7.3 已存在)
- 不动 `OpenBrowserCommand.CanExecute`(仍然只在 env.Status=="running" 时 enabled)
- 不动 `ReportComponentsCommand.CanExecute`
- 不动 SettingsPage 其他控件的布局
- env-list 列 Width 从 560 → 580(允许 WrapPanel 在 800px 窗口宽度下不横向滚动)

---

## 风险

| 风险 | 缓解 |
|---|---|
| 两行按钮让行高变高,DataGrid 行高自适应可能让 viewport 减少可见行数 | DataGrid 默认行高自适应,5+5 按钮每个约 30px,总高 ~70px,原单行 ~30px,影响 1 行可见性;可接受 |
| `BrowserLauncher` 抽出来跨 file 调用,ResolveChromePath() 静态方法需要在测试时可 mock | 抽 `IBrowserLauncher` 接口 + 测试 seam(同 SettingsDefaults 模式);或 `ResolveChromePath` 改成 `internal static` + InternalsVisibleTo("ComfyUI.Manager.Tests") |
| `DefaultModelsDirectory` 默认空字符串,旧 settings.json 缺字段时反序列化给 "" → env-create 走 fallback | 兼容旧 settings.json 的标准 JsonPropertyName 行为;不需要 migration |
| Chrome 启动但被用户设默认拒,Process.Start 抛 Win32Exception → 我们的代码 catch 后回退默认 | OpenBrowser 现已这么干,沿用 |
| `EnvCreatorService` 改动可能影响 v0.6.7.3 junction 测试 | EnvCreatorService.CreateAsync 已有 5+ 测试,新加 1 测试覆盖 "DefaultModelsDirectory 非空时 env.ModelsDirectory 字段被设置" 即可 |

---

## 不在范围

- 鼠标 hover 高亮按钮(不增加,沿用 MaterialButton 默认)
- 键盘 shortcut(不增加,Tab 顺序即可)
- 按钮图标(不增加,文字按钮)
- Settings 字段排序调整(不调整,新字段就放在"共享 Models 目录"之前)
- env-list 行的其他列(不动)
- BED picker / Picker 改写(不动)

---

## 验证

GUI 烟测 6 步:
1. 启动 → env-list 操作列变两行,Row1 = 启动/停止/装依赖/卸依赖/卸基础环境,Row2 = 查日志/打开浏览器/装节点/组件报告/删除
2. 点"组件报告" → Chrome 打开 reports/ 下新生成的 HTML
3. 卸载 Chrome(或设 path 指空)→ 再点组件报告 → 默认浏览器打开
4. 设置 → 全局默认 Models 目录填路径 → 保存 → 新建 env → env.ModelsDirectory = 此路径
5. 设置 → 共享 Models 目录仍可填 → 保留
6. 旧 settings.json 缺 default_models_directory 字段 → 反序列化给 "" → env-create 走 fallback `<projectRoot>/models`

自动化:
- `dotnet build` 0/0
- `dotnet test` 764+8 PASS / 2 FAIL(pre-existing flaky)/ 1 SKIP
- 无 v-bump / 无 release zip

---

## 实施策略

推荐 Subagent-Driven Development,1 个 spec = 1 个 plan。计划拆 4 个 task:
- T1: Settings.DefaultModelsDirectory 字段 + VM + View UI + tests
- T2: BrowserLauncher 抽象 + EnvironmentListViewModel 接入 + tests
- T3: EnvCreatorService 用 DefaultModelsDirectory + tests
- T4: EnvironmentListView.xaml 两行布局 + 最终整合

每个 task 独立可测,最后 1 个 final review。
# v0.6.5.9 Spec: Catalog 主页「下载」到本地节点目录

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

## 0. 背景

v0.6.5.7 之前,Catalog 主页(`CatalogView.xaml`)详情面板只有一个「安装」按钮:`CatalogViewModel.InstallAsync`(`Views/CatalogView.xaml.cs` 同行 VM at `:310-331`)先 `_envRepo.ListAll()` 取所有 env,然后**盲选 `envs[0]`**(完全没有让用户挑),调 `_nodeOps.InstallAsync(env.Id, ...)` 把节点 git clone 到该 env 的 `custom_nodes_path`,再写一行 `ScannedNode` row。

这有两个问题:
1. **「安装」按钮实际上只是在 git clone**(`NodeOperations.InstallAsync:60-164` 全程就 `git clone + (可选) git checkout tag + 写 ScannedNode` —— 没有 pip install / npm install 步骤),名字误导用户,看起来像是要做「装依赖」之类的额外动作。
2. **目标 env 是 `envs[0]`** —— 用户根本不知道装到哪个 env 了,跟 EnvList 行内「安装节点」按钮(走 `CatalogEntryPickerDialog` → `InstallDialog`,带 env ComboBox 显式选择)行为不一致,体验割裂。

GUI smoke 阶段用户提了这两个问题合并成一个修复:**把 Catalog 主页的「安装」改成「下载」**,目标 = Settings 里**新加**的「本地节点目录」字段;**不写 ScannedNode**(下载下来的节点还不属于任何 env,只是本地文件)。

后续若用户想让「下载下来的节点装到 env」是单独一条 issue(P1-X),不在本 spec 范围。

## 1. Goals

- **G1**: `Settings.LocalNodeDirectory` 新字段,默认 = `<projectRoot>/local-nodes/`(相对子目录名 `"local-nodes"`,运行时跟其它 path 字段一样 `Path.Combine(projectRoot, settings.LocalNodeDirectory)` 解析)。
- **G2**: `SettingsView` 「路径」section 加新一行 UI:TextBox + 「浏览...」按钮(`Microsoft.Win32.OpenFolderDialog`),行为跟 `EnvsDir` / `GlobalNodesDir` 那两行一模一样。
- **G3**: `NodeOperations.DownloadAsync(localDir, nodeId, repoUrl, targetTag?)` 新方法:**纯 git clone**,不查 env、不写 `ScannedNode` row。失败语义跟 `InstallAsync` 一致(用户取消 → `"用户取消"`、git 退出非零 → stderr 首行、启动失败 → 异常消息)。
- **G4**: `CatalogViewModel`:`InstallCommand` / `InstallButtonLabel` 重命名为 `DownloadCommand` / `DownloadButtonLabel`(「下载」/「下载 {tag}」);目标目录 = `_settings.LocalNodeDirectory`;**不再注入 `EnvironmentRepository`**(Catalog 主页走下载,完全跟 env 解耦)。`InstallAsync` 整个方法体重写为 `DownloadAsync` 调 `NodeOperations.DownloadAsync`。
- **G5**: `CatalogView.xaml` 按钮 `{Binding InstallButtonLabel}` → `{Binding DownloadButtonLabel}`,`Command="{Binding InstallCommand}"` → `Command="{Binding DownloadCommand}"`。
- **G6**: 老 `CatalogEntryPickerDialog` / `InstallDialog` / `InstallNodeCommand`(EnvList 行内「安装节点」流程)完全**不动** —— 那个流程本来就是显式选 env 后 git clone 到该 env,跟「本地下载」是两条正交路径,合并会让 EnvListVM 操作列更乱。

## 2. 非 Goals

- 不实现「本地下载 → 一键装到 env」桥接 —— 那是 P1-X 后续
- 不实现 Catalog 主页上的 env 选择器(Picker 已经在 `InstallDialog` 里有了,本 spec 不再加一份)
- 不实现「已下载到本地目录的节点」在 EnvList / Catalog 页面的可视化呈现(暂时纯文件系统,用户自己 `cd` 进去看)
- 不 bump version(per hotfix 偏好:跟 v0.6.5.8 P0-A 一样本地 commit + 重建 staging,不发布新 release zip;若用户后续想 release,再补 bump 任务)
- 不改 `NodeOperations.InstallAsync` 的语义(env 路径上的安装还得用)
- 不改 `Environment.CustomNodesPath` 字段(本 spec 不引入 env 维度)
- 不动 `App.xaml.cs` 启动 wiring(`_mainVm` 构造签名不变 —— CatalogViewModel 删掉 `envRepo` 注入即可,`MainViewModel` 把那个 ctor 参数移除)

## 3. 数据模型

`Models/Settings.cs` 加一个字段:

```csharp
[JsonPropertyName("local_node_directory")]
public string LocalNodeDirectory { get; set; } = "";
```

无 schema 变更(SQLite 不动),无新表。

## 4. 架构

### 4.1 Settings 字段 + 默认值

**`Models/Settings.cs:23-28`** 「路径」section 加一行:

```csharp
[JsonPropertyName("local_node_directory")] public string LocalNodeDirectory { get; set; } = "";
```

**`Infrastructure/SettingsDefaults.cs`** 加常量 + Apply 兜底:

```csharp
public const string LocalNodesSubdir = "local-nodes";
```

在 `Apply` 方法末尾(`s.PythonInterpreters` 合成块之后)加:

```csharp
s.LocalNodeDirectory = Resolve(s.LocalNodeDirectory, LocalNodesSubdir, projectRoot);
```

跟 `TemplatePythonDir` / `TemplateComfyuiDir` 同一类(template-style 自动默认子目录名),不是 user-configured-only(EnvsDir/GlobalNodesDir 那类空着保持空)。区别:
- **template paths**:空 → 填子目录名(本地节点目录、模板 Python、模板 ComfyUI 都在这一类,本地节点目录跟模板 Python/ComfyUI 同级)
- **user-configured paths**:空 → 保持空(服务层在使用时报错 —— EnvsDir/GlobalNodesDir 在这一类)

**为什么选 template-style 而不是 user-configured-style**:
- 用户在 GUI smoke 阶段明确「项目根下 `./local-nodes/`」是默认值,跟模板 Python / ComfyUI 同语义(「包自带资源类,落到程序根下」),不是「用户主动管理的数据」
- 启动时 `App.xaml.cs:44` 已经把 settings 应用 + 保存了,目录会自动预创建(`App.xaml.cs` 在 `SettingsDefaults.Apply` 后做 `Directory.CreateDirectory(localNodesDir)`)

### 4.2 Settings UI 加一行

**`Views/SettingsView.xaml`** 「路径」section 在 `GlobalNodesDir` 行(168-175)之后加:

```xml
<TextBlock Text="本地节点目录(Catalog 主页「下载」按钮的目标)" Margin="0,8,0,4" />
<DockPanel Margin="0,2,0,0">
    <Button DockPanel.Dock="Right" Content="浏览..."
            Click="BrowseLocalNodeDir"
            Style="{StaticResource MaterialButton}" Margin="4,0,0,0" />
    <TextBox Text="{Binding LocalNodeDirectory, UpdateSourceTrigger=PropertyChanged}"
             Style="{StaticResource MaterialTextBox}" />
</DockPanel>
```

**`Views/SettingsView.xaml.cs`** 加 handler:

```csharp
private void BrowseLocalNodeDir(object sender, RoutedEventArgs e)
{
    if (DataContext is SettingsViewModel vm)
    {
        var picked = vm.PickFolder();
        if (picked is not null) vm.LocalNodeDirectory = picked;
    }
}
```

**`ViewModels/SettingsViewModel.cs`** 加属性 + 在 `RaiseAllPropertiesChanged` 加一行:

```csharp
public string LocalNodeDirectory
{
    get => _settings.LocalNodeDirectory;
    set
    {
        _settings.LocalNodeDirectory = value ?? "";
        _repo.Save(_settings);
        RaisePropertyChanged();
    }
}
```

`RaiseAllPropertiesChanged()` 加 `RaisePropertyChanged(nameof(LocalNodeDirectory));`。

### 4.3 NodeOperations.DownloadAsync

**`Services/NodeOperations.cs`** 新增方法(`InstallAsync` 之后):

```csharp
/// <summary>
/// git clone &lt;repoUrl&gt; &lt;localDir/nodeId&gt;。纯下载,不查 env,不写 ScannedNode。
///
/// targetTag 非空时:clone 完再 git checkout &lt;tag&gt;。
///
/// 失败语义跟 InstallAsync 一致:用户取消 → "用户取消",git 退出非零 → stderr 首行,
/// 启动失败 → 异常消息。
/// </summary>
public virtual async Task<NodeOperationResult> DownloadAsync(
    string localDir, string nodeId, string repoUrl,
    string? targetTag = null,
    CancellationToken ct = default)
{
    if (string.IsNullOrWhiteSpace(localDir))
        return NodeOperationResult.Fail("本地节点目录为空,请先在 Settings 配置");
    if (string.IsNullOrWhiteSpace(nodeId))
        return NodeOperationResult.Fail("node id 不能为空");

    if (string.IsNullOrWhiteSpace(repoUrl))
    {
        var activeName = _settings.ActiveDownloadSourceName;
        var src = _settings.DownloadSources.FirstOrDefault(s => s.Name == activeName);
        if (src is null || string.IsNullOrWhiteSpace(src.Url))
            return NodeOperationResult.Fail("未配置下载源,请在 Settings 添加");
        repoUrl = NodeUrlResolver.Resolve(src.Url, nodeId);
        if (string.IsNullOrWhiteSpace(repoUrl))
            return NodeOperationResult.Fail("下载源 URL 解析为空");
    }

    Directory.CreateDirectory(localDir);
    var targetDir = Path.Combine(localDir, nodeId);
    if (Directory.Exists(targetDir))
        return NodeOperationResult.Fail($"目录已存在:{targetDir}");

    GitResult result;
    try
    {
        result = await _git.RunAsync(
            localDir,
            new[] { "clone", "--", repoUrl, nodeId },
            DefaultPerCallTimeout, ct);
    }
    catch (OperationCanceledException) { return NodeOperationResult.Fail("用户取消"); }
    catch (Exception ex) { return NodeOperationResult.Fail($"启动 git 失败:{ex.Message}"); }

    if (!result.Ok)
    {
        try { if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true); } catch { }
        return NodeOperationResult.Fail(FirstLine(result.Stderr, result.Stdout)
            ?? $"git 退出码 {result.ExitCode}");
    }

    if (!string.IsNullOrWhiteSpace(targetTag))
    {
        GitResult checkoutResult;
        try
        {
            checkoutResult = await _git.RunAsync(
                targetDir, new[] { "checkout", targetTag },
                DefaultPerCallTimeout, ct);
        }
        catch (OperationCanceledException) { TryDelete(targetDir); return NodeOperationResult.Fail("用户取消"); }
        catch (Exception ex) { TryDelete(targetDir); return NodeOperationResult.Fail($"启动 git checkout 失败:{ex.Message}"); }

        if (!checkoutResult.Ok)
        {
            var reason = FirstLine(checkoutResult.Stderr, checkoutResult.Stdout)
                ?? $"git checkout 退出码 {checkoutResult.ExitCode}";
            TryDelete(targetDir);
            return NodeOperationResult.Fail($"checkout {targetTag} 失败:{reason}");
        }
    }

    return NodeOperationResult.Ok(TryReadHeadShaAsync(targetDir, ct).GetAwaiter().GetResult());
}

private TryReadHeadShaAsync is the existing private helper at Services/NodeOperations.cs:345.
```

`TryReadHeadShaAsync` 是 `private async Task<string?>` —— 在同步 return 路径里 `GetAwaiter().GetResult()` 是 ok 的(没有 SyncContext 死锁风险,这里已经在 async/await 链尾)。或者改成 `await TryReadHeadShaAsync(targetDir, ct)` 然后 return —— 跟 InstallAsync 末尾一致更整洁,选后者。

```csharp
var headSha = await TryReadHeadShaAsync(targetDir, ct);
return NodeOperationResult.Ok(headSha);
```

**为什么不直接复用 `InstallAsync` 然后传一个 dummy env**:
- `InstallAsync` 内部 `RequireEnv(envId)` 强制要 envId,且会写 `ScannedNode` row,本 spec 明确不写
- 把 install 拆成 install + download 两个独立方法,职责清晰,各自可测

### 4.4 CatalogViewModel 改写

**`ViewModels/CatalogViewModel.cs`**:

- 构造参数列表:删 `EnvironmentRepository envRepo`,删 `_envRepo` 字段
- `InstallCommand` / `InstallButtonLabel` 重命名为 `DownloadCommand` / `DownloadButtonLabel`
- `InstallButtonLabel` getter 改成 `DownloadButtonLabel`:返回 `"下载"` 或 `$"下载 {_selectedVersion?.Tag}"`
- `InstallAsync` 方法体重写为 `DownloadAsync`:从 `_settings.LocalNodeDirectory` 解析出绝对路径(相对 → `Path.Combine(projectRoot, localDir)`),调 `_nodeOps.DownloadAsync(...)`,失败 `ErrorMessage`、成功 `InfoMessage`
- `LoadVersionsForSelected` 里 `RaisePropertyChanged(nameof(InstallButtonLabel))` → `RaisePropertyChanged(nameof(DownloadButtonLabel))`
- `Selected` setter 里 `RaisePropertyChanged(nameof(InstallButtonLabel))` → 同上

**`ViewModels/MainViewModel.cs`**:`CatalogViewModel` ctor 调用处删 `envRepo` 实参

**`Services/NodeOperations` ctor 不动** —— 它本来就接受 `EnvironmentRepository`,但 `DownloadAsync` 不用它。

### 4.5 CatalogView.xaml 改 1 行

**`Views/CatalogView.xaml:168-172`** 详情面板按钮:

```xml
<Button Content="{Binding DownloadButtonLabel}" Margin="0,16,0,0" HorizontalAlignment="Left"
        Padding="20,6"
        Command="{Binding DownloadCommand}"
        CommandParameter="{Binding Selected}"
        Style="{StaticResource MaterialButton}" />
```

## 5. UI 布局

- **Settings 页** 「路径」section 新增一行「本地节点目录」,跟在 `GlobalNodesDir` 行后面
- **Catalog 主页** 详情面板右下按钮文字由「安装 {version}」→「下载 {version}」(无 version 时就是「下载」)

无其它 UI 改动。

## 6. Testing

### 6.1 Settings 加 LocalNodeDirectory

```csharp
// tests-wpf/.../SettingsTests.cs

[Fact]
public void Settings_LocalNodeDirectory_DefaultsToRelativeSubdir()
{
    var s = new Settings();
    SettingsDefaults.Apply(s, projectRoot: @"C:\fake\root");
    Assert.Equal("local-nodes", s.LocalNodeDirectory);
}

[Fact]
public void Settings_LocalNodeDirectory_PersistsAcrossReload()
{
    var s = new Settings { LocalNodeDirectory = @"D:\my-nodes" };
    var json = JsonSerializer.Serialize(s);
    var s2 = JsonSerializer.Deserialize<Settings>(json)!;
    Assert.Equal(@"D:\my-nodes", s2.LocalNodeDirectory);
}

[Fact]
public void Settings_LocalNodeDirectory_AbsolutePathUnderProjectRoot_MigratesToRelative()
{
    var s = new Settings { LocalNodeDirectory = @"C:\fake\root\local-nodes" };
    SettingsDefaults.Apply(s, projectRoot: @"C:\fake\root");
    Assert.Equal("local-nodes", s.LocalNodeDirectory);
}

[Fact]
public void Settings_LocalNodeDirectory_AbsolutePathOutsideProjectRoot_KeptAsIs()
{
    var s = new Settings { LocalNodeDirectory = @"D:\elsewhere\nodes" };
    SettingsDefaults.Apply(s, projectRoot: @"C:\fake\root");
    Assert.Equal(@"D:\elsewhere\nodes", s.LocalNodeDirectory);
}
```

### 6.2 NodeOperations.DownloadAsync

```csharp
// tests-wpf/.../Services/NodeOperationsDownloadTests.cs

[Fact]
public async Task DownloadAsync_ClonesRepoIntoLocalDir()
{
    // fake GitRunner 返回 Ok
    // 调用 DownloadAsync(localDir, "node-x", "https://example.com/node-x")
    // → 验:GitRunner 被调一次,args = ["clone", "--", repoUrl, "node-x"]
    //   cwd = localDir(从 RunAsync 调用抓)
    // → 返 NodeOperationResult.Success
}

[Fact]
public async Task DownloadAsync_DoesNotWriteScannedNode()
{
    // fake GitRunner 返回 Ok
    // 调用 DownloadAsync(...)
    // → NodeRepository.Upsert 没被调(NodeRepository 注入一个 spy/mock)
}

[Fact]
public async Task DownloadAsync_TargetTag_ChecksOutAfterClone()
{
    // fake GitRunner:第一次 clone → Ok;第二次 checkout → Ok
    // → 验两次 RunAsync 都被调,顺序对
}

[Fact]
public async Task DownloadAsync_DirAlreadyExists_ReturnsFail()
{
    // 在 localDir 下预创建 node-x 目录
    // → 返 Fail("目录已存在:...")
}

[Fact]
public async Task DownloadAsync_LocalDirEmpty_ReturnsFail()
{
    // localDir = "" → Fail("本地节点目录为空...")
}

[Fact]
public async Task DownloadAsync_GitFails_CleansUpEmptyDirAndReturnsFail()
{
    // fake GitRunner 返回 ExitCode=128, stderr="repo not found"
    // → Fail("repo not found"),空目录被清理
}

[Fact]
public async Task DownloadAsync_UserCancels_ReturnsCancelReason()
{
    // fake GitRunner 抛 OperationCanceledException
    // → Fail("用户取消")
}
```

### 6.3 老 test 兼容性

- `NodeOperationsTests` 现有测试不动 —— 新增 `DownloadAsync` 不影响 `InstallAsync`
- `SettingsTests` 现有测试不动 —— 新字段独立
- `CatalogViewModelTests`(如有)现有测试如果覆盖了 `InstallCommand` → 需要把 `InstallCommand` 改成 `DownloadCommand`,但目前没看到 `CatalogViewModelTests` 这个文件,需要先查一下

## 7. 风险 + 权衡

| 风险 | 缓解 |
|---|---|
| Catalog 主页「下载」语义跟 EnvList 行内「安装节点」语义不同,用户困惑 | 「下载」就是文件下载到本地,「安装节点」是装到具体 env,文档/UI 上用文字区分(已经在按钮 label 体现)。后续 P1-X 加桥接 |
| `local-nodes` 目录不存在 → git clone 失败 | `DownloadAsync` 入口 `Directory.CreateDirectory(localDir)` 兜底 |
| 用户清空 Settings.LocalNodeDirectory → 按钮点击报错 | `DownloadAsync` 入口检查空 → Fail("本地节点目录为空,请先在 Settings 配置"),InfoMessage 提示用户去 Settings 配置 |
| 老用户升级 v0.6.5.9 → settings.json 没 `local_node_directory` 字段 | JSON 反序列化新字段默认值 `""`,`SettingsDefaults.Apply` 把空填成 `"local-nodes"`,无破坏性 |
| 用户把 `local-nodes` 放在跨机器的网盘上 → clone 慢/git 锁 | 用户主动行为,跟 `EnvsDir` 同类,不在本 spec 兜 |
| `MainViewModel` ctor 删 `envRepo` 实参 → 编译断 | catalog 不再需要 envRepo,MainViewModel 那行实参对应删即可;若 ctor 注释里有「catalog 用 envRepo」需要同步删 |
| `NodeOperations` 注入 `EnvironmentRepository` 但 `DownloadAsync` 不用 | 保留注入(`InstallAsync` 还要用),只是 `DownloadAsync` 不调用 `_envRepo`。不引入不必要的重构 |
| Settings 路径字段顺序变化导致 settings.json 字段顺序 diff | 加在「路径」section 末尾,跟现有顺序保持一致 |

## 8. 升级注意

- **直接覆盖 v0.6.5.x 文件即可**(无 version bump,本地 hotfix per 用户偏好)
- 老 settings.json 无 `local_node_directory` 字段 → JSON 反序列化用字段默认值 `""` → `SettingsDefaults.Apply` 填默认子目录名
- 首次启动后 Settings 页能看到新字段,默认 `<projectRoot>/local-nodes/`
- 不发 release zip(per `feedback_no_zip.md`)
- 不写 ledger commit(per v0.6.5.8 P0-A 偏好)

## 9. Verification

### 单元测试

- WPF `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal` → 期望 ~370 PASS / 0 FAIL / 1 SKIP(基线 362 + 新增 ~8:Settings 4 + DownloadAsync 7 ≈ 11 个,但老 `CatalogViewModelTests` 若有 `InstallCommand` 引用要同步改名可能减 1-2)

### 端到端手动测试(用户 desktop)

1. 双击 `release/staging/ComfyUI Manager/ComfyUI.Manager.exe`
2. 侧栏「设置」→ 滚到「路径」section → 看到新行「本地节点目录」,TextBox 预填 `<exe-dir>/local-nodes`,点「浏览...」可改
3. 侧栏「Catalog」→ 刷新 catalog → 选一个节点 → 详情面板右下按钮文字变成「下载 {version}」或「下载」
4. 点「下载」→ dialog 不弹(直接后台 git clone)→ 主窗口显示 `已下载 <pkg> → version=<sha>`
5. 打开 `<exe-dir>/local-nodes/` 文件夹 → 看到节点目录被 clone 下来
6. **重复点击测试**:同一个节点再点「下载」→ `已存在:<...>` 错误显示在 ErrorMessage
7. **Settings 清空测试**:在 Settings 把「本地节点目录」清空 → 回到 Catalog 点「下载」→ 提示「本地节点目录为空,请先在 Settings 配置」

### 边界

- LocalNodeDirectory 设到不存在的路径(如 `D:\foo\nodes`)→ DownloadAsync 自动 `CreateDirectory`,下次 clone OK
- git 失败(repo url 404)→ 显示 stderr 首行 + 自动清空 clone 半成品目录
- 用户取消 → 显示「用户取消」

## 10. Critical files

- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs` (+ `LocalNodeDirectory` 字段)
- Modify: `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs` (+ `LocalNodesSubdir` 常量 + `Apply` 一行)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs` (+ `Directory.CreateDirectory(localNodesDir)` after Apply,可选)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` (+ `LocalNodeDirectory` 属性 + `RaiseAllPropertiesChanged` 加一行)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` (+ 「本地节点目录」 UI 行)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml.cs` (+ `BrowseLocalNodeDir` handler)
- Modify: `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs` (+ `DownloadAsync` 方法)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs` (改写:删 envRepo 注入、Install→Download 重命名、目标 = Settings.LocalNodeDirectory)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` (删 `envRepo` 实参)
- Modify: `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml` (1 处 binding 改 Download)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsDownloadTests.cs` (~7 tests)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsDefaultsTests.cs` 或扩展现有 (~4 tests for LocalNodeDirectory)

## 11. 不在范围(留给后续 hotfix)

- 「本地下载 → 一键装到 env」桥接(P1-X)
- Catalog 主页 env 选择器(已经在 `InstallDialog` 里有,本 spec 不重复)
- 已下载到本地目录的节点在 EnvList / Catalog 页面的可视化
- 跨次启动 / 跨进程「已下载」节点状态追踪(无 ScannedNode,意味着重启后看不到下载历史 —— 用户去本地文件夹看)
- 真正的「本地节点安装到 env」安装步骤(目前纯 git clone,无 pip install)

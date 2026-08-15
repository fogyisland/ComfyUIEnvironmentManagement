# v0.6.15.7 Local Node Install Requirements Panel Polish + Env Detail Node Management Design

## Context

v0.6.15.6 刚加了本地节点的 `复制到 env` 流程(已装走 info banner + 复制完自动装节点 requirements.txt)。桌面验证发现 4 个体验/功能缺口:

1. **NodeRequirementsStatus 面板红条不自动消失**: pip 失败时面板持续显示红色错误,用户希望 5s 后自动 fade,不要持续显示
2. **env 节点缺管理界面**: 用户看不到 env 里装了哪些节点、装在哪个版本、加载有没有失败;想点删除按钮
3. **缺启动失败节点检测**: 启动 ComfyUI 后,如果有 custom node 因缺依赖/语法错误加载失败,ComfyUI 在 stdout 打 "Failed to import module X" — 当前 ProcessLauncher 只把 stdout 写到日志文件,没人解析
4. **节点面板视觉太挤**: 当前 ScrollViewer MaxHeight=180 (固定小),新行追加看不到底,需要可滚动 + 自动滚到底

## Goals (4 部分合一)

| Part | 范围 | 用户原话 |
|---|---|---|
| A. NodeReq panel auto-fade | 成功 2s 后 Hide / 失败 5s 后 Hide | "当点击之后下面红色的自动淡化,不需要持续显示" |
| B. NodeReq panel layout | MaxHeight → * (撑满),ScrollBar AlwaysVisible,新行追加自动滚到底 | "节点会自动展开到 50% ... 可以应用滚动条" |
| C. Env detail node info + delete | EnvironmentDetailView 加 RepositoryUrl/LastScannedAt/InstalledTag/LoadError/Source 列 + Delete 按钮 + 失败 badge | "节点的信息依然不完整" + "节点删除按钮" |
| D. 启动失败节点检测 | ProcessLauncher.StartEnvAsync 末尾解析 stdout,扫到 "Failed to import module X" → 写 `ScannedNode.ScanMeta["load_error"]` → UI 显示红色 badge | "在环境中增加启动失败节点检测" |

## Architecture

### Part A — `NodeRequirementsStatusViewModel` 改 RunAsync
- 成功 → `IsComplete=true,HasError=false` → 内部启 `Task.Delay(2000)` 后调 `Hide()`
- 失败/取消 → `IsComplete=true,HasError=true` → 启 `Task.Delay(5000)` 后调 `Hide()`
- 已经在 Show 的状态(用户手 Hide 又触发了 RunAsync?)→ 不叠加多个 timer,字段 `_hideCts?` 单 CancellationTokenSource
- 测试: `RunAsync_Success_HidesAfter2Seconds` + `RunAsync_Failure_HidesAfter5Seconds` + `Hide_CancelsAutoFadeTimer`

### Part B — `LocalNodeListView.xaml` 装依赖面板 Border 布局改
- 当前: `MaxHeight="180"` (固定)
- 改: `MaxHeight="*"` (跟 ListBox 抢剩余垂直空间 — 用户想要"展开到 50%")+ `VerticalScrollBarVisibility="Visible"` (让用户知道可滚)
- 新行追加自动滚到底:`ScrollViewer.ScrollToEnd()` 在 `OnLogLine` 里(同步 — UI 线程调用)
- 测试: XAML 视觉,无需单测

### Part C — `EnvironmentDetailView` 加列 + Delete
- 现有列: 包名/版本/作者/状态/锁/操作(切换)
- 新增列: 仓库 URL(截断 + tooltip)/ 加载时间(相对时间 "2 分钟前")/ 版本 tag(从 ScanMeta["installed_tag"])/ 加载错误(红 badge "加载失败")/ 来源(env vs download)/ 删除按钮
- 删除按钮: 调 `NodeOperations.RemoveNodeAsync(envId, nodeId, ct)`(新方法)
- 失败 badge: 读 `node.ScanMeta["load_error"]`,非空 → 红色 "加载失败" + tooltip 显示错误

### Part D — `NodeStartupErrorDetector` + ProcessLauncher 集成
- 新 service: `NodeStartupErrorDetector.Parse(IEnumerable<string> stdoutLines) → IReadOnlyList<NodeStartupError>`
  - 正则: `Failed to import module 'X'` + `ImportError.*?X` + `ModuleNotFoundError.*?X` + `Error loading X`
  - 返回 `(PackageName, ErrorMessage)`
- ProcessLauncher.StartEnvAsync:
  - 在 ReadySignal fire + 5s grace 后(给 ComfyUI 时间 emit 完 startup import errors)→ 调 detector 解析
  - detector 拿到的每个 package 写 `ScannedNode.ScanMeta["load_error"] = errorMessage`
  - 写入通过现有 NodeRepository.Upsert(ScanMeta 是 dict — merge)
- AppLogger: 写一条 `node-startup-fail` INFO 列出失败的 packages

### `NodeOperations.RemoveNodeAsync(envId, nodeId, ct)` 新方法
```csharp
public virtual async Task<NodeOperationResult> RemoveNodeAsync(
    string envId, string nodeId, CancellationToken ct = default)
{
    _logger?.Info("node-remove", $"env='{envId}' node='{nodeId}' 开始卸载");
    var env = RequireEnv(envId);
    if (string.IsNullOrWhiteSpace(env.CustomNodesPath))
        return NodeOperationResult.Fail("env 缺 custom_nodes_path");
    var node = _nodeRepo.Get(nodeId);
    if (node is null || node.EnvId != envId)
        return NodeOperationResult.Fail("节点未注册在该 env");
    var targetDir = !string.IsNullOrWhiteSpace(node.PackagePath)
        ? node.PackagePath
        : Path.Combine(env.CustomNodesPath, nodeId);
    if (Directory.Exists(targetDir))
    {
        try { TryDelete(targetDir); }
        catch (Exception ex)
        {
            return NodeOperationResult.Fail($"删目录失败:{ex.Message}");
        }
    }
    _nodeRepo.Delete(nodeId);
    _logger?.Info("node-remove", $"env='{envId}' node='{nodeId}' 卸载成功");
    return NodeOperationResult.Ok(node.Version);
}
```

### ScannedNode LoadError storage
- 用 `ScanMeta["load_error"]` (dict — 不需 schema migration)
- UI 读 `node.ScanMeta.TryGetValue("load_error", out var err)` 判断

### EnvironmentDetailViewModel 增强
- 新增 `DeleteCommand` (parameter: ScannedNode)
- 触发 confirm dialog → 调 `_nodeOps.RemoveNodeAsync(envId, nodeId, ct)` → 成功 → 从 `Nodes` ObservableCollection 移除
- LoadErrorBadge / InstalledTag / RepositoryUrl 通过 ScannedNode property 暴露(已是 dict 字段)

## File Changes

| File | Change |
|------|--------|
| `ViewModels/NodeRequirementsStatusViewModel.cs` | RunAsync 加 auto-fade timer (2s success / 5s failure) |
| `Views/LocalNodeListView.xaml` | Border layout 改 MaxHeight + ScrollBar Visible + auto-scroll |
| `ViewModels/EnvironmentDetailViewModel.cs` | 加 DeleteCommand + RepositoryUrl/LastScannedAt/LoadErrorBadge/InstalledTag/Sorce 等 computed props |
| `Views/EnvironmentDetailView.xaml` | 加列 + Delete 按钮 + LoadError badge |
| `Services/NodeOperations.cs` | 加 `RemoveNodeAsync(envId, nodeId, ct)` |
| `Services/NodeStartupErrorDetector.cs` (new) | 解析 stdout patterns |
| `Infrastructure/ProcessLauncher.cs` | StartEnvAsync 末尾调 detector |
| `App.xaml.cs` | DI wire `NodeStartupErrorDetector` |
| `tests-wpf/.../NodeRequirementsStatusViewModelAutoFadeTests.cs` (new) | 3 测试 |
| `tests-wpf/.../NodeStartupErrorDetectorTests.cs` (new) | 5 测试 |
| `tests-wpf/.../NodeOperationsRemoveNodeTests.cs` (new) | 4 测试 |
| `tests-wpf/.../EnvironmentDetailViewModelDeleteTests.cs` (new) | 3 测试 |

## Design Decisions (user-pending questions to lock down)

1. **ScannedNode 存 LoadError 用 ScanMeta dict 还是新列?**
   - 推荐 ScanMeta dict (跟 installed_tag 一致,不需 schema migration)
   - 用户选 ScanMeta
2. **ProcessLauncher detector 触发时机?**
   - ReadySignal fire + 5s grace (给 ComfyUI 时间 emit startup import errors)
   - 太早会漏 import error;太晚让用户等 — 5s 平衡
3. **Detector 处理 stderr 还是 stdout?**
   - 两者都处理 (ComfyUI 的 ImportError 可能走 stderr 也可能走 stdout)
   - ProcessLauncher.StartEnvAsync 已有 `logProgress` 接收 stdout/stderr 两者
4. **Delete 按钮 confirm?**
   - 是 — 调现有 `ConfirmDialogOverride` test seam,标题 "确认删除节点 {nodeId}?目录会从 env 中移除"
5. **Auto-fade 5s 太长?**
   - 失败 5s (够用户看错误消息);成功 2s (跟现有 RequirementsStatus 一致)
6. **装依赖面板 MaxHeight "*" 真的会撑到 50% 吗?**
   - Grid.RowDefinition Height="*" 在 Grid 里 + 上面 header Auto + 下面 ListBox * 的话,装依赖面板会跟 ListBox 抢空间
   - 改用 GridSplitter 给 50/50 split (更可控);或简单 MaxHeight 设个固定值 (e.g. 300) — 暂定 fixed 300 简单

## Tests

### NodeRequirementsStatusViewModelAutoFadeTests
- `RunAsync_Success_HidesAfter2Seconds` — fake installer 返 Success,等 2.1s 验 IsVisible=false
- `RunAsync_Failure_HidesAfter5Seconds` — fake installer 返 Failure,等 5.1s 验 IsVisible=false
- `Hide_CancelsAutoFadeTimer` — RunAsync 启动后立刻 Hide(),验 timer 取消 (CancellationToken.ThrowIfCancellationRequested)

### NodeStartupErrorDetectorTests
- `Parse_FailedToImportLine_ExtractsPackageName`
- `Parse_ImportErrorLine_ExtractsModuleName`
- `Parse_ModuleNotFoundErrorLine_ExtractsModuleName`
- `Parse_EmptyInput_ReturnsEmptyList`
- `Parse_MultipleFailedPackages_ReturnsAllDeduplicated`

### NodeOperationsRemoveNodeTests
- `RemoveAsync_HappyPath_DeletesDirectoryAndRow`
- `RemoveAsync_RowMissing_ReturnsFail`
- `RemoveAsync_DirectoryMissing_StillRemovesRow`
- `RemoveAsync_EnvMismatch_ReturnsFail`

### EnvironmentDetailViewModelDeleteTests
- `DeleteCommand_AfterConfirm_RemovesNodeFromCollection`
- `DeleteCommand_AfterCancel_LeavesCollectionIntact`
- `DeleteCommand_FailureFromNodeOps_LeavesNodeAndShowsError`

## Verification

1. `dotnet build` — no errors
2. `dotnet test` — expect ~1195 PASS / 1 FAIL / 1 SKIP (the 1 FAIL is pre-existing flaky ProcessLauncher)
3. Rebuild staging
4. GUI smoke:
   - **Part A/B**: 本地节点页 → 选节点 → 复制到 env → 观察装依赖面板:成功 2s 后消失 / 失败 5s 后消失,ScrollBar 可见,新行自动滚到底
   - **Part C**: env-list → 进 env detail → 看列全不全 + 点删除按钮 → 确认 → 行消失
   - **Part D**: 起一个 env (故意装一个失败节点) → 看日志有没有 "Failed to import" + ScannedNode.ScanMeta["load_error"] 有值 + env detail 显示红色 "加载失败" badge

## Out of Scope

- 启动失败节点的 *自动重装* 按钮 (检测到失败后一键 pip install -r 重试)— 下轮 SDD
- 多 env 同时显示的批量错误报告
- 历史启动失败日志 / 时间线
- 节点升级 (upgrade) 按钮 — 跟 delete 是独立功能

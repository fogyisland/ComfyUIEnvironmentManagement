---
date: 2026-08-11
topic: BulkUpdate inline 改造 — 删除 dialog,新增 sidebar tab view
base_sha: 79af0f3
spec_status: DRAFT
plan_status: PENDING
---

# BulkUpdate inline 改造 — 设计

## Scope

1. 删除 `BulkUpdateDialog.xaml` + `.xaml.cs` + `BulkUpdateDialogViewModel.cs` (含 `BulkUpdateMode` enum)
2. 新增 `BulkUpdateView.xaml` + `.xaml.cs` + `BulkUpdateViewModel.cs` 作为 sidebar tab view
3. `MainWindow.xaml` sidebar entry "批量更新" 改 navigate-to-`BulkUpdateView`(不再弹 dialog)
4. `BulkUpdateOrchestrator` 不变(v0.6.11 T8 已 OK)

## 锁定决策(用户已选)

- **面板位置**: 独立 sidebar tab(view 在 main view area,不是 dialog)
- **布局**: 顶部 toolbar + env 列表 + 行状态
- **导航离开**: 后台跑,切回 tab 状态保留(同 v0.6.5.10 env-start 后台跑模式)

## 架构

### §1 新 `BulkUpdateView` 布局

view 里有**两个独立列表**:上半是「选哪些 env」(可勾选),下半是「跑起来之后每一步的状态」。
未开始时下半区是空的;开始后上半区禁用(`IsEnabled="{Binding IsBusy, Converter=InverseBool}"`)。

```
┌──────────────────────────────────────────────────────────┐
│ 批量更新  ☑ ComfyUI  ☑ ComfyUI Manager  ☐ 全选 [开始] [取消] │
├──────────────────────────────────────────────────────────┤
│ 选择环境                                                  │
│ ☑ env-1 (Python 3.11)                                    │
│ ☐ env-2 (Python 3.10)                                    │
│ ☑ env-3 (Python 3.11)                                    │
├──────────────────────────────────────────────────────────┤
│ 执行状态                                                  │
│ ✅ env-1 · ComfyUI            done      1.2s             │
│ 🔄 env-1 · ComfyUI Manager    running                    │
│ ⚠ env-3 · ComfyUI            skipped   未配置 ComfyUI 源 │
├──────────────────────────────────────────────────────────┤
│ 进度: 3/6 完成 · 2 成功 · 1 跳过 · 0 失败                 │
└──────────────────────────────────────────────────────────┘
```

### §2 `BulkUpdateViewModel`

```csharp
public class BulkUpdateViewModel : ViewModelBase
{
    private readonly BulkUpdateOrchestrator _orchestrator;
    private CancellationTokenSource _runCts = new();

    /// 选择区:env 快照,IsSelected 可勾
    public ObservableCollection<EnvRow> Envs { get; } = new();

    /// 状态区:每个 (env × targetKind) 一行,Start 时预填,进度事件就地更新
    public ObservableCollection<BulkUpdateRowVm> Rows { get; } = new();

    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ToggleSelectAllCommand { get; }

    public bool UpdateComfyUi { get; set; } = true;
    public bool UpdateComfyUiManager { get; set; } = true;

    public bool IsBusy { get; private set; }
    public BulkUpdateSummary? Summary { get; private set; }
    public string? ErrorMessage { get; private set; }

    /// 首次进 tab 时由 MainViewModel 灌一次(见 §6),之后不再调
    public void LoadEnvs(IEnumerable<EnvRow> envs) { ... }

    /// CanStart = 至少 1 个 env 勾选 && 至少 1 个 target 勾选 && !IsBusy
    private void Start() { ... }     // → Orchestrator.StartAsync(envIds, targetKinds, _runCts.Token)
    private void Cancel() { ... }
    internal void CancelRun() { ... } // MainWindow.OnClosing 调

    // OnProgress / OnCompleted / OnCancelled 事件订阅同 v0.6.11 T8
}
```

`BulkUpdateRowVm` — 把 `BulkUpdateRow`(record,model 层不动)包成带 INPC 的 VM,
因为 `ObservableCollection` 只在增删时通知,行内字段变化必须靠 INPC 才能刷 UI:

```csharp
public class BulkUpdateRowVm : ViewModelBase
{
    public string EnvId { get; }
    public string DisplayName { get; }
    public BulkUpdateTargetKind TargetKind { get; }
    public string Status { get; private set; } = "pending";
    public string? Reason { get; private set; }
    public long LatencyMs { get; private set; }
    public void UpdateFrom(BulkUpdateRow row) { ... }
}
```

### §3 行状态变化

状态机:`pending → running → done | failed | skipped`

- ☐ 待运行 → ⏳ pending → 🔄 running → ✅ done / ❌ failed / ⚠ skipped
- 失败时 row 下方加原因行(用 `v0.6.5.10` inline 折叠风格)

### §4 导航离开行为

- 后台跑:`_orchestrator.StartAsync` 跑在 background task
- UI 离开:VM 仍持有事件订阅,orchestrator 继续跑
- 切回:状态已 updated via UI 线程 marshal(同 v0.6.5.11 教训 —— orchestrator 事件从后台线程来,
  改 `ObservableCollection` 前必须回 UI 线程;用 `Progress<T>` 或 `DispatcherHelper` 二选一,
  与 `EnvironmentListViewModel.StartAsync` 保持同一套)
- 状态之所以能保留,是因为 **`MainViewModel` 缓存 VM 和 View 实例**(见 §6),不是因为 view 本身持有 state
- 主窗口关闭:`MainWindow.xaml.cs` 的 `OnClosing`(:134)调 VM 的 `CancelRun()`

### §5 删除文件

- `Views/BulkUpdateDialog.xaml`
- `Views/BulkUpdateDialog.xaml.cs`
- `ViewModels/BulkUpdateDialogViewModel.cs`(`BulkUpdateMode` enum 随之删;`EnvRow` 类搬到新 VM 文件)

### §6 导航 entry

`src-wpf/ComfyUI.Manager/MainWindow.xaml:116-117`(注意:`MainWindow.xaml` 在项目根,不在 `Views/`)
侧栏 RadioButton 绑定改名:

```xml
<RadioButton Content="批量更新" GroupName="SidebarNav"
             Command="{Binding ShowBulkUpdateCommand}" ... />
```

`MainViewModel.cs` 完全照抄 `ShowCatalog()`(:294-306)的懒缓存形状:

```csharp
// :136  OpenBulkUpdateCommand → ShowBulkUpdateCommand
// :227  ShowBulkUpdateCommand = new RelayCommand(_ => ShowBulkUpdate());

private BulkUpdateViewModel? _bulkUpdateViewModel;
private BulkUpdateView? _bulkUpdateView;

private void ShowBulkUpdate()
{
    CurrentSection = MainSection.BulkUpdate;
    if (_bulkUpdateViewModel is null)
    {
        _bulkUpdateViewModel = new BulkUpdateViewModel(_orchestrator);
        _bulkUpdateView = new BulkUpdateView { DataContext = _bulkUpdateViewModel };
        // LoadEnvs 只在首次构造时跑 —— 每次进 tab 都重灌会抹掉正在跑的行状态
        var envRepo = new EnvironmentRepository(_dbFactory);
        _bulkUpdateViewModel.LoadEnvs(
            envRepo.ListAll().Select(env => new EnvRow(env.Id, env.Name)).ToList());
    }
    CurrentView = _bulkUpdateView;
}
```

`ResolveCurrentViewName()`(:497-508)的 switch 加一条:`"BulkUpdateView" => "BulkUpdate",`
(否则 `ui-preferences.json` 的 `LastViewName` 会存成裸类型名)。

`MainSection.BulkUpdate` 枚举成员(:24)已存在,不动。

**env 列表刷新**:本轮不做自动刷新。用户新建 env 后想让它出现在批量更新列表里,
需要重启应用。这是 YAGNI 取舍 —— 加"刷新"按钮属于 carry-forward。

## Tests

### 单元测试

`tests-wpf/ComfyUI.Manager.Tests/ViewModels/BulkUpdateViewModelTests.cs`
(新建;旧的 `BulkUpdateDialogViewModelTests.cs` 改名 + 改 ctor):

- `LoadEnvs_SnapshotsEnvList`
- `ToggleSelectAll_TogglesAllEnvs`
- `CanStart_FalseWhenNoEnvSelected`
- `CanStart_FalseWhenNoTargetKindSelected`
- `StartCommand_PreFillsRows_OneRowPerEnvTargetPair`
- `OnProgress_UpdatesExistingRow_DoesNotAddNew`
- `OnCompleted_PopulatesSummary_SetsIsBusyFalse`
- `OnCancelled_SetsErrorMessage_SetsIsBusyFalse`
- `CancelCommand_TriggersOrchestratorCancel`

### STA load test

`tests-wpf/ComfyUI.Manager.Tests/Views/BulkUpdateViewLoadTests.cs`(新建):

- `BulkUpdateView_Load_DoesNotThrow`
- `BulkUpdateView_WithRunningRows_RendersStatusIcons`

### 受影响的既有测试

- `tests-wpf/.../ViewModels/BulkUpdateDialogViewModelTests.cs` — 改名为上面的新文件
- `tests-wpf/.../ViewModels/MainViewModelTests.cs` — 若断言了 `OpenBulkUpdateCommand`,改 `ShowBulkUpdateCommand`
- `tests-wpf/.../Views/MainWindowLayoutTests.cs` — 若断言侧栏按钮绑定名,同步改

### Orchestrator 测试

`tests-wpf/ComfyUI.Manager.Tests/Services/BulkUpdateOrchestratorTests.cs` — 不变(v0.6.11 T8 已有)

## 改动文件汇总

**删除**
- `src-wpf/ComfyUI.Manager/Views/BulkUpdateDialog.xaml`
- `src-wpf/ComfyUI.Manager/Views/BulkUpdateDialog.xaml.cs`
- `src-wpf/ComfyUI.Manager/ViewModels/BulkUpdateDialogViewModel.cs`

**新建**
- `src-wpf/ComfyUI.Manager/Views/BulkUpdateView.xaml` + `.xaml.cs`
- `src-wpf/ComfyUI.Manager/ViewModels/BulkUpdateViewModel.cs`(含搬过来的 `EnvRow`)
- `src-wpf/ComfyUI.Manager/ViewModels/BulkUpdateRowVm.cs`

**修改**
- `src-wpf/ComfyUI.Manager/MainWindow.xaml`(:116-117 侧栏绑定改名)
- `src-wpf/ComfyUI.Manager/MainWindow.xaml.cs`(:134 `OnClosing` 调 `CancelRun`)
- `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`(:136 / :227 / :325-337 / :497-508)

**不动**
- `src-wpf/ComfyUI.Manager/Services/BulkUpdateOrchestrator.cs`
- `src-wpf/ComfyUI.Manager/Models/BulkUpdateRow.cs`
- `src-wpf/ComfyUI.Manager/Models/BulkUpdateSummary.cs`
- `src-wpf/ComfyUI.Manager/Models/BulkUpdateTargetKind.cs`

**测试**
- `tests-wpf/.../ViewModels/BulkUpdateViewModelTests.cs` — 新建(由旧 dialog 测试改名)
- `tests-wpf/.../Views/BulkUpdateViewLoadTests.cs` — 新建

## YAGNI 划线

- 不做多 batch queue
- 不做批量更新 history
- 不做每行 stdout log 显示
- 不做暂停/恢复,只 Cancel
- 不做 dry-run

## 风险

| 风险 | 缓解 |
|---|---|
| orchestrator 事件从后台线程改 `ObservableCollection` → WPF 抛"ItemsControl 与项源不一致" | v0.6.5.11 踩过同款;进度回调统一走 `Progress<T>`(构造时捕获 UI `SynchronizationContext`),测试里用 `TestSynchronizationContext` |
| 主窗关闭时 orchestrator 还在跑 → 后台线程访问已销毁的 VM | `MainWindow.OnClosing`(:134)调 `CancelRun()` |
| 每次进 tab 都 `LoadEnvs` → 抹掉正在跑的行状态 | `LoadEnvs` 只在 `ShowBulkUpdate` 的懒构造分支里调(§6) |
| 删文件后其他代码仍引用 → 编译失败 | 实现后 `grep -rn "BulkUpdateDialog\|BulkUpdateMode" src-wpf/ tests-wpf/ --include=*.cs --include=*.xaml` 应为空 |
| 旧 `BulkUpdateDialogViewModelTests` 引用已删类 | 同一 commit 内改名 + 改 ctor |
| `ResolveCurrentViewName` 漏加分支 → `ui-preferences.json` 存裸类型名 | switch 加 `"BulkUpdateView" => "BulkUpdate"`,单元测试覆盖 |
| env 列表不刷新,新建的 env 看不到 | 已知取舍(§6),写进 carry-forward |

## 验证

```bash
# 1. 单元测试
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BulkUpdate" -v minimal   # 既有 + 新 8 PASS

# 2. STA load test
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BulkUpdateViewLoad" -v minimal   # 新 2 PASS

# 3. 死引用检查(应为空)
grep -rn "BulkUpdateDialog\|BulkUpdateMode\|OpenBulkUpdateCommand" src-wpf/ tests-wpf/ --include=*.cs --include=*.xaml

# 4. 全套
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build   # 862/1/1 + N

# 5. Build
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0

# 6. GUI smoke
# 启动 staging → 侧栏「批量更新」→ 进 view(不弹框),看到 env 列表 + 2 个 target checkbox
# 选 env + 勾 targets → 点开始 → 状态区出现行,状态变化(pending → running → done/skipped/failed)
# 跑的过程中切到「环境」tab → 切回 → 状态继续在走,不重置
# 跑的过程中切到「节点目录」tab → 切回 → 同上
# 跑完 → 底部汇总显示成功/跳过/失败计数
# 跑的过程中关闭主窗 → 进程正常退出(orchestrator 被 cancel,不挂起)
```

## Carry-forward

- env 列表加"刷新"按钮(当前进 tab 后列表固定,新建 env 需重启才出现)
- 未来 node bulk install / env bulk create 复用此 view + orchestrator pattern

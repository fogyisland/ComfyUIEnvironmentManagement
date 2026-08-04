# v0.6.5.7 Spec: Env 行 BED 状态展示 + 启动门控

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

## 0. 背景

用户 GUI smoke v0.6.5.6+hotfix 时发现:env 行只展示 Status=stopped,点"启动"按钮 30s 后弹错 "9000 在 30s 内未 Listen"。**根本原因**:env 创建后没跑 BED(基础环境部署),venv 里没装 torch,`python main.py` 启动后无法 listen 端口。

用户期望的完整 lifecycle:
1. **创建环境** → 行新增,Status=stopped
2. **基础环境部署** → 装 torch + CUDA 等 → BED 状态=完成
3. **启动** → 只有 BED 完成后才能启动;否则按钮 disabled,hover tooltip 提示

且 env 行上需要展示 BED 状态(✓/✗/⏳/❌ + profile Id),否则排错时看不到当前 BED 装的什么。

## 1. Goals

- **G1**: env 行新加"BED"列,展示 BED 状态 + profile Id / 失败原因
- **G2**: 启动按钮 BED-aware:未装 → disabled + tooltip "基础环境未安装";BED 装中 → disabled + tooltip "BED 安装中";BED 失败 → enabled + tooltip "上次 BED 失败:{reason};运行可能也失败"
- **G3**: BED 安装/失败时回写 env.BedProfileId + BedStatus + BedFailedReason
- **G4**: 老 SQLite 数据迁移 ALTER TABLE 加 3 列(nullable);老 env 默认 BedStatus=null(视为未装)
- **G5**: 用户重跑 BED(选不同 profile)→ 覆盖 BedProfileId + BedStatus
- **G6**: 用户取消 BED → 写 BedStatus="failed" + BedFailedReason="用户取消"

## 2. 非 Goals

- 不做 CPU/GPU 型号探测(nvidia-smi 等)— 只展示 BED 装出的 PyTorch + CUDA 版本(profile 已带这些信息)
- 不做实时探测 venv 是否真有 torch(import torch 跑一次)— 只信 BaseEnvInstaller 的回写
- 不做自动恢复 BED 失败 → 不实现"重试 BED"按钮(用户自己点 BED 行部署)
- 不改 BaseEnvInstaller 的主流程(只改终态回写)

## 3. 数据模型

### 3.1 Environment 增 3 字段 (`Models/Environment.cs`)

```csharp
[JsonPropertyName("bed_profile_id")]
public string? BedProfileId { get; set; }       // BED 装的 BaseEnvProfile.Id;null=未装

[JsonPropertyName("bed_status")]
public string? BedStatus { get; set; }          // "done" | "failed" | null(未装);"installing" 仅 in-memory

[JsonPropertyName("bed_failed_reason")]
public string? BedFailedReason { get; set; }    // 失败 reason;只在 BedStatus="failed" 时有值
```

### 3.2 SQLite migration (`Data/EnvironmentRepository.cs` + `Data/SqliteConnectionFactory.cs`)

启动时 ALTER TABLE 加 3 列(列已存在则跳过,跟 v0.6.5.5 `EnsureColumn` 同模式):

```sql
ALTER TABLE environments ADD COLUMN bed_profile_id TEXT;
ALTER TABLE environments ADD COLUMN bed_status TEXT;
ALTER TABLE environments ADD COLUMN bed_failed_reason TEXT;
```

老 env 行 BedProfileId/BedStatus 都是 NULL → 视为"未装",启动按钮禁用,引导用户走 BED。

### 3.3 TestDb schema (`tests-wpf/ComfyUI.Manager.Tests/Fakes/TestDb.cs`)

`InitSchema` 里 CREATE TABLE environments 增 3 列(测试夹具与生产对齐)。

## 4. 架构

### 4.1 BaseEnvInstaller 回写

**`Services/BaseEnvInstaller.cs`** — `InstallAsync` 末尾在返回前做终态回写:

```csharp
// 现有循环结束后,逐 envId 写终态
foreach (var envId in envIds)
{
    var fresh = _envRepo.Get(envId) ?? continue;
    fresh.BedProfileId = profile.Id;
    if (failures.TryGetValue(envId, out var reason))
    {
        fresh.BedStatus = "failed";
        fresh.BedFailedReason = reason;
    }
    else
    {
        fresh.BedStatus = "done";
        fresh.BedFailedReason = null;
    }
    try { _envRepo.Upsert(fresh); } catch { /* ignore */ }
}
```

**注意**: 用户取消场景(`cancelled=true`,failures 字典里有 envId)→ 同样写 "failed" + reason="用户取消"。这覆盖 G6。

### 4.2 EnvironmentListViewModel 关 dialog 后 reload

**`ViewModels/EnvironmentListViewModel.cs`** — `OpenBaseEnvProgress` 在 `BaseEnvProgressDialog.Show` 返回后调 `Load() + RaiseCommandsChanged()`:

```csharp
private void OpenBaseEnvProgress()
{
    // ... 现有检查 ...
    if (ShowProgressDialogOverride is not null)
    {
        ShowProgressDialogOverride(envIds, profile, _baseEnvInstaller);
        return;
    }
    Views.BaseEnvProgressDialog.Show(envIds, profile, _baseEnvInstaller);
    Load();
    RaiseCommandsChanged();
}
```

`ShowDialog()` 是阻塞的,返回时 BED 已完成(env 表已写入);reload 让 UI 立即反映新 BedStatus。test seam `ShowProgressDialogOverride` 路径下,测试自己负责 reload。

### 4.3 EnvironmentListViewModel CanExecute

**`ViewModels/EnvironmentListViewModel.cs`** — `StartCommand` 的 CanExecute 改为:

```csharp
StartCommand = new RelayCommand(
    async p => await StartEnvAsync(p as Environment ?? Selected),
    p =>
    {
        var env = p as Environment ?? Selected;
        if (env is null) return false;
        if (env.Status != "stopped") return false;
        // BED 未装 → 禁用
        if (env.BedStatus is null or "installing") return false;
        return true;
    });
```

且 `StartCommand` 拿到一个 `Func<Environment, string>? startTooltipOverride` 测试 seam(test 计算 tooltip 文案)。

### 4.4 启动按钮 Tooltip (`Views/EnvironmentListView.xaml`)

按钮加 `ToolTip` 属性:

```xml
<Button Content="启动" Margin="2"
        Command="{Binding DataContext.StartCommand, ...}"
        CommandParameter="{Binding}"
        ToolTip="{Binding DataContext.StartTooltip, RelativeSource={...}}" />
```

`EnvironmentListViewModel.StartTooltip : string` 计算属性(基于 Selected):
- env is null → ""
- BedStatus is null → "基础环境未安装"
- BedStatus == "installing" → "BED 安装中,请稍候"
- BedStatus == "failed" → "上次 BED 失败:{BedFailedReason};运行可能也失败"
- BedStatus == "done" → ""

按钮 `IsEnabled` 绑 StartCommand.CanExecute(WPF 自动)。

### 4.5 行 BED 列 (`Views/EnvironmentListView.xaml`)

新加 `DataGridTextColumn Header="BED" Binding="{Binding BedDisplay}" Width="220"`。

`Environment.BedDisplay` 计算属性(string):

```csharp
public string BedDisplay => BedStatus switch
{
    "done" => $"✓ {BedProfileId}",
    "failed" => $"❌ {BedProfileId ?? "?"} ({BedFailedReason ?? "失败"})",
    "installing" => "⏳ 装中",
    _ => "✗ 未装",
};
```

INPC 不需要(Environment 一次性 read,改动时通过 EnvironmentRepository.Upsert + Load 重读)。

## 5. UI 布局

```
| ID | 名称 | 端口 | 状态 | PID | BED | 操作 |
```

- BED 列宽 220px(够装 "✓ pytorch-2.5.0-cu121-stable",失败 reason 可能更长,Ellipsis 截断)
- 操作列保持 260(5 按钮)
- 整体 DataGrid width 仍自适应;最小窗口宽度微调(原本够,无需改)

## 6. Testing

### 6.1 SQLite migration test

```csharp
[Fact]
public void EnsureBedColumns_AddThreeColumnsIfMissing()
{
    // 用 in-memory SQLite,执行 v0.6.5.6 schema(没 BED 列)
    // 调 EnsureBedColumns
    // PRAGMA table_info(environments) → 应有 3 个新列
}

[Fact]
public void EnsureBedColumns_IsIdempotent()
{
    // 跑两次 EnsureBedColumns,第二次不抛
}
```

### 6.2 BaseEnvInstaller 终态回写 test

```csharp
[Fact]
public async Task InstallAsync_OnSuccess_WritesBedStatusDone()
{
    // 给 fake RunPipAsync 返 exit=0
    // 调 InstallAsync
    // 查 env.BedStatus == "done", BedProfileId == profile.Id
}

[Fact]
public async Task InstallAsync_OnFailure_WritesBedStatusFailed()
{
    // 给 fake RunPipAsync 返 exit=1
    // 调 InstallAsync
    // 查 env.BedStatus == "failed", BedFailedReason starts with "pip 退出码 1"
}

[Fact]
public async Task InstallAsync_OnUserCancel_WritesBedStatusFailedWithUserReason()
{
    // 取消 ct
    // 调 InstallAsync
    // 查 env.BedStatus == "failed", BedFailedReason == "用户取消"
}

[Fact]
public async Task InstallAsync_RerunOverwritesBedStatus()
{
    // 第一次跑 profile A → done
    // 第二次跑 profile B → done, BedProfileId == "B"
}
```

### 6.3 StartCommand CanExecute test

```csharp
[Fact]
public void StartCommand_DisabledWhenBedStatusNull()
{
    // env status=stopped, BedStatus=null → CanExecute false
}

[Fact]
public void StartCommand_EnabledWhenBedStatusDone()
{
    // env status=stopped, BedStatus=done → CanExecute true
}

[Fact]
public void StartCommand_DisabledWhenBedStatusInstalling()
{
    // in-memory env.BedStatus="installing" → CanExecute false
}

[Fact]
public void StartCommand_EnabledWhenBedStatusFailed()
{
    // env.BedStatus="failed" → CanExecute true
}

[Fact]
public void StartTooltip_ShowsBedNotInstalled_WhenBedStatusNull()
{
    // Tooltip == "基础环境未安装"
}

[Fact]
public void StartTooltip_ShowsBedFailed_WhenBedStatusFailed()
{
    // Tooltip starts with "上次 BED 失败"
}
```

### 6.4 Environment.BedDisplay test

```csharp
[Theory]
[InlineData(null, null, null, "✗ 未装")]
[InlineData("done", "pytorch-2.5.0-cu121-stable", null, "✓ pytorch-2.5.0-cu121-stable")]
[InlineData("failed", "pytorch-2.5.0-cu121-stable", "pip 退出码 1", "❌ pytorch-2.5.0-cu121-stable (pip 退出码 1)")]
[InlineData("installing", null, null, "⏳ 装中")]
public void BedDisplay_FormatsCorrectly(string? bedStatus, string? bedProfileId, string? reason, string expected)
{
    var env = new Environment { BedStatus = bedStatus, BedProfileId = bedProfileId, BedFailedReason = reason };
    Assert.Equal(expected, env.BedDisplay);
}
```

## 7. 风险 + 权衡

| 风险 | 缓解 |
|---|---|
| 老 env 没 BED 字段 → 自动视为未装 → 启动按钮 disabled | (1) 老用户首次启动看到所有行"✗ 未装",预期内(2) tooltip 明确告知去走 BED (3) BED 完成后下次启动可用 |
| BaseEnvInstaller 失败回写覆盖成功回写(同次 install 内部分 env 成功部分失败) | 现有逻辑:`failures` dict 只记录失败的 envId;成功 env 走 `BedStatus=done`,失败走 `BedStatus=failed` + reason;两路互不干扰 |
| 用户取消 BED → 失败 reason 不一致("用户取消" vs "pip 退出码 X") | BaseEnvInstaller 已有 `cancelled` 分支填 "用户取消" 到 failures 字典,新代码读 failures 字典统一处理 |
| in-memory "installing" 状态 WPF 重启后丢失 → 用户困惑 | 设计如此:重启后按"未装"处理,允许重跑;用户能看到"✗ 未装"重新走 BED,行为可预测 |
| BED 列宽 220px + 操作列 260px + 既有列 → 总宽超 DataGrid | 当前布局操作列就 260(5 按钮),再加 220 BED 列总 + 220;可用 HorizontalScrollBar 兜底,测试主屏 1366 宽度够 |
| 行多列展示顺序,可能用户想看 BED 列靠前 | spec 默认 BED 在 PID 与 操作 之间(贴近 Status,语义连续);用户 review 时调整 |

## 8. 升级注意

- **直接覆盖 v0.6.5.6 文件即可**(无版本 bump,本地 hotfix per 用户偏好)
- 老 env 表 ALTER TABLE 加 3 列(nullable),首次启动自动迁移
- 老 env(未跑过 BED 的)在 GUI 显示 "✗ 未装",启动按钮 disabled — 用户必须先走 BED
- 用户重装可走"基础环境部署"按钮(已有流程)

## 9. Verification

### 单元测试
- WPF `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal` → 期望 ~350 PASS / 1 SKIP / 0 FAIL(基线 339 + 新增 ~11)

### 端到端手动测试
1. 双击 `ComfyUI Manager.exe`(走 `release/staging/ComfyUI Manager/`)
2. 侧边栏"环境" → 看到新列 BED,老 env 行显示 "✗ 未装"
3. 启动按钮对老 env:disabled,hover tooltip "基础环境未安装"
4. 点 BED 按钮 → 选 profile → 等装完 → 行 BED 列变 "✓ pytorch-2.5.0-cu121-stable"
5. 启动按钮:enabled,tooltip 空
6. 点启动 → 进程跑起,Status 变 "running",PID 列填值
7. 失败路径:选一个会失败的 profile(如 nightly + cu121 镜像可能下载慢/失败)→ 行 BED 变 "❌ ...(pip 退出码 N)"
8. 启动按钮:enabled,tooltip "上次 BED 失败:..."

## 10. Critical files

- Modify: `src-wpf/ComfyUI.Manager/Models/Environment.cs` (+ 3 fields + BedDisplay 计算属性)
- Modify: `src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs` (+ EnsureBedColumns)
- Modify: `src-wpf/ComfyUI.Manager/Data/SqliteConnectionFactory.cs` (调 EnsureBedColumns on startup,或 ctor)
- Modify: `src-wpf/ComfyUI.Manager/Services/BaseEnvInstaller.cs` (终态回写 env.BedProfileId/BedStatus/BedFailedReason)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` (StartCommand.CanExecute + StartTooltip)
- Modify: `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml` (BED 列 + Tooltip binding)
- Modify: `src-wpf/ComfyUI.Manager/Views/BaseEnvProgressDialog.xaml.cs` (无修改,dialog 已是阻塞返回)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Fakes/TestDb.cs` (InitSchema 加 3 列)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Data/EnvironmentRepositoryBedColumnsTests.cs` (~2 tests)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvInstallerBedWriteTests.cs` (~4 tests)
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelBedTests.cs` (~5 tests)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Models/EnvironmentBedDisplayTests.cs` (1 Theory test)
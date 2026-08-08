# v0.6.7.6 Env Create Port 默认 = DB MaxPort+1 — 设计 Spec

> **Status:** approved 2026-08-08
> **base:** `dacaf24` (v0.6.7.4 SHIP-READY;**先于** v0.6.7.5 实现,也可并行)
> **Goal:** CreateEnvDialog 打开时,Port 字段默认填 `MAX(port)+1`(空 DB = 8188),避免新建 env 跟已有 env 撞端口

---

## Context

用户桌面验 v0.6.7.4 时反馈:连续建第二个 env 时手填端口,容易跟 DB 里已有 env 的端口冲突(尤其用户复制第一个 env 的配置)。

**用户原话:**
> 另外创建环境为了避免和当前的环境端口冲突,则端口会检查当前数据库的环境默认最大端口,然后填写的端口在最大端口+1

### 范围决策(脑暴产出)

| 问题 | 决定 |
|---|---|
| 顶填时机 | Dialog 打开就顶填(用户可改) |
| 来源 | 单一:`SELECT MAX(port) FROM environments`(不做 OS-level port-in-use 检查) |
| Fallback | 空 DB / 全 NULL port → `8188`(沿用 `EnvCreatorService.PortBase`) |

### 排他

- **不做** OS-level 端口占用检查(`netstat` / `IPGlobalProperties`)— 只查 DB,符合用户原话
- **不做** 端口范围校验(< 1024 警告、> 65535 错误)— 留给现有 `EnvCreatorService` 校验链
- **不做** "智能 gap filling"(找最近未占用)— 只填 Max+1,符合用户原话
- **不做** 把 port 默认值持久化到 Settings — 跟随 DB,DB 改了 form 也跟着
- **不改** `EnvCreatorService.NextFreePort`(只在 user 不填时用)— Dialog 顶填后 user 通常会填,保留 fallback

---

## Architecture

1 个新方法 + 1 个修改 + 1 个 XAML 文案修改:

- **改** `src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs` — 新增 `int? GetMaxPort()`:`SELECT MAX(port) FROM environments` → `int?`
- **改** `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs` — ctor(走 `ApplyTemplate`)调 `_repo.GetMaxPort()` → 设 `Port = (max + 1).ToString()`(max null → `"8188"`)
- **改** `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml` — Port label 文案更新

依赖:无新注入(ctor 已持 `EnvironmentRepository _repo` via `EnvCreatorService`? — 不,`_repo` 不是 ctor 注入的,**当前 ctor 只接 `EnvCreatorService _creator`**;本 SDD 需要给 ctor 加 `EnvironmentRepository _repo` 注入)。

> **设计选择:** `CreateEnvDialogViewModel` 当前不直接拿 `EnvironmentRepository`(只通过 `_creator` 间接读 `Name` 重复检查)。需要新加 ctor 参数 — 这是 `IEnvironmentRepository`(interface 已存在 per v0.6.5.8 P0-A),便于测试 mock。

---

## Data Flow

```
User:侧栏 "新建环境" → CreateEnvDialog opens
CreateEnvDialogViewModel ctor:
  ApplyTemplate()  // 现有方法,填 PythonExe + ComfyuiSource + warning
  + var max = _repo.GetMaxPort()
  + Port = (max.HasValue ? (max.Value + 1).ToString() : "8188")

User:改 Port 字段 → 走 Port setter,不动 auto-fill(只在 ctor / ApplyTemplate 时跑)
User:点 "应用模板" 按钮 → ApplyTemplate 重跑(只改 PythonExe + ComfyuiSource,不改 Port — 设计选择)
User:点 "创建" → _creator.CreateAsync(..., port: int.Parse(Port), ...)  // 现有
```

> **Port label 文案:** 从 "端口(留空自动分配,从 8188 起)" → "端口(默认 = 现有最大端口 + 1,空 DB = 8188)"

---

## File Structure

### Create

无。

### Modify

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs` | 新增 `public int? GetMaxPort()` — 走现有 `_factory.Open()`,跑 `SELECT MAX(port) FROM environments`,reader.Read() 返 int?(`reader.IsDBNull(0) ? null : reader.GetInt32(0)`) |
| `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs` | ctor 加 `IEnvironmentRepository _repo` 参数;ctor 末尾(在 `ApplyTemplate()` 之后)调 `var max = _repo.GetMaxPort(); Port = (max + 1)?.ToString() ?? "8188";` — 注意现有 ctor 已经 `ApplyTemplate()` 在尾部,新逻辑追加在 ApplyTemplate 之后(让 warning 先弹)|
| `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml` | 第 60 行 `<TextBlock Text="端口(留空自动分配,从 8188 起)" />` → `<TextBlock Text="端口(默认 = 现有最大端口 + 1,空 DB = 8188)" />` |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` (DI) | `services.AddSingleton<CreateEnvDialogViewModel>()` 工厂:CreateEnvDialogViewModel 现在接 `_repo` 参数 → DI container 注入 `IEnvironmentRepository` 实现 |

### Keep (unchanged)

- `EnvCreatorService.NextFreePort`(port null 时 fallback)
- `EnvCreatorService.CreateAsync` 校验链(port 不在范围等)— 沿用
- 所有 `EnvironmentRepository` 既有方法(`ListAll` / `Get` / `Upsert` / `Delete` 等)

---

## Constraints

| # | Constraint | Reason |
|---|---|---|
| G1 | `GetMaxPort()` 只查 `MAX(port)`,不查 `MIN` / `AVG` | 用户原话明确 = 最大端口 |
| G2 | 空 DB / 全 NULL port → `GetMaxPort()` 返回 `null` | `SELECT MAX(NULL) → NULL`(SQL 标准),不是 0 — C# 端 `int?` 区分 |
| G3 | `GetMaxPort()` 用现有 `SqliteConnectionFactory`,不新建 connection 池 | 沿用项目 DB 访问模式 |
| G4 | `Port = "8188"`(字符串)— 跟现有 `Port` 是 `string` 字段一致,不引 `int` binding | UX 简单 |
| G5 | ApplyTemplate 重跑不覆盖 user 已改的 Port | 设计选择:ApplyTemplate 只改 PythonExe + ComfyuiSource(沿用现有约定);Port 只在 ctor 初始化时顶填 |
| G6 | 不 bump version / 不发 release zip / 无 ledger 提交 | per `feedback_no_rebuild_zip.md` |
| G7 | 中文 UI 文案 | i18n 不变 |
| G8 | 不动 `EnvCreatorService.CreateAsync` | port 校验链保留 |
| G9 | `GetMaxPort` 加测试 4 个;`CreateEnvDialogViewModel` 加 3 测试 = 7 新测试 | 充分覆盖 |
| G10 | `CreateEnvDialogViewModel` 加 ctor 参数后,grep DI 工厂 + 测试 ctor,确认不破坏 | 防漏 |

---

## Open questions

无(脑暴已全清)。

---

## Tasks plan

### Task 1: `EnvironmentRepository.GetMaxPort()` + 4 tests

- `EnvironmentRepository.cs`:新增 `int? GetMaxPort()` 方法
- `tests/.../EnvironmentRepositoryMaxPortTests.cs`(4 测试)

### Task 2: `CreateEnvDialogViewModel` ctor 顶填 + XAML hint + 3 tests + close-out + 全量 suite + staging rebuild

- `CreateEnvDialogViewModel.cs`:ctor 加 `IEnvironmentRepository _repo` 参数 + ctor 末尾调 `GetMaxPort()` + 顶填 Port
- `App.xaml.cs`:DI container 注入 `IEnvironmentRepository` 到 `CreateEnvDialogViewModel` 工厂
- `Views/CreateEnvDialog.xaml`:Port label 文案更新
- `tests/.../CreateEnvDialogViewModelMaxPortTests.cs`(3 测试)
- `dotnet build` 0/0
- `dotnet test` 649 + 7 = 656 / 0 / 1
- 重建 staging per `feedback_staging_self_contained.md`
- 无 v-bump / 无 zip

---

## Risks

| 风险 | 缓解 |
|---|---|
| `SELECT MAX(port)` 在 env 行 port 全 NULL 时返 NULL(SQL 标准),不是 0 → C# `int?` nullable,form fallback `"8188"` | T1 测试明确覆盖空 DB + 全 NULL case |
| `CreateEnvDialogViewModel` 加 ctor 参数破坏既有 DI / 测试 | T2 grep DI + 既有 `CreateEnvDialogViewModelTests` 通过(默认 ctor 也要能编译 — 把 `_repo` 加成可空或加可选 default 都行;**选择** `_repo` 必填不可空,DI 全覆盖;既有测试若硬 new 要改一行)|
| 极端:MAX(port)=65535 → 顶填 65536 > ushort,user 提交 CreateAsync 失败 | 现有 `EnvCreatorService.CreateAsync` 校验链会抛 `CreateEnvException`,错误消息会显示 |
| ApplyTemplate 重跑覆盖 user 已改的 Port → 用户挫败 | ApplyTemplate 当前不改 Port(只改 PythonExe + ComfyuiSource);本 SDD 也不改这条约束 |
| 跟 v0.6.7.5 并行 / 顺序冲突:都改 ctor 注入 / DI | **顺序:** 先 ship v0.6.7.6(只改 EnvironmentRepository + CreateEnvDialogVM),后 ship v0.6.7.5(改 NodeOperations + InstallDialogVM)— 不冲突,可并行 review |
| `port` 列在 DB 里是 INTEGER nullable,有 env 行 port = 0(老数据?) → MAX(0) = 0 → 顶填 1 | 用户接受;既有 `EnvCreatorService` 仍接受 port=1(无显式范围校验)— 不引新 bug |

---

## Verification

### 单元测试

| 测试 | 验证 |
|---|---|
| `EnvironmentRepositoryMaxPortTests.GetMaxPort_EmptyDb_ReturnsNull` | 新建空 TestDb → `GetMaxPort() == null` |
| `GetMaxPort_AllPortsNull_ReturnsNull` | 插 env,port=null → `GetMaxPort() == null` |
| `GetMaxPort_Mixed_ReturnsMaxOfNonNull` | 插 env(port=8188) + env(port=null) → `GetMaxPort() == 8188` |
| `GetMaxPort_MultipleEnvs_ReturnsHighest` | 插 3 env(port=8188,8200,8189) → `GetMaxPort() == 8200` |

| 测试 | 验证 |
|---|---|
| `CreateEnvDialogViewModelMaxPortTests.Ctor_EmptyDb_PortIs8188` | TestDb 空 + new VM → `Port == "8188"` |
| `Ctor_OneEnvPort8188_PortIs8189` | TestDb 1 env port=8188 + new VM → `Port == "8189"` |
| `Ctor_MultipleEnvs_PortIsMaxPlusOne` | TestDb 3 env (8188,8200,8189) + new VM → `Port == "8201"` |

### 全量

- `dotnet build` 0 errors / 0 warnings
- `dotnet test` 656 PASS / 0 FAIL / 1 SKIP(649 + 7,SKIP = LiveFetch real GitHub)
- 既有 `CreateEnvDialogViewModelTests` 通过(若 ctor 改参数,需要更新既有 test new 行)

### 端到端桌面(用户测)

1. 启动 staging exe
2. 侧栏 "新建环境"
3. CreateEnvDialog 打开 → Port 字段自动填 "8189"(假设已有 env port=8188)
4. 改 Port 字段为 "9999" → user override 保留
5. 点 "应用模板" → PythonExe + ComfyuiSource 重填,**Port 不变**(仍是 "9999")
6. 删第一个 env → 再开新建 dialog → Port = 9999 + 1 = 10000 之类
7. 删所有 env → 再开新建 dialog → Port = "8188"

---

## Carry forward(不做)

- OS-level 端口占用检查(`netstat` / `IPGlobalProperties`)
- 端口范围校验(< 1024 警告、> 65535 错误)
- "智能 gap filling"(找最近未占用)
- port 默认值持久化到 Settings
- 改 `EnvCreatorService.NextFreePort` 行为
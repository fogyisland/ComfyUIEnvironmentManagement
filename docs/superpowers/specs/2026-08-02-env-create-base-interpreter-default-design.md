# v0.6.5.5 — 新建环境:区分基础解释器与 venv 解释器 + 默认值继承

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this spec task-by-task.

**Goal:** 让 dialog 上的"Python 解释器"字段始终表示**基础解释器**(用于建 venv 的 base),venv 解释器(venv/Scripts/python.exe)只写入 Environment 模型供后续使用;下次打开新建 dialog 时,PythonExe 默认从**最近一次成功创建 env** 的 BasePythonPath 拉,而不是 settings.TemplatePythonDir+DefaultPythonVersion。

**Architecture:**
- `Environment` 加 `BasePythonPath`(string,必填)。
- `EnvironmentListViewModel` 新增 `RecentBasePythonPath`,从最近一次创建成功的 env 取 BasePythonPath。
- `CreateEnvDialog.Show` 多接一个 `recentBasePythonPath` 参数;`CreateEnvDialogViewModel.ApplyTemplate` 优先使用 recent,缺失/不存在回退 settings。
- `EnvCreatorService.CreateAsync` 在写库时填 `env.BasePythonPath = pythonExe`,venv 解释器照旧。
- 老行兼容:`EnvironmentRepository` 读出时若 `base_python_path IS NULL`,fallback 到 `PythonExecutable`,不报错。

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · `Microsoft.Data.Sqlite` · `System.Text.Json` · xUnit temp dir pattern

---

## 1. 范围与目标

### 1.1 范围

- **覆盖**: 基础解释器与 venv 解释器角色分离;Environment.BasePythonPath 持久化;下次新建 dialog 默认值继承。
- **不覆盖**(YAGNI / 留给后续 hotfix):
  - 启动 WPF 时扫描所有 env、检测 base 缺失、顶部黄色提示 + "重建 venv" 动作。
  - 自动重建 venv 流程(只重建 venv 步骤,不重建整个 env)。
  - venv 派生运行时校验(运行 env 时检查 base 是否还在)。

### 1.2 关键术语

| 术语 | 含义 |
|---|---|
| **基础解释器(base interpreter)** | 用于建 venv 的 Python 二进制,通常是 settings.TemplatePythonDir 下的 python.exe,或用户手填的外部 python.exe。dialog 上 `PythonExe` 字段就是这个。 |
| **venv 解释器** | venv 内的 python.exe。Python 的 venv 机制:`<venv>/Scripts/python.exe` 是个 launcher/链接,运行时**必须能访问** base(拷贝或硬链接);否则 venv 跑不起来。本 spec 把这看作固有事实,不改 venv 机制。 |
| **recent base** | 最近一次成功创建的 env 的 BasePythonPath,作为下次新建 dialog 的 PythonExe 默认值。 |

### 1.3 用户故事

- A. 用户首次打开 dialog:没有任何 env → 回退 settings(行为同 v0.6.5.4)。
- B. 用户第一次创建 env A:base = settings 那条 → 创建后 Environment.BasePythonPath = settings 那条,Environment.PythonExecutable = `<env A>/venv/Scripts/python.exe`。dialog 立即关闭。
- C. 用户第二次打开 dialog(创建 env B):PythonExe 默认 = env A 的 BasePythonPath(行为变更)。
- D. 用户第二次打开 dialog 时 env A 已被删 / BasePythonPath 文件已不在 → 回退 settings(行为同 v0.6.5.4)+ 黄色提示。
- E. 用户在 dialog 内点"应用模板":PythonExe 重置回 settings 那条(行为同 v0.6.5.4),不读 recent。
- F. 用户升级 Python / 删 base → 后续运行 env 时 venv 跑不起来,UI 不告警,留给后续 hotfix。

---

## 2. 数据模型

### 2.1 `Models/Environment.cs`

新增:

```csharp
/// <summary>
/// 创建 venv 时使用的"基础解释器"路径。等于 dialog PythonExe 在创建时的值。
/// venv python.exe 是 base 的 launcher/链接,运行时依赖 base 可用。
/// </summary>
[JsonPropertyName("base_python_path")]
public string BasePythonPath { get; set; } = "";
```

保持不变:

- `PythonExecutable`(venv/Scripts/python.exe)。
- `RootPath`、`VenvPath`、`ComfyuiLayout`、`ComfyuiSource`、`CustomNodesPath`、`ExtraModelPathsYaml`、`Port`、`Status`、`EnabledNodeIdsJson`。

### 2.2 SQLite schema(`EnvironmentRepository`)

`environments` 表新增列:

```sql
ALTER TABLE environments ADD COLUMN base_python_path TEXT NOT NULL DEFAULT '';
```

实现细节:

- 老 DB 没有此列 → `ALTER TABLE` 失败 → catch `SqliteException` 旧 schema;若列不存在,fallback 行为见 §5.2。
- 不做 destructive migration;老 DB 加列后填默认值即可。

### 2.3 老行兼容(§5 错误处理)

- `BasePythonPath == ""` → `EnvironmentRepository.Read*` 自动 fallback 到 `PythonExecutable`(不报错),同时在返回前 `BasePythonPath = PythonExecutable`,让后续 dialog 默认值继承仍然有合理值。

---

## 3. UI / dialog 行为(`CreateEnvDialogViewModel`)

### 3.1 字段语义不变

- `PythonExe` 仍是"基础解释器"(v0.6.5.4 行为不变)。
- `ApplyTemplate` 改逻辑(见 §3.2)。
- "应用模板"按钮行为不变(§3.3)。

### 3.2 `ApplyTemplate()` 优先级

伪代码:

```csharp
public void ApplyTemplate()
{
    var warnings = new List<string>();

    // 优先级 1:recent base 文件存在
    if (!string.IsNullOrEmpty(_recentBasePythonPath) && File.Exists(_recentBasePythonPath))
    {
        PythonExe = _recentBasePythonPath;
    }
    else
    {
        // 优先级 2:settings(同 v0.6.5.4)
        var pythonExe = Path.Combine(
            _projectRoot, _settings.TemplatePythonDir, _settings.DefaultPythonVersion, "python.exe");

        if (File.Exists(pythonExe))
        {
            PythonExe = pythonExe;
        }
        else
        {
            warnings.Add($"Python 模板 {_settings.DefaultPythonVersion} 未安装,请先在设置页下载");
            PythonExe = "";
        }
    }

    // ComfyUI 路径不受影响(同 v0.6.5.4):只从 settings 拉
    var comfyuiSource = Path.Combine(_projectRoot, _settings.TemplateComfyuiDir);
    if (Directory.Exists(comfyuiSource))
        ComfyuiSource = comfyuiSource;
    else
    {
        warnings.Add("ComfyUI 模板目录未安装,请先在设置页下载");
        ComfyuiSource = "";
    }

    TemplateWarningMessage = warnings.Count == 0 ? null : string.Join("\n", warnings);
}
```

### 3.3 "应用模板"按钮

- 不读 `_recentBasePythonPath`。
- 行为等同 §3.2 的"优先级 2 分支"(settings 路径),无论 recent 是否存在,点应用模板后 PythonExe 重置回 settings 那条。

### 3.4 创建后 dialog 关闭

- `Closed?.Invoke(env)` 立即关闭,同 v0.6.5.4。
- VM 内部不保留"venv 解释器"在 PythonExe 字段上;Environment 模型持有 venv 解释器。
- 用户后续在 EnvListView 选中该 env,看到的是 Environment.PythonExecutable(v0.6.5.4 行为不变)。

---

## 4. EnvCreatorService 行为

### 4.1 `CreateAsync` 签名不变

```csharp
public async Task<Environment> CreateAsync(
    string name,
    string layout,
    string pythonExe,        // = dialog PythonExe = 基础解释器
    string? comfyuiSource,
    int? port,
    CancellationToken ct = default)
```

### 4.2 写库改动

`Environment` 构造时多设一行:

```csharp
var env = new Environment
{
    Id = envId,
    Name = name,
    RootPath = rootPath,
    ComfyuiLayout = layout,
    ComfyuiSource = comfyuiResolved,
    BasePythonPath = pythonExe,                                    // ← 新增
    VenvPath = venvPath,
    PythonExecutable = Path.Combine(venvPath, "Scripts", "python.exe"),
    CustomNodesPath = Path.Combine(rootPath, "custom_nodes"),
    ExtraModelPathsYaml = extraYaml,
    Port = allocatedPort,
    Status = "stopped",
    EnabledNodeIdsJson = "[]",
};
```

### 4.3 错误处理不变

- `VENV_PYTHON_MISSING`(基础解释器不存在)同 v0.6.5.4。
- `VENV_CREATE_FAILED` 同 v0.6.5.4(失败回滚 env 根目录)。

---

## 5. EnvListViewModel + EnvListView 行为

### 5.1 `EnvironmentListViewModel` 新增属性

```csharp
public string? RecentBasePythonPath { get; private set; }
```

### 5.2 `RecentBasePythonPath` 更新时机

- 监听 `List` 集合变化或 `LoadAsync` 完成时。
- 计算逻辑:取 `List` 中 `RootPath` 最新修改时间(或 `Id` 字典序,作为无 mtime 信息的 fallback)对应 env 的 `BasePythonPath`。
- 若列表为空 → `RecentBasePythonPath = null`。

### 5.3 `CreateEnvDialog.Show` 签名

```csharp
public static void Show(
    EnvCreatorService creator,
    Models.Settings settings,
    string projectRoot,
    string? recentBasePythonPath)   // ← 新增第 4 参
```

`CreateEnvDialogViewModel` ctor:

```csharp
public CreateEnvDialogViewModel(
    EnvCreatorService creator,
    Settings settings,
    string projectRoot,
    string? recentBasePythonPath,    // ← 新增
    Action<Models.Environment?>? onResult = null)
{
    _creator = creator;
    _settings = settings;
    _projectRoot = projectRoot;
    _recentBasePythonPath = recentBasePythonPath;
    _onResult = onResult;
    // ... ApplyTemplate()
}
```

### 5.4 现有测试 call sites 兼容

`EnvironmentListViewModel.cs:CreateEnv()` 调用处更新:

```csharp
Views.CreateEnvDialog.Show(_envCreator, _settings, _projectRoot, _recentBasePythonPath);
```

7 处测试 fixture `new EnvironmentListViewModel(...)` 多加一个 trailing `null!,`(同 v0.6.5.4 T5 风格)。

### 5.5 老行兼容(`EnvironmentRepository`)

读取路径(任何 `Read*` / `ListAll` / `Upsert` 后回读):

```csharp
if (string.IsNullOrEmpty(env.BasePythonPath))
{
    env.BasePythonPath = env.PythonExecutable;   // fallback, 永远不空
}
```

写入路径:永远写 `env.BasePythonPath`(即使是 fallback 后的值)。

### 5.6 venv 是 base 的派生(写进 spec 的事实)

> Python venv 的 `python.exe` 是个 launcher/链接,运行时**必须能访问 base**(拷贝或硬链接);base 被删/移动,venv 跑不起来。本 spec 不监控、不告警、不自动重建——只是把 BasePythonPath 持久化,作为下次 dialog 默认值,以及后续 hotfix 重建 venv 的依据。

---

## 6. 错误处理

| 场景 | 行为 |
|---|---|
| `recentBasePythonPath` 文件不存在 | fallback settings(同 v0.6.5.4 行为),顶部黄色提示。 |
| 老 DB 无 `base_python_path` 列 | `ALTER TABLE` 失败 → 视为老库,读时 `BasePythonPath = PythonExecutable`(同 §5.5)。 |
| 老行 `BasePythonPath IS NULL/""` | `EnvironmentRepository.Read*` 自动 fallback,赋值为 `PythonExecutable`。 |
| `EnvCreatorService.CreateAsync` 失败 | `CreateEnvException` 码不变(`VENV_PYTHON_MISSING` 等)。 |
| 用户升级 Python / 删 base | venv 跑不起来,**不告警**;留给后续 hotfix。 |
| `recentBasePythonPath` 文件存在但已被占(权限问题) | `File.Exists` 返回 true → PythonExe 被填;创建时 `VENV_PYTHON_MISSING` 抛错(同 v0.6.5.4)。 |

---

## 7. 测试

### 7.1 `EnvCreatorServiceTests`(新增)

- `CreateAsync_WritesBasePythonPath`:断言返回 env.BasePythonPath == 传入 pythonExe 参数值。

### 7.2 `EnvironmentRepositoryTests`(新增)

- `BasePythonPath_RoundTrips`:创建 env → Upsert → ListAll 读出 → 断言 BasePythonPath 一致。
- `BasePythonPath_FallsBackToVenvPython_WhenColumnEmpty`:mock 读出行的 BasePythonPath 为空,断言 repository 读出后自动填 `PythonExecutable`(同 §5.5)。

### 7.3 `CreateEnvDialogViewModelTests`(新增)

- `Constructor_PrefersRecentBase_WhenFileExists`:传入 recentBase 文件,断言 PythonExe = recentBase,无 template warning。
- `Constructor_FallsBackToSettings_WhenRecentBasePathIsNull`:传入 null,断言走 settings(同 v0.6.5.4 测试已覆盖,本测试沿用基线)。
- `Constructor_FallsBackToSettings_WhenRecentBaseFileMissing`:recentBase 路径不存在,断言走 settings,顶部黄色提示包含"Python 模板 X.Y 未安装"。
- `Constructor_ApplyTemplateOverridesRecentBase`:点"应用模板" → PythonExe 重置回 settings(无视 recent)。
- `ApplyTemplate_FallsBackWhenRecentBaseMissing`:测试 setup 临时删除 recentBase 路径(同 §7.3 第 3 个)。

### 7.4 `EnvironmentListViewModelTests`(新增)

- `RecentBasePythonPath_NullWhenListEmpty`:初始 LoadAsync 后,List 为空,RecentBasePythonPath == null。
- `RecentBasePythonPath_LastCreatedEnvBasePython`:List 里 2 个 env,断言 RecentBasePythonPath 等于"最近"的 BasePythonPath。
- `CreateEnv_PassesRecentBasePythonPath_ToDialog`:mock EnvCreatorService.CreateAsync 捕获传入参数,断言 `Views.CreateEnvDialog.Show(...)` 第 4 参 = vm.RecentBasePythonPath。

### 7.5 venv 派生语义(测试不直接覆盖)

- venv 跑起来依赖 base 是 Python 语言的固有事实,不写 WPF 单元测试。
- 如果未来 hotfix 要加 base 监控/重建,会单独写 spec。

---

## 8. 决策记录

| # | 决策 | 理由 |
|---|---|---|
| 1 | Environment.BasePythonPath 必填(写入非空) | dialog 默认值继承需要稳定来源;空值无意义。 |
| 2 | 老行 BasePythonPath fallback PythonExecutable | 不破坏老 DB;让 dialog 默认值继承保持有效;只是语义上"最近 base"等于 venv 解释器,符合最小变更。 |
| 3 | 不监控 base 是否被删 | YAGNI;留给 hotfix;UI 现在加告警/重建动作复杂度高,用户已确认手动重建。 |
| 4 | dialog "应用模板"按钮无视 recent | 用户明确想保留"重置回 settings"语义;不引入新交互。 |
| 5 | ComfyuiSource 仍只从 settings 拉 | spec 范围限定 base/venv 解释器;ComfyuiSource 不在 v0.6.5.5 范围内。 |
| 6 | recent base 文件存在性用 `File.Exists` | 与 v0.6.5.4 settings 那条路径判断一致;无新增依赖。 |

---

## 9. 验证

### 单元测试

- WPF: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal` → 期望 285 + 5(新) = 290 PASS / 1 SKIP / 0 FAIL。
  - 增量:`EnvCreatorServiceTests` 1 + `EnvironmentRepositoryTests` 2 + `CreateEnvDialogViewModelTests` 4 + `EnvironmentListViewModelTests` 3 = 10 新测试(基线 v0.6.5.4 = 285)。
- Python: `PYTHONPATH=src python -m pytest tests/test_version_consistency.py -q` → 3 PASS(版本 bump 后跑)。

### 端到端手动测试(用户 desktop)

1. 启动 WPF,首次打开新建 dialog,PythonExe = settings 那条(行为同 v0.6.5.4)。
2. 创建 env A(base = settings 那条)→ dialog 关闭。
3. 重启 WPF,打开新建 dialog → PythonExe 默认 = env A 的 BasePythonPath。
4. 在 dialog 内点"应用模板" → PythonExe 重置回 settings 那条。
5. 删除 env A 目录(包含 venv)→ 重启 WPF,打开新建 dialog → PythonExe 回退 settings(无 env 时)。
6. 删除 `<projectRoot>/python/3.10/python.exe`(base 模板)→ 重启 WPF,打开新建 dialog → 顶部黄色提示"Python 模板 3.10 未安装"(settings 回退 + 警告)。

### Risks + Tradeoffs

| 风险 | 缓解 |
|---|---|
| 用户删 base 后 venv 跑不起来 | 不监控、不告警;用户手动重建;留作 hotfix。 |
| 老 DB `base_python_path` 列不存在 | `ALTER TABLE` 失败 → 走 fallback 分支读;不破坏现有数据。 |
| 用户在 dialog 内点"应用模板"后忘记最近 base | 顶部不显示 recent base 来源;后续 hotfix 可加"基于最近 env"按钮。 |
| 多个 env 有不同 base | `RecentBasePythonPath` 只取一个(最新);用户创建下一个 env 时如想用老 base,需手填。 |
| venv 派生语义写进 spec 但不测试 | Python 语言的固有事实,写说明足以;测试在 hotfix 时再写。 |

---

## 10. Critical files to modify

- `src-wpf/ComfyUI.Manager/Models/Environment.cs`(加 `BasePythonPath`)
- `src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs`(schema 加列 + 老行 fallback + 写入新列)
- `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs`(写库设 `BasePythonPath`)
- `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs`(`ApplyTemplate` 优先级 + `recentBasePythonPath` 参数)
- `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml.cs`(`Show` 多一参)
- `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`(`RecentBasePythonPath` 属性 + `CreateEnv()` 多传一参)
- `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceTests.cs`(新增 1 个)
- `tests-wpf/ComfyUI.Manager.Tests/Data/EnvironmentRepositoryTests.cs`(新增 2 个)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelTests.cs`(新增 4 个)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs`(新增 3 个;7 处 ctor call sites 加 trailing `null!,`)
- 5 处版本字面量 + `release/RELEASE-NOTES-v0.6.5.5.md`(T-N close-out)

---

## 11. Execution choice

**Recommended: Subagent-Driven Development**
- 估计 7-8 task(模型/POCO + repository schema + EnvCreatorService 写库 + EnvListVM Recent + dialog VM ApplyTemplate + dialog Show 串 + EnvListVM.CreateEnv 串 + EnvCreatorServiceTests + repository tests + VM tests + close-out),加 close-out。
- Per-task review gate(Sonnet implementer + Haiku reviewer / close-out)。
- 模型选择:核心 task(repository schema + 老行 fallback + EnvListVM RecentBase + dialog VM ApplyTemplate 优先级)→ Sonnet;机械 task(模型 POCO + ctor 多参 + trailing null)→ Haiku;close-out → Haiku。
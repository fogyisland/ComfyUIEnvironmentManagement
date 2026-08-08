# v0.6.7.6 Env Create Port 默认 = DB MaxPort+1 — 实施 Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** CreateEnvDialog 打开时,Port 字段默认填 `MAX(port)+1`(空 DB = 8188),避免新建 env 跟已有 env 撞端口

**Architecture:**
- `IEnvironmentRepository` 加 `int? GetMaxPort()` → `EnvironmentRepository` 实现 `SELECT MAX(port) FROM environments`
- `CreateEnvDialogViewModel` ctor 加 `IEnvironmentRepository _repo` 参数 → ApplyTemplate 之后调 `GetMaxPort()` 设 Port 字段
- `CreateEnvDialog.xaml` Port label 文案更新
- `CreateEnvDialog.Show(...)` + `EnvironmentListViewModel.CreateEnv()` 透传 envRepo

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · `Microsoft.Data.Sqlite` · hand-rolled MVVM

**base SHA:** `973c826`(v0.6.7.6 spec commit,**先于** v0.6.7.5 实现)

**Spec:** `docs/superpowers/specs/2026-08-08-env-create-maxport-design.md`

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| G1 | `GetMaxPort()` 只查 `MAX(port)`,不查 `MIN` / `AVG` | spec §Context |
| G2 | 空 DB / 全 NULL port → `GetMaxPort()` 返回 `null`(SQL `MAX(NULL)→NULL`)| spec §Risks |
| G3 | `GetMaxPort()` 用现有 `SqliteConnectionFactory`,不新建 connection 池 | spec G3 |
| G4 | `Port = "8188"`(字符串)— 跟现有 `Port` 是 `string` 字段一致 | spec G4 |
| G5 | ApplyTemplate 重跑不覆盖 user 已改的 Port(只在 ctor 顶填) | spec G5 |
| G6 | 不 bump version / 不发 release zip / 无 ledger 提交 | `feedback_no_rebuild_zip.md` |
| G7 | 中文 UI 文案,i18n 不变 | `feedback_workflow.md` |
| G8 | 不动 `EnvCreatorService.CreateAsync` 端口校验链 | spec G8 |
| G9 | 7 新测试(4 repo + 3 VM)| spec G9 |
| G10 | `CreateEnvDialogViewModel` 加 ctor 参数后 grep DI 工厂 + 测试 ctor | spec G10 |

---

## File Structure

### Create

无(纯修改 spec + repo method + VM 注入 + XAML 文案 + tests)。

### Modify

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/Data/IEnvironmentRepository.cs` | 加 `int? GetMaxPort();` |
| `src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs` | 实现 `int? GetMaxPort()` 方法 |
| `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs` | ctor 加 `IEnvironmentRepository _repo` 参数 + ctor 末尾(在 `ApplyTemplate()` 之后)调 `GetMaxPort()` + 设 Port |
| `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml` | Port label 第 60 行 文案更新 |
| `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml.cs` | `Show(...)` 静态方法加 `IEnvironmentRepository envRepo` 参数 + 传给 ctor |
| `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` | `CreateEnv()` 第 380 行透传 `_repo` 到 `CreateEnvDialog.Show(...)` |
| `tests-wpf/ComfyUI.Manager.Tests/Data/EnvironmentRepositoryMaxPortTests.cs` | 新建,4 测试 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelMaxPortTests.cs` | 新建,3 测试 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelTests.cs` | 修改,既有 8 处 `new CreateEnvDialogViewModel(...)` 加 `_repo` 实参(可传 `null!`,既有测试不验 port 行为) |

### Delete

无。

---

## Tasks

### Task 1: `EnvironmentRepository.GetMaxPort()` + 4 tests

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Data/IEnvironmentRepository.cs`(加 1 行方法签名)
- Modify: `src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs`(加 12 行方法实现)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Data/EnvironmentRepositoryMaxPortTests.cs`(4 测试,~60 行)

**Interfaces:**
- Consumes: `IEnvironmentRepository`(已存在,3 方法),`SqliteConnectionFactory`(已存在),`TestDb`(已存在)
- Produces: `IEnvironmentRepository.GetMaxPort()` + `EnvironmentRepository.GetMaxPort()` impl — 返回 `int?`(空 DB / 全 NULL 行 → null)

#### Step 1: Write failing tests

Create `tests-wpf/ComfyUI.Manager.Tests/Data/EnvironmentRepositoryMaxPortTests.cs`:

```csharp
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using Environment = ComfyUI.Manager.Models.Environment;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public class EnvironmentRepositoryMaxPortTests
{
    [Fact]
    public void GetMaxPort_EmptyDb_ReturnsNull()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);

        var max = repo.GetMaxPort();

        Assert.Null(max);
    }

    [Fact]
    public void GetMaxPort_AllPortsNull_ReturnsNull()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(new Environment
        {
            Id = "env-1", Name = "first", RootPath = "/tmp/first",
            ComfyuiLayout = "shared", BasePythonPath = "/usr/bin/python",
            PythonVersion = "3.10", Port = null,
        });

        var max = repo.GetMaxPort();

        Assert.Null(max);
    }

    [Fact]
    public void GetMaxPort_Mixed_ReturnsMaxOfNonNull()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(new Environment
        {
            Id = "env-1", Name = "first", RootPath = "/tmp/first",
            ComfyuiLayout = "shared", BasePythonPath = "/usr/bin/python",
            PythonVersion = "3.10", Port = 8188,
        });
        repo.Upsert(new Environment
        {
            Id = "env-2", Name = "second", RootPath = "/tmp/second",
            ComfyuiLayout = "shared", BasePythonPath = "/usr/bin/python",
            PythonVersion = "3.10", Port = null,
        });

        var max = repo.GetMaxPort();

        Assert.Equal(8188, max);
    }

    [Fact]
    public void GetMaxPort_MultipleEnvs_ReturnsHighest()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(MakeEnv("env-1", "first", 8188));
        repo.Upsert(MakeEnv("env-2", "second", 8200));
        repo.Upsert(MakeEnv("env-3", "third", 8189));

        var max = repo.GetMaxPort();

        Assert.Equal(8200, max);
    }

    private static Environment MakeEnv(string id, string name, int? port) => new()
    {
        Id = id, Name = name, RootPath = $"/tmp/{name}",
        ComfyuiLayout = "shared", BasePythonPath = "/usr/bin/python",
        PythonVersion = "3.10", Port = port,
    };
}
```

#### Step 2: Run tests, verify 4/4 FAIL

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~EnvironmentRepositoryMaxPortTests"`
Expected: FAIL with "EnvironmentRepository does not contain a definition for 'GetMaxPort'"

#### Step 3: Add interface method

Modify `src-wpf/ComfyUI.Manager/Data/IEnvironmentRepository.cs`,add this line after `void Upsert(Environment env);`:

```csharp
    /// <summary>
    /// SELECT MAX(port) FROM environments。空 DB / 全 NULL port 返回 null。
    /// </summary>
    int? GetMaxPort();
```

#### Step 4: Implement on concrete class

Modify `src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs`,add this method after `Delete(string envId)`(line 107):

```csharp
    public int? GetMaxPort()
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(port) FROM environments";
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }
```

#### Step 5: Run tests, verify 4/4 PASS

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~EnvironmentRepositoryMaxPortTests"`
Expected: PASS — 4 tests, 0 failures.

#### Step 6: Commit

```bash
git add src-wpf/ComfyUI.Manager/Data/IEnvironmentRepository.cs \
        src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs \
        tests-wpf/ComfyUI.Manager.Tests/Data/EnvironmentRepositoryMaxPortTests.cs
git commit -m "feat(wpf): EnvironmentRepository.GetMaxPort (v0.6.7.6 T1)"
```

---

### Task 2: `CreateEnvDialogViewModel` ctor 顶填 Port + XAML hint + 3 tests + 既有测试适配 + 全量 verify + 重建 staging

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs`(ctor 加 `IEnvironmentRepository _repo` 参数 + 顶填 Port)
- Modify: `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml`(Port label 第 60 行文案)
- Modify: `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml.cs`(`Show(...)` 静态方法加 `IEnvironmentRepository envRepo` 参数)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`(`CreateEnv()` 第 380 行透传 `_repo`)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelTests.cs`(8 既有测试 ctor 实参加 `null!`)
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelMaxPortTests.cs`(3 测试,~60 行)

**Interfaces:**
- Consumes: `IEnvironmentRepository.GetMaxPort()`(T1 已加);`EnvironmentRepository` concrete(已有);`TestDb`(已有)
- Produces: `CreateEnvDialogViewModel.Port` 默认 = `MAX(port)+1`(空 DB → "8188")

#### Step 1: Write failing tests for VM

Create `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelMaxPortTests.cs`:

```csharp
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Environment = ComfyUI.Manager.Models.Environment;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class CreateEnvDialogViewModelMaxPortTests
{
    [Fact]
    public void Ctor_EmptyDb_PortIs8188()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);

        var vm = new CreateEnvDialogViewModel(
            creator: null!,
            settings: MakeSettings(),
            projectRoot: Path.GetTempPath(),
            recentBasePythonPath: null,
            onResult: null,
            envRepo: repo);

        Assert.Equal("8188", vm.Port);
    }

    [Fact]
    public void Ctor_OneEnvPort8188_PortIs8189()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(new Environment
        {
            Id = "env-1", Name = "first", RootPath = "/tmp/first",
            ComfyuiLayout = "shared", BasePythonPath = "/usr/bin/python",
            PythonVersion = "3.10", Port = 8188,
        });

        var vm = new CreateEnvDialogViewModel(
            creator: null!,
            settings: MakeSettings(),
            projectRoot: Path.GetTempPath(),
            recentBasePythonPath: null,
            onResult: null,
            envRepo: repo);

        Assert.Equal("8189", vm.Port);
    }

    [Fact]
    public void Ctor_MultipleEnvs_PortIsMaxPlusOne()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(MakeEnv("env-1", "first", 8188));
        repo.Upsert(MakeEnv("env-2", "second", 8200));
        repo.Upsert(MakeEnv("env-3", "third", 8189));

        var vm = new CreateEnvDialogViewModel(
            creator: null!,
            settings: MakeSettings(),
            projectRoot: Path.GetTempPath(),
            recentBasePythonPath: null,
            onResult: null,
            envRepo: repo);

        Assert.Equal("8201", vm.Port);
    }

    private static Environment MakeEnv(string id, string name, int? port) => new()
    {
        Id = id, Name = name, RootPath = $"/tmp/{name}",
        ComfyuiLayout = "shared", BasePythonPath = "/usr/bin/python",
        PythonVersion = "3.10", Port = port,
    };

    private static Settings MakeSettings() => new() { ActivePythonInterpreterName = "" };
}
```

#### Step 2: Run tests, verify 3/3 FAIL

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~CreateEnvDialogViewModelMaxPortTests"`
Expected: FAIL with "CreateEnvDialogViewModel does not contain a constructor that takes 6 arguments"

#### Step 3: Update `CreateEnvDialogViewModel` ctor

Modify `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs`:

(a) Add `using ComfyUI.Manager.Data;` at top(若未导入)。

(b) Add `private readonly IEnvironmentRepository _envRepo;` field after `_recentBasePythonPath`(line 18)。

(c) Update ctor signature(line 21-26):

```csharp
    public CreateEnvDialogViewModel(
        EnvCreatorService creator,
        Settings settings,
        string projectRoot,
        string? recentBasePythonPath = null,
        Action<Models.Environment?>? onResult = null,
        IEnvironmentRepository? envRepo = null)
    {
        _creator = creator;
        _settings = settings;
        _projectRoot = projectRoot;
        _recentBasePythonPath = recentBasePythonPath;
        _onResult = onResult;
        _envRepo = envRepo;
        CreateCommand = new RelayCommand(
            async _ => await CreateAsync(),
            _ => CanCreate());
        CancelCommand = new RelayCommand(_ => Closed?.Invoke(null));
        ApplyTemplateCommand = new RelayCommand(_ =>
        {
            _recentBasePythonPath = null;
            ApplyTemplate();
        });
        ApplyTemplate();   // 初次填充
        // v0.6.7.6: Port 默认填 MAX(port)+1,空 DB / 无 envRepo 时回落 8188
        if (_envRepo is not null)
        {
            try
            {
                var max = _envRepo.GetMaxPort();
                Port = ((max + 1) ?? 8188).ToString();
            }
            catch
            {
                Port = "8188";
            }
        }
        else
        {
            Port = "8188";
        }
    }
```

> **设计选择:`IEnvironmentRepository? envRepo = null` 可选** — 让既有 8 个测试不需要 `TestDb` 包装,直接传 `null!` 即可(spec G5 兜底)。production 路径走 `EnvironmentListViewModel.CreateEnv()` 透传 `_repo`(非 null)。

#### Step 4: Run new tests, verify 3/3 PASS

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~CreateEnvDialogViewModelMaxPortTests"`
Expected: PASS — 3 tests, 0 failures.

#### Step 5: Update existing `CreateEnvDialogViewModelTests` ctor calls

Modify `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelTests.cs`:

在每个 `new CreateEnvDialogViewModel(creator!, settings, projectRoot, recentBasePythonPath, onResult)` 后追加 `, envRepo: null`(命名参数,清晰)。共 8 处(`MakeVm` 内部 1 处 + 直接构造 7 处)。

> 简化 pattern:把所有 8 处改成 `new CreateEnvDialogViewModel(creator!, settings, projectRoot, recentBasePythonPath, onResult, envRepo: null)`。

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --filter "FullyQualifiedName~CreateEnvDialogViewModelTests"`
Expected: PASS — 既有 8 测试 0 改动逻辑通过(只 ctor 加默认参数)。

#### Step 6: Update `CreateEnvDialog.xaml.cs` Show signature

Modify `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml.cs`:

(a) `using ComfyUI.Manager.Data;`(若未导入)。

(b) Update `Show` signature(line 24-30):

```csharp
    public static Models.Environment? Show(
        EnvCreatorService creator,
        Models.Settings settings,
        string projectRoot,
        string? recentBasePythonPath,
        IEnvironmentRepository envRepo)
    {
        var vm = new CreateEnvDialogViewModel(creator, settings, projectRoot, recentBasePythonPath, envRepo: envRepo);
        var dlg = new CreateEnvDialog(vm) { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();
        return dlg.Result;
    }
```

> **选择:`IEnvironmentRepository`(接口)— production 传 `EnvironmentRepository` 实例,测试可注入 mock。**

#### Step 7: Update `EnvironmentListViewModel.CreateEnv()` caller

Modify `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` line 380:

```csharp
        var created = Views.CreateEnvDialog.Show(_envCreator, _settings, _projectRoot, RecentBasePythonPath, _repo);
```

> `_repo` 已是 `EnvironmentRepository` concrete,继承 `IEnvironmentRepository` 接口(spec 兼容)。

#### Step 8: Update XAML hint text

Modify `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml` line 60:

OLD:
```xaml
                <TextBlock Text="端口(留空自动分配,从 8188 起)" />
```

NEW:
```xaml
                <TextBlock Text="端口(默认 = 现有最大端口 + 1,空 DB = 8188)" />
```

#### Step 9: Build + full suite

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Expected: 0 errors / 0 warnings

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal`
Expected: 656 PASS / 0 FAIL / 1 SKIP(649 + T1 4 + T2 3,SKIP = LiveFetch real GitHub)

#### Step 10: 重建 staging

```bash
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
    -c Release -r win-x64 --self-contained true \
    -o "release/staging/ComfyUI Manager" -v minimal
```

Verify: `git status --short` shows working tree clean(staging exe 时间戳 gitignored)。

#### Step 11: Commit

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs \
        src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs \
        src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml \
        src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelMaxPortTests.cs
git commit -m "feat(wpf): env-create dialog port 默认 = DB MaxPort+1 (v0.6.7.6)"
```

---

## Verification

### 全量测试

| 测试 | 验证 |
|---|---|
| `EnvironmentRepositoryMaxPortTests.GetMaxPort_EmptyDb_ReturnsNull` | 空 TestDb → GetMaxPort() == null |
| `GetMaxPort_AllPortsNull_ReturnsNull` | 1 行 port=null → GetMaxPort() == null |
| `GetMaxPort_Mixed_ReturnsMaxOfNonNull` | 1 行 port=8188 + 1 行 port=null → GetMaxPort() == 8188 |
| `GetMaxPort_MultipleEnvs_ReturnsHighest` | 3 行 (8188,8200,8189) → GetMaxPort() == 8200 |
| `CreateEnvDialogViewModelMaxPortTests.Ctor_EmptyDb_PortIs8188` | 空 DB → Port == "8188" |
| `Ctor_OneEnvPort8188_PortIs8189` | 1 行 port=8188 → Port == "8189" |
| `Ctor_MultipleEnvs_PortIsMaxPlusOne` | 3 行 (8188,8200,8189) → Port == "8201" |
| `CreateEnvDialogViewModelTests` 既有 8 测试 | ctor 改成 6 参(envRepo=null),行为不变 |

### 端到端桌面(用户测)

1. 启动 staging exe
2. 侧栏"新建环境"→ CreateEnvDialog 打开
3. Port 字段自动填 "8189"(假设已有 env port=8188)
4. 改 Port 为 "9999"→ user override 保留
5. 点 "应用模板" → PythonExe + ComfyuiSource 重填,**Port 不变**(仍是 "9999")
6. 删第一个 env → 再开新建 dialog → Port = 9999 + 1 = "10000"
7. 删所有 env → 再开新建 dialog → Port = "8188"

---

## Risks

| 风险 | 缓解 |
|---|---|
| `MAX(NULL) → NULL`(SQL 标准)| 已有 G2 + T1 测试覆盖 |
| `CreateEnvDialogViewModel` ctor 加可选参数后既有测试不破坏 | T2 Step 5 明确改 8 处,Step 4 新测试先 PASS 验证 ctor 兼容 |
| `CreateEnvDialog.Show` 加必传 `envRepo` 后 caller 漏改 → 编译错 | T2 Step 6 + Step 7 同时改 ctor + caller,build 验证 |
| ApplyTemplate 重跑覆盖 user Port → 用户挫败 | ApplyTemplate 当前不改 Port(只改 PythonExe + ComfyuiSource);本 plan 也不改 |
| MAX(port)=65535 → 顶填 65536 触发 CreateAsync 校验失败 | 沿用 `EnvCreatorService` 错误消息链,无新 bug |

---

## Critical files to modify

- `src-wpf/ComfyUI.Manager/Data/IEnvironmentRepository.cs`(加 1 行)
- `src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs`(加 12 行)
- `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs`(ctor 加 1 参 + 6 行逻辑)
- `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml.cs`(`Show` 加 1 参 + 1 行)
- `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`(1 行 caller)
- `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml`(1 行文案)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelTests.cs`(8 处 ctor 加 `envRepo: null`)
- `tests-wpf/ComfyUI.Manager.Tests/Data/EnvironmentRepositoryMaxPortTests.cs`(新)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelMaxPortTests.cs`(新)

---

## Execution choice

**Recommended: Subagent-Driven Development**
- 2 task(小,串行)— T1 repo method + 4 测试,T2 VM 注入 + XAML + 8 既有测试适配 + 3 新测试 + close-out
- Per-task review gate(sonnet implementer + sonnet reviewer)
- 估计 2 commits on main
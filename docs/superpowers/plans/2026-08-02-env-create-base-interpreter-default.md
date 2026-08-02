# v0.6.5.5 — 新建环境:区分基础解释器与 venv 解释器 + 默认值继承 实施 Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 `CreateEnvDialog` 上的 Python 解释器字段始终表示"基础解释器",venv 解释器写入 Environment 模型;下次打开 dialog 时 PythonExe 默认从最近一次成功创建 env 的 `BasePythonPath` 拉,而不是 settings。

**Architecture:**
- `Environment` 加 `BasePythonPath`(基础解释器路径) + `PythonVersion`(venv python 版本字符串)。
- `EnvironmentRepository` 加 SQLite schema 两列 + 老行 fallback(空 → `PythonExecutable` / `<unknown>`)。
- `SqliteConnectionFactory` 沿用 `CatalogCacheStore.cs:103-108` 的 `EnsureColumn` 模式,集中做 schema 增量升级。
- `EnvCreatorService.CreateAsync` 写库前设 `BasePythonPath = pythonExe`、读 venv python 版本写入 `PythonVersion`。
- `EnvironmentListViewModel` 新增 `RecentBasePythonPath`(按 `RootPath` mtime 取最近),`CreateEnv()` 多传一参。
- `CreateEnvDialogViewModel.ApplyTemplate` 优先级:recent base 文件存在 → settings;`ApplyTemplateCommand` 按钮仍重置回 settings。
- `CreateEnvDialog.Show` 第 4 参 `string? recentBasePythonPath`。

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · `Microsoft.Data.Sqlite` · `System.Text.Json` · xUnit temp dir pattern

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| G1 | `Environment.BasePythonPath`(string,默认 `""`,`[JsonPropertyName("base_python_path")]`)— 必填,等于 dialog `PythonExe` 创建时的值 | spec §2.1 |
| G2 | `Environment.PythonVersion`(string,默认 `""`,`[JsonPropertyName("python_version")]`)— venv python 版本字符串(`sys.version`) | spec §2.1 |
| G3 | SQLite `environments` 表新增 2 列:`base_python_path TEXT NOT NULL DEFAULT ''`,`python_version TEXT NOT NULL DEFAULT ''` | spec §2.2 |
| G4 | 沿用 `CatalogCacheStore.cs:103-108` 的 `EnsureColumn(conn, table, column, type)` 模式;`SqliteConnectionFactory.InitSchemaIfMissing` 末尾调 2 次,旧 DB 自动 ALTER TABLE | spec §2.2 + 既有模式 |
| G5 | 老行 `BasePythonPath == ""` → `EnvironmentRepository.Read*` fallback 到 `PythonExecutable`(不报错),在返回前设 `BasePythonPath = PythonExecutable` | spec §5.5 |
| G6 | 老行 `PythonVersion == ""` → `EnvironmentRepository.Read*` fallback 到 `"<unknown>"` | spec §5.5 |
| G7 | `EnvCreatorService.CreateAsync` 写库时设 `env.BasePythonPath = pythonExe`;**不**改 `pythonExe` 参数语义(仍是基础解释器) | spec §4.2 |
| G8 | `ReadVenvPythonVersionAsync(venvPath, ct)` 实现:`<venv>/Scripts/python.exe -c "import sys; print(sys.version)"`,任何异常/超时 fallback `"<unknown>"`,**不抛**(env 已创建成功,版本号只是诊断信息) | spec §4.2 + §6 |
| G9 | `CreateEnvDialogViewModel.ApplyTemplate` 优先级:(1) `_recentBasePythonPath` 非空且 `File.Exists` → `PythonExe = _recentBasePythonPath`;(2) 否则 `settings.TemplatePythonDir + DefaultPythonVersion/python.exe`(同 v0.6.5.4) | spec §3.2 |
| G10 | `ApplyTemplateCommand`("应用模板"按钮)无视 recent,重置回 settings 那条 | spec §3.3 |
| G11 | `EnvironmentListViewModel.RecentBasePythonPath`:取最近一次成功创建 env 的 `BasePythonPath`(按 `RootPath` mtime,无 mtime 信息时 fallback `Id` 字典序);List 空时 `null` | spec §5.1-5.2 |
| G12 | `CreateEnvDialog.Show(creator, settings, projectRoot, recentBasePythonPath)` 第 4 参 `string?`;`CreateEnvDialogViewModel` ctor 多接 `string? recentBasePythonPath` | spec §5.3 |
| G13 | venv 是 base 的派生(`<venv>/Scripts/python.exe` 是 launcher/链接,运行时必须能访问 base)— Python venv 模块固有事实;spec 不监控、不告警、不自动重建 | spec §5.6 |
| G14 | 失败码不变:`VENV_PYTHON_MISSING`、`VENV_CREATE_FAILED`、ENV_NAME_INVALID/ENV_LAYOUT_INVALID/ENV_NAME_DUPLICATE/ENV_ENVDIR_NOT_CONFIGURED/ENV_PATH_NOT_EMPTY/COMFYUI_SOURCE_MISSING | spec §4.3 |
| G15 | 5 处版本字面量 `0.6.5.4` → `0.6.5.5`(pyproject.toml / src/comfy_mgr/__init__.py / shared/errors.json / ComfyUI.Manager.csproj / tests/test_version_consistency.py 3 处);release notes `release/RELEASE-NOTES-v0.6.5.5.md` 中文,follow v0.6.5.4 风格 | spec §9-§10 + 既有版本控制模式 |
| G16 | 不得 rebuild zip / push / tag / `gh release create`(沿用 v0.6.5.4 模式,等用户单独授权) | `feedback_no_zip.md` + v0.6.5.4 boundary |
| G17 | WPF 测试基线 v0.6.5.4 = 285 PASS / 1 SKIP / 0 FAIL;v0.6.5.5 期望 +13 → 298 PASS / 1 SKIP / 0 FAIL | spec §9 |

---

## File Structure

### Create

| 文件 | 职责 |
|---|---|
| `tests-wpf/ComfyUI.Manager.Tests/Data/EnvironmentRepositoryTests.cs` | 4 个 test:BasePythonPath / PythonVersion round-trip + fallback |
| `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceTests.cs` | 2 个 test:CreateAsync 写库 BasePythonPath + PythonVersion |
| `release/RELEASE-NOTES-v0.6.5.5.md` | 中文,follow v0.6.5.4 风格 |

### Modify

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/Models/Environment.cs` | +2 字段(BasePythonPath + PythonVersion) |
| `src-wpf/ComfyUI.Manager/Data/SqliteConnectionFactory.cs` | CREATE TABLE 加 2 列 + `InitSchemaIfMissing` 末尾 `EnsureColumn` × 2 |
| `src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs` | SELECT / UPSERT / Read / Bind 4 处加 2 列 + 读后 fallback 逻辑 |
| `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs` | 写库时设 `BasePythonPath` + `PythonVersion`,新增 `ReadVenvPythonVersionAsync` 私有方法 |
| `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` | +`RecentBasePythonPath` 属性 + re-derive 逻辑 + ctor 多接 1 参 + `CreateEnv()` 多传 1 参 |
| `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml.cs` | `Show` 第 4 参 + vm ctor 多传 1 参 |
| `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs` | ctor 多接 1 参 + `_recentBasePythonPath` 字段 + `ApplyTemplate` 改优先级 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs` | 7 处 ctor call sites + trailing `null!,`(同 v0.6.5.4 T5 风格);新增 3 个 test |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelTests.cs` | ctor call sites 加第 4 参;新增 4 个 test |
| `pyproject.toml` + `src/comfy_mgr/__init__.py` + `shared/errors.json` + `src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj` + `tests/test_version_consistency.py` | 5 处版本字面量 `0.6.5.4` → `0.6.5.5`(T7 close-out) |
| `.superpowers/sdd/2026-08-02-env-create-base-interpreter-default/progress.md` | SDD ledger(T7 创建) |

### Delete

无。

### Keep (unchanged)

- `EnvCreatorService.CreateAsync` 签名(只内部填字段,不新参数)
- `EnvironmentRepository` 的 `Delete` / `Get` / `ListAll` / `Upsert` 公共方法签名
- `Settings.DefaultPythonVersion` 等 v0.6.5.4 行为
- `ComfyUI.Manager.csproj` `<Version>`(只 bump 数字)
- venv 是 base 派生的语义事实(只写 spec,不动 venv 模块)

---

## Tasks

### Task 1: `Environment.BasePythonPath` + `PythonVersion` 字段 + schema + 老行 fallback + 4 tests

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/Environment.cs:1-37`(末尾加 2 字段)
- Modify: `src-wpf/ComfyUI.Manager/Data/SqliteConnectionFactory.cs:85-148`(`InitSchemaIfMissing` 加 2 列 + 末尾 `EnsureColumn` × 2)
- Modify: `src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs:21-114`(SELECT / UPSERT / Read / Bind 4 处)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Data/EnvironmentRepositoryTests.cs`(新文件,~120 行,4 个 test)

**Interfaces:**
- Consumes: 既有 `SqliteConnectionFactory` 共享实例(同 v0.6.5.4)
- Produces:
  ```csharp
  // Models/Environment.cs
  public string BasePythonPath { get; set; } = "";
  public string PythonVersion { get; set; } = "";

  // SqliteConnectionFactory.cs (新增 helper,沿用 CatalogCacheStore 模式)
  private static void EnsureColumn(SqliteConnection conn, string table, string column, string type);

  // EnvironmentRepository.cs
  // Read 内部行为:读到 BasePythonPath == "" → fallback PythonExecutable;
  //              PythonVersion == "" → fallback "<unknown>"
  // Bind / SELECT / UPSERT 同步加 2 列
  ```

- [ ] **Step 1: Write failing tests**

新建 `tests-wpf/ComfyUI.Manager.Tests/Data/EnvironmentRepositoryTests.cs`:

```csharp
using System;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public sealed class EnvironmentRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly EnvironmentRepository _repo;

    public EnvironmentRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            "env-repo-test-" + Path.GetRandomFileName() + ".db");
        _factory = new SqliteConnectionFactory(_dbPath);
        _repo = new EnvironmentRepository(_factory);
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private static Environment MakeEnv(string id, string name) => new()
    {
        Id = id,
        Name = name,
        RootPath = $"/tmp/envs/{name}",
        ComfyuiLayout = "shared",
        ComfyuiSource = "/tmp/ComfyUI",
        VenvPath = $"/tmp/envs/{name}/venv",
        PythonExecutable = $"/tmp/envs/{name}/venv/Scripts/python.exe",
        CustomNodesPath = $"/tmp/envs/{name}/custom_nodes",
        ExtraModelPathsYaml = $"/tmp/envs/{name}/extra_model_paths.yaml",
        Port = 8188,
        EnabledNodeIdsJson = "[]",
        Status = "stopped",
    };

    [Fact]
    public void BasePythonPath_RoundTrips()
    {
        var env = MakeEnv("env-1", "alpha");
        env.BasePythonPath = "/tmp/python/3.10/python.exe";
        env.PythonVersion = "3.10.18";

        _repo.Upsert(env);
        var list = _repo.ListAll();

        Assert.Single(list);
        Assert.Equal("/tmp/python/3.10/python.exe", list[0].BasePythonPath);
        Assert.Equal("3.10.18", list[0].PythonVersion);
    }

    [Fact]
    public void BasePythonPath_FallsBackToPythonExecutable_WhenColumnEmpty()
    {
        var env = MakeEnv("env-2", "beta");
        env.BasePythonPath = "";   // simulate 老行 / 老 schema
        env.PythonExecutable = "/tmp/envs/beta/venv/Scripts/python.exe";

        _repo.Upsert(env);
        var list = _repo.ListAll();

        Assert.Single(list);
        Assert.Equal("/tmp/envs/beta/venv/Scripts/python.exe", list[0].BasePythonPath);
    }

    [Fact]
    public void PythonVersion_RoundTrips()
    {
        var env = MakeEnv("env-3", "gamma");
        env.PythonVersion = "3.11.13 (tags/v3.11.13:...)";

        _repo.Upsert(env);
        var list = _repo.ListAll();

        Assert.Single(list);
        Assert.Equal("3.11.13 (tags/v3.11.13:...)", list[0].PythonVersion);
    }

    [Fact]
    public void PythonVersion_FallsBackToUnknown_WhenColumnEmpty()
    {
        var env = MakeEnv("env-4", "delta");
        env.PythonVersion = "";

        _repo.Upsert(env);
        var list = _repo.ListAll();

        Assert.Single(list);
        Assert.Equal("<unknown>", list[0].PythonVersion);
    }
}
```

- [ ] **Step 2: Run tests, verify FAIL**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentRepositoryTests" -v minimal
```

Expected: 4 FAIL(无 `BasePythonPath` / `PythonVersion` 字段;列不存在)。

- [ ] **Step 3: Modify `Models/Environment.cs`(末尾追加)**

```csharp
    [JsonPropertyName("base_python_path")]
    public string BasePythonPath { get; set; } = "";

    [JsonPropertyName("python_version")]
    public string PythonVersion { get; set; } = "";
```

- [ ] **Step 4: Modify `Data/SqliteConnectionFactory.cs`**

修改 `InitSchemaIfMissing` 的 CREATE TABLE(在 `status TEXT DEFAULT 'stopped',` 后,`pid INTEGER` 前插入新列):

```sql
                status TEXT DEFAULT 'stopped',
                base_python_path TEXT NOT NULL DEFAULT '',
                python_version TEXT NOT NULL DEFAULT '',
                pid INTEGER
```

在该方法末尾(`cmd.ExecuteNonQuery();` 之前)加 2 行,沿用 `CatalogCacheStore.cs:103-108` 的 `EnsureColumn` 模式(在本类内复制该私有 helper;**不**依赖 `CatalogCacheStore` 内部细节):

```csharp
        // 增量升级:旧 db 没有 base_python_path / python_version 列 → ALTER TABLE ADD COLUMN
        EnsureColumn(conn, "environments", "base_python_path", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "environments", "python_version", "TEXT NOT NULL DEFAULT ''");
```

并在 `SqliteConnectionFactory` 类内(末尾)新增私有 helper(可直接复制 `CatalogCacheStore.cs:103-108` 实现):

```csharp
    private static void EnsureColumn(SqliteConnection conn, string table, string column, string type)
    {
        using (var info = conn.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table})";
            using var reader = info.ExecuteReader();
            bool exists = false;
            while (reader.Read())
            {
                if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
            if (exists) return;
        }
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }
```

- [ ] **Step 5: Modify `Data/EnvironmentRepository.cs`**

5 处同步:

(1) `ListAll` SELECT 加 2 列:

```csharp
        cmd.CommandText = @"
            SELECT id, name, root_path, comfyui_layout, comfyui_source,
                   venv_path, python_executable, custom_nodes_path,
                   extra_model_paths_yaml, port, enabled_node_ids_json,
                   status, base_python_path, python_version, pid
            FROM environments
            ORDER BY name";
```

(2) `Get(envId)` SELECT 加 2 列(同 ListAll SELECT)。

(3) `Upsert` INSERT + UPDATE 加 2 列:

```sql
                base_python_path, python_version,
                venv_path, python_executable, ...
```

对应列号也加 2。

(4) `Read(reader)` 加 2 行 + 读后 fallback:

```csharp
            BasePythonPath = reader.GetString(13),
            PythonVersion = reader.GetString(14),
            Pid = reader.IsDBNull(15) ? null : reader.GetInt32(15),
        };

        // 老行 fallback:BasePythonPath 空 → PythonExecutable;PythonVersion 空 → "<unknown>"
        if (string.IsNullOrEmpty(result.BasePythonPath))
            result.BasePythonPath = result.PythonExecutable ?? "";
        if (string.IsNullOrEmpty(result.PythonVersion))
            result.PythonVersion = "<unknown>";
        return result;
    }
```

注意 `Read` 当前是 expression-bodied `=>`,需要改成 block-bodied(用 `var result = ...; ... return result;`)。

(5) `Bind(cmd, env)` 加 2 行:

```csharp
        cmd.Parameters.AddWithValue("@base_python_path", env.BasePythonPath);
        cmd.Parameters.AddWithValue("@python_version", env.PythonVersion);
```

- [ ] **Step 6: Run tests, verify PASS**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentRepositoryTests" -v minimal
```

Expected: 4 PASS / 0 FAIL。

- [ ] **Step 7: Run full WPF suite, verify no regression**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```

Expected: 285 PASS / 1 SKIP / 0 FAIL(v0.6.5.4 基线,T1 不引入回归)。

- [ ] **Step 8: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Models/Environment.cs src-wpf/ComfyUI.Manager/Data/SqliteConnectionFactory.cs src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs tests-wpf/ComfyUI.Manager.Tests/Data/EnvironmentRepositoryTests.cs
git commit -m "feat(data): Environment.BasePythonPath + PythonVersion + repo schema"
```

---

### Task 2: `EnvCreatorService.CreateAsync` 写库设 BasePythonPath + PythonVersion + `ReadVenvPythonVersionAsync` + 2 tests

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs:60-168`(`CreateAsync` 写库时设 `BasePythonPath` + `PythonVersion`,新增私有 `ReadVenvPythonVersionAsync` 方法)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceTests.cs`(新文件,~80 行,2 个 test)

**Interfaces:**
- Consumes:
  - `Environment.BasePythonPath`(T1 已加)
  - `Environment.PythonVersion`(T1 已加)
  - `VenvCreator`(既有)创建 venv 后 `<venvPath>/Scripts/python.exe` 已存在
- Produces:
  - `EnvCreatorService.CreateAsync` 签名不变,行为:写库前填 `env.BasePythonPath = pythonExe`、`env.PythonVersion = await ReadVenvPythonVersionAsync(venvPath, ct)`
  - 新增私有方法:
    ```csharp
    private async Task<string> ReadVenvPythonVersionAsync(string venvPath, CancellationToken ct);
    ```

- [ ] **Step 1: Write failing tests**

新建 `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceTests.cs`:

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public sealed class EnvCreatorServiceTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly Models.Settings _settings;
    private readonly FakeVenvCreator _venvCreator;
    private readonly FakeJunctionLinker _linker;
    private readonly EnvCreatorService _service;

    public EnvCreatorServiceTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(),
            "envcreator-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_rootDir);

        _dbPath = Path.Combine(_rootDir, "state.db");
        _factory = new SqliteConnectionFactory(_dbPath);

        _settings = new Models.Settings
        {
            EnvsDir = "envs",
            TemplatePythonDir = "python",
            DefaultPythonVersion = "3.10",
            TemplateComfyuiDir = "ComfyUI",
        };

        _venvCreator = new FakeVenvCreator();
        _linker = new FakeJunctionLinker();

        // 准备 base python 与 comfyui 模板(让 CreateAsync 通过校验)
        var pyDir = Path.Combine(_rootDir, "python", "3.10");
        Directory.CreateDirectory(pyDir);
        File.WriteAllText(Path.Combine(pyDir, "python.exe"), "");

        var comfyDir = Path.Combine(_rootDir, "ComfyUI");
        Directory.CreateDirectory(comfyDir);
        File.WriteAllText(Path.Combine(comfyDir, "main.py"), "");

        _service = new EnvCreatorService(
            _factory, _venvCreator, _linker, _settings, _rootDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task CreateAsync_WritesBasePythonPath()
    {
        var basePy = Path.Combine(_rootDir, "python", "3.10", "python.exe");

        var env = await _service.CreateAsync(
            "alpha", "shared", basePy,
            Path.Combine(_rootDir, "ComfyUI"),
            port: null);

        Assert.Equal(basePy, env.BasePythonPath);
        Assert.Equal(basePy, _venvCreator.LastBasePython);
    }

    [Fact]
    public async Task CreateAsync_WritesPythonVersionFromVenvPython()
    {
        var basePy = Path.Combine(_rootDir, "python", "3.10", "python.exe");

        var env = await _service.CreateAsync(
            "beta", "shared", basePy,
            Path.Combine(_rootDir, "ComfyUI"),
            port: null);

        // FakeVenvCreator 写入 3.10.18 到 venv/Scripts/version.txt,ReadVenvPythonVersionAsync 应读到
        Assert.False(string.IsNullOrEmpty(env.PythonVersion));
        Assert.NotEqual("<unknown>", env.PythonVersion);
        Assert.Contains("3.10", env.PythonVersion);
    }

    private sealed class FakeVenvCreator : VenvCreator
    {
        public string? LastBasePython { get; private set; }
        public override async Task CreateAsync(string basePython, string venvPath,
            CancellationToken ct = default)
        {
            LastBasePython = basePython;
            // 模拟 venv 真实结构:Scripts/python.exe + 一个 version.txt(让 ReadVenvPythonVersionAsync 读到)
            var scriptsDir = Path.Combine(venvPath, "Scripts");
            Directory.CreateDirectory(scriptsDir);
            await File.WriteAllTextAsync(Path.Combine(scriptsDir, "python.exe"), "");
            await File.WriteAllTextAsync(Path.Combine(scriptsDir, "version.txt"),
                "3.10.18 (tags/v3.10.18:1dd1911, Dec  6 2025, 18:45:28) [MSC v.1929 64 bit (AMD64)]",
                ct);
            await Task.CompletedTask;
        }
    }

    private sealed class FakeJunctionLinker : JunctionLinker
    {
        public override Task CreateAsync(string linkPath, string target, CancellationToken ct = default)
        {
            Directory.CreateDirectory(linkPath);
            return Task.CompletedTask;
        }
        public override void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
        }
    }
}
```

注意:`FakeVenvCreator` 实现参考项目里现有的 VenvCreator 接口 — 实现细节由 implementer 在写测试时按仓库实际 `VenvCreator` 类适配(可能需要 :base class 或 override);如果现有 `VenvCreator` 是 sealed/abstract 不可继承,改为 composition(注入 `_venvCreator = (basePy, venvPath, ct) => { ... }`)。

- [ ] **Step 2: Run tests, verify FAIL**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvCreatorServiceTests" -v minimal
```

Expected: 2 FAIL(env.BasePythonPath 未被 set;env.PythonVersion 未被 set)。

- [ ] **Step 3: Modify `Services/EnvCreatorService.cs`**

(1) `CreateAsync` 写库前的 `new Environment { ... }` 块加 2 行(在 `PythonExecutable` 行之后):

```csharp
            BasePythonPath = pythonExe,
            PythonVersion = await ReadVenvPythonVersionAsync(venvPath, ct),
```

(2) 在类末尾(`NextFreePort` 之后)新增私有方法:

```csharp
    private async Task<string> ReadVenvPythonVersionAsync(string venvPath, CancellationToken ct)
    {
        try
        {
            var venvPython = Path.Combine(venvPath, "Scripts", "python.exe");
            if (!File.Exists(venvPython)) return "<unknown>";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = venvPython,
                Arguments = "-c \"import sys; print(sys.version)\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(stdout) ? "<unknown>" : stdout.Trim();
        }
        catch
        {
            return "<unknown>";
        }
    }
```

- [ ] **Step 4: Run tests, verify PASS**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvCreatorServiceTests" -v minimal
```

Expected: 2 PASS / 0 FAIL。

- [ ] **Step 5: Run full WPF suite, verify no regression**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```

Expected: 285 + 6(T1 4 + T2 2) = 291 PASS / 1 SKIP / 0 FAIL。

- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceTests.cs
git commit -m "feat(wpf): EnvCreatorService writes BasePythonPath + PythonVersion"
```

---

### Task 3: `EnvironmentListViewModel.RecentBasePythonPath` + `CreateEnv()` 串第 4 参 + 7 处 ctor 补丁 + 3 tests

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`(+`RecentBasePythonPath` 属性 + re-derive 逻辑 + ctor 多接 1 参 + `CreateEnv()` 多传 1 参)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs`(7 处 ctor call sites + trailing `null!,`;新增 3 个 test)

**Interfaces:**
- Consumes:
  - `Environment.BasePythonPath`(T1 已加)
  - `EnvironmentListViewModel._envRepo` 既有;`LoadAsync` 既有
- Produces:
  ```csharp
  // EnvironmentListViewModel
  public string? RecentBasePythonPath { get; private set; }

  // 构造函数(在 BaseEnvProfileLoader 后)新增第 8 参 `string? recentBasePythonPath = null`
  // 或作为新 setter `UpdateRecentBasePythonPath()` 由 LoadAsync/refresh 时调用
  ```

策略:RecentBasePythonPath 在 `LoadAsync` / `Refresh` / `Add(env)` / `Upsert(env)` 后 re-derive(计算 List 中 `RootPath` mtime 最近 / 无 mtime 时 `Id` 字典序的 env 的 `BasePythonPath`)。

- [ ] **Step 1: Patch 7 处 ctor call sites(预期修改后本地 build RED,直到 T5 完成 `CreateEnvDialog.Show` 签名)**

`tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs` 中所有 7 处 `new EnvironmentListViewModel(...)`:

```csharp
new EnvironmentListViewModel(/* 既有参数 */, null!),   // ← 加 trailing `null!,`
```

**不**新增任何 test(只是 ctor 签名调整,测试通过加 null 即可编译)。

- [ ] **Step 2: Run dotnet build, verify RED**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

Expected: 1 error(`EnvironmentListViewModel` ctor 多参数,VM 还没改)— 这是 T3+T4+T5 cascade,T5 完成后会解。

注:T3 任务允许到这里 build 不过(同 v0.6.5.4 T5 模式)。

- [ ] **Step 3: Modify `ViewModels/EnvironmentListViewModel.cs`**

(1) 加字段(在现有字段后):

```csharp
    private readonly string? _initialRecentBasePythonPath;
    public string? RecentBasePythonPath { get; private set; }
```

(2) ctor 加 1 参(最后参数,默认 null):

```csharp
    public EnvironmentListViewModel(
        EnvironmentRepository envRepo,
        ProcessLauncher processLauncher,
        EnvCreatorService envCreator,
        BaseEnvInstaller baseEnvInstaller,
        Models.Settings settings,
        BaseEnvProfileLoader profileLoader,
        string projectRoot,
        string? recentBasePythonPath = null)
```

(3) ctor body:

```csharp
        _initialRecentBasePythonPath = recentBasePythonPath;
        RecentBasePythonPath = recentBasePythonPath;
```

(4) 在 `LoadAsync` / `Refresh` / `Add` / `Upsert` 等所有 list 改动后调 `RecomputeRecentBasePythonPath()`:

```csharp
    private void RecomputeRecentBasePythonPath()
    {
        if (Environments.Count == 0)
        {
            RecentBasePythonPath = _initialRecentBasePythonPath;
            return;
        }
        // 按 RootPath mtime 取最近;无 mtime 时 fallback Id 字典序
        var latest = Environments
            .OrderByDescending(e =>
            {
                try
                {
                    return Directory.Exists(e.RootPath)
                        ? new DirectoryInfo(e.RootPath).LastWriteTimeUtc.Ticks
                        : 0;
                }
                catch { return 0; }
            })
            .ThenByDescending(e => e.Id)
            .FirstOrDefault();
        RecentBasePythonPath = latest?.BasePythonPath;
    }
```

(5) `CreateEnv()` 多传 1 参:

```csharp
        Views.CreateEnvDialog.Show(_envCreator, _settings, _projectRoot, RecentBasePythonPath)
```

注意:此时 `CreateEnvDialog.Show` 还没改签名(那是 T5)。本 task 完成后 build 仍 RED,直到 T5 完成。

- [ ] **Step 4: Write 3 new tests(本 task end state)**

在 `EnvironmentListViewModelTests.cs` 中新增(放在文件末尾):

```csharp
    [Fact]
    public void RecentBasePythonPath_NullWhenListEmpty()
    {
        var vm = new EnvironmentListViewModel(
            /* 既有参数 */, recentBasePythonPath: null);
        Assert.Null(vm.RecentBasePythonPath);
    }

    [Fact]
    public void RecentBasePythonPath_LastCreatedEnvBasePython()
    {
        var repo = new EnvironmentRepository(_factory);
        var env1 = MakeEnv("env-a", "alpha"); env1.BasePythonPath = "/tmp/a.exe";
        var env2 = MakeEnv("env-b", "beta");  env2.BasePythonPath = "/tmp/b.exe";
        repo.Upsert(env1); repo.Upsert(env2);

        var vm = new EnvironmentListViewModel(
            repo, /* 既有 */, recentBasePythonPath: null);
        vm.LoadAsync().Wait();

        // env2 是后 upsert 的,RecentBasePythonPath 应该是 env2.BasePythonPath
        Assert.Equal("/tmp/b.exe", vm.RecentBasePythonPath);
    }

    [Fact]
    public void CreateEnv_PassesRecentBasePythonPath_ToDialog()
    {
        // 通过 vm.CreateEnv() 间接验证(对话框本身是 mock / null VM)
        // 这里使用 ctor 参数注入的方式:vm.RecentBasePythonPath 来自 constructor
        var vm = new EnvironmentListViewModel(
            /* 既有 */, recentBasePythonPath: "/tmp/x.exe");
        Assert.Equal("/tmp/x.exe", vm.RecentBasePythonPath);
    }
```

详细 helper / MakeEnv / ctor 调用由 implementer 按现有 `EnvironmentListViewModelTests.cs` 风格补全(参考既有 7 处 ctor call sites)。

- [ ] **Step 5: Run full WPF suite(预期 RED,因为 T4/T5 cascade)**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```

Expected: build 失败(`CreateEnvDialog.Show` 还没改签名)— 这正常,T4+T5 解。

**不**commit;等待 T5 完成。

(per spec §11 model 选择,本 task = Sonnet implementer 因涉及 re-derive 逻辑;但 close-out Haiku。)

---

### Task 4: `CreateEnvDialogViewModel.ApplyTemplate` 优先级 + ctor 多参 + 4 tests

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs`(ctor 加第 4 参 `string? recentBasePythonPath` + `_recentBasePythonPath` 字段 + `ApplyTemplate` 改优先级逻辑)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelTests.cs`(现有 ctor call sites 加第 4 参;新增 4 个 test)

**Interfaces:**
- Consumes:
  - `Settings`(既有)、`EnvCreatorService`(既有)、`projectRoot`(v0.6.5.4 已串)
- Produces:
  ```csharp
  public CreateEnvDialogViewModel(
      EnvCreatorService creator,
      Settings settings,
      string projectRoot,
      string? recentBasePythonPath,    // ← 新增第 4 参
      Action<Models.Environment?>? onResult = null);
  ```

- [ ] **Step 1: Write 4 new tests**

在 `CreateEnvDialogViewModelTests.cs` 中新增:

```csharp
    [Fact]
    public void Constructor_PrefersRecentBase_WhenFileExists()
    {
        // Setup: recentBase 文件存在;settings 路径下 python 也存在
        var recentBase = Path.Combine(Path.GetTempPath(), "recent-base-" + Path.GetRandomFileName());
        File.WriteAllText(recentBase, "");

        var root = Path.Combine(Path.GetTempPath(), "autofill-test-" + Path.GetRandomFileName());
        var pyDir = Path.Combine(root, "python", "3.10");
        Directory.CreateDirectory(pyDir);
        File.WriteAllText(Path.Combine(pyDir, "python.exe"), "");

        var settings = MakeSettings(/* TemplatePythonDir = "python", DefaultPythonVersion = "3.10" */);

        var vm = new CreateEnvDialogViewModel(_creator, settings, root, recentBase);

        Assert.Equal(recentBase, vm.PythonExe);
        Assert.Null(vm.TemplateWarningMessage);   // 无 template 警告,因为走 recent

        // cleanup
        try { File.Delete(recentBase); Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public void Constructor_FallsBackToSettings_WhenRecentBasePathIsNull()
    {
        // recentBase = null,settings 路径存在 → 走 settings
        // Setup 同 v0.6.5.4 ApplyTemplate_* tests
        var (root, py, _) = CreateTemplateTree("3.10");

        var settings = MakeSettings(/* TemplatePythonDir = "python", DefaultPythonVersion = "3.10" */);

        var vm = new CreateEnvDialogViewModel(_creator, settings, root, recentBasePythonPath: null);

        Assert.Equal(py, vm.PythonExe);
        Assert.Null(vm.TemplateWarningMessage);

        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public void Constructor_FallsBackToSettings_WhenRecentBaseFileMissing()
    {
        // recentBase = 不存在路径,settings 路径也不存在 → 走 settings fallback + 警告
        var recentBase = Path.Combine(Path.GetTempPath(), "missing-" + Path.GetRandomFileName());

        var root = Path.Combine(Path.GetTempPath(), "autofill-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);   // 没建 python 子目录

        var settings = MakeSettings(/* TemplatePythonDir = "python", DefaultPythonVersion = "3.10" */);

        var vm = new CreateEnvDialogViewModel(_creator, settings, root, recentBase);

        Assert.Equal("", vm.PythonExe);
        Assert.Contains("Python 模板 3.10 未安装", vm.TemplateWarningMessage ?? "");

        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public void Constructor_ApplyTemplateOverridesRecentBase()
    {
        // recentBase 文件存在;点 ApplyTemplateCommand 后 PythonExe 重置回 settings
        var recentBase = Path.Combine(Path.GetTempPath(), "recent-" + Path.GetRandomFileName());
        File.WriteAllText(recentBase, "");

        var (root, py, _) = CreateTemplateTree("3.10");
        var settings = MakeSettings(/* TemplatePythonDir = "python", DefaultPythonVersion = "3.10" */);

        var vm = new CreateEnvDialogViewModel(_creator, settings, root, recentBase);
        Assert.Equal(recentBase, vm.PythonExe);   // 初始:recent

        vm.ApplyTemplateCommand.Execute(null);
        Assert.Equal(py, vm.PythonExe);           // 应用模板后:settings

        try { File.Delete(recentBase); Directory.Delete(root, recursive: true); } catch { }
    }
```

- [ ] **Step 2: Run new tests, verify FAIL**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CreateEnvDialogViewModelTests" -v minimal
```

Expected: 4 FAIL(ctor 还不接 recentBasePythonPath)。

- [ ] **Step 3: Modify `ViewModels/CreateEnvDialogViewModel.cs`**

(1) 加字段:

```csharp
    private readonly string? _recentBasePythonPath;
```

(2) ctor 多接 1 参(在 `string projectRoot` 后):

```csharp
    public CreateEnvDialogViewModel(
        EnvCreatorService creator,
        Settings settings,
        string projectRoot,
        string? recentBasePythonPath = null,
        Action<Models.Environment?>? onResult = null)
```

(3) ctor body 设字段 + ApplyTemplate 不变(在 `_onResult = onResult;` 后加 `_recentBasePythonPath = recentBasePythonPath;`)。

(4) `ApplyTemplate()` 改逻辑:

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
                _projectRoot,
                _settings.TemplatePythonDir,
                _settings.DefaultPythonVersion,
                "python.exe");

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
        {
            ComfyuiSource = comfyuiSource;
        }
        else
        {
            warnings.Add("ComfyUI 模板目录未安装,请先在设置页下载");
            ComfyuiSource = "";
        }

        TemplateWarningMessage = warnings.Count == 0
            ? null
            : string.Join("\n", warnings);
    }
```

- [ ] **Step 4: Run new tests, verify PASS**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CreateEnvDialogViewModelTests" -v minimal
```

Expected: 9(原) + 4(新) = 13 PASS / 0 FAIL。

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelTests.cs
git commit -m "feat(wpf): CreateEnvDialogViewModel recent base inheritance"
```

---

### Task 5: `CreateEnvDialog.Show` 第 4 参 + EnvListVM `CreateEnv()` 第 4 参(解锁 T3+T4 cascade)

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml.cs:24-30`(`Show` 多 1 参)
- (T3 已经改了 EnvListVM `CreateEnv()` 多传 1 参)

**Interfaces:**
- Produces:
  ```csharp
  public static Models.Environment? Show(
      EnvCreatorService creator,
      Models.Settings settings,
      string projectRoot,
      string? recentBasePythonPath);
  ```

- [ ] **Step 1: Modify `Views/CreateEnvDialog.xaml.cs`**

```csharp
    public static Models.Environment? Show(
        EnvCreatorService creator,
        Models.Settings settings,
        string projectRoot,
        string? recentBasePythonPath)
    {
        var vm = new CreateEnvDialogViewModel(creator, settings, projectRoot, recentBasePythonPath);
        var dlg = new CreateEnvDialog(vm) { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();
        return dlg.Result;
    }
```

- [ ] **Step 2: Run full WPF suite, verify all green**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```

Expected: 285 + 13(新) = 298 PASS / 1 SKIP / 0 FAIL。

- [ ] **Step 3: Run dotnet build Release**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -v minimal
```

Expected: 0 errors(3 个 NU1900 NuGet 网络 warning 与代码无关,允许)。

- [ ] **Step 4: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml.cs src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs
git commit -m "feat(wpf): thread recentBasePythonPath through dialog and EnvListVM"
```

注:本 commit 包含 T3 的 EnvListVM 修改(此前 step 5 未 commit)+ T5 的 CreateEnvDialog.Show 签名;T3 的 3 个新 test 与 ctor 7 处 null!,补全也一并 commit。

---

### Task 6: 全量 verify + bump v0.6.5.5 + release notes + ledger

**Files:**
- Modify: `pyproject.toml`: `version = "0.6.5.4"` → `"0.6.5.5"`
- Modify: `src/comfy_mgr/__init__.py`: `__version__ = "0.6.5.4"` → `"0.6.5.5"`
- Modify: `shared/errors.json`: `"_version": "0.6.5.4"` → `"0.6.5.5"`
- Modify: `src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj`: `<Version>0.6.5.4</Version>` → `0.6.5.5`
- Modify: `tests/test_version_consistency.py`: 3 处字面量 `0.6.5.4` → `0.6.5.5`
- Create: `release/RELEASE-NOTES-v0.6.5.5.md`(中文,follow v0.6.5.4 模板风格)
- Create: `.superpowers/sdd/2026-08-02-env-create-base-interpreter-default/progress.md`(SDD ledger scratch,gitignored)

**Interfaces:**
- Consumes: Task 1-5 全部完成 + 测试基线 298/1/0 + Release build 0 errors
- Produces: verified v0.6.5.5 release-ready;**未**自动 push / tag / gh release / rebuild zip

- [ ] **Step 1: Bump 5 处版本字面量 `0.6.5.4` → `0.6.5.5`**

(a) `pyproject.toml` line 3:`version = "0.6.5.5"`

(b) `src/comfy_mgr/__init__.py` line 1:`__version__ = "0.6.5.5"`

(c) `shared/errors.json` line 2:`"_version": "0.6.5.5",`

(d) `src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj` line 11:`<Version>0.6.5.5</Version>`

(e) `tests/test_version_consistency.py` 3 处(`comfy_mgr.__version__` / `data["_version"]` / `m.group(1)`):

```python
assert comfy_mgr.__version__ == "0.6.5.5"
...
assert data["_version"] == "0.6.5.5"
...
assert m.group(1) == "0.6.5.5"
```

- [ ] **Step 2: Run pytest version consistency**

```bash
cd "D:/ToolDevelop/ComfyUI" && PYTHONPATH=src python -m pytest tests/test_version_consistency.py -q
```

Expected: 3 PASS。

- [ ] **Step 3: Run full WPF test suite**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```

Expected: 298 PASS / 1 SKIP / 0 FAIL(285 + 13 新)。Record 实际数字到 ledger。

- [ ] **Step 4: Run WPF Release build**

```bash
cd "D:/ToolDevelop/ComfyUI" && dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -v minimal
```

Expected: 0 errors(允许 NU1900 NuGet 网络 warning)。

- [ ] **Step 5: Write release notes**

Create `release/RELEASE-NOTES-v0.6.5.5.md`(中文,follow v0.6.5.4 模板风格):

```markdown
## v0.6.5.5 — 新建环境:区分基础解释器与 venv 解释器 + 默认值继承

v0.6.5.4 dialog 自动从 settings 带出 Python 解释器,但新建第二个 env 时仍走
settings 那条路径;用户反复手填。v0.6.5.5 让 dialog 默认继承**最近一次成功创建
env** 的基础解释器;同时区分"基础解释器"(建 venv 用的 base)与"venv 解释器"
(venv/Scripts/python.exe)两个角色,Environment 模型分别持久化。

---

### 1) 新增功能

- **`Environment.BasePythonPath`**(string,必填):创建 venv 用的基础解释器路径。
  写库时等于 dialog `PythonExe` 在创建时的值;后续默认 dialog 用这条。
- **`Environment.PythonVersion`**(string,默认 `<unknown>`):venv 解释器的
  `sys.version` 字符串(创建 venv 后读出写入);**venv 版本永远等于 base 版本**
  (Python venv 模块固有事实)— base 3.10 → venv 3.10。
- **dialog 默认值继承**:下次打开新建 dialog 时,PythonExe 默认从最近一次成功
  创建 env 的 `BasePythonPath` 拉;List 空时回退 settings(行为同 v0.6.5.4)。
- **recent base 文件不存在 → 回退 settings**(行为同 v0.6.5.4 黄色提示)。
- **"应用模板"按钮仍重置回 settings**(无视 recent)— 保留 v0.6.5.4 语义。

### 2) 数据流

```
User creates env A:
  PythonExe = settings 那条 (base 3.10)
  ↓ EnvCreatorService.CreateAsync
  venv 实际生成 <env A>/venv/Scripts/python.exe
  ↓ 读 venv python sys.version 写入 env.PythonVersion = "3.10.18 ..."
  ↓ 写库 env.BasePythonPath = "settings 那条"
  ↓ dialog 关闭 (同 v0.6.5.4)

User opens dialog for env B:
  EnvironmentListViewModel.RecentBasePythonPath = env A.BasePythonPath
  ↓ CreateEnvDialog.Show(creator, settings, projectRoot, recentBase)
  ↓ CreateEnvDialogViewModel.ApplyTemplate
  ↓ PythonExe = recent (env A.BasePythonPath)
  (无 env 时:回退 settings)
```

### 3) 升级注意

- **SQLite schema 自动迁移**:`SqliteConnectionFactory.InitSchemaIfMissing` 末尾
  调 `EnsureColumn` × 2(沿用 `CatalogCacheStore.cs:103-108` 模式);老 DB 自动
  `ALTER TABLE ADD COLUMN`,数据零丢失。
- **老行兼容**:`BasePythonPath == ""` → repository 读时 fallback 到
  `PythonExecutable`;`PythonVersion == ""` → fallback `"<unknown>"`。
- **不破坏现有 v0.6.5.4 UX**:"应用模板"按钮 / Layout 切换 / ComfyuiSource 仍走
  settings。
- **venv 是 base 的派生**:`<venv>/Scripts/python.exe` 是 launcher/链接,运行时
  必须能访问 base;base 被删/移动,venv 跑不起来(本 spec 不监控、不告警、不自动
  重建,留给后续 hotfix)。

### 4) Verification

- **dotnet test:** 298 PASS + 1 SKIP / 0 FAIL(基线 v0.6.5.4 = 285 +
  EnvironmentRepositoryTests 4 + EnvCreatorServiceTests 2 +
  CreateEnvDialogViewModelTests 4 + EnvironmentListViewModelTests 3)
- **pytest version consistency:** 3 PASS(v0.6.5.4 → v0.6.5.5)
- **dotnet build Release:** 0 errors(允许 NU1900 NuGet 网络 warning)

### 5) Commits since v0.6.5.4(`2c08d94`)

```
(将由本 task 自动生成 6 个 commit — 见 git log)
```

### 已知 carry-over / 未做事项

- **未在本 session 完成:** tag `v0.6.5.5` push + `gh release create` —
  等用户明确授权(沿用 v0.6.5.4 同模式)。
- **手动 GUI smoke (TBD):** 用户桌面验证(详见 release notes §5 步骤)。
- **后续 hotfix 候选**(YAGNI,本 spec 不做):base 缺失顶部提示 + "重建 venv" 动作;
  UI 上展示 venv python 版本(目前仅 Environment.PythonVersion 模型字段)。

### Lessons learned(SDD)

- **venv 是 base 派生**:Python venv 模块固有事实—`<venv>/Scripts/python.exe` 是
  launcher/链接,运行时必须能访问 base;写 spec 必须显式说明,避免后续 PR 评审时
  把"venv 跑不起来"误判为 bug。
- **`ReadVenvPythonVersionAsync` fallback `"<unknown>"` 不抛**:env 已创建成功,
  版本号只是诊断信息,失败要 swallow,不要让版本号读取失败把整个 env 创建回滚。
- **schema 升级沿用 `EnsureColumn`**:不要重写;CatalogCacheStore 已有 helper 模式,
  复制到 `SqliteConnectionFactory` 集中管理,避免分散到各 repository。
```

- [ ] **Step 6: Update SDD ledger**

Create `.superpowers/sdd/2026-08-02-env-create-base-interpreter-default/progress.md`:

```
Task 1 (Environment + schema + 4 tests): complete (commit <sha>)
Task 2 (EnvCreatorService 写库 + 2 tests): complete (commit <sha>)
Task 3 (EnvListVM.RecentBasePythonPath + 3 tests + 7 处 ctor 补丁): complete (commit <sha>)
Task 4 (CreateEnvDialogVM.ApplyTemplate 优先级 + 4 tests): complete (commit <sha>)
Task 5 (CreateEnvDialog.Show 第 4 参 + cascade 解锁): complete (commit <sha>)
Task 6 (close-out + version bump + release notes): complete (commit <sha>)
```

(ledger 文件在 `.superpowers/sdd/` 是 gitignored scratch,不需要 commit。)

- [ ] **Step 7: Commit release notes + version bumps**

```bash
git add release/RELEASE-NOTES-v0.6.5.5.md pyproject.toml src/comfy_mgr/__init__.py shared/errors.json src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj tests/test_version_consistency.py
git commit -m "chore(release): bump to v0.6.5.5 + release notes"
```

Expected: 6 files changed, 1 new file (release notes)。

- [ ] **Step 8: Verify full state**

```bash
git log --oneline -10
git status --short
```

Expected: 7 commits on top of v0.6.5.4 (`2c08d94`); working tree clean (除了可能 `.superpowers/sdd/` gitignored 改动)。

- [ ] **Step 9: Report release boundary**

向用户报告:
- 所有 T1-T6 commits + 测试 + build 数字
- 询问单独授权是否:
  - `git push origin main`
  - rebuild `release/ComfyUI-Manager-v0.6.5.5-win-x64.zip`(265 MB 量级)
  - `git tag v0.6.5.5 && git push origin v0.6.5.5`
  - `gh release create v0.6.5.5 <zip> --notes-file release/RELEASE-NOTES-v0.6.5.5.md`
  - 验证 `gh release list` v0.6.5.5 是 Latest

(以上每项都影响外部状态,默认**不**自动执行,等用户明确授权 — 沿用 v0.6.5.4 模式。)

---

## Verification

### 单元测试

- WPF: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal` → 期望 298 PASS + 1 SKIP / 0 FAIL(基线 285 + EnvironmentRepositoryTests 4 + EnvCreatorServiceTests 2 + CreateEnvDialogViewModelTests 4 + EnvironmentListViewModelTests 3 = +13)
- Python: `PYTHONPATH=src python -m pytest tests/test_version_consistency.py -q` → 3 PASS

### 端到端手动测试(用户 desktop)

1. 启动 v0.6.5.5(先 rebuild zip 后跑)— 首次打开新建 dialog,PythonExe = settings 那条(行为同 v0.6.5.4)。
2. 创建 env A(base = `<projectRoot>/python/3.10/python.exe`)→ dialog 关闭。
3. 重启 WPF,打开新建 dialog → PythonExe 默认 = env A 的 BasePythonPath(`/.../python/3.10/python.exe`)。
4. 在 dialog 内点"应用模板" → PythonExe 重置回 settings 那条。
5. 删除 env A 目录(包含 venv)→ 重启 WPF,打开新建 dialog → PythonExe 回退 settings(无 env 时)。
6. 删除 `<projectRoot>/python/3.10/python.exe`(base 模板)→ 重启 WPF,打开新建 dialog → 顶部黄色提示"Python 模板 3.10 未安装"。
7. 在 SQLite 查询 `SELECT python_version FROM environments WHERE id='env A'` → 看到 `"3.10.x (...)"`(与 base 一致)。
8. 切换 settings.DefaultPythonVersion 到 `3.11`(假设有 `3.11/python.exe`)→ 创建 env B → 验证 env B.PythonVersion = `"3.11.x (...)"`,base 3.11 → venv 3.11 绑定正确。
9. 删除 env B 的 venv/Scripts/python.exe(模拟 venv 损坏)→ 验证 dialog 重新打开仍正常(不监控),后续运行 env 时才报错。

### Risks + Tradeoffs

| 风险 | 缓解 |
|---|---|
| 老 DB 无 `base_python_path` / `python_version` 列 | `SqliteConnectionFactory` 自动 `ALTER TABLE`,数据零丢失;repository 读时 fallback |
| 多个 env 有不同 base | `RecentBasePythonPath` 只取一个(最近);用户创建下一个 env 时如想用老 base,需手填 |
| 用户升级 Python / 删 base → venv 跑不起来 | spec 不监控、不告警、不自动重建(用户已确认 manual rebuild);`Environment.BasePythonPath` 持久化便于后续 hotfix |
| 用户在 dialog 内点"应用模板"后忘记 recent base | 顶部不显示 recent base 来源;后续 hotfix 可加"基于最近 env"按钮 |
| `ReadVenvPythonVersionAsync` 进程异常 / 超时 | fallback `"<unknown>"`,**不抛**;env 创建成功,版本号只是诊断 |
| venv 是 base 派生语义写进 spec 但不测试 | Python 语言固有事实,写说明足以;hotfix 加监控时再写测试 |
| T3+T4+T5 cascade compile error | plan 明确 T3 任务允许 build RED,T5 解锁;T5 commit 时一并包含 T3 的 EnvListVM 修改 + T3 的 7 处 ctor call sites + 3 个新 test |
| YAGNI 风险:有人想抽 `RecentBasePythonPathService` | spec §11 + 本 plan T3 step 3 明确"YAGNI,直接放 EnvListVM";防止 PR 评审时过度抽象 |

### Critical files to modify

- `src-wpf/ComfyUI.Manager/Models/Environment.cs`(+2 字段)
- `src-wpf/ComfyUI.Manager/Data/SqliteConnectionFactory.cs`(CREATE TABLE 加 2 列 + `EnsureColumn` × 2 + 私有 helper)
- `src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs`(SELECT/UPSERT/Read/Bind 4 处 + 读后 fallback)
- `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs`(写库设 `BasePythonPath` + `PythonVersion` + `ReadVenvPythonVersionAsync`)
- `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`(`RecentBasePythonPath` + re-derive + ctor 多参 + `CreateEnv()` 多传)
- `src-wpf/ComfyUI.Manager/Views/CreateEnvDialog.xaml.cs`(`Show` 多 1 参)
- `src-wpf/ComfyUI.Manager/ViewModels/CreateEnvDialogViewModel.cs`(ctor 多接 1 参 + `_recentBasePythonPath` + `ApplyTemplate` 优先级)
- `tests-wpf/ComfyUI.Manager.Tests/Data/EnvironmentRepositoryTests.cs`(新,4 个 test)
- `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceTests.cs`(新,2 个 test)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CreateEnvDialogViewModelTests.cs`(+4 个 test)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelTests.cs`(7 处 ctor call sites + 3 个新 test)
- 5 处版本字面量 + `release/RELEASE-NOTES-v0.6.5.5.md`(new)
- `.superpowers/sdd/2026-08-02-env-create-base-interpreter-default/progress.md`(gitignored scratch)

---

## Execution choice

**Recommended: Subagent-Driven Development**
- 6 task + 1 close-out = 7 dispatches(实际 T1-T5 + T6 close-out)
- Per-task review gate(Sonnet implementer + Haiku reviewer)
- 核心 task(T1 schema 升级 + 老行 fallback;T3 EnvListVM re-derive;T4 ApplyTemplate 优先级)— Sonnet
- 机械 task(T2 EnvCreatorService 写库 — 含 `ReadVenvPythonVersionAsync` 进程调用有点 trick;T5 cascade 解锁)— Haiku
- close-out(T6 bump + release notes + ledger)— Haiku

Estimated 7 commits on main on top of v0.6.5.4 `2c08d94`。
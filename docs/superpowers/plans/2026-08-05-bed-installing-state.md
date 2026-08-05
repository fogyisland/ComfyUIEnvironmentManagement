# v0.6.5.8 Implementation Plan: BED installing 状态写活 + 启动 reconciliation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `BaseEnvInstaller.InstallAsync` 进入 pip 之前写 `BedStatus = "installing"` 让 v0.6.5.7 已就位的 UI 门控(StartCommand disabled + StartTooltip + BedDisplay ⏳)真正生效;`App.OnStartup` 加 `ReconcileStaleOnStartup` 把上次未装完的 installing 行翻成 failed + "上次未完成"。

**Architecture:**
- `BaseEnvInstaller.InstallAsync` 在 `progress?.Report(... Running ...)` 之前 foreach envId 写 `BedStatus = "installing"`(try/catch 单行写失败静默,跟终态回写一致)
- 新增 `BaseEnvInstaller.ReconcileStaleOnStartup(EnvironmentRepository)` 静态方法:ListAll → 翻所有 `BedStatus="installing"` 为 `"failed"` + `BedFailedReason="上次未完成"`(单行写失败静默)
- `App.OnStartup` 在 `dbFactory` 之后、`_mainVm` 构造之前调 `ReconcileStaleOnStartup(envRepo)`(先于 MainViewModel.Load() 让 UI 第一次 paint 已经是终态)
- 不动 `BaseEnvProgressDialog` / `BaseEnvProgressViewModel` / `EnvironmentListViewModel` / 任何 XAML(门控 + 展示已就位)
- 不动 `BaseEnvProfile` / `Settings` / `BaseEnvViewModel`

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · `Microsoft.Data.Sqlite` · hand-rolled MVVM (`RelayCommand`)

**base SHA:** `a5b3361`(本 plan 的 spec 落地 commit,基于 v0.6.5.7 chain `9f8aaa5` + spec `a5b3361`)

**spec:** `docs/superpowers/specs/2026-08-05-bed-installing-state-design.md`(本 plan 的 source of truth)

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| G1 | `BaseEnvInstaller.InstallAsync` 在 `progress?.Report(Running...)` 之前 foreach envId 写 `BedStatus = "installing"`,单行写失败 try/catch 静默(不抛、不影响 RunPipAsync 调用) | spec §4.1 |
| G2 | 终态回写(foreach envIds 写 `done` / `failed` + reason)逻辑完全不动 — v0.6.5.7 已有,只复用 | spec §4.1 + 现有代码 |
| G3 | 写 installing 失败不致命(跟终态写 try/catch 风格一致) | spec §4.1 + 现有 code style |
| G4 | `ReconcileStaleOnStartup` 是 `public static` 方法,接收 `EnvironmentRepository`,返 `int` 表示翻了几行 | spec §4.2 |
| G5 | `ReconcileStaleOnStartup` 在 `App.OnStartup` 调,放在 `dbFactory` 创建后、`_mainVm = new MainViewModel(...)` 之前;必须先于 MainViewModel.Load() 跑(避免 UI 看到 ⏳ 装中 几秒后变 ❌) | spec §4.2 |
| G6 | reconciliation 翻完的失败 reason 字面量 = `"上次未完成"`(中文,跟其他 reason 风格一致) | spec §4.2 |
| G7 | reconciliation 写失败单行 try/catch 静默吞(下次启动再翻) | spec §4.2 |
| G8 | `BedStatus` 字符串字面量集合不变(`"done"` / `"failed"` / `"installing"` / `null`),不引入 enum | 现有 code style + v0.6.5.7 |
| G9 | 不改 `BaseEnvProgressDialog` / `BaseEnvProgressViewModel` / `EnvironmentListViewModel` / 任何 XAML | spec §4.3 |
| G10 | 不改 `Environment` model / `Environment.BedDisplay` / `EnvironmentRepository` / Sqlite schema | spec §3 |
| G11 | 不 bump version / 不发 release zip / 无 ledger 提交(per v0.6.5.6 hotfix 偏好"本地 commit + 重建 staging,不发布新 release") | user scope |
| G12 | v0.6.5.7 的 4 个 `BaseEnvInstallerBedWriteTests` 老 test 必须继续 PASS(`FakeBaseEnvInstaller` 整体 override InstallAsync 跳过基类新逻辑,正交) | spec §6.3 |
| G13 | 新 `FakeBaseEnvInstallerPartial : BaseEnvInstaller` 只 override `RunPipAsync`,InstallAsync 走基类,用来验证新写 installing 逻辑 | spec §6.1 + 现有 `FakeBaseEnvInstaller` 模式 |
| G14 | `App.OnStartup` 是 `protected override` 同步方法,reconciliation 必须同步(`ReconcileStaleOnStartup` 不返 `Task`);SQLite ListAll + foreach + 单行 Upsert 在几十行 env 量级下 < 10ms | spec §4.2 + 性能判断 |
| G15 | `ReconcileStaleOnStartup` 测试用 `new TestDb()` + `new EnvironmentRepository(db.Factory)` 直接调,不需要 `BaseEnvInstaller` 实例 | spec §6.2 |

---

## File Structure

### Create

| 文件 | 行数(估) | 职责 |
|---|---|---|
| `tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvInstallerInstallingStateTests.cs` | ~120 | 4 测试:写 installing / env 缺失不写 / python 缺失不写 / upsert 失败不致命(用 `FakeBaseEnvInstallerPartial` 只 override RunPipAsync) |
| `tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvInstallerReconcileTests.cs` | ~80 | 4 测试:翻 1 行 installing / null repo 抛 / 空 db 返 0 / 全部 installing 计数正确 |

### Modify

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/Services/BaseEnvInstaller.cs` | `InstallAsync` foreach envId 在 `progress?.Report(Running...)` 之前写 `BedStatus="installing"`(try/catch);新增 `public static int ReconcileStaleOnStartup(EnvironmentRepository envRepo)` |
| `src-wpf/ComfyUI.Manager/App.xaml.cs` | `OnStartup` 在 `dbFactory` + `envRepo` 创建后、`_mainVm = new MainViewModel(...)` 之前调 `BaseEnvInstaller.ReconcileStaleOnStartup(envRepo)` |

### Delete

无。

### Keep (unchanged)

- `BaseEnvInstaller.InstallAsync` 终态回写(`Services/BaseEnvInstaller.cs:182-208`)— 完全不动
- `BaseEnvInstaller.RunPipAsync` 保护 virtual — 测试继续可 override
- `BaseEnvProgressDialog` / `BaseEnvProgressViewModel`
- `EnvironmentListViewModel.StartCommand.CanExecute` + `StartTooltip`
- `Environment.BedDisplay` 4 分支
- `EnvironmentRepository`(无新方法、无新字段)
- `BaseEnvProfile` / `BaseEnvProfileLoader` / `BaseEnvViewModel`
- `Settings` / `SettingsView`
- 任何 XAML
- 任何 schema / migration

---

## Tasks

### Task 1: `BaseEnvInstaller.InstallAsync` 写 installing + `FakeBaseEnvInstallerPartial`

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/BaseEnvInstaller.cs:118-128`(在 `progress?.Report(new BaseEnvProgress(BaseEnvStatus.Running, completed, total, envId, env.Name, 0, ...))` 之前插入 1 个 try/catch 写 installing)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvInstallerInstallingStateTests.cs`(4 测试 + `FakeBaseEnvInstallerPartial`)

**Interfaces:**
- Consumes: `_envRepo.Get(envId)` (existing), `_envRepo.Upsert(env)` (existing), `env.BedStatus = "installing"` setter
- Produces: same `Task<BaseEnvInstallResult>` return shape;new behavior: env.BedStatus="installing" briefly visible during pip run

**Step 1: Write failing tests**

**`tests-wpf/.../Services/BaseEnvInstallerInstallingStateTests.cs`** (verbatim):

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

public sealed class BaseEnvInstallerInstallingStateTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EnvironmentRepository _envRepo;

    public BaseEnvInstallerInstallingStateTests()
    {
        _envRepo = new EnvironmentRepository(_db.Factory);
    }

    public void Dispose() => _db.Dispose();

    private Environment SeedEnv(string id, string root, string? bedStatus = null)
    {
        var venv = Path.Combine(root, "venv");
        Directory.CreateDirectory(venv);
        var fakePy = Path.Combine(venv, "fake-python.exe");
        File.WriteAllText(fakePy, "");
        var env = new Environment
        {
            Id = id,
            Name = id,
            RootPath = root,
            VenvPath = venv,
            PythonExecutable = fakePy,
            CustomNodesPath = Path.Combine(root, "nodes"),
            Port = 8188,
            Status = "stopped",
            BedStatus = bedStatus,
        };
        _envRepo.Upsert(env);
        return env;
    }

    private static BaseEnvProfile DefaultProfile() => new()
    {
        Id = "pytorch-2.5.0-cu121-stable",
        Name = "PyTorch 2.5.0 + CUDA 12.1 (stable)",
        Description = "test",
        TorchVersion = "2.5.0",
        CudaVersion = "cu121",
        Channel = "stable",
        Packages = new List<string> { "torch", "torchaudio", "torchvision", "xformers" },
    };

    [Fact]
    public async Task InstallAsync_WritesInstallingBeforePipRun_AndFlipsToDoneAfter()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-installing-{Guid.NewGuid():N}");
        SeedEnv("env-a", root, bedStatus: null);
        var partial = new FakeBaseEnvInstallerPartial(_envRepo)
        {
            // 当 pip 第一次被调时:检查 db 里 env.BedStatus 必须是 "installing"
            AssertOnFirstRun = () =>
            {
                var live = _envRepo.Get("env-a");
                Assert.NotNull(live);
                Assert.Equal("installing", live!.BedStatus);
                Assert.Equal("pytorch-2.5.0-cu121-stable", live.BedProfileId);  // 还没回写
            },
            NextResult = new PipResult(0, false),
        };
        var progress = new RecordingProgress();

        await partial.InstallAsync(
            new[] { "env-a" }, DefaultProfile(), progress, CancellationToken.None);

        partial.AssertOnFirstRunCalled.Should().BeTrue();
        // 装完:env.BedStatus = "done", BedProfileId 已设
        var final = _envRepo.Get("env-a");
        Assert.Equal("done", final!.BedStatus);
        Assert.Equal("pytorch-2.5.0-cu121-stable", final.BedProfileId);
    }

    [Fact]
    public async Task InstallAsync_EnvMissing_DoesNotWriteInstalling_GoesStraightToFailed()
    {
        var partial = new FakeBaseEnvInstallerPartial(_envRepo)
        {
            NextResult = new PipResult(0, false),
        };
        // 不 seed env:_envRepo.Get("ghost") 返 null → 直接 failed,不写 installing,不调 RunPipAsync
        var result = await partial.InstallAsync(
            new[] { "ghost" }, DefaultProfile(), null, CancellationToken.None);

        Assert.True(result.Cancelled is false);
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains("ghost", result.Failures.Keys);
        Assert.Equal(0, partial.RunCount);
    }

    [Fact]
    public async Task InstallAsync_PythonPathResolveFails_DoesNotWriteInstalling()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-pyrfail-{Guid.NewGuid():N}");
        // seed env 但 VenvPath 指向不存在目录 → GetVenvPythonPath 抛 InvalidOperationException
        var env = SeedEnv("env-pyr", root);
        env.VenvPath = Path.Combine(root, "no-such-venv");
        env.PythonExecutable = null;  // 强制 fallback 到 VenvPath(不存在)
        _envRepo.Upsert(env);

        var partial = new FakeBaseEnvInstallerPartial(_envRepo)
        {
            NextResult = new PipResult(0, false),
        };
        var result = await partial.InstallAsync(
            new[] { "env-pyr" }, DefaultProfile(), null, CancellationToken.None);

        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, partial.RunCount);  // 根本没进 pip
        // 终态:env.BedStatus="failed"
        var final = _envRepo.Get("env-pyr");
        Assert.Equal("failed", final!.BedStatus);
        Assert.NotNull(final.BedFailedReason);
    }

    [Fact]
    public async Task InstallAsync_EnvRepoUpsertFailsDuringInstalling_DoesNotAbortInstall()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bed-upsertfail-{Guid.NewGuid():N}");
        SeedEnv("env-u", root, bedStatus: null);
        // 包装 envRepo:调 Upsert 时第一次抛 SqliteException(模拟写 installing 失败),
        // 之后调用正常(让终态回写成功)。
        var flakyRepo = new FlakyEnvironmentRepository(_envRepo, failFirstUpsert: true);
        var partial = new FakeBaseEnvInstallerPartial(flakyRepo)
        {
            NextResult = new PipResult(0, false),
        };

        // 不应抛;装完仍 done
        var result = await partial.InstallAsync(
            new[] { "env-u" }, DefaultProfile(), null, CancellationToken.None);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        // 终态仍写成功(因为 upsert 失败只在第一次)
        var final = _envRepo.Get("env-u");
        Assert.Equal("done", final!.BedStatus);
    }

    // ---- helpers ----

    private sealed class FakeBaseEnvInstallerPartial : BaseEnvInstaller
    {
        public PipResult NextResult { get; set; } = new(0, false);
        public int RunCount { get; private set; }
        public Action? AssertOnFirstRun { get; set; }
        public bool AssertOnFirstRunCalled { get; private set; }
        private readonly EnvironmentRepository _repo;

        public FakeBaseEnvInstallerPartial(EnvironmentRepository repo) : base(repo) { _repo = repo; }

        protected override Task<PipResult> RunPipAsync(
            string pythonExe, IReadOnlyList<string> pipArgs,
            Action<string> onLine, Action<int?> onPercent, CancellationToken ct)
        {
            RunCount++;
            if (!AssertOnFirstRunCalled)
            {
                AssertOnFirstRun?.Invoke();
                AssertOnFirstRunCalled = true;
            }
            onLine("Looking in indexes: ...");
            return Task.FromResult(NextResult);
        }
    }

    private sealed class FlakyEnvironmentRepository : EnvironmentRepository
    {
        private readonly EnvironmentRepository _inner;
        private int _upsertCalls;
        private readonly bool _failFirstUpsert;

        public FlakyEnvironmentRepository(EnvironmentRepository inner, bool failFirstUpsert)
            : base(inner.Factory)
        {
            _inner = inner;
            _failFirstUpsert = failFirstUpsert;
        }

        public new SqliteConnectionFactory Factory => _inner.Factory;

        public override void Upsert(Environment env)
        {
            if (_failFirstUpsert && _upsertCalls++ == 0)
            {
                throw new Microsoft.Data.Sqlite.SqliteException("simulated", 0);
            }
            _inner.Upsert(env);
        }

        public override Environment? Get(string id) => _inner.Get(id);
        public override System.Collections.Generic.List<Environment> ListAll() => _inner.ListAll();
    }

    private sealed class RecordingProgress : IProgress<BaseEnvProgress>
    {
        public List<BaseEnvProgress> Events { get; } = new();
        public void Report(BaseEnvProgress value) => Events.Add(value);
    }
}
```

**Step 2: Run tests, verify 4/4 FAIL** (因为新代码没写 installing,FailOnUpsert 测试甚至会抛 SqliteException 不被吞,第一个测试的 AssertOnFirstRun 会触发 NullReference)

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvInstallerInstallingStateTests"
```

Expected:
- `InstallAsync_WritesInstallingBeforePipRun_AndFlipsToDoneAfter` — FAIL (env.BedStatus is null when AssertOnFirstRun fires)
- `InstallAsync_EnvMissing_DoesNotWriteInstalling_GoesStraightToFailed` — likely PASS (already works in current code)
- `InstallAsync_PythonPathResolveFails_DoesNotWriteInstalling` — likely PASS (existing GetVenvPythonPath throws)
- `InstallAsync_EnvRepoUpsertFailsDuringInstalling_DoesNotAbortInstall` — FAIL (SqliteException bubbles up)

**Step 3: Implement `BaseEnvInstaller` 写 installing**

Modify `src-wpf/ComfyUI.Manager/Services/BaseEnvInstaller.cs`, in `InstallAsync` foreach loop, **right after** the existing `pythonExe` resolve try/catch (lines 98-112) and **right before** the existing `progress?.Report(new BaseEnvProgress(BaseEnvStatus.Running, ...))` (line 114):

```csharp
            // G1: 进入 pip 之前立刻写 installing,UI 立刻看到 ⏳ 装中,
            // 同一行 StartCommand 立即 disabled(已有 v0.6.5.7 门控)。
            // 单 env 写失败不致命(envRepo 不可写概率几乎 0,跟终态回写 try/catch 一致)。
            try
            {
                var live = _envRepo.Get(envId);
                if (live is not null)
                {
                    live.BedStatus = "installing";
                    _envRepo.Upsert(live);
                }
            }
            catch
            {
                // 写失败不致命,继续装(终态回写仍会写 done/failed)
            }

            progress?.Report(new BaseEnvProgress(
                BaseEnvStatus.Running, completed, total,
                envId, env.Name, 0, $"开始安装 ({env.Name})", null));
```

**Step 4: Run tests, verify 4/4 PASS** (except "EnvMissing" and "PythonPathResolveFails" which already passed)

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvInstallerInstallingStateTests"
```

**Step 5: Run full test suite, verify no regression** (v0.6.5.7's `BaseEnvInstallerBedWriteTests` 4 tests still PASS — `FakeBaseEnvInstaller` 整体 override InstallAsync,正交)

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```

Expected: 358 + 4 = **362 PASS / 0 FAIL / 1 SKIP** (skip unchanged)

**Step 6: Commit** `feat(wpf): BaseEnvInstaller 写 installing 让 UI 门控生效`

```bash
git add src-wpf/ComfyUI.Manager/Services/BaseEnvInstaller.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvInstallerInstallingStateTests.cs
git commit -m "feat(wpf): BaseEnvInstaller 写 installing 让 UI 门控生效"
```

---

### Task 2: `BaseEnvInstaller.ReconcileStaleOnStartup` + `App.OnStartup` 调

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/BaseEnvInstaller.cs`(在 class 末尾新增 `public static int ReconcileStaleOnStartup(EnvironmentRepository envRepo)` 方法)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs:69-77`(在 `var envRepo = new EnvironmentRepository(dbFactory);` 之后、`var baseEnvInstaller = new BaseEnvInstaller(envRepo);` 之前调 `ReconcileStaleOnStartup(envRepo)`)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvInstallerReconcileTests.cs`(4 测试)

**Interfaces:**
- Consumes: `EnvironmentRepository.ListAll()`, `EnvironmentRepository.Upsert(Environment)`
- Produces: `int` (count of flipped rows)

**Step 1: Write failing tests**

**`tests-wpf/.../Services/BaseEnvInstallerReconcileTests.cs`** (verbatim):

```csharp
using System.Collections.Generic;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

public sealed class BaseEnvInstallerReconcileTests
{
    private static void SeedEnv(TestDb db, string id, string? bedStatus, string? reason = null)
    {
        var repo = new EnvironmentRepository(db.Factory);
        repo.Upsert(new Environment
        {
            Id = id,
            Name = id,
            RootPath = $"C:\\envs\\{id}",
            ComfyuiLayout = "isolated",
            Status = "stopped",
            BedProfileId = bedStatus is null ? null : "pytorch-2.5.0-cu121-stable",
            BedStatus = bedStatus,
            BedFailedReason = reason,
        });
    }

    [Fact]
    public void ReconcileStaleOnStartup_FlipsInstallingToFailed_LeavesOtherStatesAlone()
    {
        using var db = new TestDb();
        SeedEnv(db, "env-installing", "installing");
        SeedEnv(db, "env-done", "done");
        SeedEnv(db, "env-failed", "failed", reason: "pip 退出码 1");
        SeedEnv(db, "env-null", null);
        var repo = new EnvironmentRepository(db.Factory);

        var stale = BaseEnvInstaller.ReconcileStaleOnStartup(repo);

        Assert.Equal(1, stale);
        Assert.Equal("failed", repo.Get("env-installing")!.BedStatus);
        Assert.Equal("上次未完成", repo.Get("env-installing")!.BedFailedReason);
        Assert.Equal("done", repo.Get("env-done")!.BedStatus);
        Assert.Null(repo.Get("env-done")!.BedFailedReason);
        Assert.Equal("failed", repo.Get("env-failed")!.BedStatus);
        Assert.Equal("pip 退出码 1", repo.Get("env-failed")!.BedFailedReason);
        Assert.Null(repo.Get("env-null")!.BedStatus);
    }

    [Fact]
    public void ReconcileStaleOnStartup_NullEnvRepo_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => BaseEnvInstaller.ReconcileStaleOnStartup(null!));
    }

    [Fact]
    public void ReconcileStaleOnStartup_EmptyDb_ReturnsZero()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);

        var stale = BaseEnvInstaller.ReconcileStaleOnStartup(repo);

        Assert.Equal(0, stale);
    }

    [Fact]
    public void ReconcileStaleOnStartup_AllStale_CountsEach()
    {
        using var db = new TestDb();
        for (var i = 0; i < 5; i++)
        {
            SeedEnv(db, $"env-{i}", "installing");
        }
        var repo = new EnvironmentRepository(db.Factory);

        var stale = BaseEnvInstaller.ReconcileStaleOnStartup(repo);

        Assert.Equal(5, stale);
        for (var i = 0; i < 5; i++)
        {
            var env = repo.Get($"env-{i}")!;
            Assert.Equal("failed", env.BedStatus);
            Assert.Equal("上次未完成", env.BedFailedReason);
        }
    }
}
```

**Step 2: Run tests, verify 4/4 FAIL** (因为 `ReconcileStaleOnStartup` 还不存在)

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvInstallerReconcileTests"
```

Expected: 4 errors `BaseEnvInstaller.ReconcileStaleOnStartup does not exist`

**Step 3: Implement `ReconcileStaleOnStartup`**

Modify `src-wpf/ComfyUI.Manager/Services/BaseEnvInstaller.cs`, append at end of class (right before closing `}`):

```csharp
/// <summary>
/// 启动 reconciliation:把所有 BedStatus == "installing" 的 env 翻成
/// "failed" + BedFailedReason = "上次未完成"。
///
/// WPF 重启后没有跨进程 job 持久化,这些行只能来自:
///   1) 上次 WPF 强杀(任务管理器 / 断电 / OS 重启),pip 进程已死
///   2) 上次 WPF 正常退出但 pip 还在跑(理论上 OnExit 应 cancel + drain,
///      但 v0.6.5.6 之前没这个保证)
/// 不做更细的判断(无法知道 venv 是否真的有 torch),统一标 failed 让
/// 用户重跑,启动按钮 enabled + tooltip 提示 "上次未完成"。
/// </summary>
public static int ReconcileStaleOnStartup(EnvironmentRepository envRepo)
{
    if (envRepo is null) throw new ArgumentNullException(nameof(envRepo));

    var stale = 0;
    foreach (var env in envRepo.ListAll())
    {
        if (env.BedStatus == "installing")
        {
            env.BedStatus = "failed";
            env.BedFailedReason = "上次未完成";
            try
            {
                envRepo.Upsert(env);
                stale++;
            }
            catch
            {
                // 单行写失败不致命,下次启动再翻
            }
        }
    }
    return stale;
}
```

**Step 4: Run tests, verify 4/4 PASS**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvInstallerReconcileTests"
```

**Step 5: Wire `App.OnStartup`**

Modify `src-wpf/ComfyUI.Manager/App.xaml.cs`, in `OnStartup` right after the existing `var envRepo = new EnvironmentRepository(dbFactory);` (line 30) and before `var nodeRepo = new NodeRepository(dbFactory);` (line 31), add:

```csharp
        var dbFactory = new SqliteConnectionFactory();
        var envRepo = new EnvironmentRepository(dbFactory);
        // v0.6.5.8: 启动 reconciliation — 把上次未装完的 "installing" 行翻成
        // "failed" + "上次未完成"。必须先于 MainViewModel.Load(),否则 UI 看到
        // ⏳ 装中 几秒后变 ❌ 闪烁。
        BaseEnvInstaller.ReconcileStaleOnStartup(envRepo);
        var nodeRepo = new NodeRepository(dbFactory);
```

**Step 6: Run full test suite, verify no regression** (reconciliation logic 单元测,App wiring 是手动 smoke)

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```

Expected: 362 + 4 = **366 PASS / 0 FAIL / 1 SKIP**

**Step 7: Commit** `feat(wpf): 启动 reconciliation + App.OnStartup 接入`

```bash
git add src-wpf/ComfyUI.Manager/Services/BaseEnvInstaller.cs \
        src-wpf/ComfyUI.Manager/App.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvInstallerReconcileTests.cs
git commit -m "feat(wpf): 启动 reconciliation + App.OnStartup 接入"
```

---

### Task 3: 全量 verify + 重建 staging

**Files:**
- Modify: none
- Build: `dotnet build` + `dotnet test`
- Publish: `release/staging/ComfyUI Manager/ComfyUI.Manager.exe` self-contained rebuild

**Step 1: 全量 build**

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

Expected: 0 errors / 0 warnings

**Step 2: 全量 test**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```

Expected: **366 PASS / 0 FAIL / 1 SKIP** (基线 358 + 4 installing + 4 reconcile = 366;skip 1 沿用 v0.6.5.7)

**Step 3: 重建 staging**(per `feedback_staging_self_contained.md`)

```bash
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
  -c Release -r win-x64 --self-contained true \
  -o "release/staging/ComfyUI Manager" -v minimal
```

Expected: 0 errors,exe 264 files,时间戳更新

**Step 4: Verify no source change beyond Task 1 + Task 2**(`git status --short`)

```bash
git status --short
```

Expected: working tree clean,只有 `release/staging/ComfyUI Manager/ComfyUI.Manager.exe` 时间戳变动(gitignored)

**Step 5: 无 version bump / 无 release zip / 无 ledger 提交**(per G11 + v0.6.5.6 hotfix 偏好)

```bash
git log -3 --oneline
```

Expected: 看到 Task 1 + Task 2 两个 commit,无 chore(release) commit

**Step 6: 任务完成**

不需要 commit,无 staging gitignored 文件入仓。

---

## Verification

### 单元测试

- WPF: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal` → 期望 **366 PASS / 0 FAIL / 1 SKIP**(基线 358 + 4 installing + 4 reconcile)
- Python: 不涉及(纯 WPF 改动)

### 端到端手动测试(用户 desktop)

1. 双击 `release/staging/ComfyUI Manager/ComfyUI.Manager.exe`
2. 侧栏"环境" → 看所有行 BED 列(若 v0.6.5.7 已经跑过,显示 ✓ / ✗ / ❌)
3. 选一个 env,点"基础环境部署" → dialog 弹出,**立刻**看主列表该行变 `⏳ 装中`
4. **同时** hover 该行"启动"按钮 → tooltip 变 "基础环境安装中,请稍候",按钮变灰
5. **强杀测试**:再起一个 BED,跑到一半用任务管理器关 WPF → 重开 WPF → 该行立刻变 `❌ ... (上次未完成)`(reconciliation 生效,无 ⏳ 闪烁)
6. hover 启动按钮 → tooltip "上次基础环境部署失败:上次未完成;运行可能也失败"
7. 点启动 → 流程正常,不再有"看似成功其实没装"的 ghost 状态

### Risks + Tradeoffs

| 风险 | 缓解 |
|---|---|
| 写 installing 跟终态写之间很短,UI 看 ⏳ 装中 一闪而过 | 设计如此:用户进 BED 进度 dialog,主列表就显示 ⏳ 装中 直到 dialog 关 |
| `FakeBaseEnvInstaller` 整体 override InstallAsync 跳过了基类新逻辑,新写 installing 在老 test 不被覆盖 | 新增 `FakeBaseEnvInstallerPartial` 测试,跟老 test 正交;老 test 继续测 progress emit + 失败 dict,职责清晰 |
| 启动 reconciliation 慢(库里有几万 env?) | 现实不会:env 列表最多几十个;ListAll + foreach + 单行 Upsert 是 SQLite 毫秒级 |
| reconciliation 写入跟 `BaseEnvInstaller` 写入竞态 | WPF 单进程,无竞态 |
| "上次未完成"reason 跟真 pip 失败的 reason 混淆 | BedDisplay 用 `❌ {profile} ({reason})`,会显示 "❌ xxx (上次未完成)" — 用户能看出区别 |

### Critical files to modify

- `src-wpf/ComfyUI.Manager/Services/BaseEnvInstaller.cs`(写 installing + ReconcileStaleOnStartup)
- `src-wpf/ComfyUI.Manager/App.xaml.cs`(OnStartup 调 reconciliation)
- `tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvInstallerInstallingStateTests.cs`(4 测试,新)
- `tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvInstallerReconcileTests.cs`(4 测试,新)

---

## Execution choice

**Recommended: Subagent-Driven Development**
- 2 implementer dispatches (T1 + T2) + 1 final close-out (T3 verify)
- Per-task review (sonnet implementer + sonnet reviewer)
- No whole-branch review needed (小 hotfix,改动隔离清晰)
- Estimated 2 commits on main

(Plan agent left out: scope tightly bounded by spec, no design ambiguity. Skipping a redundant design pass.)

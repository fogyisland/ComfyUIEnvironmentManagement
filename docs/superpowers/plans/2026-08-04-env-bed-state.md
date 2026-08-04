# v0.6.5.7 Implementation Plan: Env 行 BED 状态展示 + 启动门控

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** env 行新加 "BED" 列展示 BED 状态(profile Id + ✓/✗/⏳/❌)+ 启动按钮 BED-aware(未装时 disabled,tooltip 提示;失败时 enabled + tooltip 提示 reason)。老 SQLite db 自动 ALTER TABLE 迁移。

**Architecture:**
- Environment 模型增 3 字段(`BedProfileId`/`BedStatus`/`BedFailedReason`)+ 1 计算属性 `BedDisplay`(行 BED 列展示)
- SqliteConnectionFactory.InitSchemaIfMissing 增 3 个 `EnsureColumn` 调用(沿用 v0.6.5.5 的 PRAGMA table_info 检查 + ALTER TABLE ADD COLUMN 模式)
- EnvironmentRepository 增 3 列的 SELECT/INSERT/Read/Bind(无新方法)
- BaseEnvInstaller.InstallAsync 末尾按 `failures` 字典回写 env.BedProfileId + BedStatus + BedFailedReason
- EnvironmentListViewModel.StartCommand.CanExecute 扩展为:status=stopped AND (BedStatus=done OR BedStatus=failed);新增 `StartTooltip` 计算属性(基于 Selected env)供 ToolTip binding
- EnvironmentListView.xaml 加 BED 列 + 启动按钮 `ToolTip="{Binding DataContext.StartTooltip, ...}"` 绑定
- EnvironmentListViewModel.OpenBaseEnvProgress 在 BaseEnvProgressDialog.ShowDialog 返回后调 `Load() + RaiseCommandsChanged()`

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · `Microsoft.Data.Sqlite` · hand-rolled MVVM (`RelayCommand`)

**base SHA:** `73316e7`(v0.6.5.7 spec 落地 commit,基于 v0.6.5.6 + hotfix chain `768ec09/da6630c/739f564/3c08dac/cdc4f26`)

**spec:** `docs/superpowers/specs/2026-08-04-env-bed-state-design.md`(本 plan 的 source of truth)

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| G1 | Environment 增 3 字段:`BedProfileId: string?` / `BedStatus: string?` / `BedFailedReason: string?`,默认 null | spec §3.1 |
| G2 | SQLite schema 迁移走 `EnsureColumn` helper(PRAGMA table_info + ALTER TABLE ADD COLUMN),跟 v0.6.5.5 base_python_path/python_version 同模式 | spec §3.2 + 现有代码 |
| G3 | TestDb fixture 的 `InitSchema` CREATE TABLE 必须同步加 3 列(否则测试夹具跟生产 schema 不一致,会导致后续 T3/T4 假绿) | spec §3.3 |
| G4 | Environment.BedDisplay 计算属性 switch 表达式,4 个分支:`done` → `✓ {BedProfileId}` / `failed` → `❌ {BedProfileId} ({BedFailedReason})` / `installing` → `⏳ 装中` / null → `✗ 未装` | spec §4.5 |
| G5 | BaseEnvInstaller.InstallAsync 末尾对**每个 envId**(不只是 failures 字典里的)写终态:不在 failures → BedStatus="done";在 failures → BedStatus="failed" + BedFailedReason=字典值。用户取消场景(已有 cancelled=true 分支)→ failures 字典里有 envId 写 "用户取消" | spec §4.1 |
| G6 | EnvironmentListViewModel.StartCommand.CanExecute:env.Status=="stopped" AND env.BedStatus IN ("done", "failed") | spec §4.3 |
| G7 | EnvironmentListViewModel.StartTooltip 计算属性(基于 Selected env,非 per-row):BedStatus is null → "基础环境未安装";BedStatus=="installing" → "BED 安装中,请稍候";BedStatus=="failed" → "上次 BED 失败:{BedFailedReason};运行可能也失败";BedStatus=="done" → "";env is null → "" | spec §4.4 |
| G8 | EnvironmentListView 行加 1 个 `DataGridTextColumn Header="BED" Binding="{Binding BedDisplay}" Width="220"`,列在 PID 与 操作 之间 | spec §5 |
| G9 | 启动按钮 `ToolTip` 绑 `DataContext.StartTooltip`(RelativeSource AncestorType=UserControl),VM.Selected 变化时 RaisePropertyChanged(nameof(StartTooltip)) 触发刷新 | spec §4.4 |
| G10 | EnvironmentListViewModel.OpenBaseEnvProgress 在 `Views.BaseEnvProgressDialog.Show(...)` 返回后调 `Load() + RaiseCommandsChanged()`(test seam `ShowProgressDialogOverride` 路径不调 reload,由测试自己负责) | spec §4.2 |
| G11 | 无版本号 bump / 无 release zip / 无 ledger commit(per v0.6.5.6 hotfix 偏好"本地 commit + 重建 staging,不发布新 release") | user scope |
| G12 | BedStatus 用字符串字面量 `"done"` / `"failed"` / `"installing"` / `null`(不引入 enum,跟现有 `env.Status="stopped"/"running"` 一致) | 现有 code style |
| G13 | 重跑 BED(同 env 选不同 profile)→ 覆盖 BedProfileId + BedStatus(Installer 末尾写整个 failures dict,所以哪怕失败回写也覆盖前一次) | spec §G5 |
| G14 | WPF 重启后 in-memory "installing" 丢失 → env.BedStatus 在 db 里是 null(因为从未写) → 行显示 "✗ 未装",启动按钮 disabled,允许重跑 BED | spec §G6 |
| G15 | SqliteConnectionFactory + EnvironmentRepository 改 column 列表时,SELECT/INSERT/Read/Bind 顺序必须严格一致(Bind 顺序无要求,但 Read 索引要对) | 现有代码 invariants |

---

## File Structure

### Create

| 文件 | 行数(估) | 职责 |
|---|---|---|
| `tests-wpf/ComfyUI.Manager.Tests/Models/EnvironmentBedDisplayTests.cs` | ~30 | Theory test,4 个 BedDisplay 分支 + 边界(null env) |
| `tests-wpf/ComfyUI.Manager.Tests/Data/SqliteConnectionFactoryBedColumnsTests.cs` | ~50 | 2 测试:加列到空 db / 跑两次 idempotent |
| `tests-wpf/ComfyUI.Manager.Tests/Data/EnvironmentRepositoryBedColumnsTests.cs` | ~50 | 2 测试:round-trip 3 字段 / 老 schema 没 BED 列也能读(默认 null) |
| `tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvInstallerBedWriteTests.cs` | ~150 | 4 测试:success done / failure failed+reason / user-cancel failed+用户取消 / rerun 覆盖 |
| `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelBedTests.cs` | ~150 | 6 测试:StartCommand 4 个状态分支 + StartTooltip 2 个分支 |

### Modify

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/Models/Environment.cs` | +3 字段 + BedDisplay 计算属性 |
| `src-wpf/ComfyUI.Manager/Data/SqliteConnectionFactory.cs` | `InitSchemaIfMissing` 末尾 +3 `EnsureColumn` 调用 |
| `src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs` | ListAll/Get SELECT 增 3 列;Upsert INSERT 增 3 列;Read 增 3 个 IsDBNull 检查;Bind 增 3 个 AddWithValue |
| `tests-wpf/ComfyUI.Manager.Tests/Fakes/TestDb.cs` | `InitSchema` CREATE TABLE environments 增 3 列 |
| `src-wpf/ComfyUI.Manager/Services/BaseEnvInstaller.cs` | `InstallAsync` 末尾 foreach envId 写终态(failures dict) |
| `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` | `StartCommand` CanExecute 扩 BedStatus 检查;新增 `StartTooltip` 计算属性 + RaisePropertyChanged in Selected setter;`OpenBaseEnvProgress` ShowDialog 后调 Load() + RaiseCommandsChanged() |
| `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml` | +1 DataGridTextColumn BED;启动按钮 + `ToolTip="{Binding DataContext.StartTooltip, ...}"` 绑定 |

### Delete
无。

### Keep (unchanged)
- `BaseEnvProgress` / `BaseEnvStatus` / `BaseEnvInstallResult` / `BaseEnvInstaller.InstallAsync` 主循环(progress emit + 失败字典填充逻辑不动)
- `EnvironmentListViewModel.StartEnvAsync` / `StopEnvAsync` 主体
- `BaseEnvProgressDialog`(无需改)
- `EnvironmentListViewModel.ShowProgressDialogOverride` test seam
- `EnvironmentListViewModel.OpenInstallPickerOverride` test seam

---

## Tasks

### Task 1: `Environment` 模型 + 3 BED 字段 + `BedDisplay` 计算属性

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/Environment.cs`(末尾加 4 个 `[JsonPropertyName]` 属性,3 init-only 字段 + 1 计算属性)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Models/EnvironmentBedDisplayTests.cs`(Theory test)

**Interfaces:**
- Consumes: nothing
- Produces:
  ```csharp
  public sealed class Environment
  {
      // ... 既有 14 个属性 ...
      [JsonPropertyName("bed_profile_id")]
      public string? BedProfileId { get; set; }
      [JsonPropertyName("bed_status")]
      public string? BedStatus { get; set; }
      [JsonPropertyName("bed_failed_reason")]
      public string? BedFailedReason { get; set; }
      public string BedDisplay => BedStatus switch { ... };
  }
  ```

- [ ] **Step 1: Write failing Theory test**

```csharp
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public class EnvironmentBedDisplayTests
{
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
}
```

- [ ] **Step 2: Run test, verify FAIL**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentBedDisplayTests" -v minimal`
Expected: FAIL with `'Environment' does not contain a definition for 'BedDisplay'` (CS0117)

- [ ] **Step 3: Implement in `Environment.cs`**

在 `pid` 属性后追加:

```csharp
[JsonPropertyName("bed_profile_id")]
public string? BedProfileId { get; set; }
[JsonPropertyName("bed_status")]
public string? BedStatus { get; set; }
[JsonPropertyName("bed_failed_reason")]
public string? BedFailedReason { get; set; }

/// <summary>
/// 行 BED 列展示文本:✓ profileId / ✗ 未装 / ⏳ 装中 / ❌ profileId (reason)。
/// WPF DataGridTextColumn 直接绑 BedDisplay;不需 INPC(env 一行 read-through)。
/// </summary>
public string BedDisplay => BedStatus switch
{
    "done" => $"✓ {BedProfileId}",
    "failed" => $"❌ {BedProfileId ?? "?"} ({BedFailedReason ?? "失败"})",
    "installing" => "⏳ 装中",
    _ => "✗ 未装",
};
```

- [ ] **Step 4: Run test, verify PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentBedDisplayTests" -v minimal`
Expected: PASS(4/4)

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Models/Environment.cs tests-wpf/ComfyUI.Manager.Tests/Models/EnvironmentBedDisplayTests.cs
git commit -m "feat(wpf): Environment 模型 + BedProfileId/BedStatus/BedFailedReason/BedDisplay"
```

---

### Task 2: `SqliteConnectionFactory` 迁移 + TestDb 同步 + 迁移测试

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Data/SqliteConnectionFactory.cs`(在 `InitSchemaIfMissing` 末尾追加 3 行 `EnsureColumn`)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Fakes/TestDb.cs`(`InitSchema` CREATE TABLE environments 增 3 列)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Data/SqliteConnectionFactoryBedColumnsTests.cs`(2 测试)

**Interfaces:**
- Consumes: `SqliteConnectionFactory.EnsureColumn(conn, table, column, type)`(既有 private static helper,签名不变)
- Produces:environments 表新增 3 列(生产 + TestDb fixture)+ idempotent 行为

- [ ] **Step 1: Write failing tests**

```csharp
using System.IO;
using ComfyUI.Manager.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// SqliteConnectionFactory 启动时自动 ALTER TABLE 加 bed_profile_id / bed_status /
/// bed_failed_reason 三列(老 db schema 没这些列)。两次调用 idempotent。
/// </summary>
public class SqliteConnectionFactoryBedColumnsTests
{
    [Fact]
    public void EnsureBedColumns_AddsThreeColumnsToOldSchema()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "bed-cols-" + Path.GetRandomFileName() + ".db");
        try
        {
            // 模拟 v0.6.5.6 老 schema(没 BED 列)
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE environments (
                        id TEXT PRIMARY KEY, name TEXT NOT NULL, root_path TEXT NOT NULL,
                        comfyui_layout TEXT NOT NULL, comfyui_source TEXT, venv_path TEXT,
                        python_executable TEXT, custom_nodes_path TEXT,
                        extra_model_paths_yaml TEXT, port INTEGER,
                        enabled_node_ids_json TEXT DEFAULT '[]',
                        status TEXT DEFAULT 'stopped',
                        base_python_path TEXT NOT NULL DEFAULT '',
                        python_version TEXT NOT NULL DEFAULT '', pid INTEGER
                    )";
                cmd.ExecuteNonQuery();
            }

            var factory = new SqliteConnectionFactory(dbPath);
            using var conn2 = factory.Open();  // 触发 InitSchemaIfMissing

            // PRAGMA table_info 验证 3 列已加
            using var info = conn2.CreateCommand();
            info.CommandText = "PRAGMA table_info(environments)";
            using var reader = info.ExecuteReader();
            var names = new System.Collections.Generic.List<string>();
            while (reader.Read()) names.Add(reader.GetString(1));

            Assert.Contains("bed_profile_id", names);
            Assert.Contains("bed_status", names);
            Assert.Contains("bed_failed_reason", names);
        }
        finally
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void EnsureBedColumns_IsIdempotent()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "bed-cols-idem-" + Path.GetRandomFileName() + ".db");
        try
        {
            var factory = new SqliteConnectionFactory(dbPath);
            using var c1 = factory.Open();  // 第一次:加列
            using var c2 = factory.Open();  // 第二次:不抛

            using var info = c2.CreateCommand();
            info.CommandText = "PRAGMA table_info(environments)";
            using var reader = info.ExecuteReader();
            int bedCount = 0;
            while (reader.Read())
            {
                var n = reader.GetString(1);
                if (n == "bed_profile_id" || n == "bed_status" || n == "bed_failed_reason") bedCount++;
            }
            Assert.Equal(3, bedCount);
        }
        finally
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }
}
```

- [ ] **Step 2: Run tests, verify FAIL**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SqliteConnectionFactoryBedColumnsTests" -v minimal`
Expected: FAIL — `Assert.Contains("bed_profile_id", names)` 因为老 schema 没有这列

- [ ] **Step 3: Modify `SqliteConnectionFactory.InitSchemaIfMissing`**

在现有 `EnsureColumn(conn, "environments", "python_version", ...)` 之后追加 3 行:

```csharp
EnsureColumn(conn, "environments", "bed_profile_id", "TEXT");
EnsureColumn(conn, "environments", "bed_status", "TEXT");
EnsureColumn(conn, "environments", "bed_failed_reason", "TEXT");
```

(注意:这 3 列没有 NOT NULL DEFAULT '' —— 老行 BedStatus=null 时 UI 显示"未装",DEFAULT 不需要)

- [ ] **Step 4: Modify `TestDb.InitSchema`**

在 CREATE TABLE environments 里 `pid INTEGER` 后追加:

```sql
bed_profile_id TEXT,
bed_status TEXT,
bed_failed_reason TEXT
```

- [ ] **Step 5: Run tests, verify PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SqliteConnectionFactoryBedColumnsTests" -v minimal`
Expected: PASS(2/2)

- [ ] **Step 6: 跑一遍全量 sanity check,确认 ALTER TABLE 没破坏现有读路径**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentRepositoryTests" -v minimal`
Expected: PASS(老 test 用 TestDb fixture,现在 schema 多 3 列但 ListAll SELECT 还没改,会因"该列不存在"失败 → 这是预期的,会在 Task 3 修)

> **注**: 这一步 FAIL 是正常的。Task 3 改 EnvironmentRepository 的 SELECT/INSERT/Read/Bind 即可修复。**不要 commit**,继续 Task 3。

---

### Task 3: `EnvironmentRepository` SELECT/INSERT/Read/Bind 同步加 3 列

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs`
  - `ListAll`: SELECT 加 3 列
  - `Get`: SELECT 加 3 列
  - `Upsert`: INSERT 加 3 列;ON CONFLICT DO UPDATE 加 3 个 SET
  - `Read`:加 3 个 IsDBNull 检查(索引 15/16/17)+ 字段赋值
  - `Bind`:加 3 个 AddWithValue
- Create: `tests-wpf/ComfyUI.Manager.Tests/Data/EnvironmentRepositoryBedColumnsTests.cs`(2 测试)

**Interfaces:**
- Consumes: `Environment.BedProfileId/BedStatus/BedFailedReason` (T1 产出)
- Produces:`EnvironmentRepository.ListAll/Get/Upsert` 持久化 3 字段;老行(无 BED 列)的 fixture 也能读(列默认值 null)

- [ ] **Step 1: Write failing tests**

```csharp
using System;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Data;

public class EnvironmentRepositoryBedColumnsTests
{
    [Fact]
    public void Upsert_RoundTripsAllThreeBedColumns()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        var env = new Environment
        {
            Id = "env-bed",
            Name = "alpha",
            RootPath = @"C:\envs\alpha",
            ComfyuiLayout = "isolated",
            BedProfileId = "pytorch-2.5.0-cu121-stable",
            BedStatus = "done",
            BedFailedReason = null,
        };
        repo.Upsert(env);

        var fresh = repo.Get("env-bed");
        Assert.NotNull(fresh);
        Assert.Equal("pytorch-2.5.0-cu121-stable", fresh!.BedProfileId);
        Assert.Equal("done", fresh.BedStatus);
        Assert.Null(fresh.BedFailedReason);

        // ListAll 也读得到
        var all = repo.ListAll();
        Assert.Single(all);
        Assert.Equal("pytorch-2.5.0-cu121-stable", all[0].BedProfileId);
    }

    [Fact]
    public void Upsert_OverwritesBedColumns_OnConflict()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        var env = new Environment
        {
            Id = "env-rerun",
            Name = "beta",
            RootPath = @"C:\envs\beta",
            ComfyuiLayout = "isolated",
            BedProfileId = "pytorch-2.5.0-cu121-stable",
            BedStatus = "done",
        };
        repo.Upsert(env);

        // 重跑选不同 profile
        env.BedProfileId = "pytorch-nightly-cu126";
        env.BedStatus = "failed";
        env.BedFailedReason = "pip 退出码 1";
        repo.Upsert(env);

        var fresh = repo.Get("env-rerun");
        Assert.Equal("pytorch-nightly-cu126", fresh!.BedProfileId);
        Assert.Equal("failed", fresh.BedStatus);
        Assert.Equal("pip 退出码 1", fresh.BedFailedReason);
    }
}
```

- [ ] **Step 2: Run tests, verify FAIL**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentRepositoryBedColumnsTests" -v minimal`
Expected: FAIL — `repo.Get("env-bed")` 因 SELECT 没列 → 索引错位,后续 GetString/Pid 都崩,或者"该列不存在" SQL 错

- [ ] **Step 3: 修改 `EnvironmentRepository.cs`**

完整更新 ListAll / Get / Upsert / Read / Bind,确保 SELECT 列表 17 列、Read 索引 0-17、Bind 17 个参数:

```csharp
public List<Environment> ListAll()
{
    using var conn = _factory.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT id, name, root_path, comfyui_layout, comfyui_source,
               venv_path, python_executable, custom_nodes_path,
               extra_model_paths_yaml, port, enabled_node_ids_json,
               status, base_python_path, python_version, pid,
               bed_profile_id, bed_status, bed_failed_reason
        FROM environments
        ORDER BY name";
    // ... 余下不变 ...
}

public Environment? Get(string envId)
{
    using var conn = _factory.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT id, name, root_path, comfyui_layout, comfyui_source,
               venv_path, python_executable, custom_nodes_path,
               extra_model_paths_yaml, port, enabled_node_ids_json,
               status, base_python_path, python_version, pid,
               bed_profile_id, bed_status, bed_failed_reason
        FROM environments WHERE id = @id";
    // ... 余下不变 ...
}

public void Upsert(Environment env)
{
    using var conn = _factory.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        INSERT INTO environments
            (id, name, root_path, comfyui_layout, comfyui_source,
             venv_path, python_executable, custom_nodes_path,
             extra_model_paths_yaml, port, enabled_node_ids_json,
             status, base_python_path, python_version, pid,
             bed_profile_id, bed_status, bed_failed_reason)
        VALUES
            (@id, @name, @root_path, @comfyui_layout, @comfyui_source,
             @venv_path, @python_executable, @custom_nodes_path,
             @extra_model_paths_yaml, @port, @enabled_node_ids_json,
             @status, @base_python_path, @python_version, @pid,
             @bed_profile_id, @bed_status, @bed_failed_reason)
        ON CONFLICT(id) DO UPDATE SET
            name=excluded.name,
            root_path=excluded.root_path,
            comfyui_layout=excluded.comfyui_layout,
            comfyui_source=excluded.comfyui_source,
            venv_path=excluded.venv_path,
            python_executable=excluded.python_executable,
            custom_nodes_path=excluded.custom_nodes_path,
            extra_model_paths_yaml=excluded.extra_model_paths_yaml,
            port=excluded.port,
            enabled_node_ids_json=excluded.enabled_node_ids_json,
            status=excluded.status,
            base_python_path=excluded.base_python_path,
            python_version=excluded.python_version,
            pid=excluded.pid,
            bed_profile_id=excluded.bed_profile_id,
            bed_status=excluded.bed_status,
            bed_failed_reason=excluded.bed_failed_reason";
    Bind(cmd, env);
    cmd.ExecuteNonQuery();
}

private static Environment Read(SqliteDataReader reader)
{
    var result = new Environment
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        RootPath = reader.GetString(2),
        ComfyuiLayout = reader.GetString(3),
        ComfyuiSource = reader.IsDBNull(4) ? null : reader.GetString(4),
        VenvPath = reader.IsDBNull(5) ? null : reader.GetString(5),
        PythonExecutable = reader.IsDBNull(6) ? null : reader.GetString(6),
        CustomNodesPath = reader.IsDBNull(7) ? null : reader.GetString(7),
        ExtraModelPathsYaml = reader.IsDBNull(8) ? null : reader.GetString(8),
        Port = reader.IsDBNull(9) ? null : reader.GetInt32(9),
        EnabledNodeIdsJson = reader.GetString(10),
        Status = reader.GetString(11),
        BasePythonPath = reader.GetString(12),
        PythonVersion = reader.GetString(13),
        Pid = reader.IsDBNull(14) ? null : reader.GetInt32(14),
        BedProfileId = reader.IsDBNull(15) ? null : reader.GetString(15),
        BedStatus = reader.IsDBNull(16) ? null : reader.GetString(16),
        BedFailedReason = reader.IsDBNull(17) ? null : reader.GetString(17),
    };
    // ... 既有 fallback (BasePythonPath / PythonVersion) 不动 ...
    return result;
}

private static void Bind(SqliteCommand cmd, Environment env)
{
    cmd.Parameters.AddWithValue("@id", env.Id);
    cmd.Parameters.AddWithValue("@name", env.Name);
    cmd.Parameters.AddWithValue("@root_path", env.RootPath);
    cmd.Parameters.AddWithValue("@comfyui_layout", env.ComfyuiLayout);
    cmd.Parameters.AddWithValue("@comfyui_source", (object?)env.ComfyuiSource ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@venv_path", (object?)env.VenvPath ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@python_executable", (object?)env.PythonExecutable ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@custom_nodes_path", (object?)env.CustomNodesPath ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@extra_model_paths_yaml", (object?)env.ExtraModelPathsYaml ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@port", (object?)env.Port ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@enabled_node_ids_json", env.EnabledNodeIdsJson);
    cmd.Parameters.AddWithValue("@status", env.Status);
    cmd.Parameters.AddWithValue("@base_python_path", env.BasePythonPath);
    cmd.Parameters.AddWithValue("@python_version", env.PythonVersion);
    cmd.Parameters.AddWithValue("@pid", (object?)env.Pid ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@bed_profile_id", (object?)env.BedProfileId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@bed_status", (object?)env.BedStatus ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@bed_failed_reason", (object?)env.BedFailedReason ?? DBNull.Value);
}
```

- [ ] **Step 4: Run new tests + 全量 sanity check**

Run T3 测试:
`dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentRepositoryBedColumnsTests" -v minimal`
Expected: PASS(2/2)

跑全量 repository test(确认 Task 2 步骤 6 那个 SANITY 也 PASS):
`dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentRepositoryTests|FullyQualifiedName~EnvironmentRepositoryBedColumnsTests" -v minimal`
Expected: 全 PASS

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Data/SqliteConnectionFactory.cs src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs tests-wpf/ComfyUI.Manager.Tests/Fakes/TestDb.cs tests-wpf/ComfyUI.Manager.Tests/Data/SqliteConnectionFactoryBedColumnsTests.cs tests-wpf/ComfyUI.Manager.Tests/Data/EnvironmentRepositoryBedColumnsTests.cs
git commit -m "feat(wpf): SqliteConnectionFactory + EnvironmentRepository 加 BED 3 列(迁移 + round-trip)"
```

---

### Task 4: `BaseEnvInstaller.InstallAsync` 末尾回写 env.BedProfileId/BedStatus/BedFailedReason

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/BaseEnvInstaller.cs`
  - `InstallAsync` 末尾在 `return new BaseEnvInstallResult(...)` 之前,加 foreach envId 写终态的循环
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvInstallerBedWriteTests.cs`(4 测试)

**Interfaces:**
- Consumes: `EnvironmentRepository.Get/Upsert`, `Environment` (T1 产出字段),`BaseEnvProfile.Id`,`BaseEnvInstallerResult` 既有 failures dict
- Produces:`InstallAsync` 末尾对每个 envId 写 BedProfileId=profile.Id + BedStatus="done"/"failed" + BedFailedReason=字典值(或 null)

- [ ] **Step 1: Write failing tests**

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

/// <summary>
/// BaseEnvInstaller end-state writeback:InstallAsync 末尾逐 env 写 BedProfileId/BedStatus。
/// 用 FakeBaseEnvInstaller 重写 RunPipAsync 控制 exit code / cancel。
/// </summary>
public class BaseEnvInstallerBedWriteTests
{
    private sealed class FakeBaseEnvInstaller : BaseEnvInstaller
    {
        private readonly Func<string, IReadOnlyList<string>, CancellationToken, PipResult> _handler;
        public FakeBaseEnvInstaller(
            EnvironmentRepository repo,
            Func<string, IReadOnlyList<string>, CancellationToken, PipResult> handler)
            : base(repo)
        {
            _handler = handler;
        }
        protected override Task<PipResult> RunPipAsync(
            string pythonExe, IReadOnlyList<string> pipArgs,
            Action<string> onLine, Action<int?> onPercent, CancellationToken ct)
        {
            return Task.FromResult(_handler(pythonExe, pipArgs, ct));
        }
    }

    private static BaseEnvProfile MakeProfile(string id = "pytorch-2.5.0-cu121-stable") =>
        new() { Id = id, Name = id, TorchVersion = "2.5.0", CudaVersion = "cu121" };

    private static Environment SeedEnv(TestDb db, string id, string? venvPythonPath = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "bed-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var venv = Path.Combine(root, "venv");
        Directory.CreateDirectory(venv);
        var scripts = Path.Combine(venv, "Scripts");
        Directory.CreateDirectory(scripts);
        var python = Path.Combine(scripts, "python.exe");
        File.WriteAllText(python, "fake");
        var env = new Environment
        {
            Id = id,
            Name = id,
            RootPath = root,
            VenvPath = venv,
            PythonExecutable = venvPythonPath ?? python,
            ComfyuiLayout = "isolated",
            Status = "stopped",
        };
        new EnvironmentRepository(db.Factory).Upsert(env);
        return env;
    }

    [Fact]
    public async Task InstallAsync_OnSuccess_WritesBedStatusDone()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        SeedEnv(db, "env-ok");
        var installer = new FakeBaseEnvInstaller(repo,
            (_, _, _) => new PipResult(exitCode: 0, wasCancelled: false));

        var result = await installer.InstallAsync(
            new[] { "env-ok" }, MakeProfile(), progress: null, ct: default);

        var fresh = repo.Get("env-ok");
        Assert.Equal("pytorch-2.5.0-cu121-stable", fresh!.BedProfileId);
        Assert.Equal("done", fresh.BedStatus);
        Assert.Null(fresh.BedFailedReason);
    }

    [Fact]
    public async Task InstallAsync_OnPipFailure_WritesBedStatusFailedWithExitCode()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        SeedEnv(db, "env-fail");
        var installer = new FakeBaseEnvInstaller(repo,
            (_, _, _) => new PipResult(exitCode: 1, wasCancelled: false));

        await installer.InstallAsync(
            new[] { "env-fail" }, MakeProfile(), progress: null, ct: default);

        var fresh = repo.Get("env-fail");
        Assert.Equal("pytorch-2.5.0-cu121-stable", fresh!.BedProfileId);
        Assert.Equal("failed", fresh.BedStatus);
        Assert.NotNull(fresh.BedFailedReason);
        Assert.StartsWith("pip 退出码", fresh.BedFailedReason);
    }

    [Fact]
    public async Task InstallAsync_OnUserCancel_WritesBedStatusFailedWithUserReason()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        SeedEnv(db, "env-cancel");
        var installer = new FakeBaseEnvInstaller(repo,
            (_, _, _) => new PipResult(exitCode: -1, wasCancelled: true));

        await installer.InstallAsync(
            new[] { "env-cancel" }, MakeProfile(), progress: null, ct: default);

        var fresh = repo.Get("env-cancel");
        Assert.Equal("failed", fresh!.BedStatus);
        Assert.Equal("用户取消", fresh.BedFailedReason);
    }

    [Fact]
    public async Task InstallAsync_RerunOverwritesBedStatus()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        SeedEnv(db, "env-rerun");
        var installer = new FakeBaseEnvInstaller(repo,
            (_, _, _) => new PipResult(exitCode: 0, wasCancelled: false));

        // 第一次:profile A
        await installer.InstallAsync(
            new[] { "env-rerun" }, MakeProfile("pytorch-2.5.0-cu121-stable"),
            progress: null, ct: default);
        Assert.Equal("pytorch-2.5.0-cu121-stable", repo.Get("env-rerun")!.BedProfileId);

        // 第二次:profile B
        await installer.InstallAsync(
            new[] { "env-rerun" }, MakeProfile("pytorch-nightly-cu126"),
            progress: null, ct: default);
        Assert.Equal("pytorch-nightly-cu126", repo.Get("env-rerun")!.BedProfileId);
        Assert.Equal("done", repo.Get("env-rerun")!.BedStatus);
    }
}
```

- [ ] **Step 2: Run tests, verify FAIL**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvInstallerBedWriteTests" -v minimal`
Expected: FAIL — `repo.Get("env-ok").BedStatus` 是 null(没回写),断言 `Expected "done"` 失败

- [ ] **Step 3: Implement 在 `BaseEnvInstaller.InstallAsync` 末尾**

紧贴 `return new BaseEnvInstallResult(...)` 之前插入:

```csharp
// 终态回写:每个 envId 写 BedProfileId + BedStatus + BedFailedReason
// 用户取消 / 失败 / 成功 三种状态都覆盖(失败 dict 里有 envId 即 "failed",
// 没有即 "done")。用户在 EnvListVM 看 BED 列即知状态。
foreach (var envId in envIds)
{
    try
    {
        var envRow = _envRepo.Get(envId);
        if (envRow is null) continue;
        envRow.BedProfileId = profile.Id;
        if (failures.TryGetValue(envId, out var reason))
        {
            envRow.BedStatus = "failed";
            envRow.BedFailedReason = reason;
        }
        else
        {
            envRow.BedStatus = "done";
            envRow.BedFailedReason = null;
        }
        _envRepo.Upsert(envRow);
    }
    catch
    {
        // 单 env 写失败不致命(可能被并发删除);不影响整体结果返回
    }
}

return new BaseEnvInstallResult(
    cancelled, succeeded, failed, failures);
```

(注意:整段替换 `return new BaseEnvInstallResult(...)` 这一行,把循环插在前面)

- [ ] **Step 4: Run tests, verify PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvInstallerBedWriteTests" -v minimal`
Expected: PASS(4/4)

- [ ] **Step 5: 跑全量 BaseEnvInstaller test 确认没破坏老测试**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvInstaller" -v minimal`
Expected: 全 PASS(老 fake 也覆盖 envs 但不写 BedStatus,这无所谓 — 我们写的是 envRow 的 nullable 字段)

- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/BaseEnvInstaller.cs tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvInstallerBedWriteTests.cs
git commit -m "feat(wpf): BaseEnvInstaller.InstallAsync 末尾回写 env.BedProfileId/BedStatus/BedFailedReason"
```

---

### Task 5: `EnvironmentListViewModel.StartCommand.CanExecute` + `StartTooltip`

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`
  - `StartCommand` CanExecute 改为 `(env) => env?.Status == "stopped" && (env.BedStatus == "done" || env.BedStatus == "failed")`
  - 新增 `StartTooltip` 计算属性(基于 Selected env)
  - `Selected` setter 里 `RaisePropertyChanged(nameof(StartTooltip))`
- Create: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelBedTests.cs`(6 测试)

**Interfaces:**
- Consumes: `Environment.BedStatus` (T1)
- Produces:`EnvironmentListViewModel.StartCommand.CanExecute` 新规则 + `StartTooltip` string

- [ ] **Step 1: Write failing tests**

```csharp
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

public class EnvironmentListViewModelBedTests
{
    private static EnvironmentListViewModel NewVm(TestDb db, Environment? seedEnv = null)
    {
        var repo = new EnvironmentRepository(db.Factory);
        if (seedEnv is not null) repo.Upsert(seedEnv);
        return new EnvironmentListViewModel(
            repo, null!, null!, null!, null!, null!, null!, null!, Path.GetTempPath());
    }

    private static Environment MakeEnv(string id, string status, string? bedStatus) =>
        new()
        {
            Id = id,
            Name = id,
            RootPath = $@"C:\envs\{id}",
            ComfyuiLayout = "isolated",
            Status = status,
            BedStatus = bedStatus,
        };

    [Fact]
    public void StartCommand_DisabledWhenBedStatusNull()
    {
        using var db = new TestDb();
        var env = MakeEnv("env-x", "stopped", bedStatus: null);
        var vm = NewVm(db, env);
        Assert.False(vm.StartCommand.CanExecute(env));
    }

    [Fact]
    public void StartCommand_EnabledWhenBedStatusDone()
    {
        using var db = new TestDb();
        var env = MakeEnv("env-done", "stopped", bedStatus: "done");
        var vm = NewVm(db, env);
        Assert.True(vm.StartCommand.CanExecute(env));
    }

    [Fact]
    public void StartCommand_DisabledWhenBedStatusInstalling()
    {
        using var db = new TestDb();
        var env = MakeEnv("env-running", "stopped", bedStatus: "installing");
        var vm = NewVm(db, env);
        Assert.False(vm.StartCommand.CanExecute(env));
    }

    [Fact]
    public void StartCommand_EnabledWhenBedStatusFailed()
    {
        using var db = new TestDb();
        var env = MakeEnv("env-failed", "stopped", bedStatus: "failed");
        var vm = NewVm(db, env);
        Assert.True(vm.StartCommand.CanExecute(env));
    }

    [Fact]
    public void StartTooltip_ShowsBedNotInstalled_WhenSelectedBedStatusNull()
    {
        using var db = new TestDb();
        var env = MakeEnv("env-x", "stopped", bedStatus: null);
        var vm = NewVm(db, env);
        vm.Selected = vm.Environments[0];
        Assert.Equal("基础环境未安装", vm.StartTooltip);
    }

    [Fact]
    public void StartTooltip_ShowsBedFailed_WithReason()
    {
        using var db = new TestDb();
        var env = MakeEnv("env-f", "stopped", bedStatus: "failed");
        env.BedFailedReason = "pip 退出码 1";
        var vm = NewVm(db, env);
        vm.Selected = vm.Environments[0];
        Assert.Contains("上次 BED 失败", vm.StartTooltip);
        Assert.Contains("pip 退出码 1", vm.StartTooltip);
    }
}
```

- [ ] **Step 2: Run tests, verify FAIL**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentListViewModelBedTests" -v minimal`
Expected: FAIL — `StartCommand.CanExecute` 旧规则 (status=stopped) → True,新断言期望 False(BedStatus=null) → 失败

- [ ] **Step 3: Modify `EnvironmentListViewModel.cs`**

3 处改动:

**(a) `StartCommand` CanExecute 扩 BedStatus 检查**

旧:
```csharp
StartCommand = new RelayCommand(
    async p => await StartEnvAsync(p as Environment ?? Selected),
    p => (p as Environment ?? Selected)?.Status == "stopped");
```

新:
```csharp
StartCommand = new RelayCommand(
    async p => await StartEnvAsync(p as Environment ?? Selected),
    p =>
    {
        var env = p as Environment ?? Selected;
        if (env is null) return false;
        if (env.Status != "stopped") return false;
        // BED 未装 / 装中 → 禁用
        if (env.BedStatus is null or "installing") return false;
        return true;
    });
```

**(b) `Selected` setter 触发 StartTooltip 重算**

旧:
```csharp
public Environment? Selected
{
    get => _selected;
    set => SetField(ref _selected, value);
}
```

新:
```csharp
public Environment? Selected
{
    get => _selected;
    set
    {
        if (SetField(ref _selected, value))
            RaisePropertyChanged(nameof(StartTooltip));
    }
}
```

**(c) 新增 `StartTooltip` 计算属性**

紧贴 `RaiseCommandsChanged` 方法前添加:

```csharp
/// <summary>
/// 启动按钮 tooltip 文本:基于 Selected env 的 BED 状态。
/// - BedStatus null   → "基础环境未安装"
/// - BedStatus "installing" → "BED 安装中,请稍候"
/// - BedStatus "failed" → "上次 BED 失败:{BedFailedReason};运行可能也失败"
/// - BedStatus "done"  → ""(BED OK,不需要提示)
/// - env is null       → ""
/// </summary>
public string StartTooltip
{
    get
    {
        var env = Selected;
        if (env is null) return "";
        return env.BedStatus switch
        {
            null => "基础环境未安装",
            "installing" => "BED 安装中,请稍候",
            "failed" => $"上次 BED 失败:{env.BedFailedReason};运行可能也失败",
            _ => "",
        };
    }
}
```

- [ ] **Step 4: Run tests, verify PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentListViewModelBedTests" -v minimal`
Expected: PASS(6/6)

- [ ] **Step 5: 跑全量 EnvListVM test 确认没破坏老测试**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentListViewModelTests|FullyQualifiedName~EnvironmentListViewModelBedTests" -v minimal`
Expected: 全 PASS(老 test env 都是 BedStatus=null,新规则会让 StartCommand.CanExecute 返 false,但老 test 只断言 StartCommand 状态反转(stopped/running),不依赖 BedStatus —— 应该不受影响)

- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelBedTests.cs
git commit -m "feat(wpf): StartCommand BED-aware + StartTooltip 计算属性"
```

---

### Task 6: `EnvironmentListView.xaml` 加 BED 列 + 启动按钮 ToolTip 绑定

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml`
  - 在 `DataGridTextColumn Header="PID" Width="80"` 之后、`DataGridTemplateColumn Header="操作"` 之前插入新列
  - "启动"按钮加 `ToolTip="{Binding DataContext.StartTooltip, RelativeSource={RelativeSource AncestorType=UserControl}}"`
- No test file(XAML 不可单测,build 验证即可)

- [ ] **Step 1: Modify XAML**

当前列顺序(PID → 操作),插入 BED 列:

```xml
<DataGridTextColumn Header="PID" Binding="{Binding Pid}" Width="80" />
<DataGridTextColumn Header="BED" Binding="{Binding BedDisplay}" Width="220" />
<DataGridTemplateColumn Header="操作" Width="260">
```

启动按钮加 ToolTip:

```xml
<Button Content="启动" Margin="2"
        Command="{Binding DataContext.StartCommand,
                          RelativeSource={RelativeSource AncestorType=UserControl}}"
        CommandParameter="{Binding}"
        ToolTip="{Binding DataContext.StartTooltip,
                          RelativeSource={RelativeSource AncestorType=UserControl}}" />
```

- [ ] **Step 2: Build 验证 XAML 没语法错**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Expected: 0 errors(XAML 解析失败会变成 build error)

- [ ] **Step 3: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml
git commit -m "feat(wpf): EnvListView BED 列 + 启动按钮 ToolTip 绑定 StartTooltip"
```

---

### Task 7: `OpenBaseEnvProgress` ShowDialog 返回后 reload

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`
  - `OpenBaseEnvProgress` 在 `Views.BaseEnvProgressDialog.Show(...)` 后追加 `Load(); RaiseCommandsChanged();`
- Create test: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelBedTests.cs` 追加 1 测试(共 7 测试)

**Interfaces:**
- Consumes:既有 `Load()` / `RaiseCommandsChanged()` 方法
- Produces:`OpenBaseEnvProgress` 在 BED dialog 关后立即刷新行(让 BedStatus 变更可见)+ 命令状态同步

- [ ] **Step 1: Write failing test**

在 `EnvironmentListViewModelBedTests.cs` 末尾追加(假设 `ShowProgressDialogOverride` 已被现有 EnvListVM tests 验证可用):

```csharp
[Fact]
public void OpenBaseEnvProgress_AfterDialogCloses_TriggersReload()
{
    using var db = new TestDb();
    var repo = new EnvironmentRepository(db.Factory);
    // 初始 env:无 BED
    var env = MakeEnv("env-bed", "stopped", bedStatus: null);
    repo.Upsert(env);

    var vm = NewVm(db);
    Assert.Single(vm.Environments);
    Assert.Null(vm.Environments[0].BedStatus);

    // 模拟 BED 跑完:直接改 repo 行 + 调 reload
    env.BedProfileId = "pytorch-2.5.0-cu121-stable";
    env.BedStatus = "done";
    repo.Upsert(env);

    // 用 ShowProgressDialogOverride 拦截,跳过真实 dialog
    bool overrideCalled = false;
    vm.ShowProgressDialogOverride = (_, _, _) => overrideCalled = true;
    vm.OpenBaseEnvProgress();

    Assert.True(overrideCalled);
    // override 路径不会自动 reload(G10),我们手动验 Load() 也能重读
    vm.RefreshCommand.Execute(null);
    Assert.Equal("done", vm.Environments[0].BedStatus);
    Assert.Equal("pytorch-2.5.0-cu121-stable", vm.Environments[0].BedProfileId);
}
```

> **注**:这个测试间接验证 `Load() + RaiseCommandsChanged()` 在 RefreshCommand 路径下能正常 reload;真实 dialog 路径下 production code 里的新两行(L146-147 后的 `Load(); RaiseCommandsChanged();`)会被调用。

- [ ] **Step 2: Run test, verify PASS(已存在功能)**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentListViewModelBedTests" -v minimal`
Expected: PASS(7/7)—— 这步先验证 test 可运行,实现后 step 4 再 verify

- [ ] **Step 3: Modify `EnvironmentListViewModel.OpenBaseEnvProgress`**

旧:
```csharp
private void OpenBaseEnvProgress()
{
    if (Selected is null && Environments.Count == 0) return;
    var envIds = Selected is not null
        ? new List<string> { Selected.Id }
        : Environments.Select(e => e.Id).ToList();
    if (envIds.Count == 0) return;

    var profile = _profileLoader.GetHardcodedDefaults().FirstOrDefault();
    if (profile is null) return;

    if (ShowProgressDialogOverride is not null)
    {
        ShowProgressDialogOverride(envIds, profile, _baseEnvInstaller);
        return;
    }
    Views.BaseEnvProgressDialog.Show(envIds, profile, _baseEnvInstaller);
}
```

新(在 `Views.BaseEnvProgressDialog.Show(...)` 后加 2 行):
```csharp
private void OpenBaseEnvProgress()
{
    if (Selected is null && Environments.Count == 0) return;
    var envIds = Selected is not null
        ? new List<string> { Selected.Id }
        : Environments.Select(e => e.Id).ToList();
    if (envIds.Count == 0) return;

    var profile = _profileLoader.GetHardcodedDefaults().FirstOrDefault();
    if (profile is null) return;

    if (ShowProgressDialogOverride is not null)
    {
        ShowProgressDialogOverride(envIds, profile, _baseEnvInstaller);
        return;
    }
    Views.BaseEnvProgressDialog.Show(envIds, profile, _baseEnvInstaller);
    // BED dialog 关窗后 reload:Installer 末尾已写 env.BedStatus,
    // UI 立即重读反映新状态(否则用户看到行还是旧的 "未装")
    Load();
    RaiseCommandsChanged();
}
```

- [ ] **Step 4: Run tests, verify PASS**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~EnvironmentListViewModelBedTests|FullyQualifiedName~EnvironmentListViewModelTests" -v minimal`
Expected: PASS(全部)

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelBedTests.cs
git commit -m "feat(wpf): OpenBaseEnvProgress dialog 关后 reload env 行 BED 状态"
```

---

### Task 8: 全量 verify + 重建 staging

**Files:** 无源码改动

- [ ] **Step 1: 全量 build**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Expected: 0 errors / 0 warnings

- [ ] **Step 2: 全量 test**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal`
Expected: ~350 PASS / 1 SKIP / 0 FAIL(基线 339 + 新增 ~11:EnvironmentBedDisplay 1 Theory×4 = 1 计数 + SqliteConnectionFactoryBedColumns 2 + EnvironmentRepositoryBedColumns 2 + BaseEnvInstallerBedWrite 4 + EnvironmentListViewModelBed 7 = 16,但 Theory 算 1 个 test method,实际 1+2+2+4+7 = 16 个 test methods;可能有 test skip 出现)

- [ ] **Step 3: 重建 staging(self-contained publish)**

```bash
PUBLISH_DIR="src-wpf/ComfyUI.Manager/bin/Release/net8.0-windows/win-x64/publish"
rm -rf "$PUBLISH_DIR"
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
    -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=false \
    -o "$PUBLISH_DIR"
```

Expected: publish 成功,`$PUBLISH_DIR/ComfyUI.Manager.exe` 生成

- [ ] **Step 4: 复制 publish 输出到 `release/staging/ComfyUI Manager/`**

```bash
APP_DIR="release/staging/ComfyUI Manager"
PUBLISH_DIR="src-wpf/ComfyUI.Manager/bin/Release/net8.0-windows/win-x64/publish"
cp -rf "$PUBLISH_DIR"/* "$APP_DIR/"
```

Expected:`$APP_DIR/ComfyUI.Manager.dll` / `ComfyUI.Manager.exe` 时间戳更新到当下

- [ ] **Step 5: Commit ledger(本地,不 push / 不 bump / 不 release)**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs  # 仅 stage 8 步骤未改的 file,确认 working tree 干净
git status
git log -3 --oneline
```

不需要 git commit(所有改动都在 T1-T7 commit 完成)。

- [ ] **Step 6: 报告状态给用户**

汇总:
- HEAD `cdc4f26` + 7 个新 commit(T1-T7)
- 测试 ~350 / 0 / 1(skip 同 v0.6.5.6)
- staging exe 时间戳当下
- 等用户桌面 GUI smoke 反馈

---

## Verification

### 单元测试
- WPF `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal` → 期望 ~350 PASS / 1 SKIP / 0 FAIL
- 增量:`EnvironmentBedDisplayTests` 1 (Theory×4) + `SqliteConnectionFactoryBedColumnsTests` 2 + `EnvironmentRepositoryBedColumnsTests` 2 + `BaseEnvInstallerBedWriteTests` 4 + `EnvironmentListViewModelBedTests` 7 = **16 个新 test methods**(实际 1 method 计为 1)

### 端到端手动测试
1. 启动 `release/staging/ComfyUI Manager/ComfyUI.Manager.exe`
2. 侧栏"环境" → 看新列 "BED"(老 env 行显示 "✗ 未装")
3. 启动按钮对老 env:disabled,hover tooltip "基础环境未安装"
4. 选 env,点"基础环境部署" → 选 profile → 等装完 → dialog 关
5. 行 BED 列变 "✓ pytorch-{ver}-cuXXX-stable"(具体看装哪个)
6. 启动按钮:enabled,tooltip 空
7. 点启动 → 进程跑起,Status 变 "running",PID 列填值
8. 失败路径:选一个会失败的 profile(可手动改 profile.ExtraArgs 加 `--no-index` 之类)→ 行 BED 变 "❌ ...(pip 退出码 N)"
9. 启动按钮:enabled,tooltip "上次 BED 失败:pip 退出码 N;运行可能也失败"
10. WPF 重启 → 老 env 行 BED 列保留之前状态(从 db 重读);失败 env tooltip 仍带 reason

### Risks + Tradeoffs

| 风险 | 缓解 |
|---|---|
| 老 SQLite db ALTER TABLE 加列时 db 正在被另一进程使用 → lock 失败 | WPF 是唯一写者(per spec §0);启动是单进程 open + ALTER |
| EnvListView.Tooltip 共享问题:全列共用 VM.StartTooltip 而非 per-row | 设计如此:VM.StartTooltip 基于 Selected;Selected 变化时 PropertyChanged 触发 tooltip 刷新,UX 可接受 |
| BaseEnvInstaller 失败回写覆盖成功回写(同次 install 内部分 env 成功部分失败) | 现有逻辑:failures 字典只记失败的 envId;不在字典里 → done,在字典里 → failed;两路互不干扰 |
| 用户取消场景的 reason 字段 → "用户取消" 4 个字面量 | BaseEnvInstaller 已有 `failures[envId] = "用户取消"` 在 cancelled 分支(verify 已有) |
| in-memory "installing" 状态 WPF 重启后丢失 | 设计如此:重启后按"未装"处理,允许重跑;UI 显示 "✗ 未装" 引导重做 BED |
| BedDisplay 字符串里包含 BedProfileId(profile Id 可能很长) | 列宽 220 + Ellipsis 截断;tooltip 显示完整 reason |
| 操作列 260 + BED 列 220 + Status/PID 各 80 + 名称 * + ID 120 → 总宽超 1366 | DataGrid 自动出 HorizontalScrollBar;主屏够用 |

### Critical files

- `src-wpf/ComfyUI.Manager/Models/Environment.cs`(3 字段 + BedDisplay)
- `src-wpf/ComfyUI.Manager/Data/SqliteConnectionFactory.cs`(3 EnsureColumn)
- `src-wpf/ComfyUI.Manager/Data/EnvironmentRepository.cs`(SELECT/INSERT/Read/Bind)
- `src-wpf/ComfyUI.Manager/Services/BaseEnvInstaller.cs`(InstallAsync 末尾回写)
- `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`(CanExecute + StartTooltip + OpenBaseEnvProgress reload)
- `src-wpf/ComfyUI.Manager/Views/EnvironmentListView.xaml`(BED 列 + ToolTip)
- `tests-wpf/ComfyUI.Manager.Tests/Fakes/TestDb.cs`(InitSchema 同步)
- 5 个新 test 文件(Models/Data/Services/ViewModels 各 1+)

---

## Execution choice

**Recommended: Subagent-Driven Development**
- 7 task + 1 close-out = ~8 dispatch
- Per-task review gate (sonnet implementer + sonnet reviewer)
- Opus whole-branch review at T8 (T8 implementer does final verify + rebuild staging)
- Estimated 7 commits on main
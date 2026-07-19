# v0.6.4 Hotfix — Catalog:Settings 手动刷新 + 分页 + 磁贴/列表切换 + Cache 拆分 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-KIND: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** WPF Catalog 页 (a) 去掉默认的 10 个包,改为 Settings 手动刷新入口 + (b) 分页 + (c) 列表/磁贴视图切换 + (d) cache db 拆到包目录。

**Architecture:**
- `CatalogRefreshService` 抽取共享的 refresh 逻辑,Settings + Catalog 两个 VM 都调它
- `CatalogCacheStore` 窄化版 SqliteConnectionFactory,path = `<AppBaseDir>/data/catalog-cache.db`
- `SqliteConnectionFactory` 改成 user 表 db (`%APPDATA%/state.db`),首次启动自动 `File.Move(catalog.db → state.db)` 一次
- `CatalogViewModel` 加分页/视图模式,refresh 走 service
- `SettingsViewModel` 加 RefreshCatalogCommand,共用同一个 service 实例

**Tech Stack:** .NET 8, WPF, MVVM (hand-rolled `ViewModelBase` + `RelayCommand`), `Microsoft.Data.Sqlite 8.0.0`, `System.Text.Json`, Moq (已有).

**Base SHA:** v0.6.3 tag (`2ac2829`).

**Spec:** `docs/superpowers/specs/2026-07-18-hotfix-catalog-pagination-design.md` (commits `54d8ad4`, `2b504c8`, `903d9bb`).

## Global Constraints

[Copy these verbatim into every task's requirements — they bind the whole hotfix.]

- `CatalogViewMode` 枚举加在 `src-wpf/ComfyUI.Manager/Models/Settings.cs`,值 `List` (默认) / `Tile`
- `Settings.CatalogPageSize` 默认值 `20`
- `Settings.CatalogAutoRefresh` 已存在,本次默认行为变 `false` (deprecate; 不删,后续可恢复)
- Cache db 路径常量:`<AppBaseDir>/data/catalog-cache.db`(`AppContext.BaseDirectory/data/catalog-cache.db`)
- User db 路径常量:`%APPDATA%\ComfyUI-Manager\state.db`(从旧 `catalog.db` 自动 rename)
- 旧 `%APPDATA%/catalog.db` 首次启动 v0.6.4 时自动 rename 成 `state.db` (用 `SqliteConnectionFactory.ResolveDbPath`)
- `CatalogRefreshService` 是 App.xaml.cs 构造的 1 个实例,共享给 2 个 VM 注入
- `CatalogView.xaml` 现有 5 列 DataGrid 绑定 `Author` / `Stars` / `Description` 但 `CatalogEntry` 上没这些 property → **必须修**(T5 阶段加 VM-level 适配属性,或 T7 阶段改 XAML 路径 — 见 T7 决策点)
- `release/*.zip` 已 `.gitignore` — 永不 `git add -A`
- WPF runtime 是 .NET 8 self-contained;不加新 NuGet
- 测试:`dotnet test tests-wpf/ComfyUI.Manager.Tests/` → 期望 82-83/82-83 PASS (旧 74 不掉 + 改 2 + 加 8-9)
- `dotnet build src-wpf/ComfyUI.Manager/` → 0 警告 0 错误
- `pytest tests/test_version_consistency.py` → 3/3 PASS (0.6.3 → 0.6.4)
- 全局 app 不用新增 `using System.IO` 等 namespace,沿用现有文件已有的 imports

## Existing code touch points (read before starting)

- `src-wpf/ComfyUI.Manager/Models/Settings.cs:31-41` — `ExtraPaths` 之后,加 `CatalogViewMode` enum + 2 字段
- `src-wpf/ComfyUI.Manager/Models/CatalogEntry.cs:1-23` — 只有 `Package`,无 `Author`/`Stars`/`Description` → **VM adapter 在 T5 解决**
- `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs:30-83` — 加 2 个常量 + Apply 尾加 2 个 fallback
- `src-wpf/ComfyUI.Manager/Data/SqliteConnectionFactory.cs:34-45` — `ResolveDbPath` 改成 user db; 加旧 db rename 逻辑
- `src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs:19-24` — ctor 改用 `CatalogCacheStore` 而非 `SqliteConnectionFactory`
- `src-wpf/ComfyUI.Manager/Services/CatalogFetcher.cs:34-75` — `FetchAsync` 不变; `CatalogRefreshService` 调它
- `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs:1-143` — 大改 (T5): 新 ctor + 分页 + 视图模式 + refresh 走 service
- `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs:26-30` — ctor 加 `CatalogRefreshService` 参数 + 末尾加 `RefreshCatalogCommand`
- `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml:16-35` — 大改: toggle / DataTemplateSelector / 分页 / 空状态 / 快速刷新按钮
- `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml:79-80` — "查询节点的源" section 末尾加刷新按钮
- `src-wpf/ComfyUI.Manager/App.xaml.cs:54-66` — 构造 `CatalogRefreshService` + 注入 2 个 VM
- `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs:72-80` — `ShowCatalog` 改用 `catalogCacheStore` 而非 `dbFactory`; `ShowSettings` 传 `refreshService`
- `src-wpf/ComfyUI.Manager/Resources/Theme.xaml:1-60` — 加 `CatalogTileTemplate` (WrapPanel + 详细卡片)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelTests.cs:67-213` — 2 tests 改签名 + 改语义 (Ctor 改名, RefreshAsync 改 mock service),加 ~6 个新 tests
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelTests.cs:36-154` — ctor 签名改 + 加 2 个 RefreshCatalogCommand tests
- `tests-wpf/ComfyUI.Manager.Tests/Fakes/TestDb.cs:28-101` — 加 `CatalogCacheStoreTests` 用类似 schema (catalog_cache 已存在,可用)
- **T12 NEW (user request):** Bundle ComfyUI source template at `<repo>/ComfyUI/` via `scripts/fetch_comfyui_template.ps1` (shallow clone from `comfyanonymous/ComfyUI` using bundled portable git). `.gitignore` 加 `ComfyUI/`. `build_release.ps1` 在 git-portable 步骤后插入新步骤。

---

## Task 1: Settings 字段 + Defaults (CatalogViewMode + CatalogPageSize)

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs:31-41`
- Modify: `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs:30-83`
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsDefaultsTests.cs`

**Interfaces:**
- Produces `Settings.CatalogViewMode: CatalogViewMode` (default `List`)
- Produces `Settings.CatalogPageSize: int` (default `20`)
- Produces `static SettingsDefaults.DefaultCatalogViewMode = CatalogViewMode.List`
- Produces `static SettingsDefaults.DefaultCatalogPageSize = 20`
- After `SettingsDefaults.Apply` on a fresh `Settings`, `CatalogViewMode == List` and `CatalogPageSize == 20`

- [ ] **Step 1: Add enum + 2 fields to `Settings.cs`**

Open `src-wpf/ComfyUI.Manager/Models/Settings.cs`. After the `ExtraPaths` block on line 31 and **before** the `QuerySources` block on line 34, add:

```csharp

    // —— Catalog 视图 ——
    [JsonPropertyName("catalog_view_mode")]
    public CatalogViewMode CatalogViewMode { get; set; } = CatalogViewMode.List;
    [JsonPropertyName("catalog_page_size")]
    public int CatalogPageSize { get; set; } = 20;
```

Then at the top of the file (right after `namespace ComfyUI.Manager.Models;` on line 5, before `public class Settings` on line 7), add the enum:

```csharp

public enum CatalogViewMode
{
    List,
    Tile,
}
```

- [ ] **Step 2: Add 2 default constants + Apply tail to `SettingsDefaults.cs`**

In `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs`, after the 4 `Source` constants on lines 36-40 (before `public static void Apply` on line 52), add:

```csharp
    public const CatalogViewMode DefaultCatalogViewMode = CatalogViewMode.List;
    public const int DefaultCatalogPageSize = 20;
```

Then in the `Apply` method body (after line 83 `s.ActiveDownloadSourceName = s.DownloadSources[0].Name;` and before the closing `}` on line 84), add:

```csharp

        // Catalog 视图:默认值兜底(空枚举/0 表示未设 → 默认 List / 20)
        if (s.CatalogPageSize <= 0) s.CatalogPageSize = DefaultCatalogPageSize;
        // CatalogViewMode 枚举:JSON 反序列化时无效值会落到 0 (List),不需要额外 fallback
```

- [ ] **Step 3: Write 3 new tests in `SettingsDefaultsTests.cs`**

Append to `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsDefaultsTests.cs`:

```csharp
    [Fact]
    public void Apply_CatalogPageSize_ZeroOrNegativeGetsDefault()
    {
        var s = new Settings { CatalogPageSize = 0 };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(20, s.CatalogPageSize);
    }

    [Fact]
    public void Apply_CatalogPageSize_NegativeGetsDefault()
    {
        var s = new Settings { CatalogPageSize = -1 };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(20, s.CatalogPageSize);
    }

    [Fact]
    public void Apply_CatalogViewMode_DefaultsToList_OnFreshSettings()
    {
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(CatalogViewMode.List, s.CatalogViewMode);
    }

    [Fact]
    public void Apply_CatalogPageSize_PositiveValuePreserved()
    {
        var s = new Settings { CatalogPageSize = 50 };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(50, s.CatalogPageSize);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsDefaultsTests" -v minimal`
Expected: 17/17 PASS (旧 13 + 新 4).

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Models/Settings.cs \
        src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs \
        tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsDefaultsTests.cs
git commit -m "feat(wpf): add CatalogViewMode enum + CatalogPageSize field + Defaults"
```

---

## Task 2: CatalogCacheStore + SqliteConnectionFactory 拆分 (catalog.db → state.db rename)

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Data/CatalogCacheStore.cs`
- Modify: `src-wpf/ComfyUI.Manager/Data/SqliteConnectionFactory.cs:34-74`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Data/CatalogCacheStoreTests.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Data/SqliteConnectionFactoryTests.cs`

**Interfaces:**
- Produces `CatalogCacheStore.DbPath: string`
- Produces `CatalogCacheStore()` 默认 `AppContext.BaseDirectory/data/catalog-cache.db`
- Produces `CatalogCacheStore(string dbPath)` test 注入
- Produces `CatalogCacheStore.Open(): SqliteConnection`
- Produces `static SqliteConnectionFactory.ResolveDbPath()` 改返 `%APPDATA%/ComfyUI-Manager/state.db`; 旧 `catalog.db` 存在时 rename
- Produces `SqliteConnectionFactory.Open()` 加 `InitSchemaIfMissing` (CREATE TABLE IF NOT EXISTS for 6 user 表)
- 新建 db 文件目录不存在时自动 `Directory.CreateDirectory`

- [ ] **Step 1: Create `Data/CatalogCacheStore.cs`**

Write to `src-wpf/ComfyUI.Manager/Data/CatalogCacheStore.cs`:

```csharp
using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// CatalogCacheStore:窄化的 SQLite 连接工厂,只服务 <c>catalog_cache</c> 表。
/// db 文件位于 &lt;AppBaseDir&gt;/data/catalog-cache.db,随包发布走,不混入
/// %APPDATA% 的用户数据。
/// </summary>
public sealed class CatalogCacheStore
{
    public string DbPath { get; }

    public CatalogCacheStore()
    {
        var baseDir = AppContext.BaseDirectory;
        var dataDir = Path.Combine(baseDir, "data");
        Directory.CreateDirectory(dataDir);
        DbPath = Path.Combine(dataDir, "catalog-cache.db");
    }

    /// <summary>
    /// Test 注入用。
    /// </summary>
    public CatalogCacheStore(string dbPath)
    {
        DbPath = dbPath;
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();

        InitSchemaIfMissing(conn);
        return conn;
    }

    private static void InitSchemaIfMissing(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS catalog_cache (
                id TEXT PRIMARY KEY,
                source_url TEXT NOT NULL,
                package TEXT NOT NULL,
                raw_metadata TEXT NOT NULL,
                cached_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                UNIQUE(source_url, package)
            );";
        cmd.ExecuteNonQuery();
    }
}
```

- [ ] **Step 2: Modify `SqliteConnectionFactory.cs` — rename + init user schema**

Replace the full content of `src-wpf/ComfyUI.Manager/Data/SqliteConnectionFactory.cs` with:

```csharp
using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// SqliteConnectionFactory:用户数据表 db (environments / scanned_nodes /
/// process_state / version_history / nodes 等)。位于 %APPDATA%/ComfyUI-Manager/state.db。
///
/// 升级兼容:首次 v0.6.4 启动时,如果旧的 catalog.db 存在且 state.db 不存在,
/// 自动 File.Move(catalog.db → state.db),把旧 db 里残留的 user 表带过去。
/// 旧 db 里的 catalog_cache 会被丢弃(用户主动去 Settings 重新刷新)。
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _dbPath;

    public string DbPath => _dbPath;

    public SqliteConnectionFactory()
    {
        _dbPath = ResolveDbPath();
    }

    /// <summary>
    /// Constructor used by tests to inject an explicit db path.
    /// </summary>
    public SqliteConnectionFactory(string dbPath)
    {
        _dbPath = dbPath;
    }

    /// <summary>
    /// Resolves the user-db path. If a legacy <c>catalog.db</c> is present
    /// and <c>state.db</c> is not, renames it. Caller should not rename the
    /// file out from under running SQLite connections.
    /// </summary>
    private static string ResolveDbPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("COMFY_MGR_DB_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "ComfyUI-Manager");
        Directory.CreateDirectory(dir);

        var newPath = Path.Combine(dir, "state.db");
        var legacyPath = Path.Combine(dir, "catalog.db");
        if (!File.Exists(newPath) && File.Exists(legacyPath))
        {
            // 一次性升级迁移:旧 catalog.db 含混合表,移到 state.db
            // 后旧 db 的 catalog_cache 会被丢弃(用户从 Settings 重新拉)。
            try { File.Move(legacyPath, newPath); }
            catch { /* 容错:rename 失败时仍用旧 db(下次启动再试) */ }
        }
        return newPath;
    }

    /// <summary>
    /// Opens a new SqliteConnection with user-table schema ensured.
    /// Caller owns disposal.
    /// </summary>
    public SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();

        InitSchemaIfMissing(conn);
        return conn;
    }

    /// <summary>
    /// CREATE TABLE IF NOT EXISTS for all user tables WPF reads from.
    /// Mirrors the schema in <c>tests-wpf/.../Fakes/TestDb.cs</c>.
    /// </summary>
    private static void InitSchemaIfMissing(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS environments (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                root_path TEXT NOT NULL,
                comfyui_layout TEXT NOT NULL,
                comfyui_source TEXT,
                venv_path TEXT,
                python_executable TEXT,
                custom_nodes_path TEXT,
                extra_model_paths_yaml TEXT,
                port INTEGER,
                enabled_node_ids_json TEXT DEFAULT '[]',
                status TEXT DEFAULT 'stopped',
                pid INTEGER
            );
            CREATE TABLE IF NOT EXISTS scanned_nodes (
                id TEXT PRIMARY KEY,
                env_id TEXT NOT NULL,
                package TEXT NOT NULL,
                package_path TEXT NOT NULL,
                version TEXT,
                author TEXT,
                description TEXT,
                class_mappings TEXT NOT NULL DEFAULT '[]',
                status TEXT NOT NULL DEFAULT 'enabled',
                scan_meta TEXT NOT NULL DEFAULT '{}',
                last_scanned_at TEXT,
                locked INTEGER NOT NULL DEFAULT 0,
                UNIQUE(env_id, package)
            );
            CREATE TABLE IF NOT EXISTS version_history (
                id TEXT PRIMARY KEY,
                env_id TEXT NOT NULL,
                package TEXT NOT NULL,
                action TEXT NOT NULL,
                version_before TEXT,
                version_after TEXT,
                pkg_version TEXT,
                result TEXT NOT NULL,
                error_message TEXT,
                performed_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS dep_records (
                id TEXT PRIMARY KEY,
                env_id TEXT NOT NULL,
                package TEXT NOT NULL,
                source TEXT NOT NULL,
                dep_name TEXT NOT NULL,
                dep_version_spec TEXT,
                scanned_at TEXT NOT NULL,
                UNIQUE(env_id, package, source, dep_name)
            );
            CREATE TABLE IF NOT EXISTS process_state (
                env_id TEXT PRIMARY KEY,
                pid INTEGER NOT NULL,
                port INTEGER NOT NULL,
                started_at TIMESTAMP NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }
}
```

- [ ] **Step 3: Create `tests-wpf/ComfyUI.Manager.Tests/Data/CatalogCacheStoreTests.cs`**

Write to `tests-wpf/ComfyUI.Manager.Tests/Data/CatalogCacheStoreTests.cs`:

```csharp
using System;
using System.IO;
using ComfyUI.Manager.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public class CatalogCacheStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public CatalogCacheStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"catalog-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "catalog-cache.db");
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Open_CreatesDbFileAndCatalogCacheTable_WhenMissing()
    {
        var store = new CatalogCacheStore(_dbPath);

        using var conn = store.Open();

        Assert.True(File.Exists(_dbPath));
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type='table' AND name='catalog_cache'";
        Assert.Equal("catalog_cache", Assert.Single(cmd.ExecuteScalarEnumerable<string>()));
    }

    [Fact]
    public void Open_IsIdempotent_CalledTwice_DoesNotFail()
    {
        var store = new CatalogCacheStore(_dbPath);
        store.Open();
        using var conn = store.Open();
        Assert.NotNull(conn);
    }

    [Fact]
    public void Constructor_WithPath_ExposesDbPath()
    {
        var store = new CatalogCacheStore(_dbPath);
        Assert.Equal(_dbPath, store.DbPath);
    }
}
```

- [ ] **Step 4: Create `tests-wpf/ComfyUI.Manager.Tests/Data/SqliteConnectionFactoryTests.cs`**

Write to `tests-wpf/ComfyUI.Manager.Tests/Data/SqliteConnectionFactoryTests.cs`:

```csharp
using System;
using System.IO;
using ComfyUI.Manager.Data;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public class SqliteConnectionFactoryTests : IDisposable
{
    private readonly string _tempDir;

    public SqliteConnectionFactoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"user-factory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Open_CreatesDbFileAndUserTables_WhenMissing()
    {
        var dbPath = Path.Combine(_tempDir, "state.db");
        var factory = new SqliteConnectionFactory(dbPath);

        using var conn = factory.Open();

        Assert.True(File.Exists(dbPath));
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type='table' " +
            "AND name IN ('environments','scanned_nodes','version_history'," +
            "'dep_records','process_state') ORDER BY name";
        var tables = new System.Collections.Generic.List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) tables.Add(reader.GetString(0));
        Assert.Equal(5, tables.Count);
    }

    [Fact]
    public void Constructor_WithPath_ExposesDbPath()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_tempDir, "x.db"));
        Assert.EndsWith("x.db", factory.DbPath);
    }
}
```

NOTE: The legacy `catalog.db → state.db` rename is exercised in production by `App.xaml.cs` first call to `ResolveDbPath`. We do NOT unit-test the production path directly because `ResolveDbPath` is private and env-dependent (`%APPDATA%`); instead T9 covers it by manual smoke after build.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogCacheStoreTests|FullyQualifiedName~SqliteConnectionFactoryTests" -v minimal`
Expected: 5/5 PASS (3 + 2).

- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Data/CatalogCacheStore.cs \
        src-wpf/ComfyUI.Manager/Data/SqliteConnectionFactory.cs \
        tests-wpf/ComfyUI.Manager.Tests/Data/CatalogCacheStoreTests.cs \
        tests-wpf/ComfyUI.Manager.Tests/Data/SqliteConnectionFactoryTests.cs
git commit -m "refactor(wpf): split catalog_cache to <AppBaseDir>/data/catalog-cache.db + rename legacy catalog.db → state.db"
```

---

## Task 3: CatalogRepository 改造 (用 CatalogCacheStore)

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs:19-24`
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Fakes/TestDb.cs:24,64-72` (如果需要保留现有 test 兼容)

**Interfaces:**
- Produces `CatalogRepository(CatalogCacheStore store)` (替换原 `SqliteConnectionFactory`)
- 其余 method (Search / ListNonExpired / Upsert) 不变

- [ ] **Step 1: Change ctor + field in `CatalogRepository.cs`**

In `src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs`:

- Replace line 19: `private readonly SqliteConnectionFactory _factory;` with `private readonly CatalogCacheStore _store;`
- Replace lines 21-24 (the ctor body) with:

```csharp
    public CatalogRepository(CatalogCacheStore store)
    {
        _store = store;
    }
```

- Replace all 3 `_factory.Open()` calls (in `Search` line 28, `ListNonExpired` line 50, `Upsert` line 71) with `_store.Open()`.

- [ ] **Step 2: Verify TestDb pattern still works (no TestDb.cs changes needed if we keep TestDb.Factory for OTHER repos; CatalogRepository tests now need CatalogCacheStore)**

`TestDb.cs` already creates `SqliteConnectionFactory(Path)`. Since `CatalogRepository` now takes `CatalogCacheStore`, the existing `CatalogViewModelTests.SeedCatalog` line `new CatalogRepository(db.Factory)` will **fail to compile**. Update:

- In `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelTests.cs:21`, change `new CatalogRepository(db.Factory)` to:

```csharp
        var cacheStore = new CatalogCacheStore(db.Path);
        var repo = new CatalogRepository(cacheStore);
```

NOTE: This will fail the existing tests since they're updated in T5 (with new VM ctor). T5 will rewrite all tests. For now, to keep T3 buildable, **T3 also bumps the call site** so the build stays green between commits. Subsequent T5 will refactor the VM and the tests together.

- [ ] **Step 3: Build to verify compile**

Run: `dotnet build src-wpf/ComfyUI.Manager/ -v minimal`
Expected: 0 警告 0 错误 (existing tests will still FAIL because they call old VM ctor — T5 fixes).

- [ ] **Step 4: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelTests.cs
git commit -m "refactor(wpf): CatalogRepository consumes CatalogCacheStore (not user db factory)"
```

---

## Task 4: CatalogRefreshService (共享 Service + RefreshResult record)

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceTests.cs`

**Interfaces:**
- Produces `CatalogRefreshService(CatalogFetcher fetcher, CatalogRepository repo, Settings settings)`
- Produces `Task<RefreshResult> RefreshAsync(CancellationToken ct = default)`
- Produces `record RefreshResult(bool Success, int EntryCount, string? Error)`
- Produces `static RefreshResult.Ok(int n)` / `Fail(string err)`

- [ ] **Step 1: Create `Services/CatalogRefreshService.cs`**

Write to `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs`:

```csharp
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// CatalogRefreshService:Settings 和 Catalog 两个页面共享的"从 active
/// QuerySource 拉 catalog JSON → 写 SQLite"流程。失败时不抛,返回
/// RefreshResult.Fail(reason)。
/// </summary>
public class CatalogRefreshService
{
    private readonly CatalogFetcher _fetcher;
    private readonly CatalogRepository _repo;
    private readonly Settings _settings;

    public CatalogRefreshService(
        CatalogFetcher fetcher,
        CatalogRepository repo,
        Settings settings)
    {
        _fetcher = fetcher;
        _repo = repo;
        _settings = settings;
    }

    public async Task<RefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        var src = _settings.QuerySources
            .FirstOrDefault(s => s.Name == _settings.ActiveQuerySourceName);
        if (src is null || string.IsNullOrWhiteSpace(src.Url))
        {
            return RefreshResult.Fail("未配置查询源,请先在 Settings 添加");
        }

        try
        {
            var entries = await _fetcher.FetchAsync(src.Url, ct);
            foreach (var e in entries)
            {
                e.SourceUrl = src.Url;
                _repo.Upsert(e);
            }
            return RefreshResult.Ok(entries.Count);
        }
        catch (Exception ex)
        {
            return RefreshResult.Fail($"拉取失败: {ex.Message}(本地缓存仍可用)");
        }
    }
}

public record RefreshResult(bool Success, int EntryCount, string? Error)
{
    public static RefreshResult Ok(int n) => new(true, n, null);
    public static RefreshResult Fail(string err) => new(false, 0, err);
}
```

- [ ] **Step 2: Create test file `Services/CatalogRefreshServiceTests.cs`**

Write to `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Moq;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class CatalogRefreshServiceTests : IDisposable
{
    private readonly TestDb _db;
    private readonly Settings _settings;

    public CatalogRefreshServiceTests()
    {
        _db = new TestDb();
        _settings = new Settings();
        ComfyUI.Manager.Infrastructure.SettingsDefaults.Apply(_settings, @"D:\ToolDevelop\ComfyUI");
    }

    public void Dispose() => _db.Dispose();

    private sealed class FakeCatalogFetcher : CatalogFetcher
    {
        public List<CatalogEntry> EntriesToReturn { get; set; } = new();
        public Exception? ThrowOnFetch { get; set; }

        public FakeCatalogFetcher()
            : base(new HttpClient(new Mock<HttpMessageHandler>().Object), 60) { }

        public override Task<List<CatalogEntry>> FetchAsync(string url, CancellationToken ct = default)
        {
            if (ThrowOnFetch is not null) throw ThrowOnFetch;
            return Task.FromResult(EntriesToReturn);
        }
    }

    [Fact]
    public async Task RefreshAsync_NoActiveSource_ReturnsFailure()
    {
        var svc = new CatalogRefreshService(
            new FakeCatalogFetcher(),
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            new Settings
            {
                QuerySources = new(),  // 空列表 → 无 active source
                ActiveQuerySourceName = "nonexistent",
            });

        var result = await svc.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("未配置查询源", result.Error);
        Assert.Equal(0, result.EntryCount);
    }

    [Fact]
    public async Task RefreshAsync_Success_UpsertsEntriesAndReturnsCount()
    {
        var fetcher = new FakeCatalogFetcher
        {
            EntriesToReturn = new List<CatalogEntry>
            {
                new() { Package = "pkg-x" },
                new() { Package = "pkg-y" },
            },
        };

        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings);

        var result = await svc.RefreshAsync();

        Assert.True(result.Success);
        Assert.Equal(2, result.EntryCount);
        Assert.Null(result.Error);

        var entries = new CatalogRepository(new CatalogCacheStore(_db.Path)).Search("", 10);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Package == "pkg-x");
    }

    [Fact]
    public async Task RefreshAsync_FetcherThrows_ReturnsFailureWithLocalCacheStillUsable()
    {
        var fetcher = new FakeCatalogFetcher
        {
            ThrowOnFetch = new HttpRequestException("dns fail"),
        };

        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings);

        var result = await svc.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("拉取失败", result.Error);
        Assert.Contains("dns fail", result.Error);
    }

    [Fact]
    public async Task RefreshAsync_SetsSourceUrlOnEachEntry()
    {
        var fetcher = new FakeCatalogFetcher
        {
            EntriesToReturn = new List<CatalogEntry> { new() { Package = "pkg-z" } },
        };

        var svc = new CatalogRefreshService(
            fetcher,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            _settings);

        await svc.RefreshAsync();

        var entries = new CatalogRepository(new CatalogCacheStore(_db.Path)).Search("", 10);
        Assert.Equal(_settings.QuerySources[0].Url, entries[0].SourceUrl);
    }
}
```

- [ ] **Step 3: Run tests to verify they pass**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogRefreshServiceTests" -v minimal`
Expected: 4/4 PASS.

- [ ] **Step 4: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceTests.cs
git commit -m "feat(wpf): CatalogRefreshService — shared refresh logic for Settings + Catalog"
```

---

## Task 5: CatalogViewModel — 分页 + 视图模式 + Refresh 走 Service + Author/Stars/Description 适配

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs` (大改,全文件替换)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelTests.cs` (全文件替换)

**Interfaces:**
- Produces `CatalogViewModel(CatalogRepository repo, EnvironmentRepository envRepo, NodeOperations nodeOps, CatalogRefreshService refreshService, Settings settings)` — **新 ctor,替换 5-arg 旧版**
- Produces `ObservableCollection<CatalogEntry> PagedEntries { get; }` (替换 `Entries`)
- Produces `int CurrentPage { get; }` (1-based)
- Produces `int TotalPages { get; }`
- Produces `int PageSize => _settings.CatalogPageSize`
- Produces `CatalogViewMode ViewMode { get; }`
- Produces `bool IsListMode => ViewMode == CatalogViewMode.List`
- Produces `bool IsTileMode => ViewMode == CatalogViewMode.Tile`
- Produces `bool HasEntries => _allEntries.Count > 0`
- Produces `string? ErrorMessage { get; }` (已存在)
- Produces `string? InfoMessage { get; }` (新)
- Produces `bool IsBusy { get; }` (已存在)
- Produces `RelayCommand NextPageCommand / PrevPageCommand / SetListViewCommand / SetTileViewCommand / RefreshCommand / InstallCommand`
- Produces adapter: `string Author { get; }` / `string? StarsDisplay { get; }` / `string Description { get; }` 在 VM 内部针对单条 entry 的 helper(给 XAML 绑定用);**或** T7 阶段改 XAML 路径;**本 task 选 VM adapter**(简单,与 VM 已有 ErrorMessage/InfoMessage 模式一致)

NOTE on **VM adapter 决策**:CatalogEntry 模型只有 `Package`; XAML 现有 5 列 DataGrid 绑定 `Name` / `Author` / `Stars` / `Description` / `操作` — 其中 `Name` 和 `Author`/`Stars`/`Description` 三个都未绑定到 model 字段,这是 pre-existing latent bug(T5 修复)。**方案**: 把 `Entries` (CatalogEntry) 改为 `PagedEntries` (CatalogEntry), 然后 XAML 在 T7 阶段把 `{Binding Author}` 等改为 `{Binding RawMetadata[author]}` 等(直接走 dictionary indexer)。CatalogEntry 不改 model。

- [ ] **Step 1: Replace `CatalogViewModel.cs` full content**

Replace the entire content of `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

public class CatalogViewModel : ViewModelBase
{
    private readonly CatalogRepository _repo;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeOperations _nodeOps;
    private readonly CatalogRefreshService _refreshService;
    private readonly Settings _settings;
    private readonly SettingsRepository _settingsRepo;  // 视图模式 set 时持久化

    private List<CatalogEntry> _allEntries = new();

    public ObservableCollection<CatalogEntry> PagedEntries { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand NextPageCommand { get; }
    public RelayCommand PrevPageCommand { get; }
    public RelayCommand SetListViewCommand { get; }
    public RelayCommand SetTileViewCommand { get; }

    private int _currentPage = 1;
    public int CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetField(ref _currentPage, value))
            {
                RaisePropertyChanged(nameof(CanPrevPage));
                RaisePropertyChanged(nameof(CanNextPage));
            }
        }
    }

    private int _totalPages = 1;
    public int TotalPages
    {
        get => _totalPages;
        private set => SetField(ref _totalPages, value);
    }

    public int PageSize => _settings.CatalogPageSize;

    public CatalogViewMode ViewMode => _settings.CatalogViewMode;
    public bool IsListMode => ViewMode == CatalogViewMode.List;
    public bool IsTileMode => ViewMode == CatalogViewMode.Tile;

    public bool HasEntries => _allEntries.Count > 0;
    public bool CanPrevPage => CurrentPage > 1;
    public bool CanNextPage => CurrentPage < TotalPages;

    private string _query = "";
    public string Query
    {
        get => _query;
        set { if (SetField(ref _query, value)) Search(); }
    }

    private CatalogEntry? _selected;
    public CatalogEntry? Selected { get => _selected; set => SetField(ref _selected, value); }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetField(ref _errorMessage, value);
    }

    private string? _infoMessage;
    public string? InfoMessage
    {
        get => _infoMessage;
        private set => SetField(ref _infoMessage, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public CatalogViewModel(
        CatalogRepository repo,
        EnvironmentRepository envRepo,
        NodeOperations nodeOps,
        CatalogRefreshService refreshService,
        Settings settings,
        SettingsRepository settingsRepo)
    {
        _repo = repo;
        _envRepo = envRepo;
        _nodeOps = nodeOps;
        _refreshService = refreshService;
        _settings = settings;
        _settingsRepo = settingsRepo;

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => !IsBusy);
        InstallCommand = new RelayCommand(
            async p => await InstallAsync(p as CatalogEntry ?? Selected),
            p => (p as CatalogEntry ?? Selected) is not null);
        NextPageCommand = new RelayCommand(_ => GoToPage(CurrentPage + 1), _ => CanNextPage);
        PrevPageCommand = new RelayCommand(_ => GoToPage(CurrentPage - 1), _ => CanPrevPage);
        SetListViewCommand = new RelayCommand(_ => SetViewMode(CatalogViewMode.List));
        SetTileViewCommand = new RelayCommand(_ => SetViewMode(CatalogViewMode.Tile));

        Search();  // 读本地 cache → 第一页,无 auto refresh
    }

    private void Search()
    {
        _allEntries = _repo.Search(_query, limit: 0);  // 0 = 全读
        CurrentPage = 1;
        ApplyPage();
    }

    private void ApplyPage()
    {
        PagedEntries.Clear();
        var size = PageSize;
        var skip = (CurrentPage - 1) * size;
        foreach (var e in _allEntries.Skip(skip).Take(size)) PagedEntries.Add(e);
        TotalPages = Math.Max(1, (int)Math.Ceiling((double)_allEntries.Count / size));
        RaisePropertyChanged(nameof(HasEntries));
        RaisePropertyChanged(nameof(CanPrevPage));
        RaisePropertyChanged(nameof(CanNextPage));
    }

    private void GoToPage(int page)
    {
        if (page < 1 || page > TotalPages) return;
        CurrentPage = page;
        ApplyPage();
    }

    private void SetViewMode(CatalogViewMode mode)
    {
        if (_settings.CatalogViewMode == mode) return;
        _settings.CatalogViewMode = mode;
        _settingsRepo.Save(_settings);
        RaisePropertyChanged(nameof(ViewMode));
        RaisePropertyChanged(nameof(IsListMode));
        RaisePropertyChanged(nameof(IsTileMode));
    }

    public async Task RefreshAsync()
    {
        ErrorMessage = null;
        InfoMessage = null;
        IsBusy = true;
        try
        {
            var result = await _refreshService.RefreshAsync();
            if (result.Success)
            {
                Search();
                CurrentPage = 1;
                ApplyPage();
                InfoMessage = $"刷新成功,共 {result.EntryCount} 个条目";
            }
            else
            {
                ErrorMessage = result.Error;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallAsync(CatalogEntry? entry)
    {
        if (entry is null) return;
        var templateUrl = ExtractRepoUrl(entry);
        if (string.IsNullOrWhiteSpace(templateUrl))
        {
            ErrorMessage = "catalog 条目缺 repository url";
            return;
        }
        var envs = _envRepo.ListAll();
        if (envs.Count == 0)
        {
            ErrorMessage = "没有 env 可安装,先创建一个";
            return;
        }
        var env = envs[0];
        var result = await _nodeOps.InstallAsync(env.Id, entry.Package, templateUrl);
        if (!result.Success) ErrorMessage = $"安装失败:{result.Reason}";
        else ErrorMessage = $"已安装 {entry.Package} → version={result.Version}";
    }

    private static string? ExtractRepoUrl(CatalogEntry entry)
    {
        if (entry.RawMetadata is null) return null;
        if (entry.RawMetadata.TryGetValue("repository", out var r) && r is string rs
            && !string.IsNullOrWhiteSpace(rs)) return rs;
        if (entry.RawMetadata.TryGetValue("url", out var u) && u is string us
            && !string.IsNullOrWhiteSpace(us)) return us;
        return null;
    }
}
```

- [ ] **Step 2: Replace `CatalogViewModelTests.cs` full content**

Replace the entire content of `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelTests.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class CatalogViewModelTests : IDisposable
{
    private readonly TestDb _db;
    private readonly string _settingsRepoPath;
    private readonly SettingsRepository _settingsRepo;
    private readonly Settings _settings;
    private readonly FakeRefreshService _refreshService;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeRepository _nodeRepo;
    private readonly NoopNodeOps _nodeOps;
    private readonly CatalogRepository _catRepo;

    public CatalogViewModelTests()
    {
        _db = new TestDb();
        _settings = new Settings();
        SettingsDefaults.Apply(_settings, @"D:\ToolDevelop\ComfyUI");
        _settingsRepoPath = Path.Combine(
            Path.GetTempPath(), $"cat-vm-{Guid.NewGuid():N}.json");
        _settingsRepo = new SettingsRepository(_settingsRepoPath);
        _refreshService = new FakeRefreshService();
        _envRepo = new EnvironmentRepository(_db.Factory);
        _nodeRepo = new NodeRepository(_db.Factory);
        _nodeOps = new NoopNodeOps(_envRepo, _nodeRepo, _settings);
        _catRepo = new CatalogRepository(new CatalogCacheStore(_db.Path));
    }

    public void Dispose() => _db.Dispose();

    private CatalogViewModel NewVm() =>
        new CatalogViewModel(_catRepo, _envRepo, _nodeOps, _refreshService, _settings, _settingsRepo);

    private void SeedCatalog(string package)
    {
        _catRepo.Upsert(new CatalogEntry
        {
            Id = package,
            SourceUrl = _settings.QuerySources[0].Url,
            Package = package,
            CachedAt = "2026-07-13T00:00:00",
            ExpiresAt = "2027-07-13T00:00:00",
        });
    }

    private sealed class FakeRefreshService : CatalogRefreshService
    {
        public RefreshResult NextResult { get; set; } =
            RefreshResult.Ok(0);
        public int RefreshCallCount { get; private set; }

        public FakeRefreshService()
            : base(new NullCatalogFetcher(), new NullCatalogRepository(), new Settings())
        { }

        public override Task<RefreshResult> RefreshAsync(
            System.Threading.CancellationToken ct = default)
        {
            RefreshCallCount++;
            return Task.FromResult(NextResult);
        }

        // 不调底层 fetcher / repo 的占位实现(RefreshAsync 已被 override)
        private sealed class NullCatalogFetcher : CatalogFetcher
        {
            public NullCatalogFetcher()
                : base(new System.Net.Http.HttpClient(
                    new Moq.Mock<System.Net.Http.HttpMessageHandler>().Object), 60)
            { }
            public override Task<List<CatalogEntry>> FetchAsync(
                string url, System.Threading.CancellationToken ct = default)
                => throw new NotImplementedException();
        }
        private sealed class NullCatalogRepository : CatalogRepository
        {
            public NullCatalogRepository()
                : base(new CatalogCacheStore(Path.Combine(
                    Path.GetTempPath(), $"null-repo-{Guid.NewGuid():N}.db")))
            { }
        }
    }

    private sealed class NoopNodeOps : NodeOperations
    {
        public NoopNodeOps(EnvironmentRepository envRepo, NodeRepository nodeRepo, Settings settings)
            : base(new GitRunner("git"), envRepo, nodeRepo, settings) { }
    }

    // —— Tests ——

    [Fact]
    public void Ctor_LoadsLocalCache_AsFirstPage_NoAutoRefresh()
    {
        SeedCatalog("pkg-a");
        SeedCatalog("pkg-b");

        var vm = NewVm();

        Assert.Equal(2, vm.PagedEntries.Count);
        Assert.False(vm.IsBusy);
        Assert.Equal(0, _refreshService.RefreshCallCount);  // 不应自动 refresh
    }

    [Fact]
    public void Query_FiltersAndResetsToFirstPage()
    {
        SeedCatalog("alpha");
        SeedCatalog("beta");

        var vm = NewVm();
        vm.Query = "alph";

        Assert.Single(vm.PagedEntries);
        Assert.Equal("alpha", vm.PagedEntries[0].Package);
        Assert.Equal(1, vm.CurrentPage);
    }

    [Fact]
    public void NextPageCommand_AdvancesPage_WhenMorePages()
    {
        for (var i = 0; i < 25; i++) SeedCatalog($"pkg-{i:D2}");
        var vm = NewVm();

        vm.NextPageCommand.Execute(null);

        Assert.Equal(2, vm.CurrentPage);
        Assert.Equal(5, vm.PagedEntries.Count);  // 25 - 20
    }

    [Fact]
    public void NextPageCommand_CannotExecute_OnLastPage()
    {
        for (var i = 0; i < 5; i++) SeedCatalog($"pkg-{i:D2}");
        var vm = NewVm();

        Assert.False(vm.NextPageCommand.CanExecute(null));
    }

    [Fact]
    public void PrevPageCommand_CannotExecute_OnFirstPage()
    {
        SeedCatalog("pkg-a");
        var vm = NewVm();

        Assert.False(vm.PrevPageCommand.CanExecute(null));
    }

    [Fact]
    public void ViewMode_DefaultsFromSettings_List()
    {
        var vm = NewVm();
        Assert.Equal(CatalogViewMode.List, vm.ViewMode);
        Assert.True(vm.IsListMode);
        Assert.False(vm.IsTileMode);
    }

    [Fact]
    public void SetTileViewCommand_PersistsToSettings()
    {
        var vm = NewVm();

        vm.SetTileViewCommand.Execute(null);

        Assert.Equal(CatalogViewMode.Tile, vm.ViewMode);
        Assert.True(vm.IsTileMode);
        Assert.False(vm.IsListMode);

        var reloaded = new SettingsRepository(_settingsRepoPath).Load();
        Assert.Equal(CatalogViewMode.Tile, reloaded.CatalogViewMode);
    }

    [Fact]
    public async Task RefreshCommand_DelegatesToRefreshService()
    {
        var vm = NewVm();

        vm.RefreshCommand.Execute(null);
        await Task.Delay(50);

        Assert.Equal(1, _refreshService.RefreshCallCount);
    }

    [Fact]
    public async Task RefreshCommand_Success_ShowsInfoMessageAndJumpsToFirstPage()
    {
        _refreshService.NextResult = RefreshResult.Ok(120);
        // seed 21 条 → 2 页; refresh 后应跳到 page 1
        for (var i = 0; i < 21; i++) SeedCatalog($"pkg-{i:D2}");
        var vm = NewVm();
        vm.NextPageCommand.Execute(null);  // 手动跳到 page 2
        Assert.Equal(2, vm.CurrentPage);

        vm.RefreshCommand.Execute(null);
        await Task.Delay(50);

        Assert.Equal(1, vm.CurrentPage);
        Assert.Contains("刷新成功,共 120 个条目", vm.InfoMessage);
    }

    [Fact]
    public async Task RefreshCommand_Failure_SetsErrorMessage()
    {
        _refreshService.NextResult = RefreshResult.Fail("拉取失败: dns fail");
        var vm = NewVm();

        vm.RefreshCommand.Execute(null);
        await Task.Delay(50);

        Assert.Contains("拉取失败", vm.ErrorMessage);
    }
}
```

NOTE: the test class declares `_settingsRepoPath` in the field list (above) for the `SetTileViewCommand_PersistsToSettings` test to use; init order in ctor ensures it's created before `_settingsRepo` references it.

- [ ] **Step 3: Run tests**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogViewModelTests" -v minimal`
Expected: 10/10 PASS.

- [ ] **Step 4: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelTests.cs
git commit -m "feat(wpf): CatalogViewModel — paging + view mode + refresh via shared service (no auto-refresh)"
```

---

## Task 6: SettingsViewModel — RefreshCatalogCommand + IsBusy / Status / Error

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs:26,135-138,329-347`
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelTests.cs:36,154`

**Interfaces:**
- Produces `SettingsViewModel(SettingsRepository repo, GitProxyConfig proxy, CatalogRefreshService refreshService)` — 新 3-arg ctor
- Produces `bool IsBusy { get; }`
- Produces `string? StatusMessage { get; }`
- Produces `string? ErrorMessage { get; }`
- Produces `RelayCommand RefreshCatalogCommand { get; }`

- [ ] **Step 1: Modify `SettingsViewModel.cs`**

- Change line 26 ctor signature to:

```csharp
    public SettingsViewModel(SettingsRepository repo, GitProxyConfig proxy, CatalogRefreshService refreshService)
    {
        _repo = repo;
        _proxy = proxy;
        _refreshService = refreshService;
```

- At the top of the class (after line 17 `private Settings _settings;`), add:

```csharp
    private readonly CatalogRefreshService _refreshService;
```

- After line 137 (`CancelAddDownloadSourceCommand = new RelayCommand(_ => { IsAddDownloadSourceOpen = false; });` on line 137) and **before `RaiseAllPropertiesChanged();` on line 138**, add:

```csharp
        RefreshCatalogCommand = new RelayCommand(
            _ => _ = RefreshCatalogAsync(),
            _ => !IsBusy);
```

- Inside the class (anywhere; pick end of properties section), add:

```csharp
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                RefreshCatalogCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string? _statusMessage;
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetField(ref _errorMessage, value);
    }

    public RelayCommand RefreshCatalogCommand { get; }

    private async Task RefreshCatalogAsync()
    {
        ErrorMessage = null;
        StatusMessage = null;
        IsBusy = true;
        try
        {
            var result = await _refreshService.RefreshAsync();
            if (result.Success)
            {
                StatusMessage = $"刷新成功,共 {result.EntryCount} 个条目";
            }
            else
            {
                ErrorMessage = result.Error;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
```

NOTE: also add `using ComfyUI.Manager.Services;` if not present at the top — it already is (line 7).

- [ ] **Step 2: Update existing tests in `SettingsViewModelTests.cs`**

All existing ctor calls pass 2 args `(repo, proxy)`. They need a 3rd `refreshService` arg. To keep the 8 existing tests green without rewriting them, add a small `FakeRefreshService` helper inside the test class:

Add to `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelTests.cs` (inside the class, near top):

```csharp
    private sealed class FakeRefreshService : CatalogRefreshService
    {
        public RefreshResult NextResult { get; set; } = RefreshResult.Ok(0);
        public int CallCount { get; private set; }

        public FakeRefreshService()
            : base(new NullFetcher(), new NullRepo(), new Settings())
        { }

        public override Task<RefreshResult> RefreshAsync(
            System.Threading.CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(NextResult);
        }

        private sealed class NullFetcher : CatalogFetcher
        {
            public NullFetcher() : base(
                new System.Net.Http.HttpClient(
                    new Moq.Mock<System.Net.Http.HttpMessageHandler>().Object), 60) { }
            public override Task<List<CatalogEntry>> FetchAsync(
                string url, System.Threading.CancellationToken ct = default)
                => throw new NotImplementedException();
        }
        private sealed class NullRepo : CatalogRepository
        {
            public NullRepo() : base(new CatalogCacheStore(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"null-repo-{System.Guid.NewGuid():N}.db"))) { }
        }
    }
```

Then update each existing `new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled)` (8 call sites) to pass `new FakeRefreshService()` as the 3rd arg:

- Line 36: `new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled, new FakeRefreshService())`
- Same pattern for lines 46, 57, 70, 88, 102, 118, 128, 144.

Also add `using ComfyUI.Manager.Services;` at top of test file if not already.

- [ ] **Step 3: Add 2 new tests for RefreshCatalogCommand**

Append to `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelTests.cs`:

```csharp
    [Fact]
    public void RefreshCatalogCommand_CallsService()
    {
        var svc = new FakeRefreshService();
        var vm = new SettingsViewModel(
            new SettingsRepository(_path), GitProxyConfig.Disabled, svc);

        vm.RefreshCatalogCommand.Execute(null);
        System.Threading.Thread.Sleep(50);  // wait for fire-and-forget

        Assert.Equal(1, svc.CallCount);
    }

    [Fact]
    public void RefreshCatalogCommand_Success_SetsStatusMessage()
    {
        var svc = new FakeRefreshService { NextResult = RefreshResult.Ok(50) };
        var vm = new SettingsViewModel(
            new SettingsRepository(_path), GitProxyConfig.Disabled, svc);

        vm.RefreshCatalogCommand.Execute(null);
        System.Threading.Thread.Sleep(50);

        Assert.Contains("刷新成功,共 50 个条目", vm.StatusMessage);
    }
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsViewModelTests" -v minimal`
Expected: 10/10 PASS (旧 8 + 新 2).

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelTests.cs
git commit -m "feat(wpf): SettingsViewModel — RefreshCatalogCommand + IsBusy/Status/Error"
```

---

## Task 7: CatalogView.xaml 大改 (toggle + 视图切换 + 分页 + 空状态 + 快速刷新按钮)

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml:1-37` (全文件替换)
- Create: `src-wpf/ComfyUI.Manager/Views/CatalogViewTemplateSelector.cs`

**Interfaces:**
- XAML 顶部 toggle "列表" / "磁贴" — Command 绑 `SetListViewCommand` / `SetTileViewCommand`
- XAML 快速 "刷新" 按钮 — Command 绑 `RefreshCommand`
- XAML `ErrorMessage` / `InfoMessage` TextBlock 显示(Visibility 由 BoolToVis / text 绑定)
- XAML 列表模式:1 个 `ContentControl` + `CatalogViewTemplateSelector` (替换原 DataGrid 直接 ItemsSource)
- XAML 磁贴模式:1 个 `ContentControl` + WrapPanel ItemsPanel + 详细卡片 DataTemplate
- XAML 底部分页:`Prev` / `1/N` / `Next`
- XAML 空状态:`<TextBlock Text="暂无数据,去 Settings 刷新">`(`Visibility={Binding !HasEntries}`)
- XAML `CatalogEntry` 字段绑定修复:用 `RawMetadata[author]` / `RawMetadata[stars_count]` / `RawMetadata[description]` 等 dictionary indexer

- [ ] **Step 1: Create `Views/CatalogViewTemplateSelector.cs`**

Write to `src-wpf/ComfyUI.Manager/Views/CatalogViewTemplateSelector.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

/// <summary>
/// CatalogViewTemplateSelector:根据 VM 的 ViewMode 选择 List 模式或 Tile 模式
/// DataTemplate。每个 ContentControl 用这个 selector 在渲染时挑 template。
/// </summary>
public class CatalogViewTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ListTemplate { get; set; }
    public DataTemplate? TileTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (container is FrameworkElement fe && fe.DataContext is CatalogViewModel vm)
        {
            return vm.IsTileMode ? TileTemplate : ListTemplate;
        }
        return base.SelectTemplate(item, container);
    }
}
```

- [ ] **Step 2: Replace `CatalogView.xaml` full content**

Replace the entire content of `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml` with:

```xaml
<UserControl x:Class="ComfyUI.Manager.Views.CatalogView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:views="clr-namespace:ComfyUI.Manager.Views"
             xmlns:vm="clr-namespace:ComfyUI.Manager.ViewModels"
             d:DataContext="{d:DesignInstance Type=vm:CatalogViewModel}"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             mc:Ignorable="d">
    <UserControl.Resources>
        <views:CatalogViewTemplateSelector x:Key="ViewSelector">
            <views:CatalogViewTemplateSelector.ListTemplate>
                <DataTemplate>
                    <DataGrid ItemsSource="{Binding DataContext.PagedEntries,
                                RelativeSource={RelativeSource AncestorType=UserControl}}"
                              SelectedItem="{Binding DataContext.Selected,
                                RelativeSource={RelativeSource AncestorType=UserControl}}"
                              AutoGenerateColumns="False" IsReadOnly="True" Margin="8">
                        <DataGrid.Columns>
                            <DataGridTextColumn Header="包名" Binding="{Binding Package}" Width="*" />
                            <DataGridTextColumn Header="作者" Binding="{Binding RawMetadata[author]}" Width="*" />
                            <DataGridTextColumn Header="⭐" Binding="{Binding RawMetadata[stars]}" Width="60" />
                            <DataGridTextColumn Header="说明" Binding="{Binding RawMetadata[description]}" Width="2*" />
                            <DataGridTemplateColumn Header="操作" Width="80">
                                <DataGridTemplateColumn.CellTemplate>
                                    <DataTemplate>
                                        <Button Content="安装"
                                                Command="{Binding DataContext.InstallCommand,
                                                    RelativeSource={RelativeSource AncestorType=UserControl}}"
                                                CommandParameter="{Binding}" />
                                    </DataTemplate>
                                </DataGridTemplateColumn.CellTemplate>
                            </DataGridTemplateColumn>
                        </DataGrid.Columns>
                    </DataGrid>
                </DataTemplate>
            </views:CatalogViewTemplateSelector.ListTemplate>
            <views:CatalogViewTemplateSelector.TileTemplate>
                <DataTemplate>
                    <ItemsControl ItemsSource="{Binding DataContext.PagedEntries,
                                RelativeSource={RelativeSource AncestorType=UserControl}}"
                                  ItemTemplate="{StaticResource CatalogTileTemplate}"
                                  ItemsPanel="{StaticResource CatalogTileWrapPanel}"
                                  Margin="8" />
                </DataTemplate>
            </views:CatalogViewTemplateSelector.TileTemplate>
        </views:CatalogViewTemplateSelector>
    </UserControl.Resources>
    <DockPanel>
        <!-- 顶部工具栏:搜索 + 视图 toggle + 刷新 -->
        <Grid DockPanel.Dock="Top" Margin="8">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <TextBox Grid.Column="0" Text="{Binding Query, UpdateSourceTrigger=PropertyChanged}"
                     Style="{StaticResource MaterialTextBox}" />
            <!-- 视图 toggle -->
            <StackPanel Grid.Column="1" Orientation="Horizontal" Margin="8,0,0,0">
                <Button Content="列表" Margin="2,0"
                        Command="{Binding SetListViewCommand}"
                        Style="{StaticResource MaterialButton}"
                        Background="{Binding IsListMode, Converter={StaticResource BoolToBrush}}" />
                <Button Content="磁贴" Margin="2,0,0,0"
                        Command="{Binding SetTileViewCommand}"
                        Style="{StaticResource MaterialButton}"
                        Background="{Binding IsTileMode, Converter={StaticResource BoolToBrush}}" />
            </StackPanel>
            <!-- 快速刷新 -->
            <Button Grid.Column="2" Content="刷新" Margin="8,0,0,0"
                    Command="{Binding RefreshCommand}"
                    Style="{StaticResource MaterialButton}" />
            <!-- 进度 -->
            <ProgressBar Grid.Column="3" IsIndeterminate="True" Width="120"
                         Visibility="{Binding IsBusy, Converter={StaticResource BoolToVisibility}}"
                         Margin="8,0,0,0" />
        </Grid>

        <!-- 信息条 -->
        <StackPanel DockPanel.Dock="Top" Margin="8,0,8,4">
            <TextBlock Text="{Binding ErrorMessage}" Foreground="{StaticResource ErrorBrush}"
                       Visibility="{Binding ErrorMessage, Converter={StaticResource NullToVisibility}}" />
            <TextBlock Text="{Binding InfoMessage}" Foreground="Green"
                       Visibility="{Binding InfoMessage, Converter={StaticResource NullToVisibility}}" />
        </StackPanel>

        <!-- 底部分页 -->
        <Grid DockPanel.Dock="Bottom" Margin="8"
              Visibility="{Binding HasEntries, Converter={StaticResource BoolToVisibility}}">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <Button Grid.Column="0" Content="&lt; Prev"
                    Command="{Binding PrevPageCommand}"
                    Style="{StaticResource MaterialButton}" />
            <TextBlock Grid.Column="1" Text="{Binding CurrentPage, StringFormat={}{0}}" VerticalAlignment="Center" />
            <TextBlock Grid.Column="1" HorizontalAlignment="Center" VerticalAlignment="Center">
                <Run Text="{Binding CurrentPage, Mode=OneWay}" />
                <Run Text=" / " />
                <Run Text="{Binding TotalPages, Mode=OneWay}" />
            </TextBlock>
            <Button Grid.Column="2" Content="Next &gt;"
                    Command="{Binding NextPageCommand}"
                    Style="{StaticResource MaterialButton}" />
        </Grid>

        <!-- 空状态 -->
        <TextBlock Text="暂无数据,去 Settings 刷新" FontSize="14" Foreground="Gray"
                   HorizontalAlignment="Center" VerticalAlignment="Center"
                   Visibility="{Binding HasEntries, Converter={StaticResource InverseBoolToVisibility}}" />

        <!-- 视图内容(由 selector 挑 template) -->
        <ContentControl Content="{Binding}" ContentTemplateSelector="{StaticResource ViewSelector}" />
    </DockPanel>
</UserControl>
```

- [ ] **Step 3: Add 2 converters to `Views/Converters.cs` (or new file)**

`CatalogView.xaml` references 3 converters not yet defined: `BoolToBrush`, `NullToVisibility`, `InverseBoolToVisibility`. Open `src-wpf/ComfyUI.Manager/Views/Converters.cs` and verify what's there. If empty / missing these, append:

```csharp
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ComfyUI.Manager.Views;

public class BoolToBrushConverter : IValueConverter
{
    public Brush ActiveBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x67, 0x50, 0xA4));
    public Brush InactiveBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) ? ActiveBrush : InactiveBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class NullToVisibilityConverter : IValueConverter
{
    public Visibility WhenNull { get; set; } = Visibility.Collapsed;
    public Visibility WhenNotNull { get; set; } = Visibility.Visible;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null ? WhenNull : WhenNotNull;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

Then register all 3 converters in `Resources/Theme.xaml` (single app-wide location; both T7 and T8 XAML will resolve them from here):

Open `src-wpf/ComfyUI.Manager/Resources/Theme.xaml`. At the top of the file, after `<ResourceDictionary ...>` on line 2, add the `xmlns:views` declaration:

```xaml
    xmlns:views="clr-namespace:ComfyUI.Manager.Views"
```

Then after the `<BooleanToVisibilityConverter x:Key="BoolToVisibility" />` on line 5, add:

```xaml
    <views:BoolToBrushConverter x:Key="BoolToBrush" />
    <views:NullToVisibilityConverter x:Key="NullToVisibility" />
    <views:InverseBoolToVisibilityConverter x:Key="InverseBoolToVisibility" />
```

And add `using System.Windows.Data;` to `Converters.cs` if not already.

- [ ] **Step 4: Build**

Run: `dotnet build src-wpf/ComfyUI.Manager/ -v minimal`
Expected: 0 警告 0 错误. (Tests still pass because ViewModel contract unchanged.)

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/CatalogView.xaml \
        src-wpf/ComfyUI.Manager/Views/CatalogViewTemplateSelector.cs \
        src-wpf/ComfyUI.Manager/Views/Converters.cs \
        src-wpf/ComfyUI.Manager/Resources/Theme.xaml
git commit -m "feat(wpf): CatalogView — view mode toggle + paging + empty state + raw_metadata bindings"
```

NOTE: CatalogTileTemplate + CatalogTileWrapPanel are referenced in XAML but defined in Theme.xaml (T10). **Run T10 BEFORE T7** (or, if you must run T7 first, define placeholder `<DataTemplate>` + `<ItemsPanelTemplate>` inside T7's `<UserControl.Resources>` and remove them when T10 lands). The recommended execution order swaps T7 and T10: T1 → T2 → T3 → T4 → T5 → T6 → **T10** → **T7** → T8 → T9 → T11. The task numbers above match the spec's §9 enumeration for traceability; ignore the numbering when deciding actual execution order.

---

## Task 8: SettingsView.xaml — 加"刷新节点目录"按钮

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml:79-80` (在 "查询节点的源" section 末尾插入)

- [ ] **Step 1: Insert refresh button + status text after line 79**

After the closing `</Grid>` on line 79 and before the `<!-- ============ 下载节点的源 ============ -->` comment on line 81, insert:

```xaml

            <StackPanel Margin="0,16,0,0">
                <Button Content="{Binding IsBusy, Converter={StaticResource BoolToRefreshText}}"
                        Command="{Binding RefreshCatalogCommand}"
                        Style="{StaticResource MaterialButton}"
                        HorizontalAlignment="Left"
                        IsEnabled="{Binding IsBusy, Converter={StaticResource InverseBool}}" />
                <TextBlock Text="{Binding StatusMessage}" Foreground="Green" Margin="0,4,0,0"
                           Visibility="{Binding StatusMessage, Converter={StaticResource NullToVisibility}}" />
                <TextBlock Text="{Binding ErrorMessage}" Foreground="{StaticResource ErrorBrush}" Margin="0,4,0,0"
                           Visibility="{Binding ErrorMessage, Converter={StaticResource NullToVisibility}}" />
            </StackPanel>
```

- [ ] **Step 2: Add 2 more converters in `Views/Converters.cs`**

Append to `src-wpf/ComfyUI.Manager/Views/Converters.cs`:

```csharp
public class BoolToRefreshTextConverter : IValueConverter
{
    public string BusyText { get; set; } = "刷新中...";
    public string IdleText { get; set; } = "刷新节点目录";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) ? BusyText : IdleText;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}
```

- [ ] **Step 3: Register 2 new converters in `Theme.xaml`** (T7 already registered 3 converters here; just append the 2 new ones)

In `src-wpf/ComfyUI.Manager/Resources/Theme.xaml`, after the existing converter registrations (added by T7), add:

```xaml
    <views:BoolToRefreshTextConverter x:Key="BoolToRefreshText" />
    <views:InverseBoolConverter x:Key="InverseBool" />
```

The `xmlns:views` declaration at the top of Theme.xaml was added in T7.

- [ ] **Step 4: Build**

Run: `dotnet build src-wpf/ComfyUI.Manager/ -v minimal`
Expected: 0 警告 0 错误.

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/SettingsView.xaml \
        src-wpf/ComfyUI.Manager/Views/Converters.cs \
        src-wpf/ComfyUI.Manager/Resources/Theme.xaml
git commit -m "feat(wpf): SettingsView — Refresh node catalog button + status/error display"
```

---

## Task 9: App.xaml.cs 注入 CatalogRefreshService + MainViewModel 传新 ctor

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs:54-66`
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs:36-80`

**Interfaces:**
- App.xaml.cs 构造 1 个 `CatalogCacheStore` + 1 个 `CatalogRepository(cacheStore)` + 1 个 `CatalogRefreshService(fetcher, catRepo, settings)`
- `MainViewModel` ctor 加 2 个新参数 (`CatalogRefreshService`, `SettingsRepository`)
- `MainViewModel.ShowCatalog` 改用 `_catalogCacheStore` 构造 `CatalogRepository`,并传 6 个参数给 `CatalogViewModel`
- `MainViewModel.ShowSettings` 传 `_settingsRepo` + `_refreshService` 给 `SettingsViewModel`

- [ ] **Step 1: Update `App.xaml.cs`**

In `src-wpf/ComfyUI.Manager/App.xaml.cs`, replace lines 54-66 (the section creating `catalogFetcher` and `_mainVm`):

Original (lines 54-66):
```csharp
        var gitRunner = new GitRunner(gitExe, gitProxy);
        var nodeOps = new NodeOperations(gitRunner, envRepo, nodeRepo, settings);
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var catalogFetcher = new CatalogFetcher(http, settings.CatalogCacheTtlMinutes);
        var bulkOrchestrator = new BulkUpdateOrchestrator(
            projectRoot, gitExe, envRepo, nodeRepo, gitProxy);
        var envCreator = new EnvCreatorService(
            dbFactory, new VenvCreator(), new JunctionLinker(), settings, projectRoot);

        _mainVm = new MainViewModel(
            dbFactory, _launcher, bulkOrchestrator, nodeOps, envCreator, settingsRepo, gitProxy,
            settings, catalogFetcher);
```

Replace with:
```csharp
        var gitRunner = new GitRunner(gitExe, gitProxy);
        var nodeOps = new NodeOperations(gitRunner, envRepo, nodeRepo, settings);
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var catalogFetcher = new CatalogFetcher(http, settings.CatalogCacheTtlMinutes);

        // v0.6.4: catalog_cache 拆到 <AppBaseDir>/data/catalog-cache.db
        var catalogCacheStore = new CatalogCacheStore();
        var catalogRepository = new CatalogRepository(catalogCacheStore);
        var catalogRefreshService = new CatalogRefreshService(
            catalogFetcher, catalogRepository, settings);

        var bulkOrchestrator = new BulkUpdateOrchestrator(
            projectRoot, gitExe, envRepo, nodeRepo, gitProxy);
        var envCreator = new EnvCreatorService(
            dbFactory, new VenvCreator(), new JunctionLinker(), settings, projectRoot);

        _mainVm = new MainViewModel(
            dbFactory, _launcher, bulkOrchestrator, nodeOps, envCreator, settingsRepo, gitProxy,
            settings, catalogFetcher, catalogCacheStore, catalogRefreshService);
```

- [ ] **Step 2: Update `MainViewModel.cs` ctor + ShowCatalog + ShowSettings**

Replace lines 12-19 (the field declarations) with:

```csharp
    private readonly SqliteConnectionFactory _dbFactory;
    private readonly ProcessLauncher _launcher;
    private readonly BulkUpdateOrchestrator _orchestrator;
    private readonly NodeOperations _nodeOps;
    private readonly EnvCreatorService _envCreator;
    private readonly SettingsRepository _settingsRepo;
    private readonly GitProxyConfig _gitProxy;
    private readonly Settings _settings;
    private readonly CatalogFetcher _catalogFetcher;
    private readonly CatalogCacheStore _catalogCacheStore;
    private readonly CatalogRefreshService _refreshService;
```

Replace lines 36-61 (ctor signature + body) with:

```csharp
    public MainViewModel(
        SqliteConnectionFactory dbFactory,
        ProcessLauncher launcher,
        BulkUpdateOrchestrator orchestrator,
        NodeOperations nodeOps,
        EnvCreatorService envCreator,
        SettingsRepository settingsRepo,
        GitProxyConfig gitProxy,
        Settings settings,
        CatalogFetcher catalogFetcher,
        CatalogCacheStore catalogCacheStore,
        CatalogRefreshService refreshService)
    {
        _dbFactory = dbFactory;
        _launcher = launcher;
        _orchestrator = orchestrator;
        _nodeOps = nodeOps;
        _envCreator = envCreator;
        _settingsRepo = settingsRepo;
        _gitProxy = gitProxy;
        _settings = settings;
        _catalogFetcher = catalogFetcher;
        _catalogCacheStore = catalogCacheStore;
        _refreshService = refreshService;
```

Replace lines 72-80 (`ShowCatalog`) with:

```csharp
    private void ShowCatalog()
    {
        var catRepo = new CatalogRepository(_catalogCacheStore);
        var envRepo = new EnvironmentRepository(_dbFactory);
        CurrentView = new CatalogView
        {
            DataContext = new CatalogViewModel(
                catRepo, envRepo, _nodeOps, _refreshService, _settings, _settingsRepo),
        };
    }
```

Replace lines 82-88 (`ShowSettings`) with:

```csharp
    private void ShowSettings()
    {
        CurrentView = new SettingsView
        {
            DataContext = new SettingsViewModel(_settingsRepo, _gitProxy, _refreshService),
        };
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build src-wpf/ComfyUI.Manager/ -v minimal`
Expected: 0 警告 0 错误.

- [ ] **Step 4: Run full test suite**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal`
Expected: ~74-77 PASS (depending on T5/T6 ordering). CatalogViewModelTests + SettingsViewModelTests should still pass; any old tests that referenced `db.Factory` directly for CatalogRepository need to be updated.

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/App.xaml.cs \
        src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs
git commit -m "feat(wpf): wire CatalogCacheStore + CatalogRefreshService into App + MainViewModel"
```

---

## Task 10: Theme.xaml — CatalogTileTemplate + CatalogTileWrapPanel

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Resources/Theme.xaml:1-60`

**Interfaces:**
- Produces `DataTemplate CatalogTileTemplate` (StackPanel 卡片,320 宽,包名/作者/⭐/说明/安装按钮)
- Produces `ItemsPanelTemplate CatalogTileWrapPanel` (WrapPanel)

- [ ] **Step 1: Add Tile template + WrapPanel to Theme.xaml**

Append to `src-wpf/ComfyUI.Manager/Resources/Theme.xaml` (after the closing `</Style>` on line 60 for `MaterialTextBox`):

```xaml

    <!-- Catalog 磁贴视图:WrapPanel + 详细卡片 -->
    <ItemsPanelTemplate x:Key="CatalogTileWrapPanel">
        <WrapPanel Orientation="Horizontal" />
    </ItemsPanelTemplate>

    <DataTemplate x:Key="CatalogTileTemplate">
        <Border Width="320" Margin="8" Padding="12"
                Background="White" BorderBrush="LightGray" BorderThickness="1"
                CornerRadius="8">
            <StackPanel>
                <TextBlock Text="{Binding Package}" FontWeight="Bold" FontSize="14" />
                <StackPanel Orientation="Horizontal" Margin="0,4,0,8">
                    <TextBlock Text="{Binding RawMetadata[author]}" Foreground="Gray" />
                    <TextBlock Text=" · " Foreground="Gray" />
                    <TextBlock Text="⭐" />
                    <TextBlock Text="{Binding RawMetadata[stars]}" />
                </StackPanel>
                <TextBlock Text="{Binding RawMetadata[description]}"
                           TextWrapping="Wrap" MaxHeight="60"
                           TextTrimming="CharacterEllipsis" Margin="0,0,0,8" />
                <Button Content="安装" HorizontalAlignment="Right"
                        Command="{Binding DataContext.InstallCommand,
                                  RelativeSource={RelativeSource AncestorType=UserControl}}"
                        CommandParameter="{Binding}"
                        Style="{StaticResource MaterialButton}" />
            </StackPanel>
        </Border>
    </DataTemplate>
```

- [ ] **Step 2: Build + run tests**

Run: `dotnet build src-wpf/ComfyUI.Manager/ -v minimal && dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal`
Expected: 0 build warnings, all tests PASS.

- [ ] **Step 3: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Resources/Theme.xaml
git commit -m "feat(wpf): Theme.xaml — CatalogTileTemplate + CatalogTileWrapPanel"
```

---

## Task 12: Bundle ComfyUI source template via fetcher script + build_release auto-fetch

**Files:**
- Create: `scripts/fetch_comfyui_template.ps1`
- Modify: `scripts/build_release.ps1` (insert ComfyUI fetch step after git-portable fetch, before publish)
- Modify: `.gitignore` (add `ComfyUI/` line)

**Interfaces:**
- Produces `scripts/fetch_comfyui_template.ps1` — shallow-clones `comfyanonymous/ComfyUI` (default branch `master`) into `<repo>/ComfyUI/`. Idempotent: skip if `<repo>/ComfyUI/.git` exists AND `main.py` exists.
- Uses bundled portable git (`bin/git-portable/cmd/git.exe`) — **NOT** system git, so build works on machines without pre-installed git.
- Default branch: `master` (ComfyUI's main dev branch; user can pin via `-Ref` param to a tag/commit)
- Default depth: `--depth 1` (source-only, no history; ~50-80 MB)
- Excludes large/unnecessary content via `.gitattributes` / `git sparse-checkout`: just the source tree, no models. (TBD: simplify by full shallow clone first; if size > 200 MB, switch to sparse-checkout)
- `.gitignore` adds `ComfyUI/` so the bundled dir doesn't pollute git history
- `build_release.ps1` calls the new script before `dotnet publish`, with failure-stop semantics matching git-portable

- [ ] **Step 1: Create `scripts/fetch_comfyui_template.ps1`**

Write to `D:/ToolDevelop/ComfyUI/scripts/fetch_comfyui_template.ps1`:

```powershell
# scripts/fetch_comfyui_template.ps1
# 把 ComfyUI 源 shallow-clone 到 <repo>/ComfyUI/,作为 "shared" 布局 env 的
# 模板源(替代 v0.6.3 之前的 "用户必须自己 git clone" 步骤)。
# 使用 bundled portable git (bin/git-portable/cmd/git.exe),不依赖系统 PATH。
# 幂等:目录已存在 + main.py 存在 → 跳过。

param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/.."),
    [string]$Ref = "master",
    [string]$RemoteUrl = "https://github.com/comfyanonymous/ComfyUI.git"
)

$ErrorActionPreference = "Stop"
$ComfyUiDir = Join-Path $ProjectRoot "ComfyUI"
$GitPortableExe = Join-Path $ProjectRoot "bin/git-portable/cmd/git.exe"

# 1. 选 git exe(优先 portable,fallback 到 PATH)
$GitExe = if (Test-Path $GitPortableExe) {
    $GitPortableExe
} else {
    Write-Warning "[warn] bin/git-portable/cmd/git.exe not found; falling back to system 'git'."
    "git"
}

# 2. 幂等:目录存在 + main.py 存在 → 跳过
if ((Test-Path $ComfyUiDir) -and (Test-Path (Join-Path $ComfyUiDir "main.py"))) {
    $existing = & $GitExe -C $ComfyUiDir rev-parse --short HEAD 2>&1
    Write-Host "[skip] ComfyUI template already present at $ComfyUiDir (HEAD=$existing)" -ForegroundColor DarkGray
    return
}

# 3. 目录存在但内容不对(比如上次 fetch 中断) → 清掉重拉
if (Test-Path $ComfyUiDir) {
    Write-Host "[clean] Removing stale $ComfyUiDir" -ForegroundColor Yellow
    Remove-Item -Recurse -Force $ComfyUiDir
}

# 4. shallow clone
Write-Host "[fetch] Cloning $RemoteUrl ($Ref) → $ComfyUiDir" -ForegroundColor Yellow
& $GitExe clone --depth 1 --branch $Ref $RemoteUrl $ComfyUiDir
if ($LASTEXITCODE -ne 0) {
    throw "git clone failed (exit=$LASTEXITCODE)"
}

# 5. 验证
$MainPy = Join-Path $ComfyUiDir "main.py"
if (-not (Test-Path $MainPy)) {
    throw "Clone did not produce expected main.py at $MainPy — refusing to ship"
}

$Head = & $GitExe -C $ComfyUiDir rev-parse --short HEAD 2>&1
$Size = (Get-ChildItem $ComfyUiDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host "[ok] Installed ComfyUI template: HEAD=$Head, $Size MB at $ComfyUiDir" -ForegroundColor Green
```

- [ ] **Step 2: Modify `scripts/build_release.ps1` to call new script**

Read `scripts/build_release.ps1` first to find the git-portable fetch step. Add a new step immediately after it (before `dotnet publish`):

```powershell
# 4.5: fetch ComfyUI source template(幂等,跟 git-portable 同套路)
& (Join-Path $PSScriptRoot "fetch_comfyui_template.ps1") -ProjectRoot $ProjectRoot
if ($LASTEXITCODE -ne 0) { throw "ComfyUI template fetch failed" }
```

Use the same `& ... ; if ($LASTEXITCODE -ne 0) { throw ... }` pattern as the existing git-portable step.

- [ ] **Step 3: Add `ComfyUI/` to `.gitignore`**

Append to `D:/ToolDevelop/ComfyUI/.gitignore` (after the `bin/` / `python/` block at lines around 38-39, or wherever fits):

```
# ComfyUI source template (bundled at build time, not tracked in git)
ComfyUI/
```

- [ ] **Step 4: Manual smoke test**

Run:
```bash
powershell -ExecutionPolicy Bypass -File scripts/fetch_comfyui_template.ps1
```

Verify:
- `<repo>/ComfyUI/main.py` exists
- `<repo>/ComfyUI/.git/` exists
- `<repo>/ComfyUI/comfy/` Python package exists
- Re-running the script prints `[skip] ComfyUI template already present ...`
- No errors

Then run `dotnet build src-wpf/ComfyUI.Manager/ -v minimal` — should still succeed (this task doesn't touch WPF code).

- [ ] **Step 5: Commit**

```bash
git add scripts/fetch_comfyui_template.ps1 \
        scripts/build_release.ps1 \
        .gitignore
git commit -m "feat(scripts): bundle ComfyUI source template via auto-fetch on release build"
```

NOTE: this commit does NOT include the actual `<repo>/ComfyUI/` directory (it's .gitignore'd). The T11 release step will run `scripts/fetch_comfyui_template.ps1` automatically as part of `build_release.ps1`, so the bundled dir is created at build time and ends up in the zip.

---

## Task 11: 全量 test 跑 + UI 手动验证 + bump v0.6.4 + release

**Files:**
- Modify: 5 version literals (csproj / pyproject / __init__.py / errors.json / test_version_consistency.py)
- Create: `release/RELEASE-NOTES-v0.6.4.md`
- Modify: `.superpowers/sdd/progress.md` (ledger update)

- [ ] **Step 1: Run full test suites (WPF + Python)**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
pytest tests/test_version_consistency.py -v
```

Expected:
- WPF: ~82-83 tests PASS
- pytest: 3/3 version consistency tests pass (still 0.6.3 until step 2 bump)

- [ ] **Step 2: Bump version 0.6.3 → 0.6.4 in 5 files**

Edit `src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj`: line `<Version>0.6.3</Version>` → `<Version>0.6.4</Version>`
Edit `pyproject.toml`: line `version = "0.6.3"` → `version = "0.6.4"`
Edit `src/comfy_mgr/__init__.py`: line `__version__ = "0.6.3"` → `__version__ = "0.6.4"`
Edit `shared/errors.json`: `"_version": "0.6.3"` → `"_version": "0.6.4"`
Edit `tests/test_version_consistency.py`: 3 assertions 0.6.3 → 0.6.4

- [ ] **Step 3: Run version consistency tests**

Run: `pytest tests/test_version_consistency.py -v`
Expected: 3/3 PASS.

- [ ] **Step 4: Kill any running exe + build zip**

```bash
taskkill //F //IM ComfyUI.Manager.exe 2>&1 | Out-Null
powershell -ExecutionPolicy Bypass -File scripts/build_release.ps1 -Version 0.6.4
```

Expected: `release/ComfyUI-Manager-v0.6.4-win-x64.zip` (~300 MB; v0.6.3 was 253.9 MB, +50 MB ComfyUI source template).

Verify zip contents include:
- `ComfyUI Manager.exe` (WPF)
- `python/` (portable Python template)
- `ComfyUI/` (ComfyUI source template — T12 added)
- `bin/git-portable/cmd/git.exe` (git portable)
- `release/ComfyUI-Manager-v0.6.4-win-x64.zip` is created

- [ ] **Step 5: Manual UI smoke**

Launch the built exe; verify:
1. Catalog page → empty state "暂无数据,去 Settings 刷新"
2. Settings → "刷新节点目录" 按钮 visible
3. Click refresh → wait 15s → toast "刷新成功,共 N 个条目"
4. Switch back to Catalog → DataGrid 满 20 行 + bottom `1 / N`
5. Click `磁贴` → switch to WrapPanel cards
6. Close + relaunch → view mode persists
7. Page 2 → page 1 via Prev
8. Offline: click Catalog top refresh → "拉取失败: <reason>"

- [ ] **Step 6: Write release notes**

Write to `release/RELEASE-NOTES-v0.6.4.md` covering:
- 用户痛点:Catalog 默认 10 个 → 改为 Settings 手动刷新 + 分页 + 视图切换
- 新功能(Settings 加刷新按钮 + 分页 + 视图切换 + cache db 拆分)
- 升级注意(自动 rename catalog.db → state.db;catalog_cache 需手动刷新)
- 包含的 commits since v0.6.3
- 测试(WPF 82-83 / pytest 3/3)
- 已知 carry-over (4 pre-existing WS tests, InfoMessage 未绑 XAML)

- [ ] **Step 7: Commit + push + release**

```bash
git add src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
        pyproject.toml \
        src/comfy_mgr/__init__.py \
        shared/errors.json \
        tests/test_version_consistency.py \
        release/RELEASE-NOTES-v0.6.4.md \
        .superpowers/sdd/progress.md
git commit -m "chore(release): bump to v0.6.4 + release notes"
git push origin main
git push origin v0.6.4
gh release create v0.6.4 release/ComfyUI-Manager-v0.6.4-win-x64.zip \
    --notes-file release/RELEASE-NOTES-v0.6.4.md \
    --title "v0.6.4 — Catalog: Settings 手动刷新 + 分页 + 磁贴/列表 + cache 拆分"
```

Verify: `gh release list` shows v0.6.4 as **Latest**.

---

## Self-Review (after writing all tasks)

1. **Spec coverage:** Skim spec §9 T1-T11 + §10 验收 — every requirement maps to a task:
   - Settings 字段 + Defaults → T1 ✓
   - db 拆分 → T2 ✓
   - Repo 改造 → T3 ✓
   - RefreshService → T4 ✓
   - CatalogViewModel 分页/视图/Refresh → T5 ✓
   - SettingsViewModel RefreshCatalogCommand → T6 ✓
   - CatalogView.xaml → T7 ✓
   - SettingsView.xaml → T8 ✓
   - App.xaml.cs 注入 → T9 ✓
   - Theme.xaml TileTemplate → T10 ✓
   - release → T11 ✓

2. **Placeholder scan:** No "TBD" / "TODO" / "fill in later" — all code blocks complete.

3. **Type consistency:**
   - `CatalogViewModel` ctor: 6 args (catRepo, envRepo, nodeOps, refreshService, settings, settingsRepo) — used in T5 definition, T9 ctor wiring, T5 tests. ✓
   - `SettingsViewModel` ctor: 3 args (repo, proxy, refreshService) — used in T6 definition, T6 tests, T9 ctor wiring. ✓
   - `MainViewModel` ctor: 11 args — used in T9 definition, T9 App.xaml.cs wiring. ✓
   - `CatalogCacheStore` ctor: `()` or `(string dbPath)` — T2 definition + T3 usage. ✓
   - `CatalogRefreshService.RefreshAsync` signature matches T4 + T5 + T6 callers. ✓

4. **Pre-existing bug fix:** `CatalogView.xaml` 5 列绑定到不存在的 `Author`/`Stars`/`Description` → T7 改 XAML 路径为 `RawMetadata[author]` 等 dictionary indexer. CatalogEntry 模型不动(避免 schema 变化)。 ✓

5. **Edge cases:**
   - 旧 db rename 容错 (T2): try/catch 包住 `File.Move`,失败时仍用 state.db 路径(下次启动再试)
   - 空列表 + 空 active → RefreshService 返 Fail 而非抛 (T4)
   - fetch 异常 → RefreshService 返 Fail 而非抛 (T4)
   - 查询源为空但 list 有 → CatalogViewModel 显示本地 cache (T5)
   - 视图模式 set 时持久化 (T5 via `_settingsRepo.Save`)
   - 分页边界 (T5 NextPage / PrevPage CanExecute)

Plan complete and saved to `docs/superpowers/plans/2026-07-18-hotfix-catalog-pagination.md`. Two execution options:

**1. Subagent-Driven (recommended)** — Dispatch a fresh subagent per task with TDD discipline; reviewer between tasks; whole-branch review at end.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch with checkpoints for review.

Which approach?
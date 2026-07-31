# Multi-Version BED Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在基础环境部署页面增加 PyTorch stable/nightly 版本选择，并根据所选版本显示可安装的 CUDA 与 CPU profiles，同时保持 v0.6.5.2 的安装与 fallback 兼容性。

**Architecture:** 保留现有 `BaseEnvProfileLoader`、`BaseEnvInstaller`、`BaseEnvViewModel` 和 progress UI。新增 PyPI 版本目录、永久目录 cache 与目录协调层；ViewModel 选择目录项后调用 Loader 按版本生成 profiles。旧的单版本 `pytorch_versions_cache.json` 与新的 `pytorch_catalog_cache.json` 独立存在。

**Tech Stack:** WPF .NET 8 / C# 12、xUnit、Moq、`HttpClient`/`HttpMessageHandler` fake、`System.Text.Json`、现有 MVVM 基础设施。

## Global Constraints

- stable 数据源必须是 `https://pypi.org/pypi/torch/json`。
- stable 版本必须过滤 pre-release、development 和 post-release 版本。
- CUDA 变体必须从 wheel filename 的 local tag 动态解析；不得写死为全版本共用列表。
- CPU profile 随所选 stable 版本生成，不是独立 dropdown 项。
- nightly 是虚拟 dropdown 项，始终位于第一项，选中后只显示 nightly cu126 profile。
- latest stable 是第一项 stable，并且是默认选择项。
- catalog cache 文件必须是 `%APPDATA%/ComfyUI-Manager/pytorch_catalog_cache.json`。
- catalog cache 永久有效、无 TTL；删除 cache 文件才会触发重新获取。
- 旧 `%APPDATA%/ComfyUI-Manager/pytorch_versions_cache.json` 必须继续保留，不得覆盖或复用。
- cache miss 后 PyPI 请求失败时必须使用 v0.6.5.2 fallback，UI 不得显示空 profiles。
- fallback 不写入永久 catalog cache。
- `<exe-dir>/base_env_profiles.json` 的用户 override 行为保持优先。
- `BaseEnvProfile`、`BaseEnvInstaller`、`BaseEnvProgressViewModel` 和 `BaseEnvProgressDialog` 的现有安装/进度契约不改变。
- 所有网络测试必须使用假的 `HttpMessageHandler`，不得访问真实 PyPI 或 pytorch.org。
- release notes 必须说明永久 cache 需要手动删除才能刷新。

---

## File Map

### New files

- `src-wpf/ComfyUI.Manager/Data/PyTorchVersion.cs` — 单个 stable 版本及 release date、CUDA/CPU 能力。
- `src-wpf/ComfyUI.Manager/Data/PyTorchVersionCatalog.cs` — 请求并解析 PyPI torch JSON。
- `src-wpf/ComfyUI.Manager/Data/PyTorchVersionCatalogCache.cs` — 永久 JSON cache。
- `src-wpf/ComfyUI.Manager/Data/PyTorchVersionDirectory.cs` — cache/fetch/fallback、nightly 注入和排序。
- `tests-wpf/ComfyUI.Manager.Tests/Data/PyTorchVersionCatalogTests.cs` — catalog parser/fetch tests。
- `tests-wpf/ComfyUI.Manager.Tests/Data/PyTorchVersionCatalogCacheTests.cs` — cache IO tests。
- `tests-wpf/ComfyUI.Manager.Tests/Data/PyTorchVersionDirectoryTests.cs` — directory orchestration tests。

### Modified files

- `src-wpf/ComfyUI.Manager/Data/BaseEnvProfileLoader.cs` — 增加按版本生成 profiles 的入口。
- `tests-wpf/ComfyUI.Manager.Tests/Data/BaseEnvProfileLoaderTests.cs` — 增加 stable/nightly/CPU/variant tests。
- `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvViewModel.cs` — 增加版本列表和 selection reload。
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvViewModelTests.cs` — 增加版本切换测试。
- `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml` — 增加 ComboBox。
- `src-wpf/ComfyUI.Manager/App.xaml.cs` — 装配 catalog/cache/directory 并注入 VM。
- `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` — 若现有构造链需要，将 directory 传给 BaseEnvViewModel。
- `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml.cs` — 仅在现有 code-behind 需要显式处理 selection 时修改。
- `release/RELEASE-NOTES-v0.6.5.3.md` — 发布说明及永久 cache 提示。
- `.superpowers/sdd/progress.md` — SDD task ledger 收尾。

---

## Task 1: PyTorchVersionCatalog and Model

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Data/PyTorchVersion.cs`
- Create: `src-wpf/ComfyUI.Manager/Data/PyTorchVersionCatalog.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Data/PyTorchVersionCatalogTests.cs`

**Interfaces:**
- Consumes: `HttpClient` injected through `PyTorchVersionCatalog(HttpClient http)`.
- Produces:
  ```csharp
  public sealed class PyTorchVersion
  {
      public string Version { get; init; } = "";
      public DateTimeOffset ReleaseDate { get; init; }
      public IReadOnlyList<string> CudaVariants { get; init; } = Array.Empty<string>();
      public bool HasCpu { get; init; }
  }

  public sealed class PyTorchVersionCatalog
  {
      public const string PageUrl = "https://pypi.org/pypi/torch/json";
      public PyTorchVersionCatalog(HttpClient http);
      public Task<IReadOnlyList<PyTorchVersion>?> FetchAsync(CancellationToken ct = default);
      internal static IReadOnlyList<PyTorchVersion>? Parse(string json);
  }
  ```

- [ ] **Step 1: Write failing parser tests.**

Use a compact JSON fixture with stable `2.13.0`, stable `2.5.1`, a `2.6.0rc1`, filenames containing `+cu126`, `+cu121`, and `+cpu`, and upload timestamps. Assert that stable entries are retained, `2.6.0rc1` is absent, CUDA tags are deduplicated, CPU is detected, and release date is the latest valid upload time.

```csharp
[Fact]
public void Parse_FiltersPrereleaseAndExtractsWheelVariants()
{
    var result = PyTorchVersionCatalog.Parse(FixtureJson);

    var latest = Assert.Single(result!, x => x.Version == "2.13.0");
    Assert.Equal(new[] { "cu126" }, latest.CudaVariants);
    Assert.True(latest.HasCpu);
    Assert.DoesNotContain(result!, x => x.Version == "2.6.0rc1");
}
```

Add tests for multiple CUDA tags, duplicate tags, missing releases, invalid JSON, and post/development versions.

- [ ] **Step 2: Run parser tests to verify failure.**

Run:

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~PyTorchVersionCatalogTests" -v minimal
```

Expected: FAIL because the model/parser does not exist.

- [ ] **Step 3: Implement the model and parser.**

Read `releases` as JSON, parse each stable version with `System.Version`/NuGet version semantics already used by the project, skip pre-release/post/development identifiers, inspect every file’s `filename` and `upload_time`, match local tags such as `+cu118` and `+cpu`, deduplicate tags, and return null for malformed input. Keep `FetchAsync` responsible for GET, status validation, JSON reading, and returning null on request/parse failures.

- [ ] **Step 4: Add HTTP fake tests.**

Use Moq’s protected `HttpMessageHandler.SendAsync` pattern from `BaseEnvProfileLoaderTests.cs`. Assert that `FetchAsync` requests `PageUrl`, parses a successful response, returns null for 404, and returns null for `HttpRequestException`.

- [ ] **Step 5: Run focused and project tests.**

Run:

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~PyTorchVersionCatalogTests" -v minimal
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```

Expected: all focused and existing tests pass.

- [ ] **Step 6: Commit.**

```bash
git add src-wpf/ComfyUI.Manager/Data/PyTorchVersion.cs src-wpf/ComfyUI.Manager/Data/PyTorchVersionCatalog.cs tests-wpf/ComfyUI.Manager.Tests/Data/PyTorchVersionCatalogTests.cs
git commit -m "feat(wpf): parse PyTorch versions from PyPI"
```

---

## Task 2: Permanent Catalog Cache

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Data/PyTorchVersionCatalogCache.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Data/PyTorchVersionCatalogCacheTests.cs`

**Interfaces:**
- Consumes: `PyTorchVersion` from Task 1.
- Produces:
  ```csharp
  public sealed class PyTorchVersionCatalogCache
  {
      public const string FileName = "pytorch_catalog_cache.json";
      public PyTorchVersionCatalogCache(string appDataDir);
      public string FilePath { get; }
      public Task<IReadOnlyList<PyTorchVersion>?> TryReadAsync(CancellationToken ct = default);
      public Task WriteAsync(IReadOnlyList<PyTorchVersion> versions, CancellationToken ct = default);
  }
  ```

- [ ] **Step 1: Write failing cache tests.**

Cover missing file, valid round-trip, malformed JSON, permanent validity after an old timestamp, directory creation, and write failure not throwing into callers.

```csharp
[Fact]
public async Task TryRead_ReturnsNullWhenFileMissing()
{
    var cache = new PyTorchVersionCatalogCache(TemporaryDirectory());
    Assert.Null(await cache.TryReadAsync());
}
```

- [ ] **Step 2: Run focused tests to verify failure.**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~PyTorchVersionCatalogCacheTests" -v minimal
```

Expected: FAIL because the cache type does not exist.

- [ ] **Step 3: Implement permanent JSON cache.**

Use the project’s `System.Text.Json` options. `TryReadAsync` returns null for missing/corrupt content and never applies TTL. `WriteAsync` creates the parent directory and serializes the complete list. Preserve cancellation for normal IO; make the documented write-failure path non-fatal.

- [ ] **Step 4: Run focused tests.**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~PyTorchVersionCatalogCacheTests" -v minimal
```

Expected: all cache tests pass.

- [ ] **Step 5: Commit.**

```bash
git add src-wpf/ComfyUI.Manager/Data/PyTorchVersionCatalogCache.cs tests-wpf/ComfyUI.Manager.Tests/Data/PyTorchVersionCatalogCacheTests.cs
git commit -m "feat(wpf): add permanent PyTorch catalog cache"
```

---

## Task 3: Version Directory

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Data/PyTorchVersionDirectory.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Data/PyTorchVersionDirectoryTests.cs`

**Interfaces:**
- Consumes: `PyTorchVersionCatalog`, `PyTorchVersionCatalogCache`, and `PyTorchVersion`.
- Produces:
  ```csharp
  public sealed class PyTorchVersionDirectory
  {
      public const string NightlyVersion = "nightly";
      public PyTorchVersionDirectory(PyTorchVersionCatalog catalog, PyTorchVersionCatalogCache cache);
      public Task<IReadOnlyList<PyTorchVersionEntry>> GetAllAsync(CancellationToken ct = default);
  }

  public sealed class PyTorchVersionEntry
  {
      public string Version { get; init; } = "";
      public bool IsNightly { get; init; }
      public string DisplayName { get; init; } = "";
      public PyTorchVersion? StableMetadata { get; init; }
  }
  ```

- [ ] **Step 1: Write failing orchestration tests.**

Use fake catalog/cache implementations or injected test doubles matching the production boundary. Assert cache hit avoids catalog fetch, cache miss fetches and writes, stable entries are release-date descending, nightly is index 0, and latest stable is the first stable. Add a failure test asserting fallback contains `2.13.0`, stable CUDA/CPU metadata compatible with v0.6.5.2, and nightly entry.

- [ ] **Step 2: Run focused tests to verify failure.**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~PyTorchVersionDirectoryTests" -v minimal
```

Expected: FAIL because directory types do not exist.

- [ ] **Step 3: Implement cache → fetch → fallback.**

Read cache first. If null, call catalog and write only successful catalog data. On fetch failure return fixed v0.6.5.2-compatible fallback metadata without writing it. Always create nightly as the first virtual entry and stable entries from metadata sorted by `ReleaseDate` descending. Do not touch `pytorch_versions_cache.json`.

- [ ] **Step 4: Run focused tests.**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~PyTorchVersionDirectoryTests" -v minimal
```

Expected: all directory tests pass.

- [ ] **Step 5: Commit.**

```bash
git add src-wpf/ComfyUI.Manager/Data/PyTorchVersionDirectory.cs tests-wpf/ComfyUI.Manager.Tests/Data/PyTorchVersionDirectoryTests.cs
git commit -m "feat(wpf): add PyTorch version directory"
```

---

## Task 4: Version-Aware Base Environment Profiles

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Data/BaseEnvProfileLoader.cs`
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Data/BaseEnvProfileLoaderTests.cs`

**Interfaces:**
- Consumes: `PyTorchVersionEntry`/`PyTorchVersion` metadata from Task 3 and existing loader constructor.
- Produces:
  ```csharp
  public Task<IReadOnlyList<BaseEnvProfile>> LoadProfilesForVersionAsync(
      string version,
      CancellationToken ct = default);
  ```

- [ ] **Step 1: Write failing tests.**

Add tests asserting stable `2.5.1` profiles use that exact `TorchVersion`, include only metadata CUDA variants plus CPU, and preserve existing package lists. Add tests for CPU-only metadata and nightly producing exactly one `nightly`/`cu126` profile. Add a test that existing `base_env_profiles.json` behavior remains unchanged.

```csharp
[Fact]
public async Task LoadProfilesForVersion_StableUsesSelectedVersionAndVariants()
{
    var loader = new BaseEnvProfileLoader(tempDir);
    var profiles = await loader.LoadProfilesForVersionAsync("2.5.1", metadata);
    Assert.All(profiles.Where(x => x.Channel == "stable"), x => Assert.Equal("2.5.1", x.TorchVersion));
}
```

The implementation may expose an overload accepting `PyTorchVersion` metadata if needed; the final public entry point must support the exact version-selection call used by ViewModel and must not hardcode all CUDA variants.

- [ ] **Step 2: Run focused tests to verify failure.**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvProfileLoaderTests" -v minimal
```

Expected: FAIL because the version-aware method and metadata path do not exist.

- [ ] **Step 3: Implement minimal version-aware generation.**

Keep the existing hardcoded/live fallback methods intact for compatibility. Add a metadata-aware generation path that constructs stable CUDA profiles from the selected version’s `CudaVariants`, adds CPU when `HasCpu` is true (or preserves the agreed fallback CPU behavior), and constructs nightly cu126 with `TorchVersion = "nightly"`. Reuse existing names, package lists, channels, and pip argument conventions. Do not modify `BaseEnvProfile`, installer, or progress types.

- [ ] **Step 4: Run focused and regression tests.**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvProfileLoaderTests" -v minimal
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```

Expected: all loader and existing tests pass.

- [ ] **Step 5: Commit.**

```bash
git add src-wpf/ComfyUI.Manager/Data/BaseEnvProfileLoader.cs tests-wpf/ComfyUI.Manager.Tests/Data/BaseEnvProfileLoaderTests.cs
git commit -m "feat(wpf): generate BED profiles per PyTorch version"
```

---

## Task 5: Version-Aware BaseEnvViewModel

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/BaseEnvViewModel.cs`
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvViewModelTests.cs`

**Interfaces:**
- Consumes: `PyTorchVersionDirectory.GetAllAsync`, `PyTorchVersionEntry`, and Loader’s version-aware profile method.
- Produces:
  ```csharp
  public ObservableCollection<PyTorchVersionEntry> Versions { get; }
  public PyTorchVersionEntry? SelectedVersion { get; set; }
  ```

- [ ] **Step 1: Write failing ViewModel tests.**

Add a fake directory and fake loader seam, then test: `LoadAsync` fills Versions; nightly is first; latest stable is default; selected stable loads matching profiles; changing `SelectedVersion` refreshes profiles and clears profile selection; Envs and selected environment IDs remain unchanged; fallback profiles remain available; StartCommand state is updated.

```csharp
[Fact]
public async Task LoadAsync_DefaultsToLatestStableAndLoadsProfiles()
{
    var vm = MakeVmWithDirectory(FakeDirectoryWithNightlyAndStableVersions());
    await vm.LoadAsync();
    Assert.True(vm.Versions[0].IsNightly);
    Assert.Equal("2.13.0", vm.SelectedVersion!.Version);
    Assert.All(vm.Profiles, p => Assert.Equal("2.13.0", p.TorchVersion));
}
```

- [ ] **Step 2: Run focused tests to verify failure.**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvViewModelTests" -v minimal
```

Expected: FAIL because Versions, SelectedVersion, and directory injection do not exist.

- [ ] **Step 3: Implement directory injection and selection refresh.**

Extend the constructor with the directory dependency while preserving existing testability. `LoadAsync` gets directory entries, sets default to the first stable entry rather than nightly, loads profiles, and reloads Envs once. Selection changes asynchronously reload profiles only, clear selected profiles, preserve environment selection, and raise `StartCommand` availability. Ensure a stale selection cannot replace a newer selection’s profiles; use the project’s existing async/UI continuation style.

- [ ] **Step 4: Run focused and regression tests.**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~BaseEnvViewModelTests" -v minimal
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```

Expected: all ViewModel and existing tests pass.

- [ ] **Step 5: Commit.**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/BaseEnvViewModel.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/BaseEnvViewModelTests.cs
git commit -m "feat(wpf): add PyTorch version selection to BED view model"
```

---

## Task 6: BaseEnvView ComboBox

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml`
- Modify only if required by existing event wiring: `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml.cs`

**Interfaces:**
- Consumes: ViewModel `Versions`, `SelectedVersion`, and `PyTorchVersionEntry.DisplayName`.
- Produces: a bound ComboBox above `ProfileListBox`; existing profile/env selection and Start behavior remain unchanged.

- [ ] **Step 1: Add a binding verification test or build-time XAML test fixture.**

Use the project’s existing WPF test/build conventions to verify the XAML contains `ItemsSource="{Binding Versions}"`, `SelectedItem="{Binding SelectedVersion}"`, `DisplayMemberPath="DisplayName"`, and that the ComboBox occurs before `ProfileListBox`. If no XAML parser test exists, make the smallest XAML change and use the WPF project build as the failing verification boundary.

- [ ] **Step 2: Run the verification to establish the pre-change failure.**

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

Expected: the binding assertion fails if a test is added; otherwise this step records the baseline before the XAML change.

- [ ] **Step 3: Add the ComboBox.**

Insert directly above the profile ListBox:

```xml
<ComboBox DockPanel.Dock="Top"
          Margin="0,0,0,8"
          ItemsSource="{Binding Versions}"
          SelectedItem="{Binding SelectedVersion}"
          DisplayMemberPath="DisplayName" />
```

Do not alter the profile DataTemplate, environment ListBox, selection events, or Start button.

- [ ] **Step 4: Build the WPF project.**

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

Expected: 0 errors.

- [ ] **Step 5: Commit.**

```bash
git add src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml.cs
git commit -m "feat(wpf): add PyTorch version selector to BED view"
```

---

## Task 7: Application Dependency Wiring

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs`
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`
- Modify: `src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml.cs` only if constructor/data-context wiring requires it.

**Interfaces:**
- Consumes: `PyTorchVersionCatalog(HttpClient)`, `PyTorchVersionCatalogCache(appDataDir)`, `PyTorchVersionDirectory(catalog, cache)`, and the updated `BaseEnvViewModel` constructor.
- Produces: one shared catalog directory instance passed through the existing MainViewModel construction path.

- [ ] **Step 1: Write a wiring/build regression test.**

Add the narrowest available composition test or constructor test proving that the MainViewModel/BaseEnvViewModel receives a non-null directory and that App uses the shared 15-second `HttpClient`. Do not start WPF or make a real network request in the test.

- [ ] **Step 2: Run the test/build before implementation.**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~MainViewModel" -v minimal
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

Expected: the new wiring assertion fails or compilation reports the missing constructor dependency.

- [ ] **Step 3: Wire the dependencies.**

In `App.xaml.cs`, reuse the existing `http` instance and `appDataDir`, create `PyTorchVersionCatalog`, `PyTorchVersionCatalogCache`, and `PyTorchVersionDirectory`, then pass the directory through MainViewModel to BaseEnvViewModel. Keep the existing `BaseEnvProfileLoader(projectRoot, appDataDir, http)` wiring and preserve the old `pytorch_versions_cache.json` path.

- [ ] **Step 4: Run tests and Release build.**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -v minimal
```

Expected: all tests pass and the WPF Release build reports 0 errors.

- [ ] **Step 5: Commit.**

```bash
git add src-wpf/ComfyUI.Manager/App.xaml.cs src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml.cs
git commit -m "feat(wpf): wire PyTorch version directory into BED"
```

---

## Task 8: Verification, Manual Smoke, Release Notes, and Ledger

**Files:**
- Create: `release/RELEASE-NOTES-v0.6.5.3.md`
- Modify: `.superpowers/sdd/progress.md`
- Modify if required by release process: version consistency files and release metadata only after confirming the intended release version.

**Interfaces:**
- Consumes: completed catalog, cache, directory, loader, ViewModel, XAML, and App wiring tasks.
- Produces: verified multi-version BED feature and SDD ledger entry; no external push/release action is included without explicit user authorization.

- [ ] **Step 1: Add failing release-note content check.**

Create a test or scripted check that release notes contain the permanent cache path and manual-delete instruction:

```text
pytorch_catalog_cache.json
删除
永久
```

Run it before writing the notes and verify it fails because the file does not exist.

- [ ] **Step 2: Write release notes.**

Document the new dropdown, all stable versions, per-version wheel-derived CUDA variants, CPU profile, nightly cu126, permanent cache behavior, manual cache deletion path, PyPI source, and v0.6.5.2 fallback. Do not claim a release was published unless the release process is explicitly requested.

- [ ] **Step 3: Run automated verification.**

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
python -m pytest tests/test_version_consistency.py -q
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -v minimal
```

Expected: all tests pass, version consistency passes, and Release build has 0 errors. Record actual counts in the ledger, not estimates.

- [ ] **Step 4: Run manual smoke from staging.**

Start `release/staging/ComfyUI Manager/ComfyUI.Manager.exe` directly. Verify: the BED page shows the ComboBox; default is latest stable; at least two stable versions change profile TorchVersion/CUDA entries; nightly shows only nightly cu126; selecting a profile and environment enables Start; restarting uses the catalog cache; deleting `%APPDATA%/ComfyUI-Manager/pytorch_catalog_cache.json` allows refetch; offline startup still shows fallback profiles. Do not run a real pip installation.

- [ ] **Step 5: Update the SDD ledger.**

Append one completion line for each reviewed task and the final verification result to `.superpowers/sdd/progress.md`, including commit ranges and actual test output. Leave unrelated pre-existing edits untouched.

- [ ] **Step 6: Commit notes and ledger.**

```bash
git add release/RELEASE-NOTES-v0.6.5.3.md .superpowers/sdd/progress.md
git commit -m "docs(sdd): close out multi-version BED implementation"
```

- [ ] **Step 7: Report release boundary.**

Report the commits, test/build results, and manual smoke outcome. Ask separately before any `git push`, tag creation, GitHub release, or release artifact build because those affect shared external state.

---

## Self-Review

- Spec coverage: catalog parsing is Task 1; permanent cache is Task 2; directory fallback/sorting/nightly is Task 3; version-aware profiles are Task 4; ViewModel selection is Task 5; ComboBox is Task 6; App wiring is Task 7; testing, manual smoke, cache instructions, and ledger are Task 8. System overview remains explicitly out of scope.
- Placeholder scan: no `TBD`, `TODO`, or unspecified implementation step is used. The XAML verification step explicitly permits the project’s existing build boundary if no XAML parser test exists.
- Type consistency: Task 1’s `PyTorchVersion` is consumed by Tasks 2–4; Task 3’s `PyTorchVersionEntry` is consumed by Tasks 4–7; Task 4’s public loader method is consumed by Task 5; Task 5’s properties are consumed by Task 6; Task 7 wires the same objects.
- Release boundary: the plan does not authorize pushing or publishing externally; it only prepares notes, verification, and ledger closure.

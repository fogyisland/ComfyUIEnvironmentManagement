# v0.6.3 Hotfix — 节点下载/查询源可配置下拉列表 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** WPF Settings adds two ComboBox-driven source lists (query + download), each editable and selectable as active. Catalog refresh and node install both consume the active selections.

**Architecture:**
- New model `NodeSource {Name, Url}` mirrors `ExtraPath {Name, Path}`.
- `Settings` gets 4 new fields (2 lists + 2 active-name strings); `SettingsDefaults.Apply` fills empties with the built-in "comfyui manager" entries.
- `SettingsViewModel` exposes two `ObservableCollection<NodeSource>` + two active selection properties, mirrors the `ExtraPaths` collection-change-to-disk pattern.
- `Services/NodeUrlResolver` is a pure static helper that substitutes `{node}` in the download URL.
- `Services/CatalogFetcher` is a new HTTP-GET-then-parse helper that `CatalogViewModel.Refresh` calls instead of the existing `MessageBox.Show("TODO(M5.2-T7)")` stub.
- `NodeOperations.InstallAsync` and `CatalogViewModel` both receive the live `Settings` instance, so toggling active in Settings takes effect immediately.

**Tech Stack:** .NET 8, WPF, MVVM (hand-rolled `ViewModelBase` + `RelayCommand`), `System.Net.Http.HttpClient`, `System.Text.Json`, Microsoft.Data.Sqlite, Moq (test doubles for HttpClient via `MockHttpMessageHandler`).

**Base SHA:** v0.6.2 tag (`564c481`).

**Spec:** `docs/superpowers/specs/2026-07-18-hotfix-node-source-list-design.md` (commit `a8b8a6f`).

## Global Constraints

[Copy these verbatim into every task's requirements — they bind the whole hotfix.]

- New fields on `Settings` go **after** `ExtraPaths` at `src-wpf/ComfyUI.Manager/Models/Settings.cs:31`. `CompatApiBaseUrl` stays untouched at line 15.
- Default query URL (exact string): `https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main/custom-node-list.json`.
- Default download URL (exact string): `https://github.com/comfyanonymous/{node}`.
- Both lists' default `ActiveXxxSourceName` = `"comfyui manager"` (exact).
- HTTP timeout for `CatalogFetcher`: 15 seconds.
- `SettingsDefaults.Apply` must not throw on null settings (preserve `if (s is null) return;` at line 48).
- `NodeOperations.InstallAsync` signature stays `Task<NodeOperationResult> InstallAsync(string envId, string nodeId, string repoUrl, CancellationToken ct = default)` — add `Settings` via ctor; do not change method signature.
- Tests must all pass: `dotnet test tests-wpf/ComfyUI.Manager.Tests/` → expected 50+/50+ PASS (was 45/45 before hotfix).
- `compat_api_base_url` field is **untouched** (user decision: it's for compat checking, not for node sources).
- `release/*.zip` already in `.gitignore` — never `git add -A`.
- WPF runtime is .NET 8 self-contained; do not add new NuGet packages (HttpClient is in `System.Net.Http`, Moq already in test project).

## Existing code touch points (read before starting)

- `src-wpf/ComfyUI.Manager/Models/Settings.cs` — lines 1-38, add fields after line 31
- `src-wpf/ComfyUI.Manager/Models/CatalogEntry.cs` — lines 1-23, `Id` / `SourceUrl` / `Package` / `RawMetadata` / `CachedAt` / `ExpiresAt`
- `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs` — lines 46-54 (`Apply`), add new defaults at end
- `src-wpf/ComfyUI.Manager/Data/SettingsRepository.cs` — lines 43-70 (`Load` / `Save`), JSON serializer handles new fields automatically (PropertyNameCaseInsensitive + WriteIndented), **no changes needed**
- `src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs` — lines 26-90 (`Search` / `ListNonExpired` / `Upsert`), `Upsert` signature already matches our needs, **no changes needed**
- `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` — lines 11-174 (full file), follow ExtraPaths pattern at lines 22-32
- `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` — lines 33-80 (ctor + ShowCatalog + ShowSettings), inject `Settings` + `CatalogFetcher`
- `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs` — lines 1-106 (full file), Refresh at line 52 is the M5.2-T7 stub
- `src-wpf/ComfyUI.Manager/ViewModels/ViewModelBase.cs` — lines 1-23 (full file), `SetField<T>` + `RaisePropertyChanged()` available
- `src-wpf/ComfyUI.Manager/ViewModels/RelayCommand.cs` — lines 1-30 (full file), **no generic version**, cast parameter inside execute
- `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs` — lines 36-44 (ctor) + lines 53-113 (InstallAsync), ctor needs `Settings` parameter
- `src-wpf/ComfyUI.Manager/App.xaml.cs` — lines 28-66 (wiring), add `CatalogFetcher` instantiation + pass `Settings` to `NodeOperations` and `MainViewModel`
- `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` — lines 1-152 (full file), insert two new sections after line 28 (end of 基础), before line 30 (start of 路径)
- `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsDefaultsTests.cs` — lines 1-115, 8 existing tests must still pass
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelTests.cs` — lines 1-52, 2 existing tests must still pass; ctor signature unchanged
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelTests.cs` — lines 1-68, 2 existing tests use `new CatalogViewModel(repo, envRepo, nodeOps)` — **ctor signature will change**; update those tests
- `tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsTests.cs` — lines 1-289, 5 existing tests use `new NodeOperations(gitRunner, envRepo, nodeRepo)` — **ctor signature will change**; update those tests
- `tests-wpf/ComfyUI.Manager.Tests/Fakes/TestDb.cs` — lines 1-113 (full file), TestDb already provides the catalog_cache schema; no changes needed
- `src-wpf/ComfyUI.Manager.Tests.csproj` — verify Moq is referenced (used for HttpMessageHandler mocking in T5)

---

## Task 1: Model + SettingsDefaults defaults

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Models/NodeSource.cs`
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs:31`
- Modify: `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs:46-54`
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsDefaultsTests.cs`

**Interfaces:**
- Produces `Models.NodeSource { string Name; string Url; }` (default both `""`)
- Produces `Settings.QuerySources: List<NodeSource>` (default empty)
- Produces `Settings.DownloadSources: List<NodeSource>` (default empty)
- Produces `Settings.ActiveQuerySourceName: string` (default `""`)
- Produces `Settings.ActiveDownloadSourceName: string` (default `""`)
- After `SettingsDefaults.Apply` on a fresh `Settings`, these 4 fields must contain: 1-entry list with "comfyui manager" + correct URL, and active name = "comfyui manager"

- [ ] **Step 1: Create `Models/NodeSource.cs`**

Write to `src-wpf/ComfyUI.Manager/Models/NodeSource.cs`:

```csharp
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>
/// NodeSource: a single entry in the user-managed query/download source list.
/// Used as both the catalog JSON source URL (query) and the git clone base URL
/// (download, may contain a <c>{node}</c> placeholder).
/// </summary>
public class NodeSource
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")]  public string Url  { get; set; } = "";
}
```

- [ ] **Step 2: Add 4 fields to `Settings.cs`**

In `src-wpf/ComfyUI.Manager/Models/Settings.cs`, after the `ExtraPaths` declaration on line 31, add (before the closing `}` on line 32):

```csharp

    // —— 节点源(查询/下载):两个列表 + 两个 active 名称 ——
    [JsonPropertyName("query_sources")]
    public List<NodeSource> QuerySources { get; set; } = new();
    [JsonPropertyName("download_sources")]
    public List<NodeSource> DownloadSources { get; set; } = new();
    [JsonPropertyName("active_query_source_name")]
    public string ActiveQuerySourceName { get; set; } = "";
    [JsonPropertyName("active_download_source_name")]
    public string ActiveDownloadSourceName { get; set; } = "";
```

`CompatApiBaseUrl` on line 15 stays untouched.

- [ ] **Step 3: Add 4 default constants + Apply tail to `SettingsDefaults.cs`**

In `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs`, after the existing 4 `Subdir` constants on lines 31-34 (before `public static void Apply` on line 36), add:

```csharp
    public const string DefaultQuerySourceName = "comfyui manager";
    public const string DefaultQuerySourceUrl =
        "https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main/custom-node-list.json";
    public const string DefaultDownloadSourceName = "comfyui manager";
    public const string DefaultDownloadSourceUrl = "https://github.com/comfyanonymous/{node}";
```

Then in the `Apply` method body (after line 53's `s.GlobalNodesDir = MigrateOnly(...)`; before the closing `}` of `Apply`), add:

```csharp

        // 节点源:空列表 → 装默认 "comfyui manager";空 active → 回落到列表第一条
        if (s.QuerySources is null || s.QuerySources.Count == 0)
        {
            s.QuerySources = new List<NodeSource>
            {
                new() { Name = DefaultQuerySourceName, Url = DefaultQuerySourceUrl },
            };
        }
        if (s.DownloadSources is null || s.DownloadSources.Count == 0)
        {
            s.DownloadSources = new List<NodeSource>
            {
                new() { Name = DefaultDownloadSourceName, Url = DefaultDownloadSourceUrl },
            };
        }
        if (string.IsNullOrWhiteSpace(s.ActiveQuerySourceName))
        {
            s.ActiveQuerySourceName = s.QuerySources[0].Name;
        }
        if (string.IsNullOrWhiteSpace(s.ActiveDownloadSourceName))
        {
            s.ActiveDownloadSourceName = s.DownloadSources[0].Name;
        }
```

- [ ] **Step 4: Write 4 new tests in `SettingsDefaultsTests.cs`**

Append to `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsDefaultsTests.cs` (after line 114, the last existing test):

```csharp
    [Fact]
    public void Apply_QuerySources_EmptyGetsDefault()
    {
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Single(s.QuerySources);
        Assert.Equal("comfyui manager", s.QuerySources[0].Name);
        Assert.Equal(SettingsDefaults.DefaultQuerySourceUrl, s.QuerySources[0].Url);
    }

    [Fact]
    public void Apply_DownloadSources_EmptyGetsDefault()
    {
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Single(s.DownloadSources);
        Assert.Equal("comfyui manager", s.DownloadSources[0].Name);
        Assert.Equal(SettingsDefaults.DefaultDownloadSourceUrl, s.DownloadSources[0].Url);
    }

    [Fact]
    public void Apply_ActiveQuerySourceName_EmptyFallbacksToFirst()
    {
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("comfyui manager", s.ActiveQuerySourceName);
    }

    [Fact]
    public void Apply_ActiveDownloadSourceName_EmptyFallbacksToFirst()
    {
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("comfyui manager", s.ActiveDownloadSourceName);
    }

    [Fact]
    public void Apply_ExistingQuerySources_NotOverwritten()
    {
        // 用户已有自定义 query sources → 不覆盖
        var s = new Settings
        {
            QuerySources = new List<NodeSource>
            {
                new() { Name = "my-mirror", Url = "https://my-mirror/catalog.json" },
            },
            ActiveQuerySourceName = "my-mirror",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Single(s.QuerySources);
        Assert.Equal("my-mirror", s.QuerySources[0].Name);
        Assert.Equal("my-mirror", s.ActiveQuerySourceName);
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsDefaultsTests" -v minimal`
Expected: 13/13 PASS (8 old + 5 new).

- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Models/NodeSource.cs \
        src-wpf/ComfyUI.Manager/Models/Settings.cs \
        src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs \
        tests-wpf/ComfyUI.Manager.Tests/Infrastructure/SettingsDefaultsTests.cs
git commit -m "feat(wpf): add NodeSource model + 4 Settings fields + Defaults"
```

---

## Task 2: NodeUrlResolver pure helper

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/NodeUrlResolver.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/NodeUrlResolverTests.cs`

**Interfaces:**
- Produces `Services.NodeUrlResolver` (static class) with `string Resolve(string templateUrl, string nodeId)`
- Behavior: empty/whitespace templateUrl → returns templateUrl; replaces `{node}` with `nodeId`; if templateUrl has no `{node}`, returns templateUrl unchanged

- [ ] **Step 1: Write failing tests**

Create `tests-wpf/ComfyUI.Manager.Tests/Services/NodeUrlResolverTests.cs`:

```csharp
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class NodeUrlResolverTests
{
    [Fact]
    public void Resolve_NodeTemplate_Substitutes()
    {
        var result = NodeUrlResolver.Resolve(
            "https://github.com/comfyanonymous/{node}", "ComfyUI-IPAdapter-Flux");
        Assert.Equal("https://github.com/comfyanonymous/ComfyUI-IPAdapter-Flux", result);
    }

    [Fact]
    public void Resolve_NoTemplate_ReturnsOriginal()
    {
        // 用户可以填一个不带 {node} 的固定 URL
        var url = "https://github.com/foo/SpecificRepo";
        var result = NodeUrlResolver.Resolve(url, "any-node");
        Assert.Equal(url, result);
    }

    [Fact]
    public void Resolve_EmptyUrl_ReturnsEmpty()
    {
        var result = NodeUrlResolver.Resolve("", "any-node");
        Assert.Equal("", result);
    }

    [Fact]
    public void Resolve_WhitespaceUrl_ReturnsWhitespace()
    {
        var result = NodeUrlResolver.Resolve("   ", "any-node");
        Assert.Equal("   ", result);
    }

    [Fact]
    public void Resolve_MultipleNodeOccurrences_AllSubstituted()
    {
        // 防御性:用户写了多次 {node} 时全部替换
        var result = NodeUrlResolver.Resolve(
            "https://mirror/{node}/extra/{node}", "my-node");
        Assert.Equal("https://mirror/my-node/extra/my-node", result);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~NodeUrlResolverTests" -v minimal`
Expected: compile error (NodeUrlResolver doesn't exist yet).

- [ ] **Step 3: Implement `NodeUrlResolver.cs`**

Create `src-wpf/ComfyUI.Manager/Services/NodeUrlResolver.cs`:

```csharp
namespace ComfyUI.Manager.Services;

/// <summary>
/// NodeUrlResolver: 把下载源模板 URL 里的 <c>{node}</c> 占位替换成实际 node id。
/// 纯静态函数,无副作用,易于单测。
///
/// 规则:
/// - 空 / 空白 templateUrl → 原样返回
/// - 包含 <c>{node}</c> → 全部替换为 nodeId
/// - 不包含 <c>{node}</c> → 原样返回(用户填了固定 URL)
/// </summary>
public static class NodeUrlResolver
{
    public static string Resolve(string templateUrl, string nodeId)
    {
        if (string.IsNullOrWhiteSpace(templateUrl)) return templateUrl;
        return templateUrl.Replace("{node}", nodeId);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~NodeUrlResolverTests" -v minimal`
Expected: 5/5 PASS.

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/NodeUrlResolver.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/NodeUrlResolverTests.cs
git commit -m "feat(wpf): NodeUrlResolver — substitute {node} in download URLs"
```

---

## Task 3: SettingsViewModel QuerySources / DownloadSources collections + commands

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelTests.cs`

**Interfaces:**
- Produces `SettingsViewModel.QuerySources: ObservableCollection<NodeSource>`
- Produces `SettingsViewModel.DownloadSources: ObservableCollection<NodeSource>`
- Produces `SettingsViewModel.ActiveQuerySource: NodeSource?` (getter: looks up `_settings.ActiveQuerySourceName` in QuerySources; setter: persists Name to settings + saves)
- Produces `SettingsViewModel.ActiveDownloadSource: NodeSource?` (same pattern)
- Produces 8 commands: `RemoveQuerySourceCommand`, `RemoveDownloadSourceCommand`, `AddQuerySourceCommand`, `AddDownloadSourceCommand`, `ConfirmAddQuerySourceCommand`, `ConfirmAddDownloadSourceCommand`, `CancelAddQuerySourceCommand`, `CancelAddDownloadSourceCommand`
- Produces 6 UI-state properties: `IsAddQuerySourceOpen`, `IsAddDownloadSourceOpen`, `NewQuerySourceName`, `NewQuerySourceUrl`, `NewDownloadSourceName`, `NewDownloadSourceUrl`
- Behavior: `ConfirmAdd*` validates Name + Url non-empty, appends to list, sets new entry active, closes the form

- [ ] **Step 1: Write 6 failing tests in `SettingsViewModelTests.cs`**

Append to `tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelTests.cs` (after line 51, last existing test). The ctor signature `new SettingsViewModel(repo, GitProxyConfig.Disabled)` does not change.

```csharp
    [Fact]
    public void Defaults_LoadsQuerySourcesAndDownloadSources_FromAppliedDefaults()
    {
        // 全新 settings.json → 走 SettingsDefaults 兜底,两个列表各 1 条 "comfyui manager"
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled);

        Assert.Single(vm.QuerySources);
        Assert.Equal("comfyui manager", vm.QuerySources[0].Name);
        Assert.Single(vm.DownloadSources);
        Assert.Equal("comfyui manager", vm.DownloadSources[0].Name);
        Assert.Equal("comfyui manager", vm.ActiveQuerySource?.Name);
        Assert.Equal("comfyui manager", vm.ActiveDownloadSource?.Name);
    }

    [Fact]
    public void ConfirmAddQuerySourceCommand_AppendsAndSetsActive()
    {
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled);
        vm.NewQuerySourceName = "my-mirror";
        vm.NewQuerySourceUrl = "https://my-mirror/catalog.json";

        vm.IsAddQuerySourceOpen = true;
        vm.ConfirmAddQuerySourceCommand.Execute(null);

        Assert.Equal(2, vm.QuerySources.Count);
        Assert.Equal("my-mirror", vm.QuerySources[1].Name);
        Assert.Same(vm.QuerySources[1], vm.ActiveQuerySource);
        Assert.False(vm.IsAddQuerySourceOpen);
        Assert.Equal("", vm.NewQuerySourceName);
        Assert.Equal("", vm.NewQuerySourceUrl);
    }

    [Fact]
    public void ConfirmAddQuerySourceCommand_EmptyFields_DoesNothing()
    {
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled);
        vm.NewQuerySourceName = "";
        vm.NewQuerySourceUrl = "";
        vm.IsAddQuerySourceOpen = true;

        vm.ConfirmAddQuerySourceCommand.Execute(null);

        Assert.Single(vm.QuerySources);  // 没追加
        Assert.False(vm.IsAddQuerySourceOpen);  // 仍然关闭(等价于取消)
    }

    [Fact]
    public void RemoveQuerySourceCommand_WhenActive_FallsBackToFirst()
    {
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled);
        // 默认只有 1 条,先加一条自定义并切到它
        vm.NewQuerySourceName = "my-mirror";
        vm.NewQuerySourceUrl = "https://my-mirror/catalog.json";
        vm.ConfirmAddQuerySourceCommand.Execute(null);
        // 现在 active = "my-mirror"

        vm.RemoveQuerySourceCommand.Execute(vm.QuerySources[1]);

        Assert.Single(vm.QuerySources);
        Assert.Equal("comfyui manager", vm.ActiveQuerySource?.Name);
    }

    [Fact]
    public void RemoveQuerySourceCommand_LastOne_LeavesListEmpty()
    {
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled);
        vm.RemoveQuerySourceCommand.Execute(vm.QuerySources[0]);

        Assert.Empty(vm.QuerySources);
        Assert.Null(vm.ActiveQuerySource);
    }

    [Fact]
    public void SwitchActive_PersistsImmediately()
    {
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled);
        vm.NewQuerySourceName = "alt";
        vm.NewQuerySourceUrl = "https://alt/catalog.json";
        vm.ConfirmAddQuerySourceCommand.Execute(null);
        // active = "alt" now (auto-set on add)

        // switch back to first
        vm.ActiveQuerySource = vm.QuerySources[0];

        var reloaded = new SettingsRepository(_path).Load();
        Assert.Equal("comfyui manager", reloaded.ActiveQuerySourceName);
    }

    [Fact]
    public void ConfirmAddDownloadSourceCommand_AppendsAndSetsActive()
    {
        var vm = new SettingsViewModel(new SettingsRepository(_path), GitProxyConfig.Disabled);
        vm.NewDownloadSourceName = "gh-proxy";
        vm.NewDownloadSourceUrl = "https://gh-proxy.com/{node}";

        vm.IsAddDownloadSourceOpen = true;
        vm.ConfirmAddDownloadSourceCommand.Execute(null);

        Assert.Equal(2, vm.DownloadSources.Count);
        Assert.Equal("gh-proxy", vm.DownloadSources[1].Name);
        Assert.Same(vm.DownloadSources[1], vm.ActiveDownloadSource);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsViewModelTests" -v minimal`
Expected: compile errors (QuerySources / ConfirmAddQuerySourceCommand / etc don't exist).

- [ ] **Step 3: Extend `SettingsViewModel.cs` with the new properties/commands**

In `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`:

1. **Update ctor body** — keep `AddExtraPathCommand` + `RemoveExtraPathCommand` (current lines 28-32) unchanged, REPLACE the ExtraPaths init block at lines 22-27 (the ObservableCollection ctor + its CollectionChanged subscription) with the block below that adds QuerySources + DownloadSources init + their CollectionChanged subscriptions + 8 new commands. The new code block runs from `ExtraPaths = new ObservableCollection…` through `RaiseAllPropertiesChanged();` (which is the last line of the ctor).

```csharp
        ExtraPaths = new ObservableCollection<ExtraPath>(_settings.ExtraPaths);
        ExtraPaths.CollectionChanged += (_, _) =>
        {
            _settings.ExtraPaths = new List<ExtraPath>(ExtraPaths);
            _repo.Save(_settings);
        };
        QuerySources = new ObservableCollection<NodeSource>(_settings.QuerySources);
        QuerySources.CollectionChanged += (_, _) =>
        {
            _settings.QuerySources = new List<NodeSource>(QuerySources);
            _repo.Save(_settings);
            RaisePropertyChanged(nameof(ActiveQuerySource));
        };
        DownloadSources = new ObservableCollection<NodeSource>(_settings.DownloadSources);
        DownloadSources.CollectionChanged += (_, _) =>
        {
            _settings.DownloadSources = new List<NodeSource>(DownloadSources);
            _repo.Save(_settings);
            RaisePropertyChanged(nameof(ActiveDownloadSource));
        };
        AddExtraPathCommand = new RelayCommand(_ => ExtraPaths.Add(new ExtraPath()));
        RemoveExtraPathCommand = new RelayCommand(p =>
        {
            if (p is ExtraPath ep) ExtraPaths.Remove(ep);
        });
        AddQuerySourceCommand = new RelayCommand(_ =>
        {
            NewQuerySourceName = "";
            NewQuerySourceUrl = "";
            IsAddQuerySourceOpen = true;
        });
        RemoveQuerySourceCommand = new RelayCommand(p =>
        {
            if (p is NodeSource ns)
            {
                QuerySources.Remove(ns);
                // 如果删的是 active,settings.ActiveQuerySourceName 仍指向已删项;
                // 下次 ActiveQuerySource getter 找不到就返回 null。下次 Refresh 时 service 会报错"未配置查询源"。
            }
        });
        ConfirmAddQuerySourceCommand = new RelayCommand(_ =>
        {
            if (string.IsNullOrWhiteSpace(NewQuerySourceName) ||
                string.IsNullOrWhiteSpace(NewQuerySourceUrl))
            {
                IsAddQuerySourceOpen = false;
                return;
            }
            var ns = new NodeSource { Name = NewQuerySourceName, Url = NewQuerySourceUrl };
            QuerySources.Add(ns);
            ActiveQuerySource = ns;  // 自动 active
            IsAddQuerySourceOpen = false;
        });
        CancelAddQuerySourceCommand = new RelayCommand(_ =>
        {
            IsAddQuerySourceOpen = false;
        });
        AddDownloadSourceCommand = new RelayCommand(_ =>
        {
            NewDownloadSourceName = "";
            NewDownloadSourceUrl = "";
            IsAddDownloadSourceOpen = true;
        });
        RemoveDownloadSourceCommand = new RelayCommand(p =>
        {
            if (p is NodeSource ns) DownloadSources.Remove(ns);
        });
        ConfirmAddDownloadSourceCommand = new RelayCommand(_ =>
        {
            if (string.IsNullOrWhiteSpace(NewDownloadSourceName) ||
                string.IsNullOrWhiteSpace(NewDownloadSourceUrl))
            {
                IsAddDownloadSourceOpen = false;
                return;
            }
            var ns = new NodeSource { Name = NewDownloadSourceName, Url = NewDownloadSourceUrl };
            DownloadSources.Add(ns);
            ActiveDownloadSource = ns;
            IsAddDownloadSourceOpen = false;
        });
        CancelAddDownloadSourceCommand = new RelayCommand(_ =>
        {
            IsAddDownloadSourceOpen = false;
        });
        RaiseAllPropertiesChanged();
```

2. **Add the new properties + commands**, after the existing `ExtraPaths` block at line 131-134 (before `CheckUpdateCommand` on line 136):

```csharp

    // —— 节点源(query / download) ——
    public ObservableCollection<NodeSource> QuerySources { get; }
    public ObservableCollection<NodeSource> DownloadSources { get; }

    public NodeSource? ActiveQuerySource
    {
        get => QuerySources.FirstOrDefault(s => s.Name == _settings.ActiveQuerySourceName);
        set
        {
            _settings.ActiveQuerySourceName = value?.Name ?? "";
            _repo.Save(_settings);
            RaisePropertyChanged();
        }
    }

    public NodeSource? ActiveDownloadSource
    {
        get => DownloadSources.FirstOrDefault(s => s.Name == _settings.ActiveDownloadSourceName);
        set
        {
            _settings.ActiveDownloadSourceName = value?.Name ?? "";
            _repo.Save(_settings);
            RaisePropertyChanged();
        }
    }

    public bool IsAddQuerySourceOpen
    {
        get => _isAddQuerySourceOpen;
        set => SetField(ref _isAddQuerySourceOpen, value);
    }
    public bool IsAddDownloadSourceOpen
    {
        get => _isAddDownloadSourceOpen;
        set => SetField(ref _isAddDownloadSourceOpen, value);
    }
    public string NewQuerySourceName
    {
        get => _newQuerySourceName;
        set => SetField(ref _newQuerySourceName, value);
    }
    public string NewQuerySourceUrl
    {
        get => _newQuerySourceUrl;
        set => SetField(ref _newQuerySourceUrl, value);
    }
    public string NewDownloadSourceName
    {
        get => _newDownloadSourceName;
        set => SetField(ref _newDownloadSourceName, value);
    }
    public string NewDownloadSourceUrl
    {
        get => _newDownloadSourceUrl;
        set => SetField(ref _newDownloadSourceUrl, value);
    }

    public RelayCommand AddQuerySourceCommand { get; }
    public RelayCommand RemoveQuerySourceCommand { get; }
    public RelayCommand ConfirmAddQuerySourceCommand { get; }
    public RelayCommand CancelAddQuerySourceCommand { get; }
    public RelayCommand AddDownloadSourceCommand { get; }
    public RelayCommand RemoveDownloadSourceCommand { get; }
    public RelayCommand ConfirmAddDownloadSourceCommand { get; }
    public RelayCommand CancelAddDownloadSourceCommand { get; }
```

3. **Add private backing fields** at the top of the class (after `private Settings _settings;` on line 15), before `public SettingsViewModel(...)`:

```csharp

    private bool _isAddQuerySourceOpen;
    private bool _isAddDownloadSourceOpen;
    private string _newQuerySourceName = "";
    private string _newQuerySourceUrl = "";
    private string _newDownloadSourceName = "";
    private string _newDownloadSourceUrl = "";
```

4. **Add `using System.Linq;`** at the top of the file (needed for `FirstOrDefault`).

5. **Extend `RaiseAllPropertiesChanged()`** (lines 158-173) with the new property names:

```csharp
    private void RaiseAllPropertiesChanged()
    {
        RaisePropertyChanged(nameof(Language));
        RaisePropertyChanged(nameof(ThemeMode));
        RaisePropertyChanged(nameof(CacheTtlMinutes));
        RaisePropertyChanged(nameof(CompatApiBaseUrl));
        RaisePropertyChanged(nameof(TemplatePythonDir));
        RaisePropertyChanged(nameof(TemplateComfyuiDir));
        RaisePropertyChanged(nameof(EnvsDir));
        RaisePropertyChanged(nameof(GlobalNodesDir));
        RaisePropertyChanged(nameof(PythonVenvBaseline));
        RaisePropertyChanged(nameof(GitExe));
        RaisePropertyChanged(nameof(GitProxyUrl));
        RaisePropertyChanged(nameof(GitProxyPort));
        RaisePropertyChanged(nameof(GitProxyEnabled));
        RaisePropertyChanged(nameof(ActiveQuerySource));
        RaisePropertyChanged(nameof(ActiveDownloadSource));
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~SettingsViewModelTests" -v minimal`
Expected: 9/9 PASS (2 old + 7 new).

- [ ] **Step 5: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelTests.cs
git commit -m "feat(wpf): SettingsViewModel — QuerySources/DownloadSources + commands"
```

---

## Task 4: SettingsView.xaml — two new sections with ComboBox + ItemsControl + inline add form

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`

**Interfaces:**
- Inserts `<TextBlock Text="查询节点的源" />` + `<TextBlock Text="下载节点的源" />` sections between current 基础 (ends at line 28) and 路径 (starts at line 30)
- Each section has: section header → ComboBox bound to ActiveQuerySource/ActiveDownloadSource → ItemsControl listing all entries → "+ 添加" button → conditional inline form

- [ ] **Step 1: Verify current `SettingsView.xaml` line 28-30 is the insertion point**

Read `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` and confirm the existing pattern at lines 14-28 (基础 section with two ComboBoxes + two TextBoxes). The two new sections must follow the same visual grammar (section header `FontSize=16, FontWeight=Bold`, ComboBox `Width=320` to fit longer URLs, ItemsControl rows with 3-column Grid).

- [ ] **Step 2: Insert two new sections after line 28 (before the `<!-- ============ 路径 ============ -->` comment on line 30)**

In `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`, between the closing `</TextBox>` of 基础 section on line 28 and the `<!-- ============ 路径 ============ -->` comment on line 30, insert:

```xml

            <!-- ============ 查询节点的源 ============ -->
            <TextBlock Text="查询节点的源" FontSize="16" FontWeight="Bold" Margin="0,24,0,8" />
            <TextBlock Text="当前使用" Margin="0,0,0,4" />
            <ComboBox ItemsSource="{Binding QuerySources}"
                      DisplayMemberPath="Name"
                      SelectedItem="{Binding ActiveQuerySource, Mode=TwoWay}"
                      Width="320" HorizontalAlignment="Left" />
            <ItemsControl ItemsSource="{Binding QuerySources}" Margin="0,8,0,0">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <Grid Margin="0,4,0,0">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="160" />
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <TextBlock Grid.Column="0" Text="{Binding Name}" VerticalAlignment="Center" />
                            <TextBlock Grid.Column="1" Text="{Binding Url}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" Margin="8,0,0,0" />
                            <Button Grid.Column="2" Content="删除" Margin="8,0,0,0"
                                    Command="{Binding DataContext.RemoveQuerySourceCommand,
                                              RelativeSource={RelativeSource AncestorType=UserControl}}"
                                    CommandParameter="{Binding}"
                                    Style="{StaticResource MaterialButton}" />
                        </Grid>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
            <Button Content="+ 添加查询源" Margin="0,8,0,0" HorizontalAlignment="Left"
                    Command="{Binding AddQuerySourceCommand}"
                    Style="{StaticResource MaterialButton}" />
            <Grid Margin="0,8,0,0"
                  Visibility="{Binding IsAddQuerySourceOpen,
                                Converter={StaticResource BoolToVisibility}}">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="160" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <TextBox Grid.Column="0" Style="{StaticResource MaterialTextBox}"
                         Text="{Binding NewQuerySourceName, UpdateSourceTrigger=PropertyChanged}" />
                <TextBox Grid.Column="1" Style="{StaticResource MaterialTextBox}" Margin="8,0,0,0"
                         Text="{Binding NewQuerySourceUrl, UpdateSourceTrigger=PropertyChanged}" />
                <Button Grid.Column="2" Content="确定" Margin="8,0,0,0"
                        Command="{Binding ConfirmAddQuerySourceCommand}"
                        Style="{StaticResource MaterialButton}" />
                <Button Grid.Column="3" Content="取消" Margin="4,0,0,0"
                        Command="{Binding CancelAddQuerySourceCommand}"
                        Style="{StaticResource MaterialButton}" />
            </Grid>

            <!-- ============ 下载节点的源 ============ -->
            <TextBlock Text="下载节点的源" FontSize="16" FontWeight="Bold" Margin="0,24,0,8" />
            <TextBlock Text="当前使用(URL 中可用 {node} 占位)" Margin="0,0,0,4" />
            <ComboBox ItemsSource="{Binding DownloadSources}"
                      DisplayMemberPath="Name"
                      SelectedItem="{Binding ActiveDownloadSource, Mode=TwoWay}"
                      Width="320" HorizontalAlignment="Left" />
            <ItemsControl ItemsSource="{Binding DownloadSources}" Margin="0,8,0,0">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <Grid Margin="0,4,0,0">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="160" />
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <TextBlock Grid.Column="0" Text="{Binding Name}" VerticalAlignment="Center" />
                            <TextBlock Grid.Column="1" Text="{Binding Url}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" Margin="8,0,0,0" />
                            <Button Grid.Column="2" Content="删除" Margin="8,0,0,0"
                                    Command="{Binding DataContext.RemoveDownloadSourceCommand,
                                              RelativeSource={RelativeSource AncestorType=UserControl}}"
                                    CommandParameter="{Binding}"
                                    Style="{StaticResource MaterialButton}" />
                        </Grid>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
            <Button Content="+ 添加下载源" Margin="0,8,0,0" HorizontalAlignment="Left"
                    Command="{Binding AddDownloadSourceCommand}"
                    Style="{StaticResource MaterialButton}" />
            <Grid Margin="0,8,0,0"
                  Visibility="{Binding IsAddDownloadSourceOpen,
                                Converter={StaticResource BoolToVisibility}}">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="160" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <TextBox Grid.Column="0" Style="{StaticResource MaterialTextBox}"
                         Text="{Binding NewDownloadSourceName, UpdateSourceTrigger=PropertyChanged}" />
                <TextBox Grid.Column="1" Style="{StaticResource MaterialTextBox}" Margin="8,0,0,0"
                         Text="{Binding NewDownloadSourceUrl, UpdateSourceTrigger=PropertyChanged}" />
                <Button Grid.Column="2" Content="确定" Margin="8,0,0,0"
                        Command="{Binding ConfirmAddDownloadSourceCommand}"
                        Style="{StaticResource MaterialButton}" />
                <Button Grid.Column="3" Content="取消" Margin="4,0,0,0"
                        Command="{Binding CancelAddDownloadSourceCommand}"
                        Style="{StaticResource MaterialButton}" />
            </Grid>

```

- [ ] **Step 3: Verify `BoolToVisibility` converter exists in Theme.xaml resources**

Run: `grep -rn "BoolToVisibility" src-wpf/ComfyUI.Manager/Resources/`
Expected: a converter registered in Theme.xaml. If NOT found, register one in `Resources/Theme.xaml`:

```xml
<BooleanToVisibilityConverter x:Key="BoolToVisibility" />
```

inside the `<ResourceDictionary>` block. (Skip if already present.)

- [ ] **Step 4: Build the WPF project**

Run: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal`
Expected: 0 errors, 0 warnings about new bindings.

If binding errors mention `ActiveQuerySource` or `IsAddQuerySourceOpen`, check that `RaiseAllPropertiesChanged()` was updated in T3.

- [ ] **Step 5: Run all WPF tests**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal`
Expected: 50+/50+ PASS (existing 50 + 5 new from T1 + 5 from T2 + 7 from T3 = 67 total).

- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Views/SettingsView.xaml \
        src-wpf/ComfyUI.Manager/Resources/Theme.xaml
git commit -m "feat(wpf): SettingsView — query/download source sections"
```

---

## Task 5: CatalogFetcher — HTTP GET + JSON parse

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/CatalogFetcher.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogFetcherTests.cs`

**Interfaces:**
- Produces `Services.CatalogFetcher` class
- ctor: `CatalogFetcher(HttpClient http, int cacheTtlMinutes = 60)` — HttpClient is injectable for tests
- Method: `Task<List<CatalogEntry>> FetchAsync(string url, CancellationToken ct = default)`
- Behavior: GET URL → parse JSON array → for each row extract `id` (fallback `name`) as `Package`, fill `RawMetadata` from raw JSON, set `SourceUrl=url`, `CachedAt=UtcNow ISO`, `ExpiresAt=UtcNow+TTL ISO`, `Id=Guid.NewGuid().ToString()`; rows missing both `id` and `name` are skipped
- HTTP failure / JSON parse failure: throws (caller in T6 handles)

- [ ] **Step 1: Verify Moq is referenced in test project**

Read `tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj`. Confirm `<PackageReference Include="Moq" .../>` exists. If missing, add it (current version: `4.20.72`).

- [ ] **Step 2: Write 5 failing tests**

Create `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogFetcherTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services;
using Moq;
using Moq.Protected;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class CatalogFetcherTests
{
    /// <summary>
    /// Build a mocked HttpClient whose single SendAsync call returns the given JSON body.
    /// </summary>
    private static HttpClient MockedHttpClient(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = status,
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        return new HttpClient(handler.Object);
    }

    [Fact]
    public async Task FetchAsync_ParsesValidJson_ReturnsEntries()
    {
        var json = @"[
            { ""id"": ""comfy-node-a"", ""author"": ""alice"", ""description"": ""node A"" },
            { ""id"": ""comfy-node-b"", ""author"": ""bob"" }
        ]";
        var fetcher = new CatalogFetcher(MockedHttpClient(json), cacheTtlMinutes: 60);

        var entries = await fetcher.FetchAsync("https://example/registry.json");

        Assert.Equal(2, entries.Count);
        Assert.Equal("comfy-node-a", entries[0].Package);
        Assert.Equal("https://example/registry.json", entries[0].SourceUrl);
        Assert.Contains("alice", entries[0].RawMetadata.Values);
        Assert.Equal("comfy-node-b", entries[1].Package);
    }

    [Fact]
    public async Task FetchAsync_FallsBackToName_WhenIdMissing()
    {
        var json = @"[{ ""name"": ""fallback-name"" }]";
        var fetcher = new CatalogFetcher(MockedHttpClient(json));

        var entries = await fetcher.FetchAsync("https://example/registry.json");

        Assert.Single(entries);
        Assert.Equal("fallback-name", entries[0].Package);
    }

    [Fact]
    public async Task FetchAsync_SkipsRows_BothIdAndNameMissing()
    {
        var json = @"[
            { ""id"": ""keep-me"" },
            { ""unrelated"": ""field"" },
            { ""name"": ""also-keep"" }
        ]";
        var fetcher = new CatalogFetcher(MockedHttpClient(json));

        var entries = await fetcher.FetchAsync("https://example/registry.json");

        Assert.Equal(2, entries.Count);
        Assert.Equal("keep-me", entries[0].Package);
        Assert.Equal("also-keep", entries[1].Package);
    }

    [Fact]
    public async Task FetchAsync_EmptyArray_ReturnsEmptyList()
    {
        var fetcher = new CatalogFetcher(MockedHttpClient("[]"));

        var entries = await fetcher.FetchAsync("https://example/registry.json");

        Assert.Empty(entries);
    }

    [Fact]
    public async Task FetchAsync_NetworkFailure_Throws()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network down"));
        var fetcher = new CatalogFetcher(new HttpClient(handler.Object));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => fetcher.FetchAsync("https://example/registry.json"));
    }

    [Fact]
    public async Task FetchAsync_SetsExpiresAt_AccordingToTtl()
    {
        var json = @"[{ ""id"": ""pkg"" }]";
        var fetcher = new CatalogFetcher(MockedHttpClient(json), cacheTtlMinutes: 30);

        var entries = await fetcher.FetchAsync("https://example/registry.json");

        Assert.Single(entries);
        // CachedAt + 30min ≈ ExpiresAt
        var cached = DateTime.Parse(entries[0].CachedAt);
        var expires = DateTime.Parse(entries[0].ExpiresAt);
        var diff = expires - cached;
        Assert.InRange(diff.TotalMinutes, 29.5, 30.5);
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogFetcherTests" -v minimal`
Expected: compile error (CatalogFetcher doesn't exist).

- [ ] **Step 4: Implement `CatalogFetcher.cs`**

Create `src-wpf/ComfyUI.Manager/Services/CatalogFetcher.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// CatalogFetcher:HTTP GET 一个 catalog JSON URL,解析为 <see cref="CatalogEntry"/> 列表。
///
/// JSON 解析策略(宽松):
/// - 每个 row 必须有 <c>id</c> 或 <c>name</c> 字段;否则跳过该 row。
/// - <c>id</c> 优先,缺失则用 <c>name</c>;都缺跳过。
/// - 整个原始 row 序列化为 <see cref="CatalogEntry.RawMetadata"/>(后续 UI/服务可能用)。
///
/// 失败:
/// - HTTP 失败 / timeout → 抛 <see cref="HttpRequestException"/>(caller 处理)。
/// - 顶层 JSON 不是 array → 抛 <see cref="JsonException"/>。
/// </summary>
public class CatalogFetcher
{
    private readonly HttpClient _http;
    private readonly int _cacheTtlMinutes;

    public CatalogFetcher(HttpClient http, int cacheTtlMinutes = 60)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cacheTtlMinutes = cacheTtlMinutes;
    }

    public async Task<List<CatalogEntry>> FetchAsync(string url, CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(url, ct);
        var rawArray = JsonSerializer.Deserialize<List<JsonElement>>(json)
            ?? new List<JsonElement>();

        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(_cacheTtlMinutes);
        var entries = new List<CatalogEntry>();

        foreach (var element in rawArray)
        {
            string package = "";
            if (element.TryGetProperty("id", out var idProp))
            {
                package = idProp.GetString() ?? "";
            }
            if (string.IsNullOrEmpty(package) &&
                element.TryGetProperty("name", out var nameProp))
            {
                package = nameProp.GetString() ?? "";
            }
            if (string.IsNullOrWhiteSpace(package))
            {
                continue;  // 跳过无 id/name 的 row
            }

            var rawMeta = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                element.GetRawText()) ?? new Dictionary<string, object?>();

            entries.Add(new CatalogEntry
            {
                Id = Guid.NewGuid().ToString(),
                SourceUrl = url,
                Package = package,
                RawMetadata = rawMeta,
                CachedAt = now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ExpiresAt = expires.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            });
        }

        return entries;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogFetcherTests" -v minimal`
Expected: 6/6 PASS.

- [ ] **Step 6: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/CatalogFetcher.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/CatalogFetcherTests.cs
git commit -m "feat(wpf): CatalogFetcher — HTTP GET + parse catalog JSON"
```

---

## Task 6: CatalogViewModel — wire Refresh to CatalogFetcher

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs`
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`
- Modify: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelTests.cs`

**Interfaces:**
- `CatalogViewModel` ctor signature changes from:
  ```
  (CatalogRepository repo, EnvironmentRepository envRepo, NodeOperations nodeOps)
  ```
  to:
  ```
  (CatalogRepository repo, EnvironmentRepository envRepo, NodeOperations nodeOps,
   CatalogFetcher fetcher, Settings settings)
  ```
- `Refresh()` becomes `RefreshAsync()` (async). Behavior:
  - active source lookup → null → set `ErrorMessage = "未配置查询源,请先在 Settings 添加"` + return
  - set `IsBusy = true`
  - try `await _fetcher.FetchAsync(src.Url, ct)` → for each entry set `SourceUrl = src.Url` + `_repo.Upsert(e)` → call `Search()`
  - catch → set `ErrorMessage = $"拉取失败: {ex.Message}(本地缓存仍可用)"` + call `Search()`
  - finally → `IsBusy = false`
- New `ErrorMessage` property (`string?`)
- New `IsBusy` property (`bool`)
- `MainViewModel.ShowCatalog()` passes `_settings` + `_fetcher` to `CatalogViewModel` ctor

- [ ] **Step 1: Update existing 2 tests in `CatalogViewModelTests.cs` for new ctor signature**

In `tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelTests.cs`, the existing tests at lines 44-49 and 59-63 build `CatalogViewModel` with 3 args. Replace both call sites to use 5 args (using a real `Settings` + a `FakeCatalogFetcher`).

First, add a fake fetcher inside the test class (right after `NoopNodeOps` at line 35):

```csharp
    private sealed class FakeCatalogFetcher : CatalogFetcher
    {
        public List<CatalogEntry> EntriesToReturn { get; set; } = new();
        public Exception? ThrowOnFetch { get; set; }
        public string? LastUrl { get; private set; }

        public FakeCatalogFetcher() : base(new HttpClient(new Moq.Mock<HttpMessageHandler>().Object), 60) { }

        public override async Task<List<CatalogEntry>> FetchAsync(string url, CancellationToken ct = default)
        {
            LastUrl = url;
            if (ThrowOnFetch is not null) throw ThrowOnFetch;
            return await Task.FromResult(EntriesToReturn);
        }
    }
```

For `FetchAsync` to be overrideable, change `CatalogFetcher.FetchAsync` from non-virtual to `virtual` (in `CatalogFetcher.cs`):

```csharp
    public virtual async Task<List<CatalogEntry>> FetchAsync(string url, CancellationToken ct = default)
```

And add `using System.Threading;` to `CatalogFetcher.cs` (already there) and `using System.Threading.Tasks;` (already there). And `Moq.Mock<HttpMessageHandler>` requires `using Moq;` and `using System.Net.Http;` in the test file.

Then update the two existing tests:

```csharp
    [Fact]
    public void Ctor_LoadsAllCatalogEntries()
    {
        using var db = new TestDb();
        SeedCatalog(db, "pkg-a");
        SeedCatalog(db, "pkg-b");

        var settings = new ComfyUI.Manager.Models.Settings();
        SettingsDefaults.Apply(settings, @"D:\ToolDevelop\ComfyUI");

        var vm = new CatalogViewModel(
            new CatalogRepository(db.Factory),
            new EnvironmentRepository(db.Factory),
            new NoopNodeOps(new EnvironmentRepository(db.Factory), new NodeRepository(db.Factory)),
            new FakeCatalogFetcher(),
            settings);

        Assert.Equal(2, vm.Entries.Count);
    }

    [Fact]
    public void Query_FiltersEntries()
    {
        using var db = new TestDb();
        SeedCatalog(db, "alpha");
        SeedCatalog(db, "beta");

        var settings = new ComfyUI.Manager.Models.Settings();
        SettingsDefaults.Apply(settings, @"D:\ToolDevelop\ComfyUI");

        var vm = new CatalogViewModel(
            new CatalogRepository(db.Factory),
            new EnvironmentRepository(db.Factory),
            new NoopNodeOps(new EnvironmentRepository(db.Factory), new NodeRepository(db.Factory)),
            new FakeCatalogFetcher(),
            settings);
        vm.Query = "alph";

        Assert.Single(vm.Entries);
        Assert.Equal("alpha", vm.Entries[0].Package);
    }
```

Add `using ComfyUI.Manager.Infrastructure;` to the test file (for `SettingsDefaults`).

- [ ] **Step 2: Add 4 new tests for RefreshAsync behavior**

Append to the test file:

```csharp
    [Fact]
    public async Task RefreshAsync_FetchesFromActiveQuerySource_AndUpserts()
    {
        using var db = new TestDb();
        var settings = new ComfyUI.Manager.Models.Settings();
        SettingsDefaults.Apply(settings, @"D:\ToolDevelop\ComfyUI");

        var fetcher = new FakeCatalogFetcher
        {
            EntriesToReturn = new List<CatalogEntry>
            {
                new() { Package = "from-active-source" },
            },
        };

        var vm = new CatalogViewModel(
            new CatalogRepository(db.Factory),
            new EnvironmentRepository(db.Factory),
            new NoopNodeOps(new EnvironmentRepository(db.Factory), new NodeRepository(db.Factory)),
            fetcher,
            settings);

        vm.RefreshCommand.Execute(null);
        // RefreshCommand currently wraps Refresh(); Refresh becomes RefreshAsync and
        // RefreshCommand.Execute wraps an async lambda. Wait briefly for completion:
        await Task.Delay(200);

        Assert.Equal(settings.QuerySources[0].Url, fetcher.LastUrl);
        var entries = new CatalogRepository(db.Factory).Search("", 10);
        Assert.Single(entries);
        Assert.Equal("from-active-source", entries[0].Package);
        Assert.Equal(settings.QuerySources[0].Url, entries[0].SourceUrl);
    }

    [Fact]
    public async Task RefreshAsync_NoActiveSource_SetsErrorMessage()
    {
        using var db = new TestDb();
        var settings = new ComfyUI.Manager.Models.Settings
        {
            QuerySources = new(),  // 列表也空
            ActiveQuerySourceName = "nonexistent",
        };
        // 不跑 SettingsDefaults.Apply,settings 保持空 query_sources + 错误 active name

        var vm = new CatalogViewModel(
            new CatalogRepository(db.Factory),
            new EnvironmentRepository(db.Factory),
            new NoopNodeOps(new EnvironmentRepository(db.Factory), new NodeRepository(db.Factory)),
            new FakeCatalogFetcher(),
            settings);

        vm.RefreshCommand.Execute(null);
        await Task.Delay(100);

        Assert.Contains("未配置查询源", vm.ErrorMessage);
    }

    [Fact]
    public async Task RefreshAsync_NetworkFailure_SetsErrorMessageAndSearchesLocal()
    {
        using var db = new TestDb();
        SeedCatalog(db, "cached-pkg");  // 本地 cache 已有一条
        var settings = new ComfyUI.Manager.Models.Settings();
        SettingsDefaults.Apply(settings, @"D:\ToolDevelop\ComfyUI");

        var fetcher = new FakeCatalogFetcher
        {
            ThrowOnFetch = new HttpRequestException("dns fail"),
        };

        var vm = new CatalogViewModel(
            new CatalogRepository(db.Factory),
            new EnvironmentRepository(db.Factory),
            new NoopNodeOps(new EnvironmentRepository(db.Factory), new NodeRepository(db.Factory)),
            fetcher,
            settings);

        vm.RefreshCommand.Execute(null);
        await Task.Delay(100);

        Assert.Contains("拉取失败", vm.ErrorMessage);
        // 本地 cache 仍在
        Assert.Single(vm.Entries);
        Assert.Equal("cached-pkg", vm.Entries[0].Package);
    }

    [Fact]
    public async Task RefreshAsync_ClearsIsBusy_OnCompletion()
    {
        using var db = new TestDb();
        var settings = new ComfyUI.Manager.Models.Settings();
        SettingsDefaults.Apply(settings, @"D:\ToolDevelop\ComfyUI");

        var vm = new CatalogViewModel(
            new CatalogRepository(db.Factory),
            new EnvironmentRepository(db.Factory),
            new NoopNodeOps(new EnvironmentRepository(db.Factory), new NodeRepository(db.Factory)),
            new FakeCatalogFetcher(),
            settings);

        vm.RefreshCommand.Execute(null);
        await Task.Delay(200);

        Assert.False(vm.IsBusy);
    }
```

- [ ] **Step 3: Run tests to verify new ones fail (compile error)**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogViewModelTests" -v minimal`
Expected: compile error (CatalogViewModel ctor arity mismatch).

- [ ] **Step 4: Rewrite `CatalogViewModel.cs`**

Replace the entire `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs` with:

```csharp
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

public class CatalogViewModel : ViewModelBase
{
    private const int SearchLimit = 50;
    private readonly CatalogRepository _repo;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeOperations _nodeOps;
    private readonly CatalogFetcher _fetcher;
    private readonly Settings _settings;

    public ObservableCollection<CatalogEntry> Entries { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand InstallCommand { get; }

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
        set => SetField(ref _errorMessage, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    public CatalogViewModel(
        CatalogRepository repo,
        EnvironmentRepository envRepo,
        NodeOperations nodeOps,
        CatalogFetcher fetcher,
        Settings settings)
    {
        _repo = repo;
        _envRepo = envRepo;
        _nodeOps = nodeOps;
        _fetcher = fetcher;
        _settings = settings;
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
        InstallCommand = new RelayCommand(
            async p => await InstallAsync(p as CatalogEntry ?? Selected),
            p => (p as CatalogEntry ?? Selected) is not null);
        Search();
    }

    private void Search()
    {
        Entries.Clear();
        foreach (var e in _repo.Search(_query, SearchLimit)) Entries.Add(e);
    }

    private async Task RefreshAsync()
    {
        ErrorMessage = null;
        var active = _settings.ActiveQuerySourceName;
        var src = _settings.QuerySources.FirstOrDefault(s => s.Name == active);
        if (src is null || string.IsNullOrWhiteSpace(src.Url))
        {
            ErrorMessage = "未配置查询源,请先在 Settings 添加";
            return;
        }

        IsBusy = true;
        try
        {
            var entries = await _fetcher.FetchAsync(src.Url);
            foreach (var e in entries)
            {
                e.SourceUrl = src.Url;
                _repo.Upsert(e);
            }
            Search();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"拉取失败: {ex.Message}(本地缓存仍可用)";
            Search();
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
        if (!result.Success)
        {
            ErrorMessage = $"安装失败:{result.Reason}";
        }
        else
        {
            ErrorMessage = $"已安装 {entry.Package} → version={result.Version}";
        }
    }

    private static string? ExtractRepoUrl(CatalogEntry entry)
    {
        if (entry.RawMetadata is null) return null;
        if (entry.RawMetadata.TryGetValue("repository", out var r) && r is string rs
            && !string.IsNullOrWhiteSpace(rs)) return rs;
        if (entry.RawMetadata.TryGetValue("url", out var u) && u is string us
            && !string.IsNullOrWhiteSpace(us)) return us;
        if (!string.IsNullOrWhiteSpace(entry.SourceUrl)) return entry.SourceUrl;
        return null;
    }
}
```

Note: `MessageBox.Show` calls removed — replaced with `ErrorMessage` setter for testability. The UI layer will need to bind to `ErrorMessage` (out of scope for this hotfix; can wire in T7 if simple, otherwise leave for manual binding later).

- [ ] **Step 5: Update `MainViewModel.cs` to pass `Settings` + `CatalogFetcher` to `CatalogViewModel`**

In `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs`:

1. Add `using ComfyUI.Manager.Models;` at top (if not present) — check line 1-7.

2. Add two private fields after line 17 (after `_gitProxy`):

```csharp
    private readonly Settings _settings;
    private readonly CatalogFetcher _catalogFetcher;
```

3. Add ctor params (after `GitProxyConfig gitProxy)` on line 40):

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
        CatalogFetcher catalogFetcher)
```

4. In the ctor body (after line 48 `_gitProxy = gitProxy;`), add:

```csharp
        _settings = settings;
        _catalogFetcher = catalogFetcher;
```

5. Update `ShowCatalog()` at line 65-73 to pass the new args:

```csharp
    private void ShowCatalog()
    {
        var catRepo = new CatalogRepository(_dbFactory);
        var envRepo = new EnvironmentRepository(_dbFactory);
        CurrentView = new CatalogView
        {
            DataContext = new CatalogViewModel(catRepo, envRepo, _nodeOps, _catalogFetcher, _settings),
        };
    }
```

- [ ] **Step 5.5: Update `App.xaml.cs` to keep build green after MainViewModel ctor change**

The plan split `App.xaml.cs` wiring across T6 and T7. To avoid an intermediate broken-commit state where T6 is committed but `App.xaml.cs` still calls `new MainViewModel(...)` with 7 args (compile error), update `App.xaml.cs` IN T6 to pass the new 2 args. T7's step 5 will then only update the `NodeOperations` ctor call (different file change, no conflict).

In `src-wpf/ComfyUI.Manager/App.xaml.cs`, line 61-62 currently reads:

```csharp
        _mainVm = new MainViewModel(
            dbFactory, _launcher, bulkOrchestrator, nodeOps, envCreator, settingsRepo, gitProxy);
```

Replace with:

```csharp
        _mainVm = new MainViewModel(
            dbFactory, _launcher, bulkOrchestrator, nodeOps, envCreator, settingsRepo, gitProxy,
            settings, null!);  // catalogFetcher filled in T7
```

Pass `null!` (null-forgiving) for `catalogFetcher` since the T6 wiring does not yet need an actual fetcher — `ShowCatalog` is only called when the user clicks the Catalog button, by which time T7 will have wired the real fetcher. (T7 step 5 replaces this `null!` with the real `catalogFetcher` instance.)

Verify build is green: `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal` — 0 errors.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogViewModelTests" -v minimal`
Expected: 6/6 PASS (2 old + 4 new).

- [ ] **Step 7: Run full WPF test suite**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal`
Expected: all green (no regressions from ctor changes).

- [ ] **Step 8: Commit**

```bash
git add src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs \
        src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs \
        src-wpf/ComfyUI.Manager/Services/CatalogFetcher.cs \
        tests-wpf/ComfyUI.Manager.Tests/ViewModels/CatalogViewModelTests.cs
git commit -m "feat(wpf): CatalogViewModel.Refresh wires to CatalogFetcher (resolves M5.2-T7)"
```

---

## Task 7: NodeOperations consume active DownloadSource when catalog lacks repo URL

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs`
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsTests.cs`
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs`

**Interfaces:**
- `NodeOperations` ctor changes from:
  ```
  (GitRunner git, EnvironmentRepository envRepo, NodeRepository nodeRepo)
  ```
  to:
  ```
  (GitRunner git, EnvironmentRepository envRepo, NodeRepository nodeRepo, Settings settings)
  ```
- `InstallAsync(envId, nodeId, repoUrl, ct)` behavior change: if `repoUrl` arg is null/empty, fall back to active `DownloadSource.Url` and substitute `{node}` via `NodeUrlResolver.Resolve`. If no active source, throw `InvalidOperationException("未配置下载源,请在 Settings 添加")`.

- [ ] **Step 1: Update 5 existing tests for new ctor signature**

In `tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsTests.cs`:

The 5 existing tests (`InstallAsync_ClonesAndRegistersNode`, `UpgradeAsync_PullsFastForward_UpdatesVersion`, `InstallAsync_TargetDirExists_Fails`, `RollbackAsync_ResetsToGivenSha`, `LockUnlock_PersistsFlag`) each construct `NodeOperations` with 3 args (e.g., line 122, 174, 196, 236, 263). Replace each `new NodeOperations(new GitRunner("git"), envRepo, nodeRepo)` with `new NodeOperations(new GitRunner("git"), envRepo, nodeRepo, new ComfyUI.Manager.Models.Settings())` (use fresh Settings + run `SettingsDefaults.Apply` if needed; for these tests, the InstallAsync calls pass a non-empty `repoUrl`, so no Settings defaults needed; bare `new Settings()` is fine).

Add `using ComfyUI.Manager.Models;` to the test file (already present per line 6).

- [ ] **Step 2: Add 2 new tests for active download source behavior**

Append to the test file:

```csharp
    [Fact]
    public async Task InstallAsync_EmptyRepoUrl_FallsBackToActiveDownloadSourceUrl()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-install-fallback-{Guid.NewGuid():N}");
        var (remote, _) = InitRepoPair(tempRoot);
        var customNodes = Path.Combine(tempRoot, "nodes");
        Directory.CreateDirectory(customNodes);

        using var db = new TestDb();
        var (envRepo, nodeRepo, _) = SeedEnv(db, customNodes);

        // Settings 含一个 active download source 模板
        var settings = new ComfyUI.Manager.Models.Settings
        {
            DownloadSources = new List<NodeSource>
            {
                new() { Name = "test-source", Url = remote + "/{node}" },  // 注意带 {node}
            },
            ActiveDownloadSourceName = "test-source",
        };
        var ops = new NodeOperations(new GitRunner("git"), envRepo, nodeRepo, settings);

        // 传空 repoUrl → 应回落到 active download source,substitute {node}
        var result = await ops.InstallAsync("env-1", "node-a", "");
        Assert.True(result.Success, $"reason={result.Reason}");
        Assert.True(Directory.Exists(Path.Combine(customNodes, "node-a")));
    }

    [Fact]
    public async Task InstallAsync_EmptyRepoUrl_NoActiveSource_Fails()
    {
        if (string.IsNullOrEmpty(FindGit())) return;

        var tempRoot = Path.Combine(
            Path.GetTempPath(), $"comfy-install-nosrc-{Guid.NewGuid():N}");
        var customNodes = Path.Combine(tempRoot, "nodes");
        Directory.CreateDirectory(customNodes);

        using var db = new TestDb();
        var (envRepo, nodeRepo, _) = SeedEnv(db, customNodes);

        var settings = new ComfyUI.Manager.Models.Settings
        {
            DownloadSources = new(),  // 列表空
            ActiveDownloadSourceName = "nonexistent",
        };
        var ops = new NodeOperations(new GitRunner("git"), envRepo, nodeRepo, settings);

        var result = await ops.InstallAsync("env-1", "node-a", "");
        Assert.False(result.Success);
        Assert.Contains("未配置下载源", result.Reason);
    }
```

- [ ] **Step 3: Run tests to verify new ones fail (compile error)**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~NodeOperationsTests" -v minimal`
Expected: compile error (NodeOperations ctor arity mismatch).

- [ ] **Step 4: Update `NodeOperations.cs`**

In `src-wpf/ComfyUI.Manager/Services/NodeOperations.cs`:

1. Update field declarations (replace lines 32-34):

```csharp
    private readonly GitRunner _git;
    private readonly EnvironmentRepository _envRepo;
    private readonly NodeRepository _nodeRepo;
    private readonly Settings _settings;
```

2. Update ctor (replace lines 36-44):

```csharp
    public NodeOperations(
        GitRunner git,
        EnvironmentRepository envRepo,
        NodeRepository nodeRepo,
        Settings settings)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _envRepo = envRepo ?? throw new ArgumentNullException(nameof(envRepo));
        _nodeRepo = nodeRepo ?? throw new ArgumentNullException(nameof(nodeRepo));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }
```

3. In `InstallAsync` (after line 65's `return NodeOperationResult.Fail("repoUrl 不能为空");`), replace it with the fallback block:

```csharp
        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            // 回落到 active download source 的 URL 模板
            var activeName = _settings.ActiveDownloadSourceName;
            var src = _settings.DownloadSources.FirstOrDefault(s => s.Name == activeName);
            if (src is null || string.IsNullOrWhiteSpace(src.Url))
            {
                return NodeOperationResult.Fail("未配置下载源,请在 Settings 添加");
            }
            repoUrl = NodeUrlResolver.Resolve(src.Url, nodeId);
            if (string.IsNullOrWhiteSpace(repoUrl))
            {
                return NodeOperationResult.Fail("下载源 URL 解析为空");
            }
        }
```

The new block above REPLACES the old `if (string.IsNullOrWhiteSpace(repoUrl)) return Fail("repoUrl 不能为空");` check at lines 62-65 — no other change needed to InstallAsync.

4. Add `using ComfyUI.Manager.Models;` (already at line 7 — confirm).

- [ ] **Step 5: Update `App.xaml.cs` to pass `Settings` to `NodeOperations` ctor**

In `src-wpf/ComfyUI.Manager/App.xaml.cs`:

1. Line 55 changes from:
   ```csharp
   var nodeOps = new NodeOperations(gitRunner, envRepo, nodeRepo);
   ```
   to:
   ```csharp
   var nodeOps = new NodeOperations(gitRunner, envRepo, nodeRepo, settings);
   ```

2. Add a `CatalogFetcher` creation block after line 55 (after the `nodeOps` line). Add `using System.Net.Http;` at top if not present.

```csharp
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var catalogFetcher = new CatalogFetcher(http, settings.CatalogCacheTtlMinutes);
```

3. Line 62 `MainViewModel` ctor call ALREADY UPDATED in T6 step 5.5 — T7 only needs to replace the `null!` placeholder with the real `catalogFetcher` instance:

```csharp
        _mainVm = new MainViewModel(
            dbFactory, _launcher, bulkOrchestrator, nodeOps, envCreator, settingsRepo, gitProxy,
            settings, catalogFetcher);
```

- [ ] **Step 6: Run full test suite**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal`
Expected: 70+/70+ PASS (cumulative: 8 old SettingsDefaults + 4 new = 12; 5 NodeUrlResolver new; 7 new SettingsViewModel + 2 old; 6 CatalogFetcher new; 2 old CatalogViewModel + 4 new; 5 old NodeOperations + 2 new = 7).

- [ ] **Step 7: Commit**

```bash
git add src-wpf/ComfyUI.Manager/Services/NodeOperations.cs \
        src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs \
        src-wpf/ComfyUI.Manager/App.xaml.cs \
        tests-wpf/ComfyUI.Manager.Tests/Services/NodeOperationsTests.cs
git commit -m "feat(wpf): NodeOperations consumes active DownloadSource (template substitution)"
```

---

## Task 8: Whole-branch review + fixes + bump v0.6.3 + release

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj:11` (`<Version>`)
- Modify: `pyproject.toml` (version)
- Modify: `src/comfy_mgr/__init__.py:1`
- Modify: `shared/errors.json:2`
- Modify: `tests/test_version_consistency.py:13,19,27`
- Create: `release/RELEASE-NOTES-v0.6.3.md`
- Modify: `memory/project_comfyui_manager.md`
- Modify: `memory/MEMORY.md`
- Modify: `.superpowers/sdd/progress.md`

- [ ] **Step 1: Run full test suite + build to confirm green**

Run:
```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
```

Expected: 0 errors, all tests pass.

- [ ] **Step 2: Dispatch whole-branch review subagent**

Dispatch a fresh `general-purpose` subagent with `scripts/review-package MERGE_BASE HEAD` output + the spec. Wait for its report. Fix any Critical / Important findings before proceeding.

If no issues: proceed to step 3.

- [ ] **Step 3: Bump version in 5 places**

1. `src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj:11`: `0.6.2` → `0.6.3`
2. `pyproject.toml` (the `[project]` `version =` line): `0.6.2` → `0.6.3`
3. `src/comfy_mgr/__init__.py:1`: `0.6.2` → `0.6.3`
4. `shared/errors.json:2` (`"_version": "0.6.2"`): `0.6.2` → `0.6.3`
5. `tests/test_version_consistency.py` (3 places: `assert comfy_mgr.__version__ == "0.6.2"`, `assert data["_version"] == "0.6.2"`, `assert m.group(1) == "0.6.2"`): all 3 → `0.6.3`

- [ ] **Step 4: Run version consistency tests**

Run: `pytest tests/test_version_consistency.py -v`
Expected: 3/3 PASS.

- [ ] **Step 5: Commit version bump**

```bash
git add src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj \
        pyproject.toml \
        src/comfy_mgr/__init__.py \
        shared/errors.json \
        tests/test_version_consistency.py
git commit -m "chore(release): bump to v0.6.3"
```

- [ ] **Step 6: Write release notes**

Create `release/RELEASE-NOTES-v0.6.3.md` with sections:

```markdown
## v0.6.3 — 节点源可配置下拉列表

**Settings 加了两个下拉列表框,每个列表管理一类节点源,默认各装 "comfyui manager"。**

### 新增

- **`查询节点的源` ComboBox** — 列表显示可用的 catalog JSON 源,默认 `comfyui manager`(URL = `https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main/custom-node-list.json`)。
- **`下载节点的源` ComboBox** — 列表显示可用的 git clone 源 URL(支持 `{node}` 占位),默认 `comfyui manager`(URL = `https://github.com/comfyanonymous/{node}`)。
- **自定义条目** — 每个 section 都有 "+ 添加" 按钮,inline 输入 Name + URL,追加到列表并自动 active。
- **删除** — 每行有"删除"按钮;删 active 项自动回落到列表第一条;删光则 Refresh/Install 给出清晰错误。
- **接线**:
  - `CatalogViewModel.Refresh` 解 M5.2-T7 TODO stub,真实 `HTTP GET → 解析 JSON → 写 SQLite catalog_cache`。网络失败时弹错误 + 保留本地 cache 可继续搜索。
  - `NodeOperations.InstallAsync` 在 catalog 条目无 repository URL 时回落到 active download source,`{node}` 占位自动替换为 node id。

### 不变

- `compat_api_base_url` 字段保留(用户明确:判度安装节点兼容性,与本次节点源无关)。
- `SettingsRepository` / `CatalogRepository` / `GitRunner` / `BulkUpdateOrchestrator` 接口不变。

### 升级注意

- v0.6.2 → v0.6.3 直接覆盖 zip。
- 旧 `settings.json` 没这 4 字段 → 自动套 `SettingsDefaults.Apply` 默认值,无需手动迁移。
- 默认源仍是 "comfyui manager";自定义条目继续存在。

### 测试

- WPF: **67+/67+ PASS**(8 → 13 SettingsDefaults;新增 5 NodeUrlResolver;2 → 9 SettingsViewModel;新增 6 CatalogFetcher;2 → 6 CatalogViewModel;5 → 7 NodeOperations)
- pytest: 181+ passed(版本一致性 3/3 PASS)

### 已知 carry-over

- 4 个 pre-existing M4 `_on_push_sync` silent-drop WS integration test 仍 fail(non-blocking, M5.2 已删 Python WS server,无对应代码可修)
```

- [ ] **Step 7: Commit release notes**

```bash
git add release/RELEASE-NOTES-v0.6.3.md
git commit -m "docs(release): RELEASE-NOTES-v0.6.3.md"
```

- [ ] **Step 8: Build release zip**

Run:
```bash
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build_release.ps1 -Version 0.6.3
```

Expected: `release/ComfyUI-Manager-v0.6.3-win-x64.zip` (~254 MB, contains `bin/git-portable/`).

- [ ] **Step 9: Push + tag + gh release**

```bash
git push origin main
git tag v0.6.3
git push origin v0.6.3
gh release create v0.6.3 release/ComfyUI-Manager-v0.6.3-win-x64.zip \
    --notes-file release/RELEASE-NOTES-v0.6.3.md \
    --title "v0.6.3 — Node source list dropdowns (query + download)"
gh release list --limit 3
```

Expected: v0.6.3 marked **Latest**.

- [ ] **Step 10: Update memory + ledger**

1. Append v0.6.3 section to `memory/project_comfyui_manager.md` (final-state section + how-to-apply).
2. Update `memory/MEMORY.md` ComfyUI Manager line to mention v0.6.3 as Latest.
3. Append v0.6.3 close-out entry to `.superpowers/sdd/progress.md`.

```bash
git add memory/project_comfyui_manager.md memory/MEMORY.md .superpowers/sdd/progress.md
git commit -m "docs(sdd): close v0.6.3 release — node source dropdowns shipped"
git push origin main
```

- [ ] **Step 11: Done**

The hotfix is shipped. v0.6.3 is the new Latest on GitHub.

---

## Self-Review (run before dispatch)

**1. Spec coverage** (skimming each spec section):

- §1.1 "Settings 页新增两个 section" → T3 (VM) + T4 (XAML) ✓
- §1.1 "ComboBox 切换 active" → T3 (ActiveQuerySource/ActiveDownloadSource) + T4 (ComboBox) ✓
- §1.1 "ItemsControl 列表 + 删除按钮" → T3 (Remove*Command) + T4 (ItemsControl) ✓
- §1.1 "+ 添加 inline form" → T3 (ConfirmAdd*Command) + T4 (form) ✓
- §1.1 "持久化到 settings.json" → T1 (Settings fields) + auto-save in T3 (CollectionChanged) ✓
- §1.1 "默认 settings.json 套默认值" → T1 (SettingsDefaults) ✓
- §1.1 "切换 active 后立即生效" → T3 (ActiveXxxSource setters write through to _settings) ✓
- §1.1 "Refresh → HTTP GET → 解析 JSON → 写 cache" → T5 (CatalogFetcher) + T6 (CatalogViewModel) ✓
- §1.1 "Install → git clone with {node} substituted" → T2 (NodeUrlResolver) + T7 (NodeOperations) ✓
- §3 "NodeSource model + 4 Settings fields + defaults" → T1 ✓
- §4 "ComboBox + ItemsControl + inline form XAML" → T4 ✓
- §5.1 "SettingsDefaults changes" → T1 ✓
- §5.2 "SettingsViewModel additions" → T3 ✓
- §5.3 "NodeUrlResolver" → T2 ✓
- §5.4 "NodeOperations consumes active download source" → T7 ✓
- §5.5 "CatalogViewModel.Refresh 解 stub" → T6 ✓
- §5.6 "CatalogFetcher" → T5 ✓
- §5.7 "App.xaml.cs wiring" → T6 + T7 ✓
- §6 "错误处理" — empty query source → ErrorMessage (T6), network failure → ErrorMessage + Search (T6), empty download source → Fail("未配置下载源") (T7), {node} in URL with no template → unchanged behavior (T2) ✓
- §7 "Tests" — all listed tests are in the plan ✓
- §10 "升级注意" → T8 release notes ✓

**2. Placeholder scan:** Searched for TBD / TODO / "implement later" / "fill in details" — none. All code blocks are complete. (One TODO reference at spec §0 is a deliberate link to the existing M5.2-T7 stub being removed, not a placeholder.)

**3. Type consistency:** Verified all type names across tasks:
- `NodeSource` (T1) → used in T1/T3/T4/T7 consistently
- `Settings.QuerySources` / `Settings.DownloadSources` (T1) → used in T1/T3/T6/T7 consistently
- `Settings.ActiveQuerySourceName` / `Settings.ActiveDownloadSourceName` (T1) → used in T1/T3/T6/T7 consistently
- `CatalogViewModel` ctor: `(repo, envRepo, nodeOps, fetcher, settings)` — used in T6 main + T6 tests + T6 MainViewModel pass-through
- `NodeOperations` ctor: `(git, envRepo, nodeRepo, settings)` — used in T7 main + T7 tests + T7 App.xaml.cs wiring
- `MainViewModel` ctor: `(...gitProxy, settings, catalogFetcher)` — used in T6 (catVM ctor) + T7 (App wiring)
- `FakeCatalogFetcher.FetchAsync` is `virtual` — must override `virtual` in T5 step 4 — verified in T6 step 1
- `SettingsDefaults.Apply` signature unchanged — verified in T1

All consistent.
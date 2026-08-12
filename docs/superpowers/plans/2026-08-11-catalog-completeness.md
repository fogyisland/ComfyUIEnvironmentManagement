# v0.6.11+ SDD A: Catalog 仓库地址 + 版本完整 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复 Catalog 列表/卡片"仓库地址空"(因 `ExtractReference` 漏查 `repository` key)、列表卡片不显示 `LatestVersion` 两个问题;详情面板 ComboBox 保持现有 v0.6.11+ T3 行为不动。

**Architecture:**
- `CatalogRefreshService.ExtractReference` 改 3-key 优先级 (`reference` → `url` → `repository`) + 加 `!IsNullOrEmpty` 守卫 + `private static` → `internal static`(测试可直接调,不走整个 `RefreshAsync`)。
- `Theme.xaml` 的 `CatalogRowCardTemplate`(466-538)新增 Grid 第 4 行 `latest: ...`(`TargetNullValue` + Style trigger 双覆盖 null + 空串)。
- 不动 `CatalogTileTemplate`(329)/ `CatalogTileItemContainerStyle`(301) — 磁贴模式本轮不改。

**Tech Stack:** WPF .NET 8 / C# 12 · xUnit · 现有 `CatalogEntry.LatestVersion` (`string?`, `Models/CatalogEntry.cs:26`) · `InternalsVisibleTo("ComfyUI.Manager.Tests")` 已有(`ComfyUI.Manager.csproj:50`)。

**base SHA:** `b3d418b`(v0.6.11+ SDD D1 MERGED,HEAD)
**target:** `b3d418b..HEAD`,2 commits on `catalog-completeness` branch
**baseline tests:** 863/0/1(主分支 baseline,D1 SHIP 后)+9 新测试 = ~872/0/1

---

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| **G1** | `ExtractReference` 改 3-key 优先级 `reference` → `url` → `repository`,每个 key 都加 `!string.IsNullOrEmpty` 守卫,避免 raw_metadata 空串绕过 | spec §1 |
| G2 | `ExtractReference` 从 `private static` 改为 `internal static` —— csproj 已有 `<InternalsVisibleTo Include="ComfyUI.Manager.Tests" />`(`ComfyUI.Manager.csproj:50`),测试直接调 | spec §1 |
| G3 | **不动** `CatalogTileTemplate`(`Theme.xaml:329`)/ `CatalogTileItemContainerStyle`(`Theme.xaml:301`) —— 磁贴模式本轮不改 | spec §2 |
| G4 | 列表卡片 `latest:` 行只加在 `CatalogRowCardTemplate`(`Theme.xaml:466-538`) —— 即 `CatalogView.xaml:113` 列表模式 `ItemTemplate` 引用的 DataTemplate | spec §2 |
| G5 | `latest:` 文本格式 `latest: {LatestVersion}`,`TargetNullValue='latest: —'`(null)+ Style trigger `LatestVersion=""`(空串)双覆盖 | spec §2 |
| G6 | ToolTip 固定文案 `非 GitHub 源或尚未刷新时无法自动获取版本`(不区分源类型) | spec §2 |
| G7 | 详情面板 ComboBox + date label 不动 —— 现有 v0.6.11+ T3 行为保留 | spec §3 |
| G8 | 不改 `LatestVersion` 写入逻辑(GitHub releases only 是 v0.6.11+ T3 决策) | spec §非目标 |
| G9 | 不改 catalog 缓存 schema | spec §非目标 |
| G10 | 不做 non-GitHub 版本自动抓取(GitLab / Bitbucket / custom URL 各自不同) | spec §非目标 |
| G11 | 不动 download 路径 | spec §非目标 |
| G12 | 不加 "version history" 列表(只显示 latest) | spec §YAGNI |
| G13 | 不加 "auto-update" 按钮 | spec §YAGNI |
| G14 | 不改 dropdown selection 行为 | spec §YAGNI |
| G15 | 所有 Theme.xaml 新增 Setter 必须 property-element + `DynamicResource` —— v0.6.9.2 教训(参见 `feedback_wpf_style_setter_dynamic_resource.md`) | 跨 SDD 约定 |
| G16 | STA load test 走 `WpfTestResources.EnsureLoaded(PaletteVariant)` helper(`tests-wpf/.../WpfTestResources.cs:59`),不自己 new Application | v0.6.9.3 教训 |

---

## File Structure

**改动**(本 SDD):

| 文件 | 职责 |
|---|---|
| `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs` | `ExtractReference` 改 3-key + `!IsNullOrEmpty` + `internal` 可见性 |
| `src-wpf/ComfyUI.Manager/Resources/Theme.xaml` | `CatalogRowCardTemplate`(466-538)新增第 4 Grid 行 `latest: ...` |

**新建**(测试):

| 文件 | 测试数 |
|---|---|
| `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceExtractReferenceTests.cs` | 8 unit tests |

**修改**(测试):

| 文件 | 测试数 |
|---|---|
| `tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs` | +1 STA test |

**不动**:
- `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml` —— 详情面板 ComboBox + date label 不动
- `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs` —— 无改动
- `src-wpf/ComfyUI.Manager/Models/CatalogEntry.cs` —— `LatestVersion` 字段已存在
- `src-wpf/ComfyUI.Manager/Services/CatalogFetcher.cs` —— 不改下载逻辑
- `src-wpf/ComfyUI.Manager/Data/CatalogRepository.cs` —— 不改 schema

---

## Task Breakdown

### Task 1: `ExtractReference` 3-key 优先级 + 8 unit tests

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs:122-130`(`ExtractReference` 方法体)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceExtractReferenceTests.cs`

**Interfaces:**
- Consumes: 无(底层任务)
- Produces:
  - `internal static string CatalogRefreshService.ExtractReference(CatalogEntry entry)` —— 3-key 优先级 + `!IsNullOrEmpty` 守卫

**Step 1.1: 写 8 个失败测试**

`tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceExtractReferenceTests.cs`:

```csharp
using System.Collections.Generic;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.11+ SDD A: ExtractReference 3-key 优先级 (reference → url → repository)。
/// raw_metadata 空串视为"未配置"—— 不返回空串本身,继续查下一个 key。
/// </summary>
public class CatalogRefreshServiceExtractReferenceTests
{
    [Fact]
    public void ExtractReference_ReferenceKeyOnly_ReturnsReference()
    {
        var entry = new CatalogEntry
        {
            RawMetadata = new Dictionary<string, object?>
            {
                ["reference"] = "https://github.com/a/b",
            },
        };
        Assert.Equal("https://github.com/a/b", CatalogRefreshService.ExtractReference(entry));
    }

    [Fact]
    public void ExtractReference_UrlKeyOnly_ReturnsUrl()
    {
        var entry = new CatalogEntry
        {
            RawMetadata = new Dictionary<string, object?>
            {
                ["url"] = "https://github.com/c/d",
            },
        };
        Assert.Equal("https://github.com/c/d", CatalogRefreshService.ExtractReference(entry));
    }

    [Fact]
    public void ExtractReference_RepositoryKeyOnly_ReturnsRepository()
    {
        var entry = new CatalogEntry
        {
            RawMetadata = new Dictionary<string, object?>
            {
                ["repository"] = "https://github.com/e/f",
            },
        };
        Assert.Equal("https://github.com/e/f", CatalogRefreshService.ExtractReference(entry));
    }

    [Fact]
    public void ExtractReference_ReferenceAndUrl_ReturnsReference_Priority()
    {
        var entry = new CatalogEntry
        {
            RawMetadata = new Dictionary<string, object?>
            {
                ["reference"] = "https://github.com/a/b",
                ["url"] = "https://github.com/c/d",
            },
        };
        Assert.Equal("https://github.com/a/b", CatalogRefreshService.ExtractReference(entry));
    }

    [Fact]
    public void ExtractReference_AllThree_ReturnsReference_Priority()
    {
        var entry = new CatalogEntry
        {
            RawMetadata = new Dictionary<string, object?>
            {
                ["reference"] = "https://github.com/a/b",
                ["url"] = "https://github.com/c/d",
                ["repository"] = "https://github.com/e/f",
            },
        };
        Assert.Equal("https://github.com/a/b", CatalogRefreshService.ExtractReference(entry));
    }

    [Fact]
    public void ExtractReference_AllEmpty_ReturnsEmptyString()
    {
        var entry = new CatalogEntry
        {
            RawMetadata = new Dictionary<string, object?>
            {
                ["reference"] = "",
                ["url"] = "",
                ["repository"] = "",
            },
        };
        Assert.Equal("", CatalogRefreshService.ExtractReference(entry));
    }

    [Fact]
    public void ExtractReference_NullRawMetadata_ReturnsEmptyString()
    {
        var entry = new CatalogEntry { RawMetadata = null! };
        Assert.Equal("", CatalogRefreshService.ExtractReference(entry));
    }

    [Fact]
    public void ExtractReference_EmptyStringValues_ReturnsEmptyString_NotFallback()
    {
        // reference="" url="" 但 repository="https://github.com/g/h" → 应返回 repository(不因 reference 空串继续返回 "")
        var entry = new CatalogEntry
        {
            RawMetadata = new Dictionary<string, object?>
            {
                ["reference"] = "",
                ["url"] = "",
                ["repository"] = "https://github.com/g/h",
            },
        };
        Assert.Equal("https://github.com/g/h", CatalogRefreshService.ExtractReference(entry));
    }
}
```

**Step 1.2: 跑测试确认编译失败**

```bash
cd D:/ToolDevelop/ComfyUI
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogRefreshServiceExtractReference" -v minimal
```

期望: 编译失败 —— `CS0117: 'CatalogRefreshService' does not contain a definition for 'ExtractReference'`(当前 `ExtractReference` 是 `private static`,跨 assembly 不可见)。这一步是因为当前是 private,internal 后才能从 tests assembly 调到。

**Step 1.3: 改 `ExtractReference` 实现 + 可见性**

`src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs:122-130`,把整个 `ExtractReference` 方法替换为:

```csharp
internal static string ExtractReference(CatalogEntry entry)
{
    if (entry.RawMetadata is null) return "";
    if (entry.RawMetadata.TryGetValue("reference", out var r) && r is string rs && !string.IsNullOrEmpty(rs))
        return rs;
    if (entry.RawMetadata.TryGetValue("url", out var u) && u is string us && !string.IsNullOrEmpty(us))
        return us;
    if (entry.RawMetadata.TryGetValue("repository", out var repo) && repo is string repos && !string.IsNullOrEmpty(repos))
        return repos;
    return "";
}
```

变更点:
1. `private static` → `internal static`(跨 assembly 可见给 tests)
2. 加 `repository` key 优先级(三 key fallback)
3. 每个 key 后加 `&& !string.IsNullOrEmpty(...)` 守卫 — 避免 raw_metadata 有空串时返回 ""

**Step 1.4: 跑测试确认 PASS**

```bash
cd D:/ToolDevelop/ComfyUI
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogRefreshServiceExtractReference" -v minimal
```

期望: 8 PASS / 0 FAIL。`ExtractReference_NullRawMetadata_ReturnsEmptyString` 走的是 entry ctor 默认 `RawMetadata = new()`(参见 `Models/CatalogEntry.cs:19`),不真为 null,但测试显式 set `null!` 覆盖了。

**Step 1.5: 跑 CatalogRefreshService 既有测试确认无 regression**

```bash
cd D:/ToolDevelop/ComfyUI
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogRefreshService" -v minimal
```

期望: 全部既有测试 PASS(`ExtractReference_ReferenceKeyOnly` / `...UrlKeyOnly` 等行为在测试数据只放 `reference` 时跟旧实现一致 → 0 regression)。

**Step 1.6: Commit**

```bash
cd D:/ToolDevelop/ComfyUI
git add src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs
git add tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceExtractReferenceTests.cs
git commit -m "feat(wpf): ExtractReference 3-key priority (reference/url/repository)

v0.6.11+ SDD A T1: 部分 catalog entry 仓库地址空,因 ExtractReference 只
查 reference/url 两个 key;download 路径写的是 repository。改 3-key 优先级
+ 加 IsNullOrEmpty 守卫 + 改 internal static 让 unit test 直接调。

8 新单元测试覆盖所有优先级 + 空串 fallback + null RawMetadata 边界。"
```

---

### Task 2: `CatalogRowCardTemplate` 新增 `latest:` 行 + STA load test

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Resources/Theme.xaml:466-538`(`CatalogRowCardTemplate` 第 4 Grid 行)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs`(+1 STA test)

**Interfaces:**
- Consumes:
  - `CatalogEntry.LatestVersion`(`Models/CatalogEntry.cs:26`,`string?`)
- Produces:
  - `CatalogRowCardTemplate` 第 4 Grid 行: `latest: {LatestVersion}` with `TargetNullValue='latest: —'` + empty-string Style trigger
  - ToolTip 固定文案 `非 GitHub 源或尚未刷新时无法自动获取版本`

**Step 2.1: 写失败测试 — STA 加载含 latest: 行的列表卡片**

`tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs` 末尾追加:

```csharp
[Fact]
public void CatalogView_LatestVersionBinding_RendersWithoutException()
{
    Exception? caught = null;

    var thread = new Thread(() =>
    {
        try
        {
            WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);

            // 构造一个 ListBox 引用 Theme.xaml 里的 CatalogRowCardTemplate +
            // CatalogCardItemContainerStyle,2 个 entry:1 个 LatestVersion="v0.6.7",
            // 1 个 LatestVersion=null(走 TargetNullValue → "latest: —")
            var app = System.Windows.Application.Current;
            var template = (System.Windows.DataTemplate)app!.Resources["CatalogRowCardTemplate"];
            var containerStyle = (System.Windows.Style)app.Resources["CatalogCardItemContainerStyle"];

            var listBox = new System.Windows.Controls.ListBox
            {
                ItemTemplate = template,
                ItemContainerStyle = containerStyle,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            };

            var entries = new System.Collections.Generic.List<ComfyUI.Manager.Models.CatalogEntry>
            {
                new()
                {
                    Id = "node-1",
                    Package = "pkg-with-latest",
                    Author = "Alice",
                    Description = "Has version",
                    LatestVersion = "v0.6.7",
                },
                new()
                {
                    Id = "node-2",
                    Package = "pkg-no-latest",
                    Author = "Bob",
                    Description = "No version",
                    LatestVersion = null,
                },
            };
            listBox.ItemsSource = entries;

            listBox.Measure(new System.Windows.Size(900, 700));
            listBox.Arrange(new System.Windows.Rect(0, 0, 900, 700));
            listBox.UpdateLayout();
        }
        catch (Exception ex)
        {
            caught = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();

    if (caught is not null)
    {
        throw new Exception(
            $"CatalogView LatestVersion binding render failed: {caught.GetType().FullName}: {caught.Message}\n" +
            $"--- InnerException ---\n{caught.InnerException}\n" +
            $"--- StackTrace ---\n{caught.StackTrace}",
            caught);
    }
}
```

**Step 2.2: 跑测试确认 PASS(此时 XAML 还没改 — 因为现有卡片没 latest: 行,绑定空 path 不应该抛)**

```bash
cd D:/ToolDevelop/ComfyUI
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogView_LatestVersionBinding" -v minimal
```

期望: PASS(XAML 解析 + ListBox 渲染 OK,binding 到不存在的 `LatestVersion` property 不抛异常 — 仅 TextBlock.Text 空)。这一步只是 "baseline 模板加载 OK"的 sanity check,不是 fail-then-fix。

**Step 2.3: 在 `CatalogRowCardTemplate` 新增第 4 Grid 行**

`src-wpf/ComfyUI.Manager/Resources/Theme.xaml:466-538`,把现有 `CatalogRowCardTemplate` 替换为:

```xml
<DataTemplate x:Key="CatalogRowCardTemplate">
    <Border Padding="12" CornerRadius="6"
            Background="{DynamicResource SurfaceBrush}"
            BorderThickness="1">
        <Border.Style>
            <Style TargetType="Border">
                <Setter Property="BorderBrush" Value="{DynamicResource OutlineBrush}" />
                <Style.Triggers>
                    <DataTrigger Value="True">
                        <DataTrigger.Binding>
                            <Binding Path="IsSelected"
                                     RelativeSource="{RelativeSource AncestorType=ListBoxItem}" />
                        </DataTrigger.Binding>
                        <Setter Property="BorderBrush" Value="{DynamicResource PrimaryBrush}" />
                        <Setter Property="BorderThickness" Value="2" />
                        <Setter Property="Padding" Value="11" />
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </Border.Style>
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>
            <!-- Row 0: Package + ⭐ -->
            <Grid Grid.Row="0">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <TextBlock Grid.Column="0" Text="{Binding Package}"
                           FontSize="16" FontWeight="Bold"
                           Foreground="{DynamicResource OnSurfaceBrush}"
                           TextTrimming="CharacterEllipsis" VerticalAlignment="Center" />
                <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Center">
                    <StackPanel.Style>
                        <Style TargetType="StackPanel">
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding RawMetadata[stars]}" Value="">
                                    <Setter Property="Visibility" Value="Collapsed" />
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </StackPanel.Style>
                    <TextBlock Text="⭐ " FontSize="12"
                               Foreground="{DynamicResource OutlineBrush}" />
                    <TextBlock Text="{Binding RawMetadata[stars]}"
                               FontSize="12" FontWeight="Bold"
                               Foreground="{DynamicResource OnSurfaceBrush}" />
                </StackPanel>
            </Grid>
            <!-- Row 1: 作者 + install_type pill -->
            <StackPanel Grid.Row="1" Orientation="Horizontal" Margin="0,6,0,0">
                <TextBlock Text="{Binding Author}"
                           FontSize="12"
                           Foreground="{DynamicResource OutlineBrush}" />
                <Border Style="{StaticResource CatalogInstallTypeBadgeStyle}">
                    <TextBlock Text="{Binding InstallType}"
                               FontSize="10"
                               Foreground="{DynamicResource OnPrimaryBrush}" />
                </Border>
            </StackPanel>
            <!-- Row 2: 描述摘要 -->
            <TextBlock Grid.Row="2" Text="{Binding Description}"
                       FontSize="12" Margin="0,6,0,0"
                       TextWrapping="Wrap" MaxHeight="40"
                       TextTrimming="CharacterEllipsis"
                       Foreground="{DynamicResource OnSurfaceBrush}" />
            <!-- Row 3 (v0.6.11+ SDD A): latest 版本 + tooltip -->
            <TextBlock Grid.Row="3"
                       FontSize="12" Margin="0,6,0,0"
                       Foreground="{DynamicResource OnSurfaceBrush}"
                       TextTrimming="CharacterEllipsis"
                       ToolTip="非 GitHub 源或尚未刷新时无法自动获取版本">
                <TextBlock.Text>
                    <Binding Path="LatestVersion"
                             StringFormat="latest: {0}"
                             TargetNullValue="latest: —" />
                </TextBlock.Text>
                <TextBlock.Style>
                    <Style TargetType="TextBlock">
                        <Style.Triggers>
                            <!-- 空串走同一 fallback 文本 -->
                            <DataTrigger Binding="{Binding LatestVersion}" Value="">
                                <Setter Property="Text" Value="latest: —" />
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </TextBlock.Style>
            </TextBlock>
        </Grid>
    </Border>
</DataTemplate>
```

变更点:
1. `Grid.RowDefinitions` 加第 4 行 `RowDefinition Height="Auto"`
2. 新增 Row 3 `TextBlock`: 绑 `LatestVersion`,`StringFormat='latest: {0}'`,`TargetNullValue='latest: —'`
3. 加 `TextBlock.Style` 含 `DataTrigger Value=""` —— 覆盖空串场景(`TargetNullValue` 只管 null,不管 `""`)
4. 加 `ToolTip` 固定文案
5. 配色用 `DynamicResource OnSurfaceBrush`(G15)

**Step 2.4: 跑 STA 测试确认 PASS**

```bash
cd D:/ToolDevelop/ComfyUI
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogViewLoad" -v minimal
```

期望: 3 PASS(原 2 个 + 新增 1 个)。新增测试验证 2 个 entry 一个带 LatestVersion 一个 null,模板不抛 XAML 解析或 binding 异常。

**Step 2.5: 跑全套测试确认无 regression**

```bash
cd D:/ToolDevelop/ComfyUI
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build
```

期望: 863/0/1 baseline + 9 新测试 = **872 PASS / 0 FAIL / 1 SKIP**(SKIP 是 `LiveGitHubVersionFetchTests.LiveFetch_RealGitHub_StoresTags`,D1 SHIP 时就有)。

**Step 2.6: Commit**

```bash
cd D:/ToolDevelop/ComfyUI
git add src-wpf/ComfyUI.Manager/Resources/Theme.xaml
git add tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs
git commit -m "feat(wpf): CatalogRowCardTemplate latest: row

v0.6.11+ SDD A T2: 列表卡片从不显示 version(只详情面板 ComboBox 显示)。
新增 Grid 第 4 行 latest: {LatestVersion},TargetNullValue='latest: —'
覆盖 null,Style DataTrigger Value='' 覆盖空串,ToolTip 解释空状态原因。

磁贴模式 (CatalogTileTemplate/CatalogTileItemContainerStyle) 不动 —
本轮只改 list mode card。1 个新增 STA load test 验证 2 个 entry
(1 LatestVersion='v0.6.7' + 1 LatestVersion=null) 渲染无异常。"
```

---

### Task 3: final review + MEMORY + staging rebuild + GUI smoke

**Files:**
- Create: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_v0_6_11_plus_catalog_completeness.md`
- Modify: `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md`(+1 行 index entry)

**Step 3.1: 跑全套 + build 终极验证**

```bash
cd D:/ToolDevelop/ComfyUI
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build   # 872/0/1
```

期望: build 0/0,test 872/0/1。

**Step 3.2: Final whole-branch review (opus)**

按 `superpowers:requesting-code-review` skill,在 worktree 里 dispatch 一个 final reviewer(subagent,opus model),覆盖范围 `b3d418b..HEAD`(2 commits)。Reviewer 输入:
- 本 plan 路径(`docs/superpowers/plans/2026-08-11-catalog-completeness.md`)
- 2 commit SHAs — 取自 `git log --oneline b3d418b..HEAD`(ledger 也会记)
- `.superpowers/sdd/2026-08-11-catalog-completeness/progress.md` ledger(reviewer 看 ledger 里的 brief + report)

Reviewer 必须返回 verdict: `Spec ✅ / Task quality Approved, ship` 或 fix items。如果 reviewer 返回 fix items,按 subagent-driven-development 的 fix loop 走(每 task 最多 5 轮,本 plan 整体在 T3 的 final review 轮处理)。

**Step 3.3: 写 ship memory**

`C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_v0_6_11_plus_catalog_completeness.md`(新建):

```markdown
---
name: v0.6.11+ SDD A Catalog 仓库地址+版本完整
description: ExtractReference 3-key priority + 列表卡片 latest: 行
type: project
---
# v0.6.11+ SDD A: Catalog 仓库地址 + 版本完整 — SHIP

## Status

✓ **SHIP-READY**,branch `catalog-completeness`,HEAD 即 merge commit SHA(填入 step 3.7 后),**872 PASS / 0 FAIL / 1 SKIP** (+9 over baseline 863)。

## Scope

修复 Catalog "仓库地址空"(因 ExtractReference 只查 reference/url 两个 key)+ "列表卡片不显示 version"(只详情面板 ComboBox 显示)两个问题。磁贴模式本轮不改。

## Architecture

- `CatalogRefreshService.ExtractReference` 改 3-key 优先级(`reference` → `url` → `repository`)+ 加 `!IsNullOrEmpty` 守卫(避免 raw_metadata 空串绕过)+ `private static` → `internal static`(test 直接调,不走整个 RefreshAsync)
- `Theme.xaml` `CatalogRowCardTemplate`(466-538)新增 Grid 第 4 行 `latest: {LatestVersion}`,`TargetNullValue='latest: —'`(null)+ Style trigger `LatestVersion=""`(空串)+ ToolTip 固定文案 `非 GitHub 源或尚未刷新时无法自动获取版本`
- 磁贴模式 (`CatalogTileTemplate`/`CatalogTileItemContainerStyle`) 不动 — YAGNI

## Locked decisions

- 触发源唯一 = `NodeOperations.InstallAsync` 成功路径(env 内装节点)
- 优先级 = `reference` > `url` > `repository`(reference 缺失再降级)
- 空串视为"未配置",继续查下一个 key,不直接返回空串
- `latest:` 行只加 list mode card,磁贴模式不动
- 列表卡 ToolTip 文案固定,不区分源类型(避免 XAML 内 host 判断)

## Files (2 source + 2 test, 2 commits)

- `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs` — `ExtractReference` 改 3-key + IsNullOrEmpty + internal
- `src-wpf/ComfyUI.Manager/Resources/Theme.xaml` — `CatalogRowCardTemplate` Row 3 + 4
- `tests-wpf/.../Services/CatalogRefreshServiceExtractReferenceTests.cs` — 新建(8 unit tests)
- `tests-wpf/.../Views/CatalogViewLoadTests.cs` — +1 STA test(LatestVersion binding render)

## Carry-forward (YAGNI,均不阻塞)

- 不做 "version history" 列表(只显示 latest)
- 不做 "auto-update" 按钮
- 不做 non-GitHub 版本自动抓取(GitLab / Bitbucket)
- 不改磁贴模式(只 list mode card 改 latest:)
- 不改 dropdown selection 行为
- 不动 download 路径
- 不改 catalog 缓存 schema
- 旧缓存 entry 没 repository 字段 → 仍显示空仓库地址,直到用户 refresh(可接受,v0.6.7.4 catalog cache 永久)

## 用户原话

> "Catalog 页面有些 entry 仓库地址是空的,版本也不完整"

## 验证

- 单元测试:8/8 ExtractReference PASS
- STA load test:3/3 CatalogViewLoad PASS(2 原 + 1 新)
- 全套:872/0/1
- Build:0/0
- GUI smoke 待桌面验证(节点目录页 → 列表卡片 → 每行 latest: 显示 / 仓库地址栏非空 / 详情面板 ComboBox 不变)

## 故障教训

(从 final reviewer 取,如有)

## 分支状态

- branch: `catalog-completeness`(尚未 merge)
- worktree: `D:/ToolDevelop/ComfyUI/.claude/worktrees/catalog-completeness`
- 待决定: merge to main / push PR / keep as-is
```

**Step 3.4: 更新 MEMORY.md index**

`C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md`,在 D1 entry 后面追加一行(保持单行 ≤ 200 chars):

```
- [v0.6.11+ Catalog 仓库地址+版本完整 SDD A](project_v0_6_11_plus_catalog_completeness.md) — ✓ SHIP-READY,872/0/1,ExtractReference 3-key + 列表卡片 latest: 行
```

**Step 3.5: rebuild staging**

```bash
cd D:/ToolDevelop/ComfyUI
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal
```

期望: 0 errors。staging `ComfyUI.Manager.exe` rebuild 完。

**Step 3.6: GUI smoke(用户桌面)**

8 步 spec §Verification:
1. 启动 staging → 主页(Dashboard 正常)
2. 节点目录页 → 刷新 → 列表模式
3. 选中 entry → 详情面板 ComboBox + date label 仍正常(G7)
4. 检查每行 `latest: vX.Y.Z` 显示(有 LatestVersion 的 entry)
5. 检查每行 `latest: —` 显示(无 LatestVersion 的 entry,鼠标 hover 看 tooltip)
6. 检查 entry 仓库地址栏(`SelectedReference`)非空(若 entry 有 origin 应都有 — 旧缓存无 repository 字段的仍空,可接受)
7. 切到磁贴模式 → 卡片**不**显示 latest 行(本轮 YAGNI)
8. 暗/亮主题切换 → 新 latest 行 brush 跟随(G15 DynamicResource)

**Step 3.7: 完成 — 等待用户决定 merge/push/keep-as-is**

通过 `superpowers:finishing-a-development-branch` skill 呈现 3 选项菜单。
```

---

## Critical Files (full list)

**Modified (across all 3 tasks):**
- `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs` (T1 `ExtractReference`)
- `src-wpf/ComfyUI.Manager/Resources/Theme.xaml` (T2 `CatalogRowCardTemplate`)
- `tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs` (T2 +1 STA test)

**Created:**
- `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceExtractReferenceTests.cs` (T1 8 unit tests)

**Memory files (T3):**
- `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\project_v0_6_11_plus_catalog_completeness.md` (new)
- `C:\Users\徐鹏\.claude\projects\D--ToolDevelop-ComfyUI\memory\MEMORY.md` (+1 index line)

---

## Verification (end-to-end)

按顺序验证 3 task commit 全 PASS:

```bash
# T1 验证 (worktree 内,目录取 worktree 路径)
git -C "$WORKTREE" status --short   # 应该 clean
git -C "$WORKTREE" log --oneline -1   # 应是 T1 commit
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogRefreshServiceExtractReference" -v minimal   # 8 PASS
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogRefreshService" -v minimal   # 既有全 PASS

# T2 验证 (worktree 内,基于 T1 commit)
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogViewLoad" -v minimal   # 3 PASS (2 + 1)

# 全套验证 (所有 commit 合并后)
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build   # 872/0/1
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal   # 0 errors
```

**GUI smoke (桌面验证, user):**
1. 启动 staging → 主页 Dashboard 正常
2. 节点目录页 → 刷新 → 列表模式
3. 选中 entry → 详情面板 ComboBox + date label 仍正常(G7)
4. 检查每行 `latest: vX.Y.Z` 显示
5. 检查每行 `latest: —` 显示 + tooltip
6. 检查 entry 仓库地址栏非空(若有 origin 应都有)
7. 切到磁贴模式 → 卡片不显示 latest(G3)
8. 暗/亮主题切换 → latest 行 brush 跟随(G15)

---

## Risks

| 风险 | 缓解 |
|---|---|
| 旧缓存 entry 没 `repository` 字段 → ExtractReference 也不命中 → 仍显示空 | 风险,用户 re-refresh 后才生效;Info log 提示 |
| 加 `latest:` 行后 list card 太高 | Card 高度自适应(Grid layout Auto rows),不需固定高度 |
| 非 GitHub 源显示 `—` 用户困惑 | tooltip 解释 "非 GitHub 源,无法自动获取版本"(spec §2 G6) |
| `ExtractReference_EmptyStringValues_ReturnsEmptyString_NotFallback` 测试期望 fallthrough,旧 raw_metadata 空串会被新逻辑跳到下一个 key — 跟 spec §1 "守卫避免空字符串绕过"匹配 |
| Theme.xaml `Setter Value="latest: —"` 加在 `TextBlock.Style.DataTrigger` 内,可能跟 `Text` binding 冲突 | 优先级:DataTrigger setter > Style binding(已生效);跟 v0.6.9.2 hotfix 同 pattern 但不涉及 DynamicResource/Style Setter 解析 |

---

## Execution Choice

**Subagent-Driven Development (沿用项目惯例)**:
- 2 task × (implementer + reviewer) ≈ 4 dispatch
- T3 final review + memory + staging 单独 dispatch(opus final reviewer)
- 3 commits on branch `catalog-completeness`,最后 staging rebuild + GUI smoke + MEMORY update

(Plan agent left out: 用户已通过 SDD A spec approve 全范围;本 plan 文件已是最终设计。下一步进入实施模式 → subagent-driven-development skill 起步 T1。)
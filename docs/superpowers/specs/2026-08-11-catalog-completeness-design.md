---
date: 2026-08-11
topic: Catalog 仓库地址 + 版本完整
base_sha: 79af0f3
spec_status: DRAFT
plan_status: PENDING
---

# Catalog 仓库地址 + 版本完整 — 设计

## Scope

修复 Catalog 页面"仓库地址、版本不完整"问题:
1. 部分 entry 显示空仓库地址(因为 `ExtractReference` 只查 `reference`/`url` 两个 key,但 download 路径用的是 `repository` key)
2. 列表卡片从不显示 version(只详情面板 ComboBox 显示)

## 修复点

### §1 `ExtractReference` 3-key 优先级

`src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs:122-130` 当前实现:

```csharp
private static string ExtractReference(CatalogEntry entry)
{
    if (entry.RawMetadata is null) return "";
    if (entry.RawMetadata.TryGetValue("reference", out var r) && r is string rs)
        return rs;
    if (entry.RawMetadata.TryGetValue("url", out var u) && u is string us)
        return us;
    return "";
}
```

改为 3-key 优先级 (`reference` → `url` → `repository`):

```csharp
private static string ExtractReference(CatalogEntry entry)
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

加 `!string.IsNullOrEmpty(rs)` 守卫避免空字符串绕过(当前实现在 raw_metadata 有空字符串时也会返回)。

方法可见性从 `private static` 改为 `internal static` —— csproj 已有
`<InternalsVisibleTo Include="ComfyUI.Manager.Tests" />`(`ComfyUI.Manager.csproj:50`),
测试直接调 `CatalogRefreshService.ExtractReference(entry)`,不需要跑整个 `RefreshAsync`。

### §2 列表卡片显示 version

改 `src-wpf/ComfyUI.Manager/Resources/Theme.xaml` 的 **`CatalogRowCardTemplate`**(`x:Key`,行 466-538)——
即 `CatalogView.xaml:113` 列表模式 `ItemTemplate` 引用的那个 DataTemplate。
**不动** `CatalogTileTemplate`(行 329)和 `CatalogTileItemContainerStyle`(行 301),磁贴模式本轮不改。

卡片底部行新增 `latest:` 段:

```
package-name                          [Git]
Author
Description text... [展开]
──────────────────────────────────────
latest: v0.3.2     2026-08-01     [Install]
```

绑定源是 `CatalogEntry.LatestVersion`(`Models/CatalogEntry.cs:26`,类型 `string?`):

- `LatestVersion` 非空 → 显示 `latest: {LatestVersion}`
- `LatestVersion` 为 null 或空串 → 显示 `latest: —`
- 实现方式:`<TextBlock Text="{Binding LatestVersion, StringFormat='latest: {0}', TargetNullValue='latest: —'}" />`
  加 `Style` DataTrigger 把空串也映射到 `latest: —`(`TargetNullValue` 只管 null,不管 `""`)
- ToolTip 固定文案:`非 GitHub 源或尚未刷新时无法自动获取版本`(不区分源类型,避免在 XAML 里判断 URL host)

### §3 详情面板不动

`CatalogView.xaml` 详情面板保留现有 ComboBox + date label。

## Tests

### 单元测试

`tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceExtractReferenceTests.cs`(新建):

- `ExtractReference_ReferenceKeyOnly_ReturnsReference`
- `ExtractReference_UrlKeyOnly_ReturnsUrl`
- `ExtractReference_RepositoryKeyOnly_ReturnsRepository`
- `ExtractReference_ReferenceAndUrl_ReturnsReference_Priority`
- `ExtractReference_AllThree_ReturnsReference_Priority`
- `ExtractReference_AllEmpty_ReturnsEmptyString`
- `ExtractReference_NullRawMetadata_ReturnsEmptyString`
- `ExtractReference_EmptyStringValues_ReturnsEmptyString_NotFallback`

### STA load test

`tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs`(既有)— 加 1 个测试:

- `CatalogView_LatestVersionBinding_RendersWithoutException`(mock 1 entry with `LatestVersion="v0.6.7"` + 1 entry with `LatestVersion=null`)

## 改动文件

- `src-wpf/ComfyUI.Manager/Services/CatalogRefreshService.cs` — `ExtractReference` 改 3-key 优先级
- `src-wpf/ComfyUI.Manager/Resources/Theme.xaml` — `CatalogRowCardTemplate`(466-538)加 `latest: ...` 段
- `src-wpf/ComfyUI.Manager/Views/CatalogView.xaml` — 不动
- `src-wpf/ComfyUI.Manager/ViewModels/CatalogViewModel.cs` — 无改动
- `tests-wpf/ComfyUI.Manager.Tests/Services/CatalogRefreshServiceExtractReferenceTests.cs` — 新建
- `tests-wpf/ComfyUI.Manager.Tests/Views/CatalogViewLoadTests.cs` — +1 STA test

## 非目标 (G6 冻结)

- 不改 `LatestVersion` 写入逻辑(GitHub releases only 是 v0.6.11+ T3 决策)
- 不改 catalog 缓存 schema
- 不做 non-GitHub 版本自动抓取(GitLab / Bitbucket / custom URL 各自不同)
- 不动 download 路径

## YAGNI 划线

- 不加 "version history" 列表(只显示 latest)
- 不加 "auto-update" 按钮(只显示版本)
- 不改 dropdown selection

## Carry-forward

- 旧缓存 entry 没 `reference`/`url`/`repository` 任一字段 → 仍显示空,直到用户 refresh。当前 v0.6.7.4 catalog cache 永久,这是 acceptable
- 如果未来要做 non-GitHub 版本,独立 SDD 重新讨论

## 验证

```bash
# 1. 单元测试
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~ExtractReference" -v minimal   # 8 PASS

# 2. STA load test
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~CatalogViewLoad" -v minimal   # 现有 +1

# 3. 全套
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build   # 862/1/1 + N

# 4. Build
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0

# 5. GUI smoke
# 启动 staging → 节点目录 → 列表卡片检查每行 `latest: vX.Y.Z` 显示
# 检查详情面板 ComboBox 仍正常
# 检查 entry 仓库地址栏都非空(若有 origin 应都有)
```

## 风险

| 风险 | 缓解 |
|---|---|
| 旧缓存 entry 没 `repository` 字段 → ExtractReference 也不命中 → 仍显示空 | 风险,用户 re-refresh 后才生效;Info log 提示 |
| 加 `latest:` 行后 list card 太高 | Card 高度自适应(Grid layout),不需固定高度 |
| 非 GitHub 源显示 `—` 用户困惑 | tooltip 解释 "非 GitHub 源,无法自动获取版本" |

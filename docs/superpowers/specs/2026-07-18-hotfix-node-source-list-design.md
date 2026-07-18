# v0.6.3 Hotfix — 节点下载/查询源可配置下拉列表

**里程碑:** v0.6.3 hotfix(M5.2 v0.6.2 之后)
**日期:** 2026-07-18
**状态:** 待用户审阅
**Base SHA:** v0.6.2 tag(HEAD `8044085`)

---

## 0. 摘要

在 WPF Settings 里加两个下拉列表框,每个列表管理 **一类节点源**(query / download),用户可在 Settings 自由增删条目并切换 active 项,默认各装一个 "comfyui manager"。

### 关键决策

| 决策 | 选择 |
|---|---|
| 列表格式 | 每条: `Name + Url`(`Models/NodeSource.cs`,跟 `ExtraPath` 同模式) |
| 同时启用数量 | 每列**同时只能选一个 active**(`ActiveXxxSourceName` 字段) |
| 默认值 | 每个列表只有 1 个内置项 "comfyui manager",active 默认选它 |
| 下载 URL 模板 | 支持 `{node}` 占位(例 `https://github.com/comfyanonymous/{node}`),不强制 |
| 查询 URL | 拉 JSON catalog(例 `custom-node-list.json`) |
| `compat_api_base_url` 字段 | **保留**,与本次无关(用户明确:它是判度安装节点兼容性,不是查/下节点源) |
| 接入面 | (a) `CatalogViewModel.Refresh` 解 M5.2-T7 TODO stub + (b) `NodeOperations.InstallAsync` 用 active download source 的 URL 替换 `{node}` |

### 不动的东西

- `compat_api_base_url`(用户已确认:兼容性检查,跟节点源无关)
- `CatalogRepository` / `SettingsRepository` / `GitRunner` / `BulkUpdateOrchestrator`(接口不变,内部消费新增字段)

---

## 1. 目标 & 非目标

### 1.1 目标(本次完成时)

- Settings 页新增两个 section:"查询节点的源" + "下载节点的源"
- 每个 section 顶部一个 ComboBox,展示当前 active 项的 Name,可切换
- ComboBox 下方一个 ItemsControl 列表,展示该 section 所有 source(每行 Name + URL + 删除按钮)
- 每个 section 底部一个"+ 添加"按钮 → inline 表单输入 Name + URL → 追加到列表
- 列表持久化到 `settings.json`,关闭 WPF 重开还在
- 默认 settings.json(全新安装或首次打开):两个列表都只有 "comfyui manager" 一条
- 切换 active 后:
  - 点 Catalog 页"刷新"按钮 → WPF HTTP GET active query URL → 解析 JSON → 写入 SQLite `catalog_cache`
  - Catalog 页"安装"按钮 → WPF `git clone <active-download-url-with-{node}-substituted>`

### 1.2 非目标(明确不做)

- ❌ 多 query source 并行合并拉取(用户明确:同时只 1 个 active)
- ❌ 多 download source 故障转移(同)
- ❌ JSON schema 校验(沿用现有无校验策略,PropertyNameCaseInsensitive)
- ❌ settings.json `version` 字段 / 迁移代码(沿用 `SettingsDefaults.Apply` 模式做空值补默认)
- ❌ 删除最后一个 source 的保护(允许空列表,但空时 Refresh/Install 应给清晰错误)
- ❌ 自带 catalog JSON 的 schema 校验(解析失败时降级到本地 cache + 错误提示)
- ❌ Source 重复检测(允许同名 / 同 URL,UI 不强制)

---

## 2. 用户故事

1. **首次安装 / 旧版本升级**
   - 用户打开 WPF,进入 Settings → 看到 "查询节点的源" + "下载节点的源" 两个 section
   - 每个 section 显示 ComboBox 选中 "comfyui manager"
   - 列表里只有这一条
   - 关 WPF,`%APPDATA%\ComfyUI-Manager\settings.json` 自动写入 4 个新字段

2. **添加自定义源**
   - 用户在"下载节点的源"section 点"+ 添加"
   - 弹出 inline 表单(名称 TextBox + URL TextBox)
   - 输入 Name="我的镜像",Url="https://gh-proxy.com/{node}",点确定
   - 新条目出现在列表底部,ComboBox 自动选中它

3. **删除源**
   - 用户在列表某行点"删除"
   - 该行消失
   - 如果删的是 active,ComboBox 自动回落列表第一条

4. **切换 active**
   - 用户在 ComboBox 里选另一个 source
   - 立即生效(无需重启)
   - 下次点"刷新"按钮就用新 query source 拉取

5. **空列表保护**
   - 用户删光所有 source 后点"刷新"
   - 弹错误"无查询源,请先在 Settings 添加"

6. **使用 query source**
   - 用户点 Catalog 页"刷新"按钮
   - WPF HTTP GET active query URL → 解析 JSON 数组 → 每条写入 `catalog_cache`(Package + RawMetadata + SourceUrl + CachedAt + ExpiresAt)
   - 列表自动刷新
   - 网络失败 → 弹错误,本地 cache 仍在(用户可手动搜)

7. **使用 download source**
   - 用户在 Catalog 页选一个 node,点"安装"
   - WPF 取 active download source URL,替换 `{node}` 为该 node 的 `node_id`
   - `git clone <url>` 落到 `envs/<env_id>/custom_nodes/<node_id>`

---

## 3. 数据模型

### 3.1 新 model: `Models/NodeSource.cs`

```csharp
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

public class NodeSource
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")]  public string Url  { get; set; } = "";
}
```

### 3.2 `Models/Settings.cs` 新增 4 字段

| 字段 | 类型 | 默认 |
|---|---|---|
| `QuerySources` | `List<NodeSource>` | `[ { Name="comfyui manager", Url="https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main/custom-node-list.json" } ]` |
| `DownloadSources` | `List<NodeSource>` | `[ { Name="comfyui manager", Url="https://github.com/comfyanonymous/{node}" } ]` |
| `ActiveQuerySourceName` | `string` | `"comfyui manager"` |
| `ActiveDownloadSourceName` | `string` | `"comfyui manager"` |

新代码放在 `Settings.cs` 第 31 行 `ExtraPaths` 之后。保留 `CompatApiBaseUrl`(原位不动)。

### 3.3 SQLite 不动

`catalog_cache` 表已经有 `source_url` 列(`CatalogRepository.Upsert` 用),本次只把 `source_url` 从空字符串填充为 active query source URL,不改 schema。

---

## 4. UI 设计

### 4.1 `Views/SettingsView.xaml` 新增两个 section

插入位置:`基础` section(行 14-28)结束后,`路径` section(行 31)之前。

```
<section: 查询节点的源>
  ┌──────────────────────────────────────────────────┐
  │ 查询节点的源                                       │   ← TextBlock FontSize=16
  │ 当前使用: [comfyui manager                  ▼]    │   ← ComboBox
  │                                                  │
  │   名称           URL                              │   ← ItemsControl header
  │   comfyui mgr    https://raw.githubusercon...    │   ← row
  │                                        [删除]    │
  │                                                  │
  │ [+ 添加]                                          │
  └──────────────────────────────────────────────────┘

<section: 下载节点的源>  同上结构,默认项 Url 用 {node} 模板
```

#### ComboBox

```xaml
<ComboBox ItemsSource="{Binding QuerySources}"
          DisplayMemberPath="Name"
          SelectedItem="{Binding ActiveQuerySource, Mode=TwoWay}"
          Width="320" HorizontalAlignment="Left" />
```

绑定 `ActiveQuerySource`(返回 `NodeSource?`),通过 `ActiveQuerySourceName` ↔ `QuerySources` 反查。ViewModel 提供同步逻辑。

#### ItemsControl 行

```xaml
<ItemsControl ItemsSource="{Binding QuerySources}">
  <ItemsControl.ItemTemplate>
    <DataTemplate>
      <Grid Margin="0,4">
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="160"/>
          <ColumnDefinition Width="*"/>
          <ColumnDefinition Width="Auto"/>
        </Grid.ColumnDefinitions>
        <TextBlock Text="{Binding Name}" Grid.Column="0"/>
        <TextBlock Text="{Binding Url}"  Grid.Column="1" TextTrimming="CharacterEllipsis"/>
        <Button Content="删除" Grid.Column="2" Margin="8,0,0,0"
                Command="{Binding DataContext.RemoveQuerySourceCommand,
                                  RelativeSource={RelativeSource AncestorType=UserControl}}"
                CommandParameter="{Binding}" />
      </Grid>
    </DataTemplate>
  </ItemsControl.ItemTemplate>
</ItemsControl>
```

#### "+ 添加" 按钮 + inline 表单

点击 "+ 添加" → `IsAddQuerySourceOpen` 切 true → 显示一个 mini form(2 个 TextBox + 确定/取消)。确定按钮触发 `AddQuerySourceCommand`,校验非空 → 追加到 `QuerySources` → 自动设为 active → 清空表单 + 关闭。

### 4.2 Catalog 页无改动

刷新按钮还是那个 RefreshCommand,只是它的内部逻辑从 stub 改成真的 HTTP GET。

---

## 5. 接线点(改动)

### 5.1 `Infrastructure/SettingsDefaults.cs`

新增 4 个默认常量 + Apply 中补默认:

```csharp
public const string DefaultQuerySourceName = "comfyui manager";
public const string DefaultQuerySourceUrl =
    "https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main/custom-node-list.json";
public const string DefaultDownloadSourceName = "comfyui manager";
public const string DefaultDownloadSourceUrl = "https://github.com/comfyanonymous/{node}";

// 在 Apply() 末尾:
if (s.QuerySources == null || s.QuerySources.Count == 0)
    s.QuerySources = new() {
        new() { Name = DefaultQuerySourceName, Url = DefaultQuerySourceUrl }
    };
if (s.DownloadSources == null || s.DownloadSources.Count == 0)
    s.DownloadSources = new() {
        new() { Name = DefaultDownloadSourceName, Url = DefaultDownloadSourceUrl }
    };
if (string.IsNullOrWhiteSpace(s.ActiveQuerySourceName))
    s.ActiveQuerySourceName = s.QuerySources[0].Name;
if (string.IsNullOrWhiteSpace(s.ActiveDownloadSourceName))
    s.ActiveDownloadSourceName = s.DownloadSources[0].Name;
```

### 5.2 `ViewModels/SettingsViewModel.cs`

新增属性 + 命令:

```csharp
public ObservableCollection<NodeSource> QuerySources { get; }
public ObservableCollection<NodeSource> DownloadSources { get; }

public NodeSource? ActiveQuerySource   // 反查自 ActiveQuerySourceName ↔ QuerySources
{
    get => QuerySources.FirstOrDefault(s => s.Name == _settings.ActiveQuerySourceName);
    set { _settings.ActiveQuerySourceName = value?.Name ?? ""; _repo.Save(_settings); RaisePropertyChanged(); }
}

public NodeSource? ActiveDownloadSource  // 同

public RelayCommand<NodeSource> RemoveQuerySourceCommand { get; }
public RelayCommand<NodeSource> RemoveDownloadSourceCommand { get; }
public RelayCommand AddQuerySourceCommand { get; }       // 打开 inline form
public RelayCommand AddDownloadSourceCommand { get; }
public RelayCommand ConfirmAddQuerySourceCommand { get; } // 提交 + 自动 active
public RelayCommand ConfirmAddDownloadSourceCommand { get; }
public RelayCommand CancelAddQuerySourceCommand { get; }
public RelayCommand CancelAddDownloadSourceCommand { get; }

public bool IsAddQuerySourceOpen { get; set; }   // 同 DownloadSource
public string NewQuerySourceName { get; set; } = "";
public string NewQuerySourceUrl  { get; set; } = "";
// DownloadSource 同

// ctor 中:
QuerySources = new ObservableCollection<NodeSource>(_settings.QuerySources);
DownloadSources = new ObservableCollection<NodeSource>(_settings.DownloadSources);
QuerySources.CollectionChanged += (_, _) => { _settings.QuerySources = QuerySources.ToList(); _repo.Save(_settings); RaisePropertyChanged(nameof(ActiveQuerySource)); };
DownloadSources.CollectionChanged += (_, _) => { _settings.DownloadSources = DownloadSources.ToList(); _repo.Save(_settings); RaisePropertyChanged(nameof(ActiveDownloadSource)); };
```

### 5.3 `Services/NodeUrlResolver.cs`(新文件)

纯函数,testable。`NodeOperations.InstallAsync` 调用:

```csharp
public static class NodeUrlResolver
{
    public static string Resolve(string templateUrl, string nodeId)
    {
        if (string.IsNullOrWhiteSpace(templateUrl)) return templateUrl;
        return templateUrl.Replace("{node}", nodeId);
    }
}
```

不包含 `{node}` 时原样返回(用户可以填 `https://github.com/foo/SomeSpecificRepo`,不走模板)。

### 5.4 `Services/NodeOperations.cs`

`InstallAsync` 第 77-80 行:

```csharp
// 之前: var repoUrl = ExtractRepoUrl(entry);
// 现在:
var templateUrl = ExtractRepoUrl(entry);  // 保留兼容,如果 catalog entry 自带 raw_metadata["repository"] 仍优先
if (string.IsNullOrWhiteSpace(templateUrl))
{
    var active = _settings.ActiveDownloadSourceName;
    var src = _settings.DownloadSources.FirstOrDefault(s => s.Name == active);
    if (src == null) throw new InvalidOperationException("未配置下载源,请在 Settings 添加");
    templateUrl = src.Url;
}
var repoUrl = NodeUrlResolver.Resolve(templateUrl, nodeId);
```

`InstallAsync` ctor 多接一个 `Settings` 参数(从 AppContext 注入)。

### 5.5 `ViewModels/CatalogViewModel.cs` — 解 M5.2-T7 TODO stub

Refresh() 当前:
```csharp
private void Refresh() {
    MessageBox.Show("TODO(M5.2-T7): refresh catalog", ...);
    Search();  // 仅本地搜
}
```

改成:
```csharp
private async Task RefreshAsync() {
    var active = _settings.ActiveQuerySourceName;
    var src = _settings.QuerySources.FirstOrDefault(s => s.Name == active);
    if (src == null) {
        ErrorMessage = "未配置查询源,请先在 Settings 添加";
        return;
    }
    IsBusy = true;
    try {
        var entries = await _catalogFetcher.FetchAsync(src.Url, ct);
        foreach (var e in entries) {
            e.SourceUrl = src.Url;  // 把 source 标记写进 cache
            _catalogRepo.Upsert(e);
        }
        Search();
    } catch (Exception ex) {
        ErrorMessage = $"拉取失败: {ex.Message}(本地缓存仍可用)";
        Search();
    } finally {
        IsBusy = false;
    }
}
```

### 5.6 `Services/CatalogFetcher.cs`(新文件)

HTTP GET URL → 解析 JSON → 转为 `List<CatalogEntry>`:

```csharp
public class CatalogFetcher
{
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);

    public CatalogFetcher(HttpClient? http = null) { _http = http ?? new HttpClient { Timeout = _timeout }; }

    public async Task<List<CatalogEntry>> FetchAsync(string url, CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(url, ct);
        var raw = JsonSerializer.Deserialize<List<JsonElement>>(json) ?? new();
        return raw.Select(e => new CatalogEntry {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = url,
            Package = e.TryGetProperty("id", out var id) ? id.GetString() ?? ""
                    : e.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            RawMetadata = JsonSerializer.Deserialize<Dictionary<string, object>>(e.GetRawText()) ?? new(),
            CachedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(_settings.CatalogCacheTtlMinutes)
        }).ToList();
    }
}
```

接受注入的 `HttpClient`(测试用)。`CatalogFetcher` 不接 `Settings`(TTL 常量化或参数化)。决定:把 TTL 通过 ctor 注入,避免循环依赖。

```csharp
public CatalogFetcher(HttpClient http, int cacheTtlMinutes = 60) { ... }
```

`App.xaml.cs` 实例化时传入 `_settings.CatalogCacheTtlMinutes`。

### 5.7 `App.xaml.cs`

新增 wiring:
- 创建 `CatalogFetcher` 实例(传入 `new HttpClient { Timeout = TimeSpan.FromSeconds(15) }` 和 `_settings.CatalogCacheTtlMinutes`),注入到 `CatalogViewModel`
- 把 `Settings` 注入到 `NodeOperations`(构造参数加)

### 5.8 不动

- `CatalogRepository`(只新增调用,不改接口)
- `SettingsRepository`(JSON 序列化自动处理新增字段)
- `GitRunner`、`BulkUpdateOrchestrator`

---

## 6. 错误处理

| 场景 | 行为 |
|---|---|
| query URL 为空 / 无 active source | 弹"未配置查询源,请先在 Settings 添加",不调 HTTP |
| HTTP GET 失败(timeout / 404 / DNS) | 弹错误 + 提示本地 cache 仍可用,Search() 继续 |
| JSON 解析失败 | 弹错误 + 同上 |
| JSON 行缺 `id`/`name` 字段 | 跳过该行,继续解析其余 |
| download URL 为空 / 无 active source | Install 时抛 `InvalidOperationException`,ViewModel 转成 ErrorMessage |
| `{node}` 在 URL 中但 nodeId 含 `/` 等特殊字符 | 不做 URL encoding(v0,YAGNI;用户需自填安全 nodeId) |
| 删除 active 的 source | auto-fallback 到 `QuerySources[0].Name`,UI ComboBox 立即刷新 |
| 删光所有 source | 列表为空;Refresh / Install 各自给清晰错误 |

---

## 7. 测试

### 7.1 新增测试

| 文件 | 新增 |
|---|---|
| `Infrastructure/SettingsDefaultsTests.cs` | `Apply_QuerySources_EmptyGetsDefault` / `Apply_DownloadSources_EmptyGetsDefault` / `Apply_ActiveQuerySourceName_EmptyFallbacksToFirst` / `Apply_ActiveDownloadSourceName_EmptyFallbacksToFirst` |
| `Services/NodeUrlResolverTests.cs`(新) | `Resolve_NodeTemplate_Substitutes` / `Resolve_NoTemplate_ReturnsOriginal` / `Resolve_EmptyUrl_ReturnsEmpty` / `Resolve_EmptyNodeId_ReturnsOriginal` |
| `ViewModels/SettingsViewModelTests.cs` | `AddQuerySource_AppendsToListAndSetsActive` / `RemoveQuerySource_DeletesAndFallsBackActive` / `RemoveQuerySource_LastOne_LeavesListEmpty` / `SwitchActive_PersistsImmediately` |
| `Services/CatalogFetcherTests.cs`(新) | `FetchAsync_ParsesValidJson` / `FetchAsync_NetworkFailure_Throws` / `FetchAsync_EmptyArray_ReturnsEmptyList` / `FetchAsync_MissingIdField_SkipsRow` |

### 7.2 现有测试更新

- `SettingsViewModelTests`:已有 8 个 `SettingsDefaultsTests` 通过;本次新增 ~4 个,不破坏旧的
- `CatalogViewModelTests`:已有 `Refresh_TodoStub` / `Install_UsesFirstEnv` 两个;本次 `Install_UsesActiveDownloadSource_TemplateSubstituted`(新增)+ `Refresh_FetchesFromActiveQuerySource`(新增,需 mock HttpClient)
- `NodeOperationsTests`:已有 5 个 git integration test;本次新增 `Install_UsesActiveDownloadSourceUrl_WhenCatalogMissingRepo`,需要 mock `Settings`

### 7.3 不重测

- `BulkUpdateOrchestratorTests` / `BulkUpdateDialogViewModelTests` / `GitProxyConfigTests` — 不动

---

## 8. 风险 & 权衡

| 风险 | 缓解 |
|---|---|
| `CatalogFetcher` 调外网 JSON,需在线 | timeout 15s + 失败降级;用户已习惯 ComfyUI-Manager 联网 |
| 第三方 catalog JSON schema 多变 | 解析用宽松策略:只读 `id` / `name`,其余进 `RawMetadata` dict 后续再用 |
| 下载源 `{node}` 模板未做 URL encoding | v0 不做,文档提示用户填合法 nodeId;后续 M6+ 再加 |
| 默认 catalog URL `ltdrdata/ComfyUI-Manager/custom-node-list.json` 国内访问慢 | 留给用户在 Settings 加 gh-proxy.com 镜像 |
| `compat_api_base_url` 与本次无关,但 UI 标签 "外部冲突 API URL" 容易误解 | 暂不动,文档说明;后续 M6 改标签 |
| `Settings.cs` 加 4 字段后,旧 settings.json(没这 4 字段)反序列化 → 走 SettingsDefaults 兜底 | 已覆盖(测试覆盖) |
| SettingsViewModel 加 ObservableCollection.CollectionChanged 写盘,高频写入风险 | 用户增删都是手动按钮触发,不会高频;非问题 |
| `NodeOperations.InstallAsync` ctor 多接 `Settings`,所有测试需要 mock | 测试已经用 fake,加参数即可 |
| `CatalogFetcher` 需要 HttpClient,tests 需 fake HttpClient | 用 `MockHttpMessageHandler`(Moq 内置) |

---

## 9. 实施 task 拆分(estimate)

为后续 writing-plans 服务;非 spec 强制:

1. **T1 model + 接线点**: `NodeSource.cs` + `Settings.cs` + `SettingsDefaults.cs` + `SettingsRepository`(不动)
2. **T2 URL 解析**:`NodeUrlResolver.cs` + tests
3. **T3 SettingsViewModel**:`QuerySources` / `DownloadSources` / active 同步 / Add / Remove 命令 + tests
4. **T4 SettingsView.xaml**:两个 section + ComboBox + ItemsControl + inline add form
5. **T5 CatalogFetcher**:`CatalogFetcher.cs` + tests
6. **T6 CatalogViewModel.Refresh 解 stub**:HTTP GET → 解析 → 写 cache + tests
7. **T7 NodeOperations 接 active download source**:改 InstallAsync + tests
8. **T8 App.xaml.cs wiring**:CatalogFetcher + Settings 注入
9. **T9 全量测试 + 整分支 review + bump v0.6.3 + release**

---

## 10. 升级注意(release notes)

- v0.6.2 → v0.6.3 直接覆盖 zip
- 旧 settings.json 没这 4 字段 → 自动套默认值(走 `SettingsDefaults.Apply`),无需手动迁移
- 旧 v0.6.2 装的 "comfyui manager" 仍是默认源;用户可在 Settings 加自定义

---

## 11. 用户决策点

1. 默认 catalog URL 是 `ltdrdata/ComfyUI-Manager/custom-node-list.json`(历史上 ComfyUI-Manager 用的)。如果用户想用别的源,本次 spec 里改。
2. 默认 download URL `https://github.com/comfyanonymous/{node}`(ComfyUI 官方仓库)。同。
3. timeout 15s、TTL 60min 都是合理默认,如要调本次 spec 里改。

---

**End of spec.** 请用户审阅。
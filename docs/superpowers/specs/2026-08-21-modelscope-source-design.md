# ModelScope 国内模型源接入设计

## Context

用户 2026-08-21 原话:"另外在模型市场添加多个国内的模型下载源"。经 brainstorming 沟通确定:
- **接入源**:魔搭 ModelScope(阿里达摩院,国内最大 ML 模型社区,公开 REST API)+ 复用现有 HuggingFaceModelSource 的国内镜像能力(默认 `https://hf-mirror.com`,Settings 字段已存在)
- **功能范围**:基础搜索 + 下载 — sort/period/baseModel/NSFW 等 CivitAI 专属过滤在 ModelScope 激活时**整行折叠**(用户主动选择「基础」,非完整对齐)
- **HF 镜像**:Settings 已有 `ModelSourceHuggingFaceUseMirror`/`ModelSourceHuggingFaceMirrorUrl`/`ModelSourceHuggingFaceProxyMode`,无需新字段

目标:用户在 Settings 勾选「ModelScope 启用」+ (可选)API token,在 model marketplace 工具栏 source radio 多一项「ModelScope」,能搜 SDXL/Flux/LoRA 模型并下载到本地。

## ModelScope 公开 API

基于 ModelScope 官方 OpenAPI v1(https://www.modelscope.cn/docs/%E9%A1%B9%E7%9B%AE%E5%B1%95%E7%A4%BA):

| 用途 | Method + URL | 关键参数 |
|---|---|---|
| 搜索模型 | `GET /api/v1/models?PageNumber=1&PageSize=N&Filter=keyword&Search=keyword` | `Filter`(tag 模糊匹配) / `Search`(名称) / `PageNumber` 1-based |
| 模型详情 | `GET /api/v1/models/{model_id}`(返回完整 schema,含 `Revision` 数组 → `Files[]`) | 路径参数 `model_id`(整数或 path-style `org/name`) |
| 下载文件 | 详情里的 `Files[].DownloadUrl`(直接 HTTP,无签名) | — |

**Response 关键 schema(搜索返回):**
```json
{
  "Code": 200,
  "Data": {
    "Model": {
      "Models": [
        {
          "Id": 12345,
          "Name": "AI-ModelScope/some-model",
          "ChineseName": "中文名(可空)",
          "Tags": ["stable-diffusion", "lora"],
          "CreatedTime": 1700000000000,
          "LastUpdatedTime": 1700000000000,
          "Downloads": 100,
          "Stars": 5,
          "Likes": 10,
          "Description": "...",
          "Task": "text-to-image",
          "Owner": { "Name": "AI-ModelScope", "DisplayName": "..." },
          "DefaultRevision": "master"
        }
      ],
      "PageNumber": 1,
      "PageSize": 20,
      "TotalCount": 1234
    }
  }
}
```

**关键差异 vs CivitAI/HF:**
- `Id` 是整数(不是 string slug)
- 无 `ModelVersion` 数组 — 直接走 `DefaultRevision` 拉详情取文件
- 无显式 `kind` 字段 — 从 `Tags[]` 推断(见下表)
- 无 `nsfw`/`nsfwLevel` 字段 — 默认全部当作 SFW(`ModelNsfwKind.SFW`),`IncludeNsfw=false` 时源层不做事(留空会让用户看到全部 — 后续可加 ModelScope `Sensitive` 字段,本 spec 不做)
- 无 `baseModel`/`sort`/`period` — VM UI chip 折叠

**Kind 推断表**(从 `Tags` 数组):
| Tag 包含 | Kind |
|---|---|
| `lora` | `ModelKind.LORA` |
| `checkpoint` | `ModelKind.Checkpoint` |
| `vae` | `ModelKind.VAE` |
| `controlnet` | `ModelKind.ControlNet` |
| `upscaler`/`esrgan`/`real-esrgan` | `ModelKind.Upscaler` |
| `text-encoder`/`clip` | `ModelKind.TextEncoder` |
| `embeddings`/`textual-inversion` | `ModelKind.Embedding` |
| `unet` | `ModelKind.UNET` |
| `hypernetwork` | `ModelKind.HyperNetwork` |
| 其他 | `ModelKind.Other`(落到 view-time filter 「其他」 chip)|

## Architecture

### 新文件 / 改动

| 文件 | 性质 | 说明 |
|---|---|---|
| `Services/ModelSources/ModelScopeModelSource.cs` | 新 | `IModelSource` 实现 — 搜索用 `/api/v1/models`,详情走 2-round fetch(列表不带 file size),失败隔离 |
| `Services/ModelSources/ModelScopeDtos.cs` | 新 | DTO 序列化(顶层 `Data.Model` envelope,加 `[JsonPropertyName]` 对 snake_case API) |
| `Services/ModelSources/ModelSourceFactory.cs` | 改 | +`CreateModelScope` 静态方法 + `CreateAll` 加 ModelScope |
| `Models/ModelSourceKind.cs` | 改 | +`ModelScope = 2` |
| `Models/Settings.cs` | 改 | +5 字段:`ModelSourceModelScopeEnabled`/`ModelSourceModelScopeApiToken`/`ModelSourceModelScopeUseMirror`/`ModelSourceModelScopeMirrorUrl`/`ModelSourceModelScopeProxyMode` + `CopyInto` 同步 |
| `ViewModels/SettingsViewModel.cs` | 改 | +5 属性 proxy + `ResetModelScopeMirrorUrl` 命令 |
| `Views/SettingsView.xaml` | 改 | 模型市场段加第 3 个 sub-section(镜像 HF/CivitAI 同款结构)|
| `Views/ModelMarketplaceView.xaml` | 改 | +第 3 个 RadioButton `ModelScope` + sort/period/baseModel 行的 `Visibility` 加 ModelScope 折叠判断 |
| `Tests/Services/ModelSources/ModelScopeModelSourceTests.cs` | 新 | 单元测试(fixture 静态 JSON,MockHttpMessageHandler)|
| `Tests/Services/ModelSourceFactoryTests.cs` | 改 | +CreateModelScope disabled/UseMirror/proxy 三分支测试 |

### 接口层

`IModelSource` 不变。`SearchPageAsync` 已有的 `sort`/`period`/`baseModel`/`includeNsfw` 参数:
- **CivitAI**:透传到 URL(已有逻辑)
- **HuggingFace**:`sort`/`period`/`baseModel` 接收但 no-op(已有)
- **ModelScope**(新):同 HF — `sort`/`period`/`baseModel` 接收但 no-op;`includeNsfw` 接收但 no-op(API 无此字段,本 spec 内不做 NSFW 推断)
- `cursor` = PageNumber-1 字符串化(从 `TotalCount`/`PageSize` 算 `MaxPage`,到顶时返回 null)

### Download 流

`ModelScopeModelSource.SearchPageAsync` 返回的 `ModelEntry.Versions[0].PrimaryDownloadUrl` 是**列表查询时拿不到**的 — 列表 response 里 `Downloads` 计数 ≠ 文件大小,`Files[]` 不在列表 schema 里。

**两种方案:**
1. **2-round fetch**:列表返回时给每个 model 一个空 Versions + `MetadataFetchHint = model_id`,ViewModel 在卡片渲染后并行调详情拉 size(竞态,可能慢)
2. **懒加载 size**:列表返回 `Versions[0]` 字段填 `SizeKb=null`/`PrimaryDownloadUrl=""`,ViewModel 卡片进入 viewport 时按需 fetch

**本 spec 选方案 1**(用户选了「基础搜索+下载」,复杂度低优先):
- `SearchPageAsync` 内部串行 await N 个 `GetModelDetailAsync(model_id)`(N=pageSize,典型 ≤ 20,5-10 秒可接受)
- 单 model detail 失败时该 entry 仍返回,`Versions[0]` 标记 `PrimaryDownloadUrl = null` + `SizeKb = 0` — ViewModel 在卡片显示「⚠ 详情失败」小字
- 后期优化:并行 detail fetch / 懒 size,本 spec 不做

### UI 集成

`ModelMarketplaceView.xaml` 行 120-130(source radio `DockPanel.Dock="Right"`):
```xml
<RadioButton Content="ModelScope" GroupName="ActiveSource"
             Tag="{x:Static models:ModelSourceKind.ModelScope}"
             IsChecked="{Binding ActiveSource, Converter={StaticResource EnumEqualsConverter}, ConverterParameter=ModelScope}"
             Click="OnSourceRadioClicked"
             Margin="12,0,0,0" VerticalAlignment="Center" />
```

sort/period/baseModel 行(行 158/233)的 `Visibility`:
```xml
Visibility="{Binding ActiveSource, Converter={StaticResource EnumEqualsVisibility},
                     ConverterParameter=CivitAi}"
```
保持只对 CivitAI 显示 — ModelScope 跟 HF 一样自动折叠。

### Settings 段(镜像 HF/CivitAI)

`SettingsView.xaml` 模型市场段(`<TextBlock x:Name="SectionModelSources" .../>` 之后)加:
- 启用 CheckBox(`ModelSourceModelScopeEnabled`)
- 折叠内容块:`API Token` BindablePasswordBox + 「测试连接」Button + 「使用镜像」CheckBox + `镜像地址` TextBox + 「重置」Button + insecure 警告 + 3 RadioGroup `ModelScopeProxyMode`

### Factory

`ModelSourceFactory.CreateModelScope(settings, httpBuilder, logger)`:
```csharp
if (!settings.ModelSourceModelScopeEnabled) return null;
var baseUrl = ResolveBaseUrl(settings.ModelSourceModelScopeUseMirror,
                             settings.ModelSourceModelScopeMirrorUrl,
                             ModelScopeOfficial);  // "https://www.modelscope.cn"
var proxy = ModelSourceProxyDecision.Resolve(
    settings.HttpProxyMode,
    settings.ModelSourceModelScopeProxyMode,
    settings);
var http = httpBuilder(proxy);
return new ModelScopeModelSource(http, baseUrl, settings.ModelSourceModelScopeApiToken, logger, proxy);
```

`CreateAll` 在 `CivitAi`/`HuggingFace` 之间塞 `CreateModelScope`。

## Files

- **新建**:ModelScopeModelSource.cs、ModelScopeDtos.cs、ModelScopeModelSourceTests.cs
- **改动**:`ModelSourceKind.cs`(enum +1 值)、`ModelSourceFactory.cs`(+CreateModelScope + CreateAll 3 项)、`Settings.cs`(+5 字段 + CopyInto)、`SettingsViewModel.cs`(+5 属性)、`SettingsView.xaml`(+1 sub-section)、`ModelMarketplaceView.xaml`(+1 RadioButton)、`ModelSourceFactoryTests.cs`(+3 测试)

## Test Strategy

**ModelScopeModelSourceTests**(7-8 测试,JSON fixture 静态):
1. `SearchAsync_EmptyQuery_ReturnsModels`
2. `SearchAsync_WithQuery_AddsSearchParam`
3. `SearchAsync_Pagination_AdvancesPageNumber`
4. `SearchAsync_LastPage_ReturnsNullCursor`
5. `SearchPageAsync_InvokesDetailFetchForEachEntry`(2-round 验证)
6. `SearchPageAsync_DetailFetchFails_EntryStillReturned`(失败隔离)
7. `SearchPageAsync_TagsMapping_CoversAllKinds`(8 类 kind 推断)
8. `SearchPageAsync_NsfwAlwaysSfw_NoApiParam`(since includeNsfw no-op)

**ModelSourceFactoryTests**(3 新增):
1. `CreateModelScope_Disabled_ReturnsNull`
2. `CreateModelScope_UseMirror_ResolvesMirrorUrl`
3. `CreateModelScope_ProxyMode_ResolvesCorrectly`

**回归**:1637/1643 PASS baseline + 新 ~11 测试,5 pre-existing flaky 不动。

## Risks & Mitigations

| 风险 | 缓解 |
|---|---|
| ModelScope API schema 跟文档不一致 | DTO 用 `[JsonPropertyName]` 严绑 + 单元测试 fixture 镜像真实 response(handcrafted from API docs)|
| 2-round fetch 慢(20 entries × detail 调用)| pageSize 限 ≤ 20;首次拉接受 5-10 秒;后期可换并行或懒加载 |
| 详情 endpoint 限流 | 单 model 失败仅丢 1 entry,不影响其他;后续可加重试 |
| 中文/英文混杂 UI | 暂时全英文(跟 CivitAI/HF 一致),用户原话没提本地化 |
| 同 model 多 revision 多个文件 | 本 spec 只取 `DefaultRevision` 第一个文件;复杂多文件版留 YAGNI |

## Out of Scope

- 多文件版本选择 UI(只取 DefaultRevision 第一个)
- NSFW 推断(API 无 `Sensitive` 字段)
- ModelScope 镜像 URL 列表(用户自填)
- sort/period/baseModel API 模拟(post-filter 不做,跟 HF 一致只接 search keyword)
- 详情 fetch 失败重试
- 并行/懒详情 fetch 优化

## Open Question

**需用户在 plan 审时确认:** 是否允许 `ModelScope` 在 `Settings → 模型市场` 默认隐藏 / 默认 enable = false(避免新装用户误启用没填 token 看到空结果)?
推荐默认 false(同 HuggingFace 模式),需要时手动勾选。
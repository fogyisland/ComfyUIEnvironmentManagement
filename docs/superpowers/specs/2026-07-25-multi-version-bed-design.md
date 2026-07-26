# 多版本 BED 设计

## 1. 目标与范围

在基础环境部署（BED）页面增加 PyTorch 版本选择能力，使用户能够在多个 stable 版本之间选择，并为所选版本显示可安装的 CUDA 与 CPU profile。nightly 作为顶部特殊入口保留。

本 spec 只覆盖多版本 BED，不包含系统概览页面。

## 2. 已确认的产品决策

- 顶部增加 Torch 版本 ComboBox，选择后刷新下方 profile ListBox。
- stable 版本范围为 PyPI 上可用的全部 stable 版本。
- dropdown 按 release date 倒序排列，nightly 始终位于最顶端；latest stable 是第一项 stable 且默认选中。
- CUDA 变体从 PyPI torch wheel 文件名动态解析，每个版本拥有独立 CUDA 列表。
- CPU profile 随所选版本生成，不作为独立 dropdown 项。
- nightly 是虚拟 dropdown 项，选中后只显示 nightly cu126 profile。
- PyPI JSON 是 stable 目录数据源；现有 pytorch.org HTML fetcher 保留，用于 nightly cu126 验证。
- catalog cache 永久保存。首次获取后不会自动发现新版本；用户删除 cache 文件后才重新请求。
- 目录 cache 文件为 `%APPDATA%/ComfyUI-Manager/pytorch_catalog_cache.json`，与现有 `pytorch_versions_cache.json` 并存。
- 请求失败且没有 cache 时使用 v0.6.5.2 fallback，UI 不显示空列表。
- 用户 `<exe-dir>/base_env_profiles.json` override 行为保持兼容。

## 3. 架构

保留 v0.6.5.2 的 `BaseEnvProfileLoader`、`BaseEnvInstaller`、`BaseEnvViewModel` 和 progress UI 架构，只新增版本目录层与当前版本选择状态。

```text
PyPI JSON
   │
   ▼
PyTorchVersionCatalog
   │
   ├── PyTorchVersionCatalogCache（永久）
   │
   ▼
PyTorchVersionDirectory
   │
   ├── nightly 虚拟项
   └── stable 版本列表
           │
           ▼
BaseEnvViewModel.SelectedVersion
           │
           ▼
BaseEnvProfileLoader.LoadProfilesForVersionAsync
           │
           ▼
Profiles + CPU/CUDA/nightly profile
```

职责边界：

- `PyTorchVersionCatalog`：请求并解析 PyPI JSON。
- `PyTorchVersionCatalogCache`：读写永久版本目录 cache。
- `PyTorchVersionDirectory`：组合 cache、网络结果、fallback，并提供排序后的版本入口。
- `BaseEnvProfileLoader`：将指定版本和 CUDA 变体转换为现有 `BaseEnvProfile`。
- `BaseEnvViewModel`：维护版本列表、当前选择及 profile 刷新。
- `BaseEnvInstaller`、progress VM/dialog：保持不变，不感知版本目录来源。

## 4. 新增组件

### 4.1 PyTorchVersionCatalog

文件：`src-wpf/ComfyUI.Manager/Data/PyTorchVersionCatalog.cs`

```csharp
public sealed class PyTorchVersionCatalog
{
    public const string PageUrl = "https://pypi.org/pypi/torch/json";

    public PyTorchVersionCatalog(HttpClient http);

    public async Task<IReadOnlyList<PyTorchVersion>> FetchAsync(
        CancellationToken ct = default);
}
```

数据模型：

```csharp
public sealed class PyTorchVersion
{
    public string Version { get; init; } = "";
    public DateTimeOffset ReleaseDate { get; init; }
    public IReadOnlyList<string> CudaVariants { get; init; } = Array.Empty<string>();
    public bool HasCpu { get; init; }
}
```

解析规则：

1. 读取 PyPI JSON 的 `releases` 字典。
2. 过滤 pre-release、development、post-release 等非 stable 版本。
3. 对每个版本的 wheel filename 解析 local tag，例如 `+cu118`、`+cu121`、`+cu124`、`+cu126`、`+cpu`。
4. CUDA tag 去重并按稳定排序；CPU wheel 设置 `HasCpu = true`。
5. release date 使用该版本文件列表中的发布时间，取最新有效文件时间。
6. JSON 损坏、HTTP 非成功或网络异常时返回 null，由 directory 层处理 fallback。

只有 PyPI 实际提供的 wheel 变体进入该版本；没有可识别 CUDA wheel 的版本仍可生成 CPU profile。

### 4.2 PyTorchVersionCatalogCache

文件：`src-wpf/ComfyUI.Manager/Data/PyTorchVersionCatalogCache.cs`

```csharp
public sealed class PyTorchVersionCatalogCache
{
    public const string FileName = "pytorch_catalog_cache.json";

    public PyTorchVersionCatalogCache(string appDataDir);
    public string FilePath { get; }

    public Task<IReadOnlyList<PyTorchVersion>?> TryReadAsync(
        CancellationToken ct = default);

    public Task WriteAsync(
        IReadOnlyList<PyTorchVersion> versions,
        CancellationToken ct = default);
}
```

cache 无 TTL。缺失或损坏时返回 null；写入时创建目录。写入失败不破坏 UI 的 fallback 流程。

### 4.3 PyTorchVersionDirectory

文件：`src-wpf/ComfyUI.Manager/Data/PyTorchVersionDirectory.cs`

```csharp
public sealed class PyTorchVersionDirectory
{
    public const string NightlyVersion = "nightly";

    public PyTorchVersionDirectory(
        PyTorchVersionCatalog catalog,
        PyTorchVersionCatalogCache cache);

    public Task<IReadOnlyList<PyTorchVersionEntry>> GetAllAsync(
        CancellationToken ct = default);
}

public sealed class PyTorchVersionEntry
{
    public string Version { get; init; } = "";
    public bool IsNightly { get; init; }
    public string DisplayName { get; init; } = "";
    public PyTorchVersion? StableMetadata { get; init; }
}
```

获取顺序：cache → PyPI → fallback。成功获取的 PyPI 数据写入永久 cache；fallback 不写入 cache。返回结果将 nightly 插入第一项，stable 按 release date 倒序。

fallback 至少提供 latest stable `2.13.0` 及 nightly 入口，并保留 v0.6.5.2 的可用 CUDA/CPU 组合。

## 5. 现有组件改动

### 5.1 BaseEnvProfileLoader

文件：`src-wpf/ComfyUI.Manager/Data/BaseEnvProfileLoader.cs`

新增：

```csharp
public Task<IReadOnlyList<BaseEnvProfile>> LoadProfilesForVersionAsync(
    string version,
    CancellationToken ct = default);
```

stable 版本生成该版本的全部可用 CUDA profile 及 CPU profile；nightly 生成单个 nightly cu126 profile。生成结果继续使用现有 `BaseEnvProfile` 字段、package 列表和 pip 参数规则。`BaseEnvProfile`、`BaseEnvInstaller`、progress VM/dialog 不改。

### 5.2 BaseEnvViewModel

文件：`src-wpf/ComfyUI.Manager/ViewModels/BaseEnvViewModel.cs`

新增：

```csharp
public ObservableCollection<PyTorchVersionEntry> Versions { get; }

public PyTorchVersionEntry? SelectedVersion { get; set; }
```

`LoadAsync` 先加载版本目录，插入 nightly 并默认选择 latest stable，再加载对应 profiles 和环境列表。版本切换只刷新 profiles，不重读环境列表；清空已选 profiles，保留环境选择。加载失败由 directory/loader 返回 fallback，VM 不向 UI 抛网络或 JSON 异常。

### 5.3 BaseEnvView

文件：`src-wpf/ComfyUI.Manager/Views/BaseEnvView.xaml`

在 profile ListBox 上方增加：

```xml
<ComboBox DockPanel.Dock="Top"
          Margin="0,0,0,8"
          ItemsSource="{Binding Versions}"
          SelectedItem="{Binding SelectedVersion}"
          DisplayMemberPath="DisplayName" />
```

保留现有 profile 模板、环境 ListBox、selection 事件和 Start 按钮。

### 5.4 App.xaml.cs

创建并装配 `PyTorchVersionCatalog`、`PyTorchVersionCatalogCache`、`PyTorchVersionDirectory`，将 directory 传入 `BaseEnvViewModel`。继续复用现有共享 `HttpClient`。两个 cache 文件路径不得合并或覆盖。

## 6. 数据流与错误处理

### 首次启动

1. ViewModel 调用 directory。
2. directory 读取永久 catalog cache。
3. cache 未命中时请求 PyPI JSON。
4. 成功解析后写 cache。
5. directory 插入 nightly 并排序 stable。
6. ViewModel 默认选择 latest stable。
7. loader 根据选择生成 profiles。

### 版本切换

`SelectedVersion` 改变后调用 `LoadProfilesForVersionAsync`，仅清空并刷新 profile 集合，不重新请求版本目录、不重新读取环境列表。

### 失败场景

- cache 缺失或损坏：尝试 PyPI。
- PyPI 请求失败且无有效 cache：使用 v0.6.5.2 fallback。
- 版本无 CUDA wheel：仍生成 CPU profile。
- nightly 验证失败：返回 fallback nightly cu126 profile。
- 所有网络和解析路径失败：UI 仍有 fallback profiles，不显示空列表。

## 7. 测试策略

所有网络测试使用假的 `HttpMessageHandler`，不访问真实 PyPI 或 pytorch.org。

### Catalog

测试 stable 版本解析、pre-release 过滤、wheel CUDA/CPU tag 解析、CUDA 去重、release date 倒序、空/损坏 JSON、HTTP 错误和网络异常。

### Cache

测试缺失、有效 round-trip、永久不失效、损坏 JSON、自动建目录、写入失败、取消令牌传递。

### Directory

测试 cache 命中不请求网络、cache miss 请求并写入、失败 fallback、nightly 首项、stable 倒序和 latest stable 默认项。

### Loader

测试 stable CUDA/CPU profile、TorchVersion、nightly cu126、无 CUDA 时 CPU profile、现有 package 字段兼容、用户 override 优先及 fallback。

### ViewModel

测试版本列表加载、nightly 首项、latest stable 默认选择、版本切换刷新 profile、清空 profile selection、保留环境选择、fallback 及 StartCommand 状态。

验证命令：

```bash
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -v minimal
```

手动 smoke 需验证版本下拉、stable 版本切换、nightly cu126、部署按钮、永久 cache、删除 cache 重拉及离线 fallback。

## 8. 用户提示与发布说明

release notes 必须说明永久 cache 的行为：

> 版本目录首次获取后永久保存。本地不会自动发现之后发布的新 PyTorch 版本。如需刷新版本列表，请手动删除 `%APPDATA%/ComfyUI-Manager/pytorch_catalog_cache.json`。

## 9. 不在本次范围内

- 系统概览页面（CPU、内存、磁盘、Nvidia-smi）。
- 修改 `BaseEnvProfile` POCO。
- 修改 `BaseEnvInstaller` 或 progress UI。
- 自动 TTL、后台定时刷新或版本目录自动更新。

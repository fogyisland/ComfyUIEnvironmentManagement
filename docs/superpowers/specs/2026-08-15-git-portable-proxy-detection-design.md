# v0.6.15.4 Git portable staging bundle + unified HttpProxy settings

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 staging build 自带 git-portable(消除 staging 用户需系统 git 的依赖),并且把 Git 代理 + HTTP 代理合并成一套 Settings.HttpProxy* 字段(单一 source of truth,手动配置,驱动 HttpClient 和 git 进程)。

**Architecture:**
- 新增 `scripts/build_staging.ps1` 镜像 `build_release.ps1` steps 5–6(fetch_git_portable + 拷贝到 staging 产物),不动 dotnet publish 参数
- 新增 `Models.HttpProxyEnabled/Url/Port` 3 个字段;deprecate `GitProxyEnabled/Url/Port`(迁移路径:Load 读到旧 key → 写到新 key → 写回删旧 key)
- 新增 `Infrastructure/HttpProxyConfig` 替代 `GitProxyConfig`:单一类两个 `ApplyTo(...)` 方法(一个 `HttpClientHandler` 一个 `ProcessStartInfo`),同时驱动 HTTP + git
- App.xaml.cs HttpClient 构造改用 `HttpClient(MakeHandler())`,handler 收 HttpProxyConfig 控制

**Tech Stack:** .NET 8 / WPF / System.Text.Json / Microsoft.Data.Sqlite (无变化) / PowerShell 7 (build 脚本)

## Global Constraints

- .NET 8 + WPF + C# 12
- 单一 Settings.json 持久化字段 schema,迁移兼容 1 版本
- UI 语言: 中文 (跟现有 Settings 面板一致)
- git executable 解析链: `Settings.GitExe` → `<projectRoot>/bin/git-portable/cmd/git.exe` → `"git"` (PATH) — **不变**
- git-portable = MinGit 2.55.0.3 (~37 MB zip, ~89 MB 解压) — **不变**
- 跨平台: 仅 Windows 验证(用户当前 OS);不破 Linux/macOS 跑 dotnet publish 的 build path
- 用户原话: "我觉得可以下载git的绿色包,用于在程序中直接调用,避免在系统中还要准备git环境" + "如何确定设置了代理之后走代理"
- 用户已选决定: **(Q1) Build-time bundle** staging / **(Q2) HTTP + git 同步** / **(Q3) 只手动** Settings.json

---

## Background

### 现状

- `bin/git-portable/cmd/git.exe` 已存在(`scripts/fetch_git_portable.ps1` 已下过 MinGit 2.55.0.3)
- `App.ResolveGitExe(projectRoot)` 检查 `<projectRoot>/bin/git-portable/cmd/git.exe` 优先, fallback PATH
- `scripts/build_release.ps1` step 5–6 bundle git-portable 到 release zip (AppDir/bin/git-portable/)
- **Staging 不 bundle git-portable**:现 staging build 路径 `dotnet publish src-wpf/ComfyUI.Manager -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o "release/staging/ComfyUI Manager"`,只 publish dll,不跑 PS 脚本,不拷 git-portable。Staging 用户跑 app,`ResolveGitExe` 找不到 portable,fall back `"git"`,需要 system git 已装。

- `GitProxyConfig` (62 lines) 写 `HTTP_PROXY/HTTPS_PROXY` env 到 git `ProcessStartInfo`。Settings 已有 `GitProxyEnabled/Url/Port` + SettingsView XAML/VM 3 控件。
- `App.xaml.cs:189` HttpClient 构造: `new HttpClient { Timeout = ... }` — **无任何 proxy 配置**。
- `GitProxyConfig` 仅覆盖 git,HTTP catalog 拉取(CatalogFetcher / GitHubVersionService / GitHubCatalogMetadataService / GitHubReleaseService / DashboardService / PyTorchVersionCatalog / PyTorchVersionFetcher / BaseEnvProfileLoader 共 8 个 caller 共享单 singleton HttpClient)任何用户 proxy 不生效。

### 范围决定(用户选)

- **不** auto-detect Windows system proxy / env vars(`HTTP_PROXY` etc.)。仅手动 Settings.json 配置。
- **不** deprecate HttpProxy fields 设计 — 直接改 schema,迁移路径保留 1 版本旧 key 读 → 新 key 写回。
- **不** 改 git.exe 解析链顺序。`Settings.GitExe` 仍 user override (现无 UI 暴露,留给 advanced user)。

---

## Design

### 1. Git portable staging build

**File:** `scripts/build_staging.ps1` (new)

```powershell
# scripts/build_staging.ps1
# Mirror build_release.ps1 steps 5–6 (git-portable bundle) for staging builds.
# 幂等: fetch_git_portable.ps1 已存在则跳; Copy-Item -Force 覆盖。
# 不动 dotnet publish 参数 — 跟现 staging publish 命令完全一致。
#
# 用法: scripts/build_staging.ps1  (从 repo root 跑)
# 输出: release/staging/ComfyUI Manager/ + bin/git-portable/ 子目录

param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/.."),
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "release/staging/ComfyUI Manager"
)

$ErrorActionPreference = "Stop"
$AppDir = Join-Path $ProjectRoot $OutputDir

Write-Host "[1/3] Ensuring git-portable..." -ForegroundColor Yellow
& "$ProjectRoot/scripts/fetch_git_portable.ps1" -ProjectRoot $ProjectRoot
if ($LASTEXITCODE -ne 0) { throw "fetch_git_portable.ps1 failed" }

Write-Host "[2/3] Publishing $Configuration $Runtime self-contained..." -ForegroundColor Yellow
dotnet publish "$ProjectRoot/src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj" `
    -c $Configuration -r $Runtime --self-contained `
    -p:PublishSingleFile=false `
    -o $AppDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "[3/3] Copying git-portable to staging output..." -ForegroundColor Yellow
$GitPortableSrc = Join-Path $ProjectRoot "bin/git-portable"
$GitPortableDst = Join-Path $AppDir "bin/git-portable"
if (Test-Path $GitPortableDst) { Remove-Item -Recurse -Force $GitPortableDst }
Copy-Item -Recurse -Force $GitPortableSrc $GitPortableDst

Write-Host "[ok] staging built at $AppDir with bundled git-portable" -ForegroundColor Green
& "$GitPortableDst/cmd/git.exe" --version
```

**Documentation:**
- `release/staging/...` README 顶部加 1 行: "Staging 不再需要 system git;git-portable bundled at bin/git-portable/cmd/git.exe"
- `docs/superpowers/...` 加段落(在下一个 spec 文档或 README)

**Verification:**
- 跑 `scripts/build_staging.ps1` → exit 0;`staging/ComfyUI Manager/bin/git-portable/cmd/git.exe` 存在
- (跨脚本) sandbox / no-git VM 跑 staging → catalog refresh + node install 不报 "git not found"

### 2. HttpProxy 字段 + Settings UI

**File:** `src-wpf/ComfyUI.Manager/Models/Settings.cs` (modify)

```csharp
// 删除旧字段 (GitProxyEnabled/Url/Port) — 顶部 + JsonPropertyName 一起移掉
// 注释标记这些字段 v0.6.15.4 重命名为 HttpProxy*

// 新增统一代理字段
[JsonPropertyName("http_proxy_enabled")]
public bool HttpProxyEnabled { get; set; } = false;

[JsonPropertyName("http_proxy_url")]
public string HttpProxyUrl { get; set; } = "";

[JsonPropertyName("http_proxy_port")]
public int HttpProxyPort { get; set; } = 0;
```

**File:** `src-wpf/ComfyUI.Manager/Data/SettingsRepository.cs` (modify)

```csharp
// Load() 路径: 读到旧 "git_proxy_*" 字段 → 写新 "http_proxy_*" 字段 → save 回写
// 步骤:
//   1. JsonDocument.Parse 原始 JSON
//   2. 检测 root "git_proxy_enabled" key 存在 → 拷值到 current "http_proxy_*" fields
//   3. 反序列化为 Settings
//   4. 如果迁移发生 → _saveInPlace(path, settings) 写回新 schema(删除旧 key)
// 5. 解析错误 / 缺文件时返 new Settings()
```

**File:** `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` (modify, ~30 行)

```xml
<!-- 删 GitProxyEnabled/Url/Port 3 控件 ~@line 606–637 -->
<!-- 新增 "网络代理" section -->
<GroupBox Header="网络代理">
  <StackPanel>
    <TextBlock Text="驱动 Git 拉取 + HTTP catalog 拉取。手动配置,重启后生效。" 
               TextWrapping="Wrap" Margin="0,0,0,8" Foreground="{...muted...}"/>
    <CheckBox x:Name="HttpProxyEnabled" Content="启用代理" 
              IsChecked="{Binding HttpProxyEnabled, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"/>
    <TextBox MaterialLabel="URL (e.g. proxy.example.com)" 
             Text="{Binding HttpProxyUrl, ...}"/>
    <TextBox MaterialLabel="端口 (e.g. 8080)" 
             Text="{Binding HttpProxyPort, ...}"/>
  </StackPanel>
</GroupBox>
```

**File:** `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` (modify)
- 删 `GitProxyEnabled/Url/Port` properties (~3 props, ~30 行)
- 新增 `HttpProxyEnabled/Url/Port` properties (3 props, copy-paste 改名字)
- `MarkDirty` 触发 Save

### 3. HttpProxyConfig: 单一类驱动 HTTP + git

**File:** `src-wpf/ComfyUI.Manager/Infrastructure/HttpProxyConfig.cs` (new, ~75 lines)

```csharp
public sealed class HttpProxyConfig {
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public int Port { get; set; }

    public static HttpProxyConfig Disabled { get; } = new();

    public static HttpProxyConfig From(Settings s) {
        if (s is null) return Disabled;
        return new HttpProxyConfig {
            Enabled = s.HttpProxyEnabled,
            Url = s.HttpProxyUrl,
            Port = s.HttpProxyPort,
        };
    }

    /// <summary>设置 HttpClientHandler.Proxy = WebProxy(url, port) 当 Enabled; 否则 UseProxy=false.</summary>
    public void ApplyTo(HttpClientHandler handler) {
        if (handler is null) return;
        if (!Enabled) {
            handler.Proxy = null;
            handler.UseProxy = false;
            return;
        }
        if (string.IsNullOrWhiteSpace(Url)) return;
        if (Port <= 0 || Port > 65535) return;
        handler.Proxy = new WebProxy(BuildProxyUri());
        handler.UseProxy = true;
    }

    /// <summary>对 git ProcessStartInfo 写 HTTP_PROXY/HTTPS_PROXY env (从 GitProxyConfig 1:1 搬). </summary>
    public void ApplyTo(ProcessStartInfo psi) {
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(Url)) return;
        if (Port <= 0 || Port > 65535) return;
        var withScheme = Url.Trim().StartsWith("http://", ...) || ... 
            ? Url.Trim() : "http://" + Url.Trim();
        var proxy = $"{withScheme}:{Port}";
        psi.EnvironmentVariables["HTTP_PROXY"] = proxy;
        psi.EnvironmentVariables["HTTPS_PROXY"] = proxy;
    }

    private Uri BuildProxyUri() {
        var rawUrl = Url.Trim();
        var withScheme = ... (跟 ApplyTo(ProcessStartInfo) 同套加 scheme 逻辑)
        return new Uri($"{withScheme}:{Port}");
    }
}
```

**File:** `src-wpf/ComfyUI.Manager/Infrastructure/GitProxyConfig.cs` (delete)
- 删文件
- 所有 caller (`App.xaml.cs`, `GitRunner.cs`, `BulkUpdateOrchestrator.cs`, `ComfyUIManagerInstaller.cs`, 4 测) 改用 `HttpProxyConfig`

### 4. App.xaml.cs HttpClient 构造

**File:** `src-wpf/ComfyUI.Manager/App.xaml.cs` (modify, ~10 行)

```csharp
// 现 (line ~189):
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
// 改:
var handler = new HttpClientHandler();
HttpProxyConfig.From(settings).ApplyTo(handler);  // Enabled=false → Proxy=null,UseProxy=false
var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
```

`HttpProxyConfig.From(settings)` 单例引用(settings 改了,下次 git/HttpClient 重新构造时生效)。

### 5. GitRunner 接 HttpProxyConfig

**File:** `src-wpf/ComfyUI.Manager/Services/GitRunner.cs` (modify)

```csharp
// 现 ctor: GitRunner(string gitExe, GitProxyConfig? proxy)
// 改: GitRunner(string gitExe, HttpProxyConfig? proxy)
// RunAsync 内: var psi = ...; proxy?.ApplyTo(psi);  // 同款
```

**Caller 改 4 处:**
- `App.xaml.cs` (line ~175-180): `new GitRunner(gitExe, HttpProxyConfig.From(settings))`
- `Services/BulkUpdateOrchestrator.cs` ctor 同款
- `Services/ComfyUIManagerInstaller.cs` ctor 同款
- `Services/CommonNodeInstaller.cs` ctor 同款
- `tests/.../Services/GitRunnerTests.cs` (如存在) 改 HttpProxyConfig

### 6. 测试

**File:** `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/HttpProxyConfigTests.cs` (new, 8 tests)

```csharp
[Fact] void ApplyTo_HttpClientHandler_Disabled_SetsProxyNullAndUseProxyFalse()
[Fact] void ApplyTo_HttpClientHandler_Enabled_SetsWebProxyAndUseProxyTrue()
[Fact] void ApplyTo_HttpClientHandler_UrlWithoutScheme_PrependsHttp()
[Fact] void ApplyTo_HttpClientHandler_InvalidPort_NoOp()
[Fact] void ApplyTo_ProcessStartInfo_Enabled_WritesHttpAndHttpsProxyEnv()
[Fact] void ApplyTo_ProcessStartInfo_Disabled_NoEnvWritten()
[Fact] void From_Settings_MapsFields()
[Fact] void From_NullSettings_ReturnsDisabled()
```

**File:** `tests-wpf/ComfyUI.Manager.Tests/Models/SettingsMigrationTests.cs` (new, 2 tests)

```csharp
[Fact] void Load_OldGitProxyKeys_MigratesToHttpProxy()
[Fact] void Load_MigrationHappens_SavesBackNewSchemaWithoutOldKeys()
```

**File:** `tests-wpf/ComfyUI.Manager.Tests/Data/AppHttpProxyWiringTests.cs` (new, 2 tests)

```csharp
[Fact] void BuildHttpClient_ProxyEnabled_HandlerHasWebProxy()
[Fact] void BuildHttpClient_ProxyDisabled_HandlerHasNullProxy()
```

**File:** `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/GitProxyConfigTests.cs` (delete)
- 旧 `GitProxyConfig` 测试 文件删 (或 rewrite 测 HttpProxyConfig 同覆盖 — 后者)

### 7. 不动 / 单独处理

- `NoProxy` / `NO_PROXY` env 处理:**R3 deferred**。Settings 当前无 NO_PROXY 字段,用户没要求。先 spec 留 TODO 标注。
- `Settings.GitExe` (line 58) 字段保留,但 SettingsView **不暴露** UI (现 UI 无该控件)。advanced user 可手改 settings.json。
- `Settings.PipMirror` + `PipMirrorCustomUrl` (line 96-97) — pip 走自己机制,与 HTTP proxy 独立。本 spec 不动。
- `BuildReleaseFetch` 路径:`build_release.ps1` 已 bundle git-portable,本 spec 不动。
- Cross-platform:Mac/Linux 上 `bin/git-portable/cmd/git.exe` 不存在,ResolveGitExe 现有 fallback `"git"`。本 spec 不变。

---

## Risks

| ID | Risk | Mitigation |
|----|------|------------|
| R1 | 迁移静默丢失:用户已有 GitProxyUrl 但 Load 路径忘了拷 → proxy 失效 | Load 路径 root-level 检测旧 key + 写新 key + 写回(2 tests cover) |
| R2 | HttpClient 默认 Proxy 在 Windows 上 = system proxy,跟用户手动 "关代理" 冲突 | Enabled=false 显式 `Proxy = null; UseProxy = false`,不走 WinHTTP default |
| R3 | NO_PROXY 没处理 | 留 TODO (R3 不算 blocker — 用户没选) |
| R4 | GitProxyConfig 改名 HttpProxyConfig 后 4 caller 漏改 → compile error | 全项目 grep 一次性 sed 改;build 0 错误强保证 |
| R5 | staging.ps1 重复跑 fetch_git_portable 慢 | fetch 本身幂等(检测到且 --version 通过就 skip),只在第一次下载后续 skip |
| R6 | build_staging.ps1 后 staging 体积 +89 MB | 用户 release zip 已 270 MB,加 89 MB 是已存在约定,fetch_git_portable.ps1 跟现 release path 同 source |
| R7 | `Settings.GitExe` 字段 UI 没暴露,但仍是 ctor 输入点 | 不动 (现 SettingsView 无 GitExe 控件) |

---

## Verification

### 自动化

- `dotnet build` 0 错误
- `dotnet test tests-wpf/ComfyUI.Manager.Tests` 1165+ PASS / 0 FAIL / 1 SKIP (新增 12 测试)
- `pwsh scripts/build_staging.ps1` exit 0,产物含 `bin/git-portable/cmd/git.exe`

### 手工 smoke (5 step)

1. 跑 `scripts/build_staging.ps1` → exit 0;`staging/ComfyUI Manager/bin/git-portable/cmd/git.exe` 存在
2. 卸 system git (`where git` 不见) → 跑 staging → Catalog refresh + Node install 不报 "git not found"
3. Settings 勾 "启用代理" + URL `127.0.0.1` + PORT `8888` (本地 tinyproxy) → 重启 staging → Logs 看到 git fetch INFO 行 proxy 走 `127.0.0.1:8888`
4. 同 3 → 看 HTTP catalog 拉取 (Fiddler) → `CONNECT github.com:443` 通过 127.0.0.1:8888
5. Settings 关代理 → 重启 staging → 直连 (Fiddler 显示无 proxy CONNECT)

### 兼容性

- 旧 users (v0.6.15.3 及之前) 有 GitProxy* setttings.json → 首次启动 v0.6.15.4 自动迁移 → UI 看到 "HttpProxy" 勾 + URL + port 已填 → save 后 settings.json 只剩 http_proxy_* keys

---

## Out of Scope

- NO_PROXY 列表 (用户没选)
- PAC file / Windows system proxy 自动检测 (用户没选)
- HTTP proxy auth (用户名密码) — 当前 Settings.HttpProxyUrl/Port 无 auth 字段,Basic auth 留给未来
- 修改 git.exe 解析链 (Settings.GitExe → bin/git-portable → PATH) — 不变
- 把 git-portable 编译期嵌入 binary (libgit2sharp) — size + complexity 太高,用户已选 shell-out 解
- 替换 Settings.PipMirror 让它也走 HttpProxy — pip 走 pip.cfg/CLI env,跟 git.exe 环境变量隔离

---

## File Checklist

**New (5 files):**
- `scripts/build_staging.ps1`
- `src-wpf/ComfyUI.Manager/Infrastructure/HttpProxyConfig.cs`
- `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/HttpProxyConfigTests.cs`
- `tests-wpf/ComfyUI.Manager.Tests/Models/SettingsMigrationTests.cs`
- `tests-wpf/ComfyUI.Manager.Tests/Data/AppHttpProxyWiringTests.cs`

**Modified (9 files):**
- `src-wpf/ComfyUI.Manager/Models/Settings.cs`
- `src-wpf/ComfyUI.Manager/Data/SettingsRepository.cs`
- `src-wpf/ComfyUI.Manager/App.xaml.cs`
- `src-wpf/ComfyUI.Manager/Services/GitRunner.cs`
- `src-wpf/ComfyUI.Manager/Services/BulkUpdateOrchestrator.cs`
- `src-wpf/ComfyUI.Manager/Services/ComfyUIManagerInstaller.cs`
- `src-wpf/ComfyUI.Manager/Services/CommonNodeInstaller.cs`
- `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`
- `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`

**Deleted (2 files):**
- `src-wpf/ComfyUI.Manager/Infrastructure/GitProxyConfig.cs`
- `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/GitProxyConfigTests.cs` (replaced by HttpProxyConfigTests)

**Test seam additions:**
- `App.xaml.cs` 内 `internal BuildHttpClient(Settings, HttpProxyConfig?)` 静态方法 (跟现 `BuildPyTorchVersionDirectory` 同 `internal` 模式) — `AppHttpProxyWiringTests` 验证

---

## Estimated Effort

- 1 T1 task: `HttpProxyConfig` + 8 tests (1 commit, ~150 LOC)
- 1 T2 task: `Settings`/`Repository` 字段迁移 + 2 tests (1 commit, ~50 LOC)
- 1 T3 task: `App.xaml.cs` HttpClient + `GitProxyConfig` rename (1 commit, ~30 LOC)
- 1 T4 task: `SettingsView` UI 改 + `SettingsViewModel` properties (1 commit, ~50 LOC)
- 1 T5 task: `build_staging.ps1` + 手工 smoke (1 commit, ~40 LOC)
- 1 final review + fixes
- Total: ~5 commits, ~320 LOC, ship ready in 1 SDD cycle

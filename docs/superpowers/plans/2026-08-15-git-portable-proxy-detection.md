# v0.6.15.4 Git Portable Staging Bundle + Unified HttpProxy Settings

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 staging build 自动 bundle git-portable (消除 staging 用户需系统 git 的依赖), 并且把 Git 代理 + HTTP 代理合并成一套 `Settings.HttpProxy*` 字段 (单一 source of truth, 手动配置, 驱动 HttpClient 和 git 进程)。

**Architecture:**
- 新 `scripts/build_staging.ps1` 镜像 `build_release.ps1` steps 5–6 (fetch_git_portable + 拷到 staging 产物), 不动 dotnet publish 参数
- 新 `Models.HttpProxyEnabled/Url/Port` 3 字段; deprecate `GitProxyEnabled/Url/Port` (迁移: Load 读到旧 key → 写到新 key → Save 写回删旧 key)
- 新 `Infrastructure/HttpProxyConfig` 替代 `GitProxyConfig`: 单类两个 `ApplyTo(...)` (HttpClientHandler + ProcessStartInfo), 同时驱动 HTTP + git
- App.xaml.cs HttpClient 构造改用 `HttpClient(MakeHandler())`, handler 收 HttpProxyConfig

**Tech Stack:** .NET 8 / WPF / System.Text.Json / Microsoft.Data.Sqlite (无变化) / PowerShell 7

**Spec:** `docs/superpowers/specs/2026-08-15-git-portable-proxy-detection-design.md`

## Global Constraints

- .NET 8 + WPF + C# 12
- 单一 Settings.json 持久化字段 schema, 迁移兼容 1 版本 (旧 `git_proxy_*` → 新 `http_proxy_*`)
- UI 语言: 中文 (跟现有 Settings 面板一致)
- git executable 解析链: `Settings.GitExe` → `<projectRoot>/bin/git-portable/cmd/git.exe` → `"git"` (PATH) — **不变**
- git-portable = MinGit 2.55.0.3 (~37 MB zip, ~89 MB 解压) — **不变**
- 跨平台: 仅 Windows 验证 (用户当前 OS); 不破 Linux/macOS 跑 dotnet publish
- `App.xaml.cs` 测试 seam 模式: `internal` 静态方法, 跟现 `BuildPyTorchVersionDirectory` 同模式
- 测试 framework: xUnit, `[Fact]` / `[Theory]` (全项目统一)
- Commit message 风格: `feat(...)` / `fix(...)` / `refactor(...)` /(v0.6.15.4 marker)
- Staging 模型: `dotnet publish src-wpf/ComfyUI.Manager -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o "release/staging/ComfyUI Manager"`, 改成跑 `scripts/build_staging.ps1` (用户改命令)

---

## File Structure (前置: 拆 / 合 决定)

**New (5 files):**
- `scripts/build_staging.ps1` — mirror build_release.ps1 steps 5–6
- `src-wpf/ComfyUI.Manager/Infrastructure/HttpProxyConfig.cs` — 单一类驱动 HTTP + git
- `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/HttpProxyConfigTests.cs` — 8 tests
- `tests-wpf/ComfyUI.Manager.Tests/Models/SettingsMigrationTests.cs` — 2 tests
- `tests-wpf/ComfyUI.Manager.Tests/Data/AppHttpProxyWiringTests.cs` — 2 tests

**Modified (9 files):**
- `src-wpf/ComfyUI.Manager/Models/Settings.cs` — 删 GitProxy* 3 字段, 加 HttpProxy* 3 字段 (line 58–61)
- `src-wpf/ComfyUI.Manager/Data/SettingsRepository.cs` — Load 路径加 migration
- `src-wpf/ComfyUI.Manager/App.xaml.cs` — line 179/189 + 加 `BuildHttpClient` test seam
- `src-wpf/ComfyUI.Manager/Services/GitRunner.cs` — ctor 接 HttpProxyConfig
- `src-wpf/ComfyUI.Manager/Services/BulkUpdateOrchestrator.cs` — ctor 接 HttpProxyConfig
- `src-wpf/ComfyUI.Manager/Services/ComfyUIManagerInstaller.cs` — ctor 接 HttpProxyConfig
- `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` — ctor 接 HttpProxyConfig
- `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` — 删 GitProxy* 3 props, 加 HttpProxy* 3 props
- `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` — 改 GitProxy 3 控件 → HttpProxy 3 控件

**Deleted (2 files):**
- `src-wpf/ComfyUI.Manager/Infrastructure/GitProxyConfig.cs`
- `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/GitProxyConfigTests.cs`

**Mechanical rename (16+ test files referencing `GitProxyConfig`):**
- `tests/.../ViewModels/SettingsViewModelTests.cs` (10+ refs)
- `tests/.../ViewModels/SettingsViewModelDirtyTests.cs` (2 refs)
- `tests/.../ViewModels/SettingsViewModelLogDirectoryTests.cs` (1 ref)
- `tests/.../ViewModels/MainViewModelUnsavedSettingsTests.cs` (1 ref)
- `tests/.../Views/SettingsViewLoadTests.cs` (3 refs)
- `tests/.../Views/MainWindowExitCleanupTests.cs` (1 ref)

---

## Task Decomposition

| Task | What | Why standalone | LOC |
|------|------|---------------|-----|
| T1 | `HttpProxyConfig` class + 8 tests + delete `GitProxyConfig` + delete `GitProxyConfigTests` | 加新 infra + 删旧 infra 一步完成, 旧 callers 暂用 `GitProxyConfig` 留到 T2 再 migrate | ~150 |
| T2 | `Settings.HttpProxy*` fields + `SettingsRepository` migration + 2 tests | Schema change + migration 1 步, 锁定 schema | ~60 |
| T3 | Caller renames: GitRunner, BulkUpdateOrchestrator, ComfyUIManagerInstaller, MainViewModel, App.xaml.cs (git proxy), 6 test files; + App.xaml.cs HttpClient wiring + 2 AppHttpProxyWiringTests | Mechanical rename + ListAll 跟 HttpClient 接线 1 步, 跟 T4 UI 隔开 | ~100 |
| T4 | SettingsView XAML + SettingsViewModel properties (HttpProxy* 3 props) | UI 改, 依赖 T2 schema | ~80 |
| T5 | `scripts/build_staging.ps1` + 文档 + 手工 smoke | 独立 (build pipeline 不依赖前面) | ~40 |
| T6 | Final review + 修复 | | |

---

### Task 1: HttpProxyConfig class + delete GitProxyConfig

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Infrastructure/HttpProxyConfig.cs`
- Create: `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/HttpProxyConfigTests.cs`
- Delete: `src-wpf/ComfyUI.Manager/Infrastructure/GitProxyConfig.cs`
- Delete: `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/GitProxyConfigTests.cs`

**Interfaces:**
- Consumes: `Settings` (line 58–61 — unchanged in this task)
- Produces: `HttpProxyConfig { Enabled, Url, Port }`; static `Disabled`; static `From(Settings) -> HttpProxyConfig`; instance `ApplyTo(HttpClientHandler)`; instance `ApplyTo(ProcessStartInfo)`

**R4 mitigation:** Task 1 不改 callers, 只 add/delete infra. Callers 仍引 `GitProxyConfig` GitProxyConfigTests 删后 callers 编译断 — T2 修。本任务 commit 后 `dotnet build` 会 fail, **正常**; T2 完成后 build 恢复。

- [ ] **Step 1: Write 8 failing tests for HttpProxyConfig** in `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/HttpProxyConfigTests.cs`

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

public class HttpProxyConfigTests
{
    [Fact]
    public void ApplyTo_HttpClientHandler_Disabled_SetsProxyNullAndUseProxyFalse()
    {
        var proxy = HttpProxyConfig.Disabled;
        var handler = new HttpClientHandler();

        proxy.ApplyTo(handler);

        Assert.Null(handler.Proxy);
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public void ApplyTo_HttpClientHandler_Enabled_SetsWebProxyAndUseProxyTrue()
    {
        var proxy = new HttpProxyConfig { Enabled = true, Url = "127.0.0.1", Port = 7890 };
        var handler = new HttpClientHandler();

        proxy.ApplyTo(handler);

        Assert.NotNull(handler.Proxy);
        Assert.True(handler.UseProxy);
        Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal(new Uri("http://127.0.0.1:7890"), ((WebProxy)handler.Proxy!).Address);
    }

    [Fact]
    public void ApplyTo_HttpClientHandler_UrlWithoutScheme_PrependsHttp()
    {
        var proxy = new HttpProxyConfig { Enabled = true, Url = "proxy.local", Port = 8080 };
        var handler = new HttpClientHandler();

        proxy.ApplyTo(handler);

        Assert.Equal(new Uri("http://proxy.local:8080"), ((WebProxy)handler.Proxy!).Address);
    }

    [Fact]
    public void ApplyTo_HttpClientHandler_InvalidPort_NoOp()
    {
        var proxy = new HttpProxyConfig { Enabled = true, Url = "127.0.0.1", Port = 0 };
        var handler = new HttpClientHandler();

        proxy.ApplyTo(handler);

        Assert.Null(handler.Proxy);
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public void ApplyTo_ProcessStartInfo_Enabled_WritesHttpAndHttpsProxyEnv()
    {
        var proxy = new HttpProxyConfig { Enabled = true, Url = "127.0.0.1", Port = 7890 };
        var psi = new ProcessStartInfo();

        proxy.ApplyTo(psi);

        Assert.Equal("http://127.0.0.1:7890", psi.EnvironmentVariables["HTTP_PROXY"]);
        Assert.Equal("http://127.0.0.1:7890", psi.EnvironmentVariables["HTTPS_PROXY"]);
    }

    [Fact]
    public void ApplyTo_ProcessStartInfo_Disabled_NoEnvWritten()
    {
        var proxy = HttpProxyConfig.Disabled;
        var psi = new ProcessStartInfo();

        proxy.ApplyTo(psi);

        Assert.False(psi.EnvironmentVariables.ContainsKey("HTTP_PROXY"));
        Assert.False(psi.EnvironmentVariables.ContainsKey("HTTPS_PROXY"));
    }

    [Fact]
    public void From_Settings_MapsFields()
    {
        var s = new Settings
        {
            HttpProxyEnabled = true,
            HttpProxyUrl = "10.0.0.1",
            HttpProxyPort = 8888,
        };

        var cfg = HttpProxyConfig.From(s);

        Assert.True(cfg.Enabled);
        Assert.Equal("10.0.0.1", cfg.Url);
        Assert.Equal(8888, cfg.Port);
    }

    [Fact]
    public void From_NullSettings_ReturnsDisabled()
    {
        var cfg = HttpProxyConfig.From(null!);

        Assert.False(cfg.Enabled);
    }
}
```

- [ ] **Step 2: Run tests to verify FAIL**

```bash
cd tests-wpf/ComfyUI.Manager.Tests && dotnet build --no-restore 2>&1 | tail -5
```
Expected: build fail (HttpProxyConfig / HttpProxy Url fields not exist).

- [ ] **Step 3: Add HttpProxyUrl/Port/Enabled to Settings** (最小, 让 build 通过)

In `src-wpf/ComfyUI.Manager/Models/Settings.cs`, 在 `GitProxy*` 字段 **上方** 加 (line 58 之前):

```csharp
[JsonPropertyName("http_proxy_enabled")] public bool HttpProxyEnabled { get; set; }
[JsonPropertyName("http_proxy_url")] public string HttpProxyUrl { get; set; } = "";
[JsonPropertyName("http_proxy_port")] public int HttpProxyPort { get; set; }
```

- [ ] **Step 4: Write HttpProxyConfig class**

Create `src-wpf/ComfyUI.Manager/Infrastructure/HttpProxyConfig.cs`:

```csharp
using System;
using System.Diagnostics;
using System.Net;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// HttpProxyConfig: 统一代理配置 —— 驱动 HttpClient(HTTP catalog 拉取)+ git 进程
/// (HTTP_PROXY/HTTPS_PROXY env 注入)。
/// v0.6.15.4 替代 GitProxyConfig: 单类两个 ApplyTo 方法, 单一 source of truth
/// (Settings.HttpProxy* 3 字段)。
///
/// 设值口径:
/// - Enabled=false → handler.Proxy=null / UseProxy=false (不走 WinHTTP default system proxy)
/// - Enabled=true 且 URL/Port 合法 → handler.Proxy = WebProxy(http://url:port) / psi.HTTP_PROXY 设
/// - URL 不带 scheme → 默认 http://
/// - Port 越界 (0, >65535) → silently noop (跟原 GitProxyConfig 行为一致)
/// </summary>
public sealed class HttpProxyConfig
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public int Port { get; set; }

    public static HttpProxyConfig Disabled { get; } = new();

    public static HttpProxyConfig From(Settings s)
    {
        if (s is null) return Disabled;
        return new HttpProxyConfig
        {
            Enabled = s.HttpProxyEnabled,
            Url = s.HttpProxyUrl,
            Port = s.HttpProxyPort,
        };
    }

    /// <summary>Application HTTP client 代理: Enabled 时设 WebProxy; 否则显式 null + UseProxy=false.</summary>
    public void ApplyTo(HttpClientHandler handler)
    {
        if (handler is null) return;
        if (!Enabled)
        {
            handler.Proxy = null;
            handler.UseProxy = false;
            return;
        }
        if (string.IsNullOrWhiteSpace(Url)) return;
        if (Port <= 0 || Port > 65535) return;
        handler.Proxy = new WebProxy(BuildProxyUri());
        handler.UseProxy = true;
    }

    /// <summary>Git 进程代理: 写 HTTP_PROXY/HTTPS_PROXY env 到 ProcessStartInfo。
    /// per-process, 不污染整个 WPF。</summary>
    public void ApplyTo(ProcessStartInfo psi)
    {
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(Url)) return;
        if (Port <= 0 || Port > 65535) return;

        var rawUrl = Url.Trim();
        var withScheme = rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                      || rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                      || rawUrl.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)
            ? rawUrl
            : "http://" + rawUrl;

        var proxy = $"{withScheme}:{Port}";
        psi.EnvironmentVariables["HTTP_PROXY"] = proxy;
        psi.EnvironmentVariables["HTTPS_PROXY"] = proxy;
    }

    private Uri BuildProxyUri()
    {
        var rawUrl = Url.Trim();
        var withScheme = rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                      || rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                      || rawUrl.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)
            ? rawUrl
            : "http://" + rawUrl;
        return new Uri($"{withScheme}:{Port}");
    }
}
```

- [ ] **Step 5: Run tests — verify all 8 pass**

```bash
cd tests-wpf/ComfyUI.Manager.Tests && dotnet build --no-restore 2>&1 | tail -3 && dotnet test --no-build --filter "FullyQualifiedName~HttpProxyConfigTests" 2>&1 | tail -10
```
Expected: 8 passed.

- [ ] **Step 6: Delete `GitProxyConfig.cs` + `GitProxyConfigTests.cs`**

```bash
git rm src-wpf/ComfyUI.Manager/Infrastructure/GitProxyConfig.cs tests-wpf/ComfyUI.Manager.Tests/Infrastructure/GitProxyConfigTests.cs
```

**注意:** 删后 `dotnet build` 会 fail (callers 仍引 `GitProxyConfig`)。这是 expected — T2 + T3 修。

- [ ] **Step 7: Commit**

```bash
cd D:/ToolDevelop/ComfyUI
git add src-wpf/ComfyUI.Manager/Models/Settings.cs src-wpf/ComfyUI.Manager/Infrastructure/HttpProxyConfig.cs tests-wpf/ComfyUI.Manager.Tests/Infrastructure/HttpProxyConfigTests.cs
git add -u src-wpf/ComfyUI.Manager/Infrastructure/GitProxyConfig.cs tests-wpf/ComfyUI.Manager.Tests/Infrastructure/GitProxyConfigTests.cs
git commit -m "feat(infrastructure): HttpProxyConfig replaces GitProxyConfig (v0.6.15.4 T1)

单一代理类驱动 HTTP + git:
- ApplyTo(HttpClientHandler): WebProxy(http://url:port) + UseProxy=true, Disabled 用 Proxy=null/UseProxy=false
- ApplyTo(ProcessStartInfo): HTTP_PROXY/HTTPS_PROXY env (per-process, 不污染 WPF)
- 8 tests cover: 两种 ApplyTo 全部 path + From(Settings) + From(null)

GitProxyConfig.cs + GitProxyConfigTests.cs 删 (callers compile error 由 T2/T3 修)。
Settings.HttpProxy* 3 字段 (line 58-61) 临时新增, T2 删 GitProxy 字段 + 加迁移。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

### Task 2: Settings.HttpProxy* fields + Repository migration

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs` (line 58–61 + CopyInto line 147–149)
- Modify: `src-wpf/ComfyUI.Manager/Data/SettingsRepository.cs` (Load 路径)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Models/SettingsMigrationTests.cs`

**Interfaces:**
- Consumes: `Settings` (HttpProxy* fields added in T1)
- Produces: `SettingsRepository.Load()` — 旧 schema `git_proxy_*` → 新 schema `http_proxy_*` 一次迁移并写回

- [ ] **Step 1: Write 2 failing tests for Settings migration**

Create `tests-wpf/ComfyUI.Manager.Tests/Models/SettingsMigrationTests.cs`:

```csharp
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public class SettingsMigrationTests : System.IDisposable
{
    private readonly string _tmpDir;
    private readonly string _settingsPath;

    public SettingsMigrationTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"settings-mig-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        _settingsPath = Path.Combine(_tmpDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_OldGitProxyKeys_MigratesToHttpProxy()
    {
        // 写一份 v0.6.15.3 old-schema settings.json
        File.WriteAllText(_settingsPath, "{\n" +
            "  \"git_proxy_enabled\": true,\n" +
            "  \"git_proxy_url\": \"192.168.1.1\",\n" +
            "  \"git_proxy_port\": 7890\n" +
            "}");

        var repo = new SettingsRepository(_settingsPath);
        var s = repo.Load();

        Assert.True(s.HttpProxyEnabled);
        Assert.Equal("192.168.1.1", s.HttpProxyUrl);
        Assert.Equal(7890, s.HttpProxyPort);
    }

    [Fact]
    public void Load_MigrationHappens_SavesBackNewSchemaWithoutOldKeys()
    {
        File.WriteAllText(_settingsPath, "{\n" +
            "  \"git_proxy_enabled\": true,\n" +
            "  \"git_proxy_url\": \"old.local\",\n" +
            "  \"git_proxy_port\": 8888\n" +
            "}");

        var repo = new SettingsRepository(_settingsPath);
        repo.Load();
        var reloadedJson = File.ReadAllText(_settingsPath);

        // 旧 key 应被写回删除
        Assert.DoesNotContain("git_proxy_enabled", reloadedJson);
        Assert.DoesNotContain("git_proxy_url", reloadedJson);
        Assert.DoesNotContain("git_proxy_port", reloadedJson);
        // 新 key 写出
        Assert.Contains("http_proxy_enabled", reloadedJson);
        Assert.Contains("http_proxy_url", reloadedJson);
        Assert.Contains("http_proxy_port", reloadedJson);
    }
}
```

- [ ] **Step 2: Run tests to verify FAIL**

```bash
cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --no-build --filter "FullyQualifiedName~SettingsMigrationTests" 2>&1 | tail -10
```
Expected: 2 fail (Load() 没迁移逻辑).

- [ ] **Step 3: Implement migration in SettingsRepository.Load**

In `src-wpf/ComfyUI.Manager/Data/SettingsRepository.cs`, modify `Load()` method (replace lines 43–67):

```csharp
public virtual Settings Load()
{
    if (!File.Exists(_settingsPath))
    {
        return new Settings();
    }

    var json = File.ReadAllText(_settingsPath);
    if (string.IsNullOrWhiteSpace(json))
    {
        return new Settings();
    }

    // v0.6.15.4: 检测旧 schema 字段 (git_proxy_*) → 迁移到新 schema (http_proxy_*)
    // 并 Save 写回 (持久化迁移)。Pay-for-once: 第一次启动 v0.6.15.4 触发一次,
    // 后续启动走新 schema 没迁移开销。
    var (migratedJson, migrated) = TryMigrateOldGitProxyKeys(json);
    if (migrated)
    {
        // 写回新 schema (旧 key 删, 新 key 落)
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_settingsPath, migratedJson);
        }
        catch
        {
            // 迁移失败不影响 Load 行为 (return migrated settings)
        }
        json = migratedJson;
    }

    var s = JsonSerializer.Deserialize<Settings>(json, JsonOptions)
        ?? new Settings();

    // v0.6.9 T2:G5 缺省 Dark;非法 theme_mode(老 settings.json 残留 "system"
    // 之外的不可识别值)normalize 到 "dark",避免下游 ParseThemeMode 失败。
    // "light"/"dark"/"system" 都是合法值,保留原样。
    if (s.ThemeMode != "light" && s.ThemeMode != "dark" && s.ThemeMode != "system")
    {
        s.ThemeMode = "dark";
    }
    return s;
}

/// <summary>
/// v0.6.15.4: 检测 JSON 中是否有 <c>git_proxy_enabled</c> 字段,有则迁移到
/// <c>http_proxy_*</c>。返回 (新 JSON, 是否迁移)。
/// </summary>
private static (string Json, bool Migrated) TryMigrateOldGitProxyKeys(string json)
{
    if (string.IsNullOrEmpty(json)) return (json, false);
    if (!json.Contains("git_proxy_", StringComparison.Ordinal)) return (json, false);

    try
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return (json, false);
        if (!root.TryGetProperty("git_proxy_enabled", out _))
        {
            // 只检测到 git_proxy_url/port 但没 enabled 说明 settings.json 是残缺 / 误填,
            // 也不触发迁移,避免 partial migration
            return (json, false);
        }

        var enabled = root.TryGetProperty("git_proxy_enabled", out var e)
            && e.ValueKind == JsonValueKind.True;
        var url = root.TryGetProperty("git_proxy_url", out var u)
            && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
        var port = root.TryGetProperty("git_proxy_port", out var p)
            && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;

        var sb = new System.Text.StringBuilder();
        using (var writer = new Utf8JsonWriter(
            new System.IO.MemoryStream(),
            new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name == "git_proxy_enabled"
                    || prop.Name == "git_proxy_url"
                    || prop.Name == "git_proxy_port")
                {
                    continue; // skip old keys
                }
                prop.WriteTo(writer);
            }
            writer.WriteBoolean("http_proxy_enabled", enabled);
            writer.WriteString("http_proxy_url", url ?? "");
            writer.WriteNumber("http_proxy_port", port);
            writer.WriteEndObject();
            writer.Flush();
            var bytes = ((System.IO.MemoryStream)writer.GetType()
                .GetField("_output", System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance)!.GetValue(writer) as System.IO.MemoryStream
                ?? new System.IO.MemoryStream()).ToArray();

            // 上面 reflection 太 hacky, 改用直接序列化整个 root 然后字符串拼接更稳;
            // 这里就用简单的 JsonSerializer.SerializeGetRaw 模式
        }

        // 简化 path: 走 JsonNode (System.Text.Json.Nodes) 文档模型
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node.Remove("git_proxy_enabled");
        node.Remove("git_proxy_url");
        node.Remove("git_proxy_port");
        node["http_proxy_enabled"] = enabled;
        node["http_proxy_url"] = url;
        node["http_proxy_port"] = port;
        return (node.ToJsonString(JsonOptions), true);
    }
    catch
    {
        return (json, false);
    }
}
```

**R4 catch (review callout):** 上面 step 3 写了 reflection hacky 那段 — 实现时 **必须删掉 `writer.WriteStartObject/...` 整段反射**, 只保留 `JsonNode.Parse` 简化 path。Reviewer 看到绕路反射 = reject。

- [ ] **Step 4: Delete GitProxy* fields from Settings.cs**

In `src-wpf/ComfyUI.Manager/Models/Settings.cs`:
- Delete lines 58–61 (the 3 `git_proxy_*` fields)
- Delete lines 147–149 (`CopyInto` 内 `target.GitProxy* = source.GitProxy*` 3 行)

```csharp
// 删除 58-61:
[JsonPropertyName("git_exe")] public string GitExe { get; set; } = "";
[JsonPropertyName("git_proxy_url")] public string GitProxyUrl { get; set; } = "";          // ← delete
[JsonPropertyName("git_proxy_port")] public int GitProxyPort { get; set; }                  // ← delete
[JsonPropertyName("git_proxy_enabled")] public bool GitProxyEnabled { get; set; }            // ← delete

// 删除 147-149 (CopyInto):
target.GitProxyUrl = source.GitProxyUrl;        // ← delete
target.GitProxyPort = source.GitProxyPort;      // ← delete
target.GitProxyEnabled = source.GitProxyEnabled; // ← delete
```

- [ ] **Step 5: Run tests — verify original 8 + 2 migration pass**

```bash
cd tests-wpf/ComfyUI.Manager.Tests && dotnet build --no-restore 2>&1 | tail -3
cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --no-build --filter "FullyQualifiedName~HttpProxyConfigTests|FullyQualifiedName~SettingsMigrationTests" 2>&1 | tail -10
```
Expected: 10 passed.

- [ ] **Step 6: Commit**

```bash
cd D:/ToolDevelop/ComfyUI
git add src-wpf/ComfyUI.Manager/Models/Settings.cs src-wpf/ComfyUI.Manager/Data/SettingsRepository.cs tests-wpf/ComfyUI.Manager.Tests/Models/SettingsMigrationTests.cs
git commit -m "feat(settings): HttpProxy 字段 + SettingsRepository 旧→新 schema 迁移 (v0.6.15.4 T2)

Settings.HttpProxyEnabled/Url/Port 3 字段取代 GitProxy*:
- Settings.cs line 58-61 删 GitProxy* 3 字段, 顶部 line 56-61 加 HttpProxy* 3 字段
- CopyInto 删 GitProxy 3 行

迁移逻辑 (SettingsRepository.Load + TryMigrateOldGitProxyKeys):
- 检测 JSON 含 git_proxy_enabled → 触发一次性迁移
- 拷贝 3 字段 → 新 http_proxy_* 字段
- 用 JsonNode 删除旧 key, 写入新 key
- Save 写回新 schema (下次启动直接走新 schema)
- 迁移失败: silently noop (current load 行为不变)

2 tests cover: 旧 key → 新 key 迁移 + 写回 schema 不含旧 key + 含新 key。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

### Task 3: Caller renames + App.xaml.cs HttpClient wiring

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/GitRunner.cs` (line 24, 28, 68)
- Modify: `src-wpf/ComfyUI.Manager/Services/BulkUpdateOrchestrator.cs` (line 39, 69; class XML comment line 28)
- Modify: `src-wpf/ComfyUI.Manager/Services/ComfyUIManagerInstaller.cs` (line 40, 44)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` (line 39, 250)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs` (line 178–179, 189 + new BuildHttpClient test seam)
- Modify: 6 test files (机械 rename `GitProxyConfig` → `HttpProxyConfig`)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Data/AppHttpProxyWiringTests.cs`

**Interfaces:**
- Consumes: `HttpProxyConfig` (T1) + `Settings.HttpProxy*` (T2)
- Produces: `GitRunner(string gitExe, HttpProxyConfig? proxy)`; `BulkUpdateOrchestrator(..., HttpProxyConfig? proxy)`; `ComfyUIManagerInstaller(..., HttpProxyConfig? proxy)`; `MainViewModel(..., HttpProxyConfig gitProxy, ...)`; `App.BuildHttpClient(Settings, HttpProxyConfig?)` (test seam)

- [ ] **Step 1: Write 2 failing tests for App HttpClient wiring**

Create `tests-wpf/ComfyUI.Manager.Tests/Data/AppHttpProxyWiringTests.cs`:

```csharp
using System.Net;
using System.Net.Http;
using System.Reflection;
using ComfyUI.Manager;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public class AppHttpProxyWiringTests
{
    [Fact]
    public void BuildHttpClient_ProxyEnabled_HandlerHasWebProxy()
    {
        var settings = new Settings
        {
            HttpProxyEnabled = true,
            HttpProxyUrl = "127.0.0.1",
            HttpProxyPort = 7890,
        };
        var proxy = HttpProxyConfig.From(settings);

        var http = InvokeBuildHttpClient(settings, proxy);

        Assert.IsType<HttpClientHandler>(handler(http));
        Assert.True(handler(http).UseProxy);
        Assert.NotNull(handler(http).Proxy);
        Assert.IsType<WebProxy>(handler(http).Proxy);
    }

    [Fact]
    public void BuildHttpClient_ProxyDisabled_HandlerHasNullProxy()
    {
        var settings = new Settings();
        var proxy = HttpProxyConfig.Disabled;

        var http = InvokeBuildHttpClient(settings, proxy);

        Assert.False(handler(http).UseProxy);
        Assert.Null(handler(http).Proxy);
    }

    private static HttpClient InvokeBuildHttpClient(Settings _, HttpProxyConfig proxy)
    {
        var method = typeof(App).GetMethod(
            "BuildHttpClient",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);
        return (HttpClient)method!.Invoke(null, new object?[] { proxy })!;
    }

    private static HttpClientHandler handler(HttpClient http)
    {
        var f = typeof(HttpClient).GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance);
        return (HttpClientHandler)f!.GetValue(http)!;
    }
}
```

- [ ] **Step 2: Run tests to verify FAIL**

```bash
cd tests-wpf/ComfyUI.Manager.Tests && dotnet build --no-restore 2>&1 | tail -5
```
Expected: build fail (GitProxyConfig.cs deleted in T1, callers still reference it).

- [ ] **Step 3: Mechanical rename in 4 src files**

全项目 grep `GitProxyConfig` → s/GitProxyConfig/HttpProxyConfig/g; `GitProxyEnabled|Url|Port` → `HttpProxyEnabled|Url|Port` only in `GitProxyConfig.cs` (已删) 和 `GitProxyConfigTests.cs` (已删):

```bash
# Step 3a: src files - rename GitProxyConfig → HttpProxyConfig
sed -i 's/GitProxyConfig/HttpProxyConfig/g' src-wpf/ComfyUI.Manager/Services/GitRunner.cs
sed -i 's/GitProxyConfig/HttpProxyConfig/g' src-wpf/ComfyUI.Manager/Services/BulkUpdateOrchestrator.cs
sed -i 's/GitProxyConfig/HttpProxyConfig/g' src-wpf/ComfyUI.Manager/Services/ComfyUIManagerInstaller.cs
sed -i 's/GitProxyConfig/HttpProxyConfig/g' src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs
sed -i 's/GitProxyConfig/HttpProxyConfig/g' src-wpf/ComfyUI.Manager/App.xaml.cs

# Verify build now only fails on App HttpClient wiring (no more GitProxyConfig refs)
cd tests-wpf/ComfyUI.Manager.Tests && dotnet build --no-restore 2>&1 | tail -10
```

Note: `GitProxyEnabled/Url/Port` Settings fields (which were deleted in T2) — the rename above also touches these references in MainViewModel.cs / App.xaml.cs / SettingsViewModel.cs. Per T2, those fields are gone, so the rename leaves `s.HttpProxyEnabled/Url/Port` references. Verify the diff doesn't accidentally leave `GitProxy*` Settings property references.

```bash
git diff src-wpf/ComfyUI.Manager/Services/GitRunner.cs | head -30
git diff src-wpf/ComfyUI.Manager/Services/BulkUpdateOrchestrator.cs | head -30
# Verify only GitProxyConfig → HttpProxyConfig, no other collateral changes
```

- [ ] **Step 4: Test files mechanical rename**

```bash
sed -i 's/GitProxyConfig/HttpProxyConfig/g' tests-wpf/ComfyUI.Manager.Tests/Views/SettingsViewLoadTests.cs
sed -i 's/GitProxyConfig/HttpProxyConfig/g' tests-wpf/ComfyUI.Manager.Tests/Views/MainWindowExitCleanupTests.cs
sed -i 's/GitProxyConfig/HttpProxyConfig/g' tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelTests.cs
sed -i 's/GitProxyConfig/HttpProxyConfig/g' tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelDirtyTests.cs
sed -i 's/GitProxyConfig/HttpProxyConfig/g' tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelLogDirectoryTests.cs
sed -i 's/GitProxyConfig/HttpProxyConfig/g' tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelUnsavedSettingsTests.cs
```

- [ ] **Step 5: Add BuildHttpClient test seam + rewire App.xaml.cs**

In `src-wpf/ComfyUI.Manager/App.xaml.cs`:

1. Replace line 178–179 comment + var:
```csharp
// 共享同一份 HttpProxyConfig,SettingsViewModel 改它会立即影响下一次 git 调用 / HTTP 拉取。
var gitProxy = HttpProxyConfig.From(settings);
```

2. Replace line 189 (`var http = new HttpClient { ... }`) + add static helper near existing `BuildPyTorchVersionDirectory` (around line 348):

```csharp
// v0.6.15.4: 网关代理走 HttpProxyConfig.Built HttpClient.BuildHttpClient test seam。
var http = BuildHttpClient(gitProxy);
http.DefaultRequestHeaders.UserAgent.ParseAdd("ComfyUI-Manager/0.6.13");
```

3. Add `internal static HttpClient BuildHttpClient(HttpProxyConfig? proxy)` near `BuildPyTorchVersionDirectory`:

```csharp
/// <summary>
/// v0.6.15.4: 构建带代理的 HttpClient。HttpProxyConfig.Enabled=true → WebProxy(http://url:port);
/// 否则显式 Proxy=null/UseProxy=false (不走 WinHTTP default system proxy, R2 mitigation)。
/// <c>internal</c> 而非 <c>private</c>:<c>AppHttpProxyWiringTests</c> 验证 (csproj 已声明
/// <c>InternalsVisibleTo("ComfyUI.Manager.Tests")</c>)。
/// </summary>
internal static HttpClient BuildHttpClient(HttpProxyConfig? proxy)
{
    var handler = new HttpClientHandler();
    if (proxy is not null)
    {
        proxy.ApplyTo(handler);
    }
    else
    {
        // Disabled 默认: 显式不走 system proxy
        handler.Proxy = null;
        handler.UseProxy = false;
    }
    return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
}
```

- [ ] **Step 6: Run tests — verify 8 + 2 + 2 + ~50 migrated tests pass**

```bash
cd tests-wpf/ComfyUI.Manager.Tests && dotnet build --no-restore 2>&1 | tail -5
cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --no-build --filter "FullyQualifiedName~HttpProxyConfigTests|FullyQualifiedName~SettingsMigrationTests|FullyQualifiedName~AppHttpProxyWiringTests" 2>&1 | tail -10
```
Expected: 12 passed.

Full suite expect: ~1150 PASS / 0 FAIL / 1 SKIP (callers migrated; SettingsViewModel's GitProxy props still refer to GitProxy* fields — but those fields are deleted, so this fails on T4 setup that's still pending).

```bash
cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --no-build 2>&1 | tail -5
```
Expected: ~50 fail (SettingsViewModel + SettingsView + MainViewModel have GitProxy refs in non-rename parts). T4 fixes.

- [ ] **Step 7: Commit**

```bash
cd D:/ToolDevelop/ComfyUI
git add src-wpf/ComfyUI.Manager/Services/GitRunner.cs src-wpf/ComfyUI.Manager/Services/BulkUpdateOrchestrator.cs src-wpf/ComfyUI.Manager/Services/ComfyUIManagerInstaller.cs src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs src-wpf/ComfyUI.Manager/App.xaml.cs
git add tests-wpf/ComfyUI.Manager.Tests/Views/SettingsViewLoadTests.cs tests-wpf/ComfyUI.Manager.Tests/Views/MainWindowExitCleanupTests.cs
git add tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelTests.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelDirtyTests.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/SettingsViewModelLogDirectoryTests.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/MainViewModelUnsavedSettingsTests.cs
git add tests-wpf/ComfyUI.Manager.Tests/Data/AppHttpProxyWiringTests.cs
git commit -m "feat(infrastructure): caller renames + App HttpClient proxy wiring (v0.6.15.4 T3)

GitProxyConfig → HttpProxyConfig rename across 4 src + 6 test files:
- GitRunner ctor(string gitExe, HttpProxyConfig? proxy)
- BulkUpdateOrchestrator ctor(... HttpProxyConfig? proxy ...)
- ComfyUIManagerInstaller ctor(... HttpProxyConfig? proxy ...)
- MainViewModel ctor(... HttpProxyConfig gitProxy ...)
- App.xaml.cs line 179 gitProxy = HttpProxyConfig.From(settings)

App.xaml.cs HttpClient 构造改走 internal BuildHttpClient 静态函数:
- 之前: var http = new HttpClient { Timeout = ... } (无 proxy)
- 现在: var http = BuildHttpClient(gitProxy) (handler 收 HttpProxyConfig.ApplyTo)
- R2 mitigation: Disabled 显式 Proxy=null/UseProxy=false (不走 WinHTTP default system proxy)
- internal 而非 private: 跟现 BuildPyTorchVersionDirectory 同测试 seam pattern

AppHttpProxyWiringTests 2 tests:
- BuildHttpClient_ProxyEnabled_HandlerHasWebProxy: 验证 handler.Proxy is WebProxy
- BuildHttpClient_ProxyDisabled_HandlerHasNullProxy: 验证 UseProxy=false + Proxy=null

SettingsViewModel + SettingsView GitProxy* refs 留给 T4 (跟 UI 一起改)。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

### Task 4: SettingsView XAML + SettingsViewModel properties

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` (line 20, 74, 523–557)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` (line 602–637)

**Interfaces:**
- Consumes: `Settings.HttpProxy*` (T2) + `HttpProxyConfig` live ref (T1)
- Produces: `SettingsViewModel.HttpProxyUrl/Port/Enabled` properties; XAML "网络代理" section

- [ ] **Step 1: Replace _proxy field type in SettingsViewModel**

In `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`:
- Line 20: `private readonly GitProxyConfig _proxy;` → `private readonly HttpProxyConfig _proxy;`
- Line 74: `GitProxyConfig proxy` → `HttpProxyConfig proxy`

- [ ] **Step 2: Replace 3 properties (lines 523–557)**

Replace lines 523–557 with:

```csharp
public string HttpProxyUrl
{
    // getter/setter 都双写: _settings (持久化) + _proxy (运行期 live)。
    // 让 HttpProxy 开关能即时生效, 不用重启。
    get => _proxy.Url;
    set
    {
        _proxy.Url = value;
        _settings.HttpProxyUrl = value;
        MarkDirty(nameof(HttpProxyUrl));
        RaisePropertyChanged();
    }
}
public int HttpProxyPort
{
    get => _proxy.Port;
    set
    {
        _proxy.Port = value;
        _settings.HttpProxyPort = value;
        MarkDirty(nameof(HttpProxyPort));
        RaisePropertyChanged();
    }
}
public bool HttpProxyEnabled
{
    get => _proxy.Enabled;
    set
    {
        _proxy.Enabled = value;
        _settings.HttpProxyEnabled = value;
        MarkDirty(nameof(HttpProxyEnabled));
        RaisePropertyChanged();
    }
}
```

- [ ] **Step 3: Update XAML — replace git 代理 section (lines 602–637)**

In `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`, replace lines 602–637 with:

```xml
<StackPanel Orientation="Horizontal" Margin="0,8,0,4">
    <TextBlock Text="网络代理" VerticalAlignment="Center"/>
    <TextBlock Text="驱动 Git 拉取 + HTTP catalog 拉取。手动配置, 重启后生效。" Margin="8,0,0,0"
               VerticalAlignment="Center" FontSize="11" Foreground="{DynamicResource MutedText}"/>
</StackPanel>
<StackPanel Orientation="Horizontal" Margin="0,0,0,4">
    <CheckBox Content="启用代理" IsChecked="{Binding HttpProxyEnabled}"
              VerticalAlignment="Center"/>
    <TextBlock Text="⚠" FontSize="11" Margin="6,0,0,0"
               VerticalAlignment="Center"
               Foreground="{DynamicResource WarningBrush}"
               ToolTip="尚未保存"
               Visibility="{Binding Dirty[HttpProxyEnabled], Converter={StaticResource BoolToVisibility}}"/>
</StackPanel>
<Grid Margin="0,2,0,0" IsEnabled="{Binding HttpProxyEnabled}">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*" />
        <ColumnDefinition Width="8" />
        <ColumnDefinition Width="120" />
    </Grid.ColumnDefinitions>
    <Grid Grid.Column="0">
        <TextBox Text="{Binding HttpProxyUrl, UpdateSourceTrigger=PropertyChanged}"
                 Style="{StaticResource MaterialTextBox}" />
        <TextBlock Text="⚠" FontSize="11" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,4,0"
                   Foreground="{DynamicResource WarningBrush}"
                   ToolTip="尚未保存"
                   Visibility="{Binding Dirty[HttpProxyUrl], Converter={StaticResource BoolToVisibility}}"/>
    </Grid>
    <Grid Grid.Column="2">
        <TextBox Text="{Binding HttpProxyPort, UpdateSourceTrigger=PropertyChanged}"
                 Style="{StaticResource MaterialTextBox}" />
        <TextBlock Text="⚠" FontSize="11" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,4,0"
                   Foreground="{DynamicResource WarningBrush}"
                   ToolTip="尚未保存"
                   Visibility="{Binding Dirty[HttpProxyPort], Converter={StaticResource BoolToVisibility}}"/>
    </Grid>
</Grid>
```

- [ ] **Step 4: Run tests — verify full suite**

```bash
cd tests-wpf/ComfyUI.Manager.Tests && dotnet build --no-restore 2>&1 | tail -5
cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --no-build 2>&1 | tail -5
```
Expected: 1165+ PASS / 0 FAIL / 1 SKIP (3 flaky `ProcessLauncherProgressTests` pre-existing).

- [ ] **Step 5: Run STA load test for SettingsView**

```bash
cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --no-build --filter "FullyQualifiedName~SettingsViewLoadTests" 2>&1 | tail -10
```
Expected: 所有 STA load tests pass (确保 XAML 改完 view 加载不崩)。

- [ ] **Step 6: Commit**

```bash
cd D:/ToolDevelop/ComfyUI
git add src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs src-wpf/ComfyUI.Manager/Views/SettingsView.xaml
git commit -m "feat(settings-ui): 网络代理 section 取代 git 代理 (v0.6.15.4 T4)

SettingsViewModel:
- _proxy 字段类型 GitProxyConfig → HttpProxyConfig
- 删 GitProxyEnabled/Url/Port 3 props
- 加 HttpProxyEnabled/Url/Port 3 props (双写 _settings + _proxy, 保持 live)

SettingsView.xaml:
- 删 'git 代理' section (3 控件 GitProxy*)
- 加 '网络代理' section (3 控件 HttpProxy*)
- 副标: '驱动 Git 拉取 + HTTP catalog 拉取。手动配置, 重启后生效。'

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

### Task 5: scripts/build_staging.ps1

**Files:**
- Create: `scripts/build_staging.ps1`

**Interfaces:**
- Consumes: `scripts/fetch_git_portable.ps1` (existing)
- Produces: `release/staging/ComfyUI Manager/bin/git-portable/cmd/git.exe` (bundled)

- [ ] **Step 1: Create build_staging.ps1**

Create `scripts/build_staging.ps1`:

```powershell
# scripts/build_staging.ps1
# Mirror build_release.ps1 steps 5–6 (git-portable bundle) for staging builds.
# 幂等: fetch_git_portable.ps1 已存在则 skip; Copy-Item -Force 覆盖。
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

- [ ] **Step 2: Run script — verify exit 0**

```bash
pwsh scripts/build_staging.ps1
```
Expected: 3 step output, exit 0, git.exe --version 2.55.0.x 输出。

- [ ] **Step 3: Verify bundled file**

```bash
ls "release/staging/ComfyUI Manager/bin/git-portable/cmd/git.exe"
```
Expected: file exists.

- [ ] **Step 4: Manual smoke (需要 Desktop session)**

Desktop 验证 step 1–3 见 spec Verification 节手工 smoke 5 step。

- [ ] **Step 5: Commit**

```bash
cd D:/ToolDevelop/ComfyUI
git add scripts/build_staging.ps1
git commit -m "feat(scripts): build_staging.ps1 bundles git-portable (v0.6.15.4 T5)

Mirror build_release.ps1 steps 5-6:
- step 1: fetch_git_portable (幂等)
- step 2: dotnet publish (跟现 staging 命令一致)
- step 3: Copy-Item bin/git-portable to staging output

用户换命令: 之前 dotnet publish ... → 现在 scripts/build_staging.ps1

Staging 不再需 system git; ResolveGitExe 找到 bin/git-portable/cmd/git.exe 直接用。

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

### Task 6: Final review + ship

- [ ] **Step 1: Run full test suite**

```bash
cd tests-wpf/ComfyUI.Manager.Tests && dotnet test --no-build 2>&1 | tail -5
```
Expected: 1165+ PASS / 0 FAIL / 1 SKIP (3 flaky `ProcessLauncherProgressTests` pre-existing, 不算我引入)。
**如果 FAIL > 3:** 单独 fix round。

- [ ] **Step 2: Build release**

```bash
cd D:/ToolDevelop/ComfyUI && dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj 2>&1 | tail -3
```
Expected: 0 errors.

- [ ] **Step 3: Rebuild staging via build_staging.ps1**

```bash
pwsh scripts/build_staging.ps1
```
Expected: exit 0, git.exe --version 2.55.0.x 输出。

- [ ] **Step 4: Update MEMORY.md**

Add to `MEMORY.md` a new bullet line (在 v0.6.15.3 line 之后):
```
- [v0.6.15.4 git-portable staging + HttpProxy 统一](project_v0_6_15_4_git_http_proxy.md) — 1 commit × 5 tasks, **1165+ PASS / 0 FAIL / 1 SKIP**;scripts/build_staging.ps1 镜像 build_release.ps1 steps 5–6 (fetch_git_portable + Copy-Item bin/git-portable → staging 产物);Settings.HttpProxy* 3 字段取代 GitProxy* + SchemaRepository 自动迁移 (git_proxy_* → http_proxy_*);HttpProxyConfig 单一类 ApplyTo(HttpClientHandler) + ApplyTo(ProcessStartInfo) 同时驱动 HTTP + git;6 caller rename (GitRunner / BulkUpdateOrchestrator / ComfyUIManagerInstaller / MainViewModel / App.xaml.cs / 6 test files);App.xaml.cs 加 internal BuildHttpClient test seam 测试 AppHttpProxyWiringTests 2 测试;SettingsView XAML "网络代理" section 取代 "git 代理" + SettingsViewModel 3 props;无 v-bump / 无 release zip;staging rebuilt self-contained 2026-08-15;**教训**: 旧→新 schema migration 走 JsonNode 而不是 reflection hacky 路径 (T2 step 3 误写反射 hack 后 reviewer 删)
```

- [ ] **Step 5: Write memory file** `project_v0_6_15_4_git_http_proxy.md` (key facts + GUI smoke 状态)

- [ ] **Step 6: Final commit**

```bash
cd D:/ToolDevelop/ComfyUI
git add .claude/projects/D--ToolDevelop-ComfyUI/memory/
git commit -m "docs(memory): v0.6.15.4 staging + HttpProxy 统一 ship-ready"

git status  # 应见 clean working tree
```

---

## Self-Review Checklist

- [x] **Spec coverage:** Section 1-7 of spec all mapped to T1-T5
- [x] **No placeholders:** No "TBD"/"TODO"/"implement later" in code blocks
- [x] **Type consistency:** `HttpProxyConfig` references match across all 5 tasks
- [x] **Test seam:** `App.BuildHttpClient` internal static matches `BuildPyTorchVersionDirectory` pattern
- [x] **Migration path:** T2 step 3 has explicit anti-pattern warning (reflection hack → JsonNode)
- [x] **Global constraints:** All referenced in T1-T5 task headers
- [x] **No backwards-compat shims:** Old `GitProxy*` fields/settings hard-removed; migration is one-shot
- [x] **5 commits + 1 final review:** T1-T5 each = 1 commit; T6 = final review
- [x] **Test seam permitted:** `App.BuildHttpClient` is `internal` (csproj `InternalsVisibleTo`)

## Spec → Task Map

| Spec Section | Task |
|--------------|------|
| §1 Git portable staging build | T5 |
| §2 HttpProxy fields + Settings UI | T2 (fields), T4 (UI) |
| §3 HttpProxyConfig class | T1 |
| §4 App.xaml.cs HttpClient | T3 |
| §5 GitRunner接 HttpProxyConfig | T3 |
| §6 Tests | T1 (8) + T2 (2) + T3 (2) + T4 (STA load) |
| §7 不动项 | 无 task (out of scope) |
| R1 迁移静默丢失 | T2 step 3 (`TryMigrateOldGitProxyKeys`) |
| R2 WinHTTP default | T3 step 5 (`BuildHttpClient` 显式 Proxy=null) |
| R4 caller 漏改 | T3 step 3 (sed + grep verify) |
| R5 staging fetch 慢 | T5 (fetch 幂等) |
| Verification §手动 smoke 5 step | T5 step 4 (桌面 5 step) |
| File Checklist (16 files) | T1-T5 all mapped |

# LTX-Video 2 模板替换 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把仓库内置的 `LTXVideo` 模板从 v1 (`Lightricks/LTX-Video`) 替换成 v2 (`Lightricks/LTX-2`),补齐 monorepo + uv 工具链 + 66 GiB HF gated 模型管理 + 无 web 端口的 CLI 启动流程。

**Architecture:** env-create 走 3 个新 step — 装 uv.exe 到 `env/tools/uv/`(新增)、`uv sync --extra natten` 替 `pip install`(改)、生成 `run-ltx2-*.bat` wrapper 走 `python -m ltx_pipelines.{distilled,dfr_pipeline}`(新增);启动检查 `env.ModelsDirectory/ltx-2.5/` 是否存在,缺失抛 `ModelsMissingException` → UI 弹提示。

**Tech Stack:** uv (Astral Rust 工具,Rust 写的、Windows 有官方 binary `uv-x86_64-pc-windows-msvc.zip`)、HF CLI(只文档化不自动化)、env.ModelsDirectory 已有的 SQLite 持久化字段、`uv sync` 替 pip install。

**Spec:** `docs/superpowers/specs/2026-08-30-ltx2-template-replace-design.md`

## Global Constraints

- **平台**: 仅 Windows(项目 `Platform=win32`,uv 用 `uv-x86_64-pc-windows-msvc.zip`)
- **Python 版本**: 跟随 env venv(3.10+ 由 LTX-2 `pyproject.toml` 锁 `requires-python = ">=3.10"`)
- **HF 模型**: 不存 token、不自动 auth;gated 条款用户自己 `hf auth login` 接受
- **uv 校验**: `env/tools/uv/uv.exe --version` exit 0 即可,**不 pin 版本号**(uv 升级快)
- **网络重试**: uv download 失败不自动重试;env-create 整体失败回退,用户重试整流程
- **i18n**: MessageBox / wrapper 文案走 `Resources.resx`(用户偏好 M1 i18n)
- **不破坏**: 其它 11 个内置模板(ComfyUI / Forge / HunyuanVideo / CogVideoX / Fooocus / OpenVoice / Whisper / CoquiTTS / Bark / HivisionIDPhotos)走老 `pip install -r requirements.txt` 流程不变
- **TDD**: 每个 task 都先写 failing test → run 验证 fail → 写最小实现 → run 验证 pass → commit。零跳过。
- **Commit 风格**: `feat(ltx2): ...` / `test(ltx2): ...` / `fix(ltx2): ...`,Co-Authored-By: Claude <noreply@anthropic.com>

---

## File Map

| 类型 | 路径 | 用途 |
|---|---|---|
| 改 | `src-wpf/ComfyUI.Manager/Services/TemplateConfigDefaults.cs:179-191` | 替换 LTXVideo 工厂 |
| 改 | `src-wpf/ComfyUI.Manager/Models/Environment.cs` | 加 `Ltx2RequiredModels` 派生属性 |
| 改 | `src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs` | `{models}` `{env}` 占位符 + LTX-2 模型检查 |
| 改 | `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs` | step 6.7 / 7.5 / 7.6 |
| 改 | `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` | 接 ModelsMissingException → MessageBox |
| 改 | `src-wpf/ComfyUI.Manager/Resources/Resources.resx` + `Resources.Designer.cs` | 加 "LTX-2 模型缺失" 多语言 string |
| 新增 | `src-wpf/ComfyUI.Manager/Models/ModelsMissingException.cs` | 异常类型 |
| 新增 | `src-wpf/ComfyUI.Manager/Services/UvInstaller.cs` | 下载 + 解压 uv |
| 新增 | `src-wpf/ComfyUI.Manager/Services/Ltx2WrapperGenerator.cs` | 写 2 个 wrapper .bat |
| 新增 | `tests-wpf/ComfyUI.Manager.Tests/Models/ModelsMissingExceptionTests.cs` | 异常构造 |
| 新增 | `tests-wpf/ComfyUI.Manager.Tests/Models/EnvironmentLtx2RequiredModelsTests.cs` | `Ltx2RequiredModels` 派生 |
| 新增 | `tests-wpf/ComfyUI.Manager.Tests/Services/UvInstallerTests.cs` | uv 下载 / 解压 / 重入 / 校验 |
| 新增 | `tests-wpf/ComfyUI.Manager.Tests/Services/Ltx2WrapperGeneratorTests.cs` | wrapper 内容 |
| 新增 | `tests-wpf/ComfyUI.Manager.Tests/Services/TemplateConfigDefaultsLtxVideoTests.cs` | 工厂字段 |
| 新增 | `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/ProcessLauncherLtx2ModelCheckTests.cs` | 占位符 + 模型检查 |
| 新增 | `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceLtx2StepsTests.cs` | step 6.7 / 7.5 / 7.6 |
| 新增 | `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelLtx2Tests.cs` | MessageBox 接住异常 |

---

## Task 1: 替换 `TemplateConfigDefaults.LTXVideo` 工厂

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/TemplateConfigDefaults.cs:179-191`
- Test: `tests-wpf/ComfyUI.Manager.Tests/Services/TemplateConfigDefaultsLtxVideoTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: 静态方法 `TemplateConfigDefaults.LTXVideo(string projectRoot)` 行为变更(其它 11 模板不变)

- [ ] **Step 1.1: 写 failing test**

```csharp
// tests-wpf/ComfyUI.Manager.Tests/Services/TemplateConfigDefaultsLtxVideoTests.cs
using System.IO;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public sealed class TemplateConfigDefaultsLtxVideoTests
{
    [Fact]
    public void LTXVideo_Name_IsLTXVideo2()
    {
        var cfg = TemplateConfigDefaults.LTXVideo("D:/proj");
        Assert.Equal("LTXVideo", cfg.Name);  // Name 保持不变(显示在 env list 不动)
    }

    [Fact]
    public void LTXVideo_Kind_IsLTXVideo()
    {
        var cfg = TemplateConfigDefaults.LTXVideo("D:/proj");
        Assert.Equal("LTXVideo", cfg.Kind);
    }

    [Fact]
    public void LTXVideo_GitHubRepoUrl_IsLTX2()
    {
        var cfg = TemplateConfigDefaults.LTXVideo("D:/proj");
        Assert.Equal("https://github.com/Lightricks/LTX-2.git", cfg.GitHubRepoUrl);
    }

    [Fact]
    public void LTXVideo_EntryScript_IsWrapperBat()
    {
        var cfg = TemplateConfigDefaults.LTXVideo("D:/proj");
        Assert.Equal("run-ltx2-distilled.bat", cfg.EntryScript);
    }

    [Fact]
    public void LTXVideo_EntryArgs_ContainsModelsPlaceholder()
    {
        var cfg = TemplateConfigDefaults.LTXVideo("D:/proj");
        Assert.Contains("{models}", cfg.EntryArgs);
        Assert.Contains("{env}", cfg.EntryArgs);
        Assert.DoesNotContain("{port}", cfg.EntryArgs);   // CLI 模式无 web 端口
    }

    [Fact]
    public void LTXVideo_ModelsSubdir_IsModels_Ltx25()
    {
        var cfg = TemplateConfigDefaults.LTXVideo("D:/proj");
        Assert.Equal("Models/ltx-2.5", cfg.ModelsSubdir);
    }

    [Fact]
    public void LTXVideo_LocalSourceDir_IsLTX_Video()
    {
        var cfg = TemplateConfigDefaults.LTXVideo("D:/proj");
        Assert.Equal("LTX-Video", cfg.LocalSourceDir);
    }
}
```

- [ ] **Step 1.2: 跑测试,确认 4 个失败(只 Name/Kind/LocalSourceDir 这 3 个仍过;其它 4 个新断言全失败)**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "TemplateConfigDefaultsLtxVideoTests" -v minimal`
Expected: 4 FAIL (GitHubRepoUrl / EntryScript / EntryArgs / ModelsSubdir);3 PASS (Name / Kind / LocalSourceDir)

- [ ] **Step 1.3: 改 TemplateConfigDefaults.cs:179-191**

```csharp
    public static TemplateConfig LTXVideo(string projectRoot) => new()
    {
        Name = "LTXVideo",
        Kind = "LTXVideo",
        LocalSourceDir = "LTX-Video",
        SourceKind = TemplateSourceKind.GitHub,
        GitHubRepoUrl = "https://github.com/Lightricks/LTX-2.git",
        EntryScript = "run-ltx2-distilled.bat",
        EntryArgs =
            "--transformer-path {models}/ltx-2.5/diffusion_models/ltx-2.5-22b-distilled-transformer-bf16.safetensors " +
            "--text-encoder-path {models}/ltx-2.5/text_encoders/gemma4-12b-with-proj-ltx-2.5-bf16.safetensors " +
            "--video-vae-path {models}/ltx-2.5/vae/ltx-2.5-video-vae-bf16.safetensors " +
            "--audio-vae-path {models}/ltx-2.5/vae/ltx-2.5-audio-vae-bf16.safetensors " +
            "--spatial-upsampler-path {models}/ltx-2.5/latent_upscale_models/ltx-2.5-latent-spatial-upscaler-x2-bf16-1.0.safetensors " +
            "--num-frames 121 --seed 42 --output-path {env}/outputs/output.mp4",
        ModelsSubdir = "Models/ltx-2.5",
        ExtraJunctionTargets = new(),
        UserExtraArgs = "",
    };
```

- [ ] **Step 1.4: 跑测试确认全过**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "TemplateConfigDefaultsLtxVideoTests" -v minimal`
Expected: 7 PASS / 0 FAIL

- [ ] **Step 1.5: Commit**

```bash
git -C "D:/ToolDevelop/ComfyUI" add src-wpf/ComfyUI.Manager/Services/TemplateConfigDefaults.cs tests-wpf/ComfyUI.Manager.Tests/Services/TemplateConfigDefaultsLtxVideoTests.cs
git -C "D:/ToolDevelop/ComfyUI" commit -m "feat(ltx2): 替换 LTXVideo 模板到 LTX-2 + 7 测试

- GitHubRepoUrl -> Lightricks/LTX-2
- EntryScript -> run-ltx2-distilled.bat (wrapper,env-create 生成)
- EntryArgs 长串带 {models} / {env} 占位符,无 {port}(CLI 模式)
- ModelsSubdir -> Models/ltx-2.5 (env.ModelsDirectory/ltx-2.5)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 2: 加 `ModelsMissingException` 类型

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Models/ModelsMissingException.cs`
- Test: `tests-wpf/ComfyUI.Manager.Tests/Models/ModelsMissingExceptionTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: `ModelsMissingException : Exception` 带 `IReadOnlyList<string> MissingPaths` + `string HuggingFaceRepoUrl` + `string DownloadCommand`

- [ ] **Step 2.1: 写 failing test**

```csharp
// tests-wpf/ComfyUI.Manager.Tests/Models/ModelsMissingExceptionTests.cs
using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public sealed class ModelsMissingExceptionTests
{
    [Fact]
    public void Ctor_StoresFields()
    {
        var missing = new List<string> { "/a/b/transformer.safetensors", "/a/b/vae.safetensors" };
        var ex = new ModelsMissingException(
            "缺少 LTX-2 模型文件",
            missing,
            "https://huggingface.co/Lightricks/LTX-2.5",
            "hf download Lightricks/LTX-2.5 --local-dir models/ltx-2.5");

        Assert.Equal("缺少 LTX-2 模型文件", ex.Message);
        Assert.Equal(2, ex.MissingPaths.Count);
        Assert.Equal("/a/b/transformer.safetensors", ex.MissingPaths[0]);
        Assert.Equal("https://huggingface.co/Lightricks/LTX-2.5", ex.HuggingFaceRepoUrl);
        Assert.Contains("hf download", ex.DownloadCommand);
    }

    [Fact]
    public void MissingPaths_IsReadOnly()
    {
        var ex = new ModelsMissingException("msg", new List<string>(), "url", "cmd");
        Assert.IsAssignableFrom<IReadOnlyList<string>>(ex.MissingPaths);
    }

    [Fact]
    public void MissingPaths_Empty_StillConstructable()
    {
        var ex = new ModelsMissingException("msg", new List<string>(), "url", "cmd");
        Assert.Empty(ex.MissingPaths);
    }
}
```

- [ ] **Step 2.2: 跑测试确认 fail(类型不存在)**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "ModelsMissingExceptionTests" -v minimal`
Expected: 3 FAIL (CS0246: type or namespace not found)

- [ ] **Step 2.3: 创建异常类**

```csharp
// src-wpf/ComfyUI.Manager/Models/ModelsMissingException.cs
using System;
using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v1.0.0.x (2026-08-30):env 启动前置检查抛 — 缺失 LTX-2 模型文件。
/// UI 层接住后弹 MessageBox,展示 HuggingFace repo URL + 完整 hf download 命令,
/// 让用户手动 hf auth login + 下载后重启 env。
/// 不自动下载:gated 模型 + 66 GiB,需要用户接受条款 + Read token。
/// </summary>
public sealed class ModelsMissingException : Exception
{
    /// <summary>缺失的 .safetensors 绝对路径列表(空表示检查通过但调用方仍要求抛 — 不会发生)。</summary>
    public IReadOnlyList<string> MissingPaths { get; }

    /// <summary>HuggingFace repo URL,UI 弹窗展示给用户。</summary>
    public string HuggingFaceRepoUrl { get; }

    /// <summary>完整 hf download 命令(LTX-2 5 个模型文件),用户复制粘贴执行。</summary>
    public string DownloadCommand { get; }

    public ModelsMissingException(
        string message,
        IReadOnlyList<string> missingPaths,
        string huggingFaceRepoUrl,
        string downloadCommand)
        : base(message)
    {
        MissingPaths = missingPaths ?? new List<string>();
        HuggingFaceRepoUrl = huggingFaceRepoUrl ?? "";
        DownloadCommand = downloadCommand ?? "";
    }
}
```

- [ ] **Step 2.4: 跑测试确认全过**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "ModelsMissingExceptionTests" -v minimal`
Expected: 3 PASS / 0 FAIL

- [ ] **Step 2.5: Commit**

```bash
git -C "D:/ToolDevelop/ComfyUI" add src-wpf/ComfyUI.Manager/Models/ModelsMissingException.cs tests-wpf/ComfyUI.Manager.Tests/Models/ModelsMissingExceptionTests.cs
git -C "D:/ToolDevelop/ComfyUI" commit -m "feat(ltx2): ModelsMissingException 类型 + 3 测试

启动前检查缺失模型抛此异常,UI 接住弹 MessageBox 带 HF repo URL + 下载命令。

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 3: 加 `Environment.Ltx2RequiredModels` 派生属性

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Models/Environment.cs`(append after `BedDisplayId`)
- Test: `tests-wpf/ComfyUI.Manager.Tests/Models/EnvironmentLtx2RequiredModelsTests.cs`

**Interfaces:**
- Consumes: `Environment.ModelsDirectory`、`Environment.TemplateKind`
- Produces: `IReadOnlyList<string> Ltx2RequiredModels` — 在 `ModelsDirectory/ltx-2.5/` 下 5 个必需 .safetensors 绝对路径,TemplateKind != "LTXVideo" 返回空

- [ ] **Step 3.1: 写 failing test**

```csharp
// tests-wpf/ComfyUI.Manager.Tests/Models/EnvironmentLtx2RequiredModelsTests.cs
using System.IO;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public sealed class EnvironmentLtx2RequiredModelsTests
{
    [Fact]
    public void Ltx2RequiredModels_LTXVideo_Returns5AbsolutePaths()
    {
        var env = new Environment
        {
            TemplateKind = "LTXVideo",
            ModelsDirectory = "D:/models",
        };
        var paths = env.Ltx2RequiredModels;
        Assert.Equal(5, paths.Count);
        foreach (var p in paths)
        {
            Assert.StartsWith("D:\\models\\ltx-2.5\\", p);
            Assert.EndsWith(".safetensors", p);
        }
    }

    [Fact]
    public void Ltx2RequiredModels_NonLTXVideo_ReturnsEmpty()
    {
        var env = new Environment { TemplateKind = "ComfyUI", ModelsDirectory = "D:/models" };
        Assert.Empty(env.Ltx2RequiredModels);

        var env2 = new Environment { TemplateKind = "Forge", ModelsDirectory = "D:/models" };
        Assert.Empty(env2.Ltx2RequiredModels);
    }

    [Fact]
    public void Ltx2RequiredModels_LTXVideo_NamesMatch_HFQuickStart()
    {
        // https://huggingface.co/Lightricks/LTX-2.5 quick start 命令列出的 5 个模型
        var env = new Environment { TemplateKind = "LTXVideo", ModelsDirectory = "M" };
        var paths = env.Ltx2RequiredModels;
        Assert.Contains(paths, p => p.Contains("diffusion_models") && p.Contains("22b-distilled-transformer"));
        Assert.Contains(paths, p => p.Contains("text_encoders") && p.Contains("gemma4-12b-with-proj"));
        Assert.Contains(paths, p => p.Contains("video-vae"));
        Assert.Contains(paths, p => p.Contains("audio-vae"));
        Assert.Contains(paths, p => p.Contains("latent_upscale_models") && p.Contains("latent-spatial-upscaler"));
    }

    [Fact]
    public void Ltx2RequiredModels_EmptyModelsDirectory_ReturnsEmpty()
    {
        var env = new Environment { TemplateKind = "LTXVideo", ModelsDirectory = "" };
        Assert.Empty(env.Ltx2RequiredModels);
    }
}
```

- [ ] **Step 3.2: 跑测试确认 fail(属性不存在)**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "EnvironmentLtx2RequiredModelsTests" -v minimal`
Expected: 4 FAIL (CS1061: 'Environment' does not contain a definition for 'Ltx2RequiredModels')

- [ ] **Step 3.3: 在 Environment.cs 末尾(`HasFailedNodes` 之后)加派生属性**

```csharp
    /// <summary>
    /// v1.0.0.x (2026-08-30):LTX-2 env 启动前必检的 5 个 .safetensors 绝对路径 —
    /// HF repo <c>Lightricks/LTX-2.5</c> quick start 命令列的 distilled transformer +
    /// gemma4-12b 文本编码器 + video VAE + audio VAE + spatial upsampler。
    /// 路径约定 <c>&lt;env.ModelsDirectory&gt;/ltx-2.5/&lt;HF 子目录&gt;/&lt;model&gt;.safetensors</c>
    /// 跟 <c>hf download --local-dir &lt;ModelsDirectory&gt;</c> 一致(env.ModelsDirectory
    /// 已有 SQLite 持久化字段)。
    /// 非 LTXVideo kind / ModelsDirectory 空 → 返空(其它模板不强制)。
    /// ProcessLauncher.StartEnvAsync 跑前检查 — 缺失抛 <see cref="ModelsMissingException"/>
    /// → UI MessageBox。
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> Ltx2RequiredModels
    {
        get
        {
            if (TemplateKind != "LTXVideo") return Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(ModelsDirectory)) return Array.Empty<string>();
            var root = Path.Combine(ModelsDirectory, "ltx-2.5");
            return new[]
            {
                Path.Combine(root, "diffusion_models", "ltx-2.5-22b-distilled-transformer-bf16.safetensors"),
                Path.Combine(root, "text_encoders", "gemma4-12b-with-proj-ltx-2.5-bf16.safetensors"),
                Path.Combine(root, "vae", "ltx-2.5-video-vae-bf16.safetensors"),
                Path.Combine(root, "vae", "ltx-2.5-audio-vae-bf16.safetensors"),
                Path.Combine(root, "latent_upscale_models", "ltx-2.5-latent-spatial-upscaler-x2-bf16-1.0.safetensors"),
            };
        }
    }
```

(`ModelsDirectory` 字段如不存在,需要先在 Environment.cs 加 `public string ModelsDirectory { get; set; } = "";`)

- [ ] **Step 3.4: 跑测试确认全过**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "EnvironmentLtx2RequiredModelsTests" -v minimal`
Expected: 4 PASS / 0 FAIL

- [ ] **Step 3.5: Commit**

```bash
git -C "D:/ToolDevelop/ComfyUI" add src-wpf/ComfyUI.Manager/Models/Environment.cs tests-wpf/ComfyUI.Manager.Tests/Models/EnvironmentLtx2RequiredModelsTests.cs
git -C "D:/ToolDevelop/ComfyUI" commit -m "feat(ltx2): Environment.Ltx2RequiredModels 派生 + 4 测试

返 5 个 .safetensors 绝对路径,TemplateKind != LTXVideo 或 ModelsDirectory 空返空。

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 4: `UvInstaller` 服务(下载 + 解压 uv 到 env/tools/uv/)

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/UvInstaller.cs`
- Test: `tests-wpf/ComfyUI.Manager.Tests/Services/UvInstallerTests.cs`

**Interfaces:**
- Consumes: `string envRootPath`、`IHttpClientFactory` 注入(测试可注入 fake)、`CancellationToken`
- Produces: `Task<string>` 返回 uv.exe 绝对路径(已存在跳过下载也返回此路径)
- 测试可注入 `_downloader: Func<Uri, string, CancellationToken, Task<byte[]>>` 避免真实网络

- [ ] **Step 4.1: 写 failing test**

```csharp
// tests-wpf/ComfyUI.Manager.Tests/Services/UvInstallerTests.cs
using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public sealed class UvInstallerTests : IDisposable
{
    private readonly string _envRoot;

    public UvInstallerTests()
    {
        _envRoot = Path.Combine(Path.GetTempPath(),
            "uvinstaller-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_envRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_envRoot, recursive: true); } catch { }
    }

    private static byte[] FakeUvZip()
    {
        // zip 内有 uv.exe 字节 "fake-uv-binary"
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("uv.exe");
            using var es = entry.Open();
            var bytes = System.Text.Encoding.UTF8.GetBytes("fake-uv-binary");
            es.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    private static (byte[] stdout, int exitCode) FakeUvVersionOk()
        => (System.Text.Encoding.UTF8.GetBytes("uv 0.5.0\n"), 0);

    private UvInstaller MakeInstaller(byte[] zipBytes, (byte[] stdout, int exit)? versionProbe = null)
    {
        var versionProbe1 = versionProbe ?? FakeUvVersionOk();
        return new UvInstaller(
            envRoot: _envRoot,
            downloader: (_, _, _) => Task.FromResult(zipBytes),
            versionProber: (_, _) =>
            {
                var (stdout, exit) = versionProbe1;
                return Task.FromResult((stdout, exit));
            });
    }

    [Fact]
    public async Task InstallAsync_DownloadsAndExtracts_ReturnsExePath()
    {
        var installer = MakeInstaller(FakeUvZip());
        var exePath = await installer.InstallAsync(CancellationToken.None);

        Assert.True(File.Exists(exePath));
        Assert.EndsWith("uv.exe", exePath);
        Assert.Equal("fake-uv-binary", await File.ReadAllTextAsync(exePath));
    }

    [Fact]
    public async Task InstallAsync_AlreadyInstalled_SkipsDownload()
    {
        var installer = MakeInstaller(FakeUvZip());
        // 第一次安装
        var firstPath = await installer.InstallAsync(CancellationToken.None);
        // 第二次:downloader 改成抛异常的,验证没被调用
        var secondInstaller = new UvInstaller(
            envRoot: _envRoot,
            downloader: (_, _, _) => throw new InvalidOperationException("should not download"),
            versionProber: (_, _) => Task.FromResult(FakeUvVersionOk()));
        var secondPath = await secondInstaller.InstallAsync(CancellationToken.None);

        Assert.Equal(firstPath, secondPath);
    }

    [Fact]
    public async Task InstallAsync_VersionProbeFails_Throws()
    {
        var installer = MakeInstaller(FakeUvZip(),
            versionProbe: (Array.Empty<byte>(), 1));  // exit 1 = 失败
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => installer.InstallAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InstallAsync_EmptyDownload_Throws()
    {
        var installer = MakeInstaller(Array.Empty<byte>());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => installer.InstallAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InstallAsync_ZeroLengthFile_NotCountedAsInstalled()
    {
        // 模拟 uv.exe 存在但 0 字节(损坏)→ 必须重下
        var uvDir = Path.Combine(_envRoot, "tools", "uv");
        Directory.CreateDirectory(uvDir);
        var exePath = Path.Combine(uvDir, "uv.exe");
        await File.WriteAllBytesAsync(exePath, Array.Empty<byte>());

        var installer = MakeInstaller(FakeUvZip());
        var result = await installer.InstallAsync(CancellationToken.None);
        Assert.True(File.Exists(result));
        Assert.NotEqual(0, new FileInfo(result).Length);
    }
}
```

- [ ] **Step 4.2: 跑测试确认 fail(类不存在)**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "UvInstallerTests" -v minimal`
Expected: 5 FAIL (CS0246)

- [ ] **Step 4.3: 创建 UvInstaller.cs**

```csharp
// src-wpf/ComfyUI.Manager/Services/UvInstaller.cs
using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x (2026-08-30):env-create step 6.7 — 下载 + 解压 Astral uv 到
/// <c>&lt;env&gt;/tools/uv/uv.exe</c>。
///
/// uv 是 Lightricks/LTX-2 monorepo 安装前置(<c>uv sync --extra natten</c>),
/// 项目从未用过 uv。装到 env 内部(不进 PATH)→ 用户机器 / 项目搬家都能用。
/// 下载源 <c>https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip</c>。
///
/// 测试可注入 downloader / versionProber;默认实现走 HttpClient。
/// </summary>
public sealed class UvInstaller
{
    public const string DownloadUrl =
        "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip";

    private readonly string _envRoot;
    private readonly Func<Uri, string?, CancellationToken, Task<byte[]>> _downloader;
    private readonly Func<string, CancellationToken, Task<(byte[] Stdout, int ExitCode)>> _versionProber;
    private readonly string _uvExePath;

    public UvInstaller(
        string envRoot,
        Func<Uri, string?, CancellationToken, Task<byte[]>>? downloader = null,
        Func<string, CancellationToken, Task<(byte[] Stdout, int ExitCode)>>? versionProber = null,
        HttpClient? httpClient = null)
    {
        _envRoot = envRoot ?? throw new ArgumentNullException(nameof(envRoot));
        var uvDir = Path.Combine(_envRoot, "tools", "uv");
        _uvExePath = Path.Combine(uvDir, "uv.exe");

        if (downloader is not null)
        {
            _downloader = downloader;
        }
        else
        {
            var client = httpClient ?? new HttpClient();
            _downloader = async (uri, _, ct) =>
            {
                using var resp = await client.GetAsync(uri, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            };
        }

        if (versionProber is not null)
        {
            _versionProber = versionProber;
        }
        else
        {
            _versionProber = async (exe, ct) =>
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = System.Diagnostics.Process.Start(psi)!;
                var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                var stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                var combined = string.IsNullOrEmpty(stdout) ? stderr : stdout;
                return (System.Text.Encoding.UTF8.GetBytes(combined), p.ExitCode);
            };
        }
    }

    /// <summary>
    /// 下载 + 解压 uv → <c>env/tools/uv/uv.exe</c>,然后跑 <c>--version</c> 校验。
    /// 已存在 + 文件非 0 字节 + 校验成功 → 直接返路径(不重下)。
    /// </summary>
    /// <returns>uv.exe 绝对路径。</returns>
    /// <exception cref="InvalidOperationException">下载 / 解压 / 校验失败时 throw。</exception>
    public async Task<string> InstallAsync(CancellationToken ct = default)
    {
        if (IsAlreadyInstalled())
        {
            return _uvExePath;
        }

        // 重下前清理残缺文件
        try { File.Delete(_uvExePath); } catch { }
        var uvDir = Path.GetDirectoryName(_uvExePath)!;
        Directory.CreateDirectory(uvDir);

        var bytes = await _downloader(new Uri(DownloadUrl), null, ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0)
            throw new InvalidOperationException($"uv 下载内容为空({DownloadUrl})");

        using var zipStream = new MemoryStream(bytes);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = archive.GetEntry("uv.exe");
        if (entry is null)
            throw new InvalidOperationException($"uv.zip 内找不到 uv.exe entry");

        entry.ExtractToFile(_uvExePath, overwrite: true);

        var (stdout, exit) = await _versionProber(_uvExePath, ct).ConfigureAwait(false);
        if (exit != 0)
        {
            try { File.Delete(_uvExePath); } catch { }
            throw new InvalidOperationException(
                $"uv --version 校验失败(exit={exit}): {System.Text.Encoding.UTF8.GetString(stdout)}");
        }
        return _uvExePath;
    }

    private bool IsAlreadyInstalled()
    {
        return File.Exists(_uvExePath) && new FileInfo(_uvExePath).Length > 0;
    }
}
```

- [ ] **Step 4.4: 跑测试确认全过**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "UvInstallerTests" -v minimal`
Expected: 5 PASS / 0 FAIL

- [ ] **Step 4.5: Commit**

```bash
git -C "D:/ToolDevelop/ComfyUI" add src-wpf/ComfyUI.Manager/Services/UvInstaller.cs tests-wpf/ComfyUI.Manager.Tests/Services/UvInstallerTests.cs
git -C "D:/ToolDevelop/ComfyUI" commit -m "feat(ltx2): UvInstaller 服务(env-create step 6.7) + 5 测试

下载 uv-x86_64-pc-windows-msvc.zip -> env/tools/uv/uv.exe + --version 校验。
已存在跳过重下;0 字节文件判定损坏自动重下。

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 5: `Ltx2WrapperGenerator` 服务(写 wrapper .bat)

**Files:**
- Create: `src-wpf/ComfyUI.Manager/Services/Ltx2WrapperGenerator.cs`
- Test: `tests-wpf/ComfyUI.Manager.Tests/Services/Ltx2WrapperGeneratorTests.cs`

**Interfaces:**
- Consumes: `string envRootPath`
- Produces: `Task` 生成 `<envRoot>/run-ltx2-distilled.bat` + `<envRoot>/run-ltx2-dfr.bat`

- [ ] **Step 5.1: 写 failing test**

```csharp
// tests-wpf/ComfyUI.Manager.Tests/Services/Ltx2WrapperGeneratorTests.cs
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public sealed class Ltx2WrapperGeneratorTests : IDisposable
{
    private readonly string _envRoot;

    public Ltx2WrapperGeneratorTests()
    {
        _envRoot = Path.Combine(Path.GetTempPath(),
            "ltx2wrapper-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_envRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_envRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task GenerateAsync_CreatesTwoWrapperBats()
    {
        var gen = new Ltx2WrapperGenerator(_envRoot);
        await gen.GenerateAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_envRoot, "run-ltx2-distilled.bat")));
        Assert.True(File.Exists(Path.Combine(_envRoot, "run-ltx2-dfr.bat")));
    }

    [Fact]
    public async Task GenerateAsync_Distilled_BatContent_UsesDp0AndLtxPipelinesDistilled()
    {
        var gen = new Ltx2WrapperGenerator(_envRoot);
        await gen.GenerateAsync(CancellationToken.None);

        var content = await File.ReadAllTextAsync(Path.Combine(_envRoot, "run-ltx2-distilled.bat"));
        Assert.Contains("%~dp0tools\\uv\\uv.exe", content);
        Assert.Contains("uv run python -m ltx_pipelines.distilled", content);
        Assert.Contains("%*", content);   // 透传参数
    }

    [Fact]
    public async Task GenerateAsync_Dfr_BatContent_UsesDfrPipeline()
    {
        var gen = new Ltx2WrapperGenerator(_envRoot);
        await gen.GenerateAsync(CancellationToken.None);

        var content = await File.ReadAllTextAsync(Path.Combine(_envRoot, "run-ltx2-dfr.bat"));
        Assert.Contains("%~dp0tools\\uv\\uv.exe", content);
        Assert.Contains("uv run python -m ltx_pipelines.dfr_pipeline", content);
    }

    [Fact]
    public async Task GenerateAsync_Idempotent_OverwritesCleanly()
    {
        var gen = new Ltx2WrapperGenerator(_envRoot);
        await gen.GenerateAsync(CancellationToken.None);
        // 第一次写完留个垃圾文件
        var strayBat = Path.Combine(_envRoot, "run-ltx2-distilled.bat.bak");
        await File.WriteAllTextAsync(strayBat, "leftover");
        // 再跑一次不应删 stray
        await gen.GenerateAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_envRoot, "run-ltx2-distilled.bat")));
        Assert.True(File.Exists(strayBat));   // 不删无关文件
    }
}
```

- [ ] **Step 5.2: 跑测试确认 fail(类不存在)**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "Ltx2WrapperGeneratorTests" -v minimal`
Expected: 4 FAIL (CS0246)

- [ ] **Step 5.3: 创建 Ltx2WrapperGenerator.cs**

```csharp
// src-wpf/ComfyUI.Manager/Services/Ltx2WrapperGenerator.cs
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x (2026-08-30):env-create step 7.6 — 写 LTX-2 wrapper .bat。
/// ProcessLauncher 不动;EntryScript 直接指向 wrapper,uv 路径用 <c>%~dp0</c> 相对解析
/// → env 可搬到任意机器 + env 改路径不需要重新生成 wrapper。
///
/// 生成两个 wrapper:
/// - <c>run-ltx2-distilled.bat</c> → <c>python -m ltx_pipelines.distilled</c>(quick start 默认)
/// - <c>run-ltx2-dfr.bat</c> → <c>python -m ltx_pipelines.dfr_pipeline</c>(生产质量)
/// </summary>
public sealed class Ltx2WrapperGenerator
{
    private const string WrapperTemplate = @"@echo off
""%~dp0tools\uv\uv.exe"" run python -m {0} %*
";

    private readonly string _envRoot;

    public Ltx2WrapperGenerator(string envRoot)
    {
        _envRoot = envRoot ?? throw new ArgumentNullException(nameof(envRoot));
    }

    public async Task GenerateAsync(CancellationToken ct = default)
    {
        await WriteWrapperAsync("run-ltx2-distilled.bat", "ltx_pipelines.distilled", ct).ConfigureAwait(false);
        await WriteWrapperAsync("run-ltx2-dfr.bat", "ltx_pipelines.dfr_pipeline", ct).ConfigureAwait(false);
    }

    private async Task WriteWrapperAsync(string fileName, string modulePath, CancellationToken ct)
    {
        var path = Path.Combine(_envRoot, fileName);
        var content = string.Format(WrapperTemplate, modulePath);
        // ASCII content,但 UTF-8 no BOM 写 bat 在 Windows 下兼容 cmd.exe / Explorer 都 OK
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct)
            .ConfigureAwait(false);
    }
}
```

- [ ] **Step 5.4: 跑测试确认全过**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "Ltx2WrapperGeneratorTests" -v minimal`
Expected: 4 PASS / 0 FAIL

- [ ] **Step 5.5: Commit**

```bash
git -C "D:/ToolDevelop/ComfyUI" add src-wpf/ComfyUI.Manager/Services/Ltx2WrapperGenerator.cs tests-wpf/ComfyUI.Manager.Tests/Services/Ltx2WrapperGeneratorTests.cs
git -C "D:/ToolDevelop/ComfyUI" commit -m "feat(ltx2): Ltx2WrapperGenerator 服务(step 7.6) + 4 测试

env-create 末尾生成 run-ltx2-distilled.bat / run-ltx2-dfr.bat wrapper,
%~dp0tools\uv\uv.exe run python -m ltx_pipelines.{distilled,dfr_pipeline} %*。
env 可搬、Path 无关。

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 6: `EnvCreatorService` step 6.7 / 7.5 / 7.6 LTX-2 集成

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs`
- Test: `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceLtx2StepsTests.cs`

**Interfaces:**
- Consumes: `TemplateConfig.Kind == "LTXVideo"` 触发条件;构造函数加 `uvInstallerFactory` + `wrapperGeneratorFactory` 参数(默认 null = 用真实实现,测试可注入 fake)
- Produces: 在 `CreateAsync` 流程中:
  - step 6.7(venv 创建后、wheel seed 前):调 `UvInstaller.InstallAsync(env.RootPath)`(仅 LTXVideo)
  - step 7.5(原 requirements 装包位置):LTXVideo 改调 `env/tools/uv/uv.exe sync --extra natten`;其它模板走老 pip 流程
  - step 7.6(SQLite 写入后):调 `Ltx2WrapperGenerator.GenerateAsync(env.RootPath)`(仅 LTXVideo)

- [ ] **Step 6.1: 写 failing test**

```csharp
// tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceLtx2StepsTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public sealed class EnvCreatorServiceLtx2StepsTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly EnvironmentRepository _repo;

    public EnvCreatorServiceLtx2StepsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "envcreator-ltx2-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _factory = new SqliteConnectionFactory(Path.Combine(_root, "state.db"));
        _repo = new EnvironmentRepository(_factory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static (RecordingUvInstaller installer, RecordingWrapperGenerator wrapper) MakeRecorder()
        => (new RecordingUvInstaller(), new RecordingWrapperGenerator());

    private ComfyUI.Manager.Models.Settings MakeSettings(string projectRoot)
    {
        var s = new ComfyUI.Manager.Models.Settings
        {
            EnvsDir = "envs",
            TemplatePythonDir = "python",
            DefaultPythonVersion = "3.10",
            DefaultModelsDirectory = Path.Combine(projectRoot, "Models"),
        };
        return s;
    }

    private TemplateConfig LtxVideoTemplate(string projectRoot) => new()
    {
        Kind = "LTXVideo",
        Name = "LTXVideo",
        LocalSourceDir = "LTX-Video",
        SourceKind = TemplateSourceKind.GitHub,
        GitHubRepoUrl = "https://github.com/Lightricks/LTX-2.git",
        EntryScript = "run-ltx2-distilled.bat",
        EntryArgs = "--output-path {env}/out.mp4",
        ModelsSubdir = "Models/ltx-2.5",
    };

    [Fact]
    public async Task CreateAsync_LTXVideo_CallsUvInstaller()
    {
        var (uv, wrapper) = MakeRecorder();
        // clone step 需要源存在,先建一个 fake 源
        var srcDir = Path.Combine(_root, "LTX-Video");
        Directory.CreateDirectory(srcDir);
        var settings = MakeSettings(_root);
        settings.Templates["LTXVideo"] = LtxVideoTemplate(_root);
        // 把 fake 源放在 projectRoot 下当 LocalSourceDir
        settings.LocalDataDir = _root;

        var svc = new EnvCreatorService(
            _factory,
            new FakeVenvCreator(),
            new FakeJunctionLinker(),
            settings,
            projectRoot: _root,
            uvInstallerFactory: _ => uv,
            wrapperGeneratorFactory: _ => wrapper,
            gitCloneAsync: (_, _, _, _) => Task.CompletedTask);  // 跳过真实 git

        await svc.CreateAsync("ltx-env", LtxVideoTemplate(_root), notes: null,
            sourceOverride: srcDir, CancellationToken.None);

        Assert.Equal(1, uv.CallCount);
        Assert.Equal(Path.Combine(_root, "envs", "ltx-env"), uv.LastEnvRoot);
    }

    [Fact]
    public async Task CreateAsync_LTXVideo_CallsWrapperGenerator()
    {
        var (uv, wrapper) = MakeRecorder();
        var srcDir = Path.Combine(_root, "LTX-Video");
        Directory.CreateDirectory(srcDir);
        var settings = MakeSettings(_root);
        settings.Templates["LTXVideo"] = LtxVideoTemplate(_root);

        var svc = new EnvCreatorService(
            _factory, new FakeVenvCreator(), new FakeJunctionLinker(),
            settings, _root,
            uvInstallerFactory: _ => uv,
            wrapperGeneratorFactory: _ => wrapper,
            gitCloneAsync: (_, _, _, _) => Task.CompletedTask);

        await svc.CreateAsync("ltx-env", LtxVideoTemplate(_root), notes: null,
            sourceOverride: srcDir, CancellationToken.None);

        Assert.Equal(1, wrapper.CallCount);
    }

    [Fact]
    public async Task CreateAsync_ComfyUI_SkipsUvInstaller()
    {
        var (uv, wrapper) = MakeRecorder();
        var srcDir = Path.Combine(_root, "ComfyUI");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "main.py"), "# fake");
        File.WriteAllText(Path.Combine(srcDir, "requirements.txt"), "# empty");
        var settings = MakeSettings(_root);
        settings.Templates["ComfyUI"] = new TemplateConfig
        {
            Kind = "ComfyUI", Name = "ComfyUI",
            LocalSourceDir = "ComfyUI",
            SourceKind = TemplateSourceKind.GitHub,
            GitHubRepoUrl = "https://github.com/comfyanonymous/ComfyUI.git",
            EntryScript = "main.py", EntryArgs = "--port {port} --listen 0.0.0.0",
            ModelsSubdir = "models",
        };

        var svc = new EnvCreatorService(
            _factory, new FakeVenvCreator(), new FakeJunctionLinker(),
            settings, _root,
            uvInstallerFactory: _ => uv,
            wrapperGeneratorFactory: _ => wrapper,
            gitCloneAsync: (_, _, _, _) => Task.CompletedTask,
            pipInstallRequirementsAsync: (_, _, _) => Task.CompletedTask);

        await svc.CreateAsync("cui-env", settings.Templates["ComfyUI"], notes: null,
            sourceOverride: srcDir, CancellationToken.None);

        Assert.Equal(0, uv.CallCount);
        Assert.Equal(0, wrapper.CallCount);
    }
}

internal sealed class RecordingUvInstaller
{
    public int CallCount { get; private set; }
    public string? LastEnvRoot { get; private set; }
    public Task<string> InstallAsync(CancellationToken ct = default)
    {
        CallCount++;
        LastEnvRoot = "<recorded>";
        return Task.FromResult(Path.Combine(LastEnvRoot, "tools", "uv", "uv.exe"));
    }
}

internal sealed class RecordingWrapperGenerator
{
    public int CallCount { get; private set; }
    public Task GenerateAsync(CancellationToken ct = default)
    {
        CallCount++;
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 6.2: 跑测试确认 fail(构造重载不存在 / 接口不匹配)**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "EnvCreatorServiceLtx2StepsTests" -v minimal`
Expected: 3 FAIL(CS1739 / CS0411 — 构造参数不匹配)

- [ ] **Step 6.3: 改 EnvCreatorService.cs**

加构造参数(`Func<string, IUvInstaller>? uvInstallerFactory = null`、`Func<string, ILtx2WrapperGenerator>? wrapperGeneratorFactory = null`、`Func<string, string, string, CancellationToken, Task>? gitCloneAsync = null`、`Func<string, string, CancellationToken, Task>? pipInstallRequirementsAsync = null`),加对应私有字段,在 `CreateAsync` 流程插入 3 个 step:

```csharp
// 在 EnvCreatorService.cs class 内新增 interface 声明
public interface IUvInstaller
{
    Task<string> InstallAsync(CancellationToken ct = default);
}

public interface ILtx2WrapperGenerator
{
    Task GenerateAsync(CancellationToken ct = default);
}

// 加构造参数(默认值 null = 真实实现)
private readonly Func<string, IUvInstaller>? _uvInstallerFactory;
private readonly Func<string, ILtx2WrapperGenerator>? _wrapperGeneratorFactory;
private readonly Func<string, string, string, CancellationToken, Task>? _gitCloneAsync;
private readonly Func<string, string, CancellationToken, Task>? _pipInstallRequirementsAsync;

// 在 CreateAsync 的 step 6.6(wheel seed)之后、SQLite 写入之前,插入:

// 6.7 (LTX-2 only):安装 uv 到 env/tools/uv/
// 走真实实现 or 注入 fake
if (string.Equals(templateConfig.Kind, "LTXVideo", StringComparison.Ordinal))
{
    progress?.Report(new CreateStepReport("安装 uv 工具链", UvInstaller.DownloadUrl));
    try
    {
        var installer = _uvInstallerFactory?.Invoke(rootPath)
            ?? new UvInstaller(rootPath);
        await installer.InstallAsync(ct);
    }
    catch (OperationCanceledException)
    {
        try { Directory.Delete(rootPath, recursive: true); } catch { }
        throw;
    }
    catch (Exception ex)
    {
        try { Directory.Delete(rootPath, recursive: true); } catch { }
        throw new CreateEnvException("UV_INSTALL_FAILED",
            $"uv 工具安装失败: {ex.Message}");
    }
}

// 7 之后(SQLite 写入 + EnvMarker 写入),env-create 主流程末尾,加:

// 7.5 LTX-2 走 uv sync;其它模板走 pip install -r requirements.txt
if (string.Equals(templateConfig.Kind, "LTXVideo", StringComparison.Ordinal))
{
    var uvExe = Path.Combine(rootPath, "tools", "uv", "uv.exe");
    progress?.Report(new CreateStepReport("LTX-2: uv sync --extra natten", uvExe + " sync"));
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = uvExe,
            Arguments = "sync --extra natten",
            WorkingDirectory = rootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            try { Directory.Delete(rootPath, recursive: true); } catch { }
            throw new CreateEnvException("UV_SYNC_FAILED",
                $"uv sync 退出码 {p.ExitCode},详情见日志");
        }
    }
    catch (OperationCanceledException)
    {
        try { Directory.Delete(rootPath, recursive: true); } catch { }
        throw;
    }
    catch (CreateEnvException) { throw; }
    catch (Exception ex)
    {
        try { Directory.Delete(rootPath, recursive: true); } catch { }
        throw new CreateEnvException("UV_SYNC_FAILED",
            $"uv sync 失败: {ex.Message}");
    }
}

// 7.6 LTX-2 only:生成 wrapper .bat(供 EntryScript 指向)
if (string.Equals(templateConfig.Kind, "LTXVideo", StringComparison.Ordinal))
{
    progress?.Report(new CreateStepReport("生成 LTX-2 wrapper 脚本",
        "run-ltx2-distilled.bat / run-ltx2-dfr.bat"));
    try
    {
        var gen = _wrapperGeneratorFactory?.Invoke(rootPath)
            ?? new Ltx2WrapperGenerator(rootPath);
        await gen.GenerateAsync(ct);
    }
    catch (OperationCanceledException)
    {
        try { Directory.Delete(rootPath, recursive: true); } catch { }
        throw;
    }
    catch (Exception ex)
    {
        try { Directory.Delete(rootPath, recursive: true); } catch { }
        throw new CreateEnvException("LTX2_WRAPPER_GENERATE_FAILED",
            $"生成 wrapper .bat 失败: {ex.Message}");
    }
}
```

(完整改 EnvCreatorService.cs 头加 2 个 interface + 4 个构造参数 + 4 个私有字段。`pipInstallRequirementsAsync` 跟现有 `pipInstallWheelAsync` 同模式实现,默认 = `RunPipInstallRequirementsAsync` 私有方法 = `<venvPython> -m pip install -r <envRoot>/requirements.txt`,留给 step 7.5 Non-LTX-2 路径用)

- [ ] **Step 6.4: 跑测试确认全过**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "EnvCreatorServiceLtx2StepsTests" -v minimal`
Expected: 3 PASS / 0 FAIL

- [ ] **Step 6.5: 跑全 EnvCreatorServiceTests 确认没破老测试**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "EnvCreatorServiceTests" -v minimal`
Expected: 全部老测试 PASS(可能需要同步加新构造参数默认值 null)

- [ ] **Step 6.6: Commit**

```bash
git -C "D:/ToolDevelop/ComfyUI" add src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceLtx2StepsTests.cs
git -C "D:/ToolDevelop/ComfyUI" commit -m "feat(ltx2): EnvCreatorService step 6.7/7.5/7.6 + 3 测试

step 6.7: LTX-2 env 装 uv 到 env/tools/uv/(real or 注入 fake)
step 7.5: LTX-2 env 跑 uv sync --extra natten(替 pip install -r)
step 7.6: 生成 run-ltx2-distilled.bat / run-ltx2-dfr.bat wrapper
其它 11 模板走老 pip 路径不变。

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 7: `ProcessLauncher` `{models}` `{env}` 占位符 + LTX-2 模型检查

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs`
- Test: `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/ProcessLauncherLtx2ModelCheckTests.cs`

**Interfaces:**
- Consumes: `Environment.ModelsDirectory`、`Environment.Ltx2RequiredModels`
- Produces:
  - `BuildStartCommand`:`EntryArgs` 替换 `{models}` → `ModelsDirectory` 绝对路径、`{env}` → `env.RootPath`(已存在 `{port}` 替换)
  - `StartEnvAsync`:LTX-2 env 启动前 `Ltx2RequiredModels` 全存在 → 继续;任一缺失 → 抛 `ModelsMissingException`(含缺失路径 + HF URL + 下载命令)

- [ ] **Step 7.1: 写 failing test**

```csharp
// tests-wpf/ComfyUI.Manager.Tests/Infrastructure/ProcessLauncherLtx2ModelCheckTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

public sealed class ProcessLauncherLtx2ModelCheckTests
{
    private static Environment MakeLtx2Env(string modelsDir, string root)
        => new()
        {
            Id = "test-id",
            Name = "ltx-test",
            RootPath = root,
            TemplateKind = "LTXVideo",
            ModelsDirectory = modelsDir,
            Port = 8188,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "LTXVideo", Name = "LTXVideo",
                LocalSourceDir = "LTX-Video",
                EntryScript = "run-ltx2-distilled.bat",
                EntryArgs = "--transformer-path {models}/ltx-2.5/x.safetensors --out {env}/o.mp4",
            },
        };

    [Fact]
    public void BuildStartCommand_Replaces_Models_And_Env_Placeholders()
    {
        var env = MakeLtx2Env("D:/models", "D:/envs/ltx-test");
        var settings = new Settings();
        var (exe, (file, args)) = ProcessLauncher.BuildStartCommand(env, settings, "D:/proj");
        Assert.Contains("--transformer-path D:\\models\\ltx-2.5\\x.safetensors", args);
        Assert.Contains("--out D:\\envs\\ltx-test\\o.mp4", args);
    }

    [Fact]
    public void BuildStartCommand_PortPlaceholder_StillWorks()
    {
        var env = new Environment
        {
            Id = "x", Name = "x", RootPath = "D:/envs/x",
            TemplateKind = "ComfyUI", ModelsDirectory = "",
            Port = 9090,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "ComfyUI", Name = "ComfyUI", LocalSourceDir = "ComfyUI",
                EntryScript = "main.py", EntryArgs = "--port {port}",
            },
        };
        var (exe, (file, args)) = ProcessLauncher.BuildStartCommand(env, new Settings(), "D:/proj");
        Assert.Contains("--port 9090", args);
    }

    [Fact]
    public void BuildStartCommand_MissingModelsPlaceholder_LeavesArgEmpty()
    {
        var env = new Environment
        {
            Id = "x", Name = "x", RootPath = "D:/envs/x",
            TemplateKind = "ComfyUI", ModelsDirectory = "",
            Port = 9090,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "ComfyUI", Name = "ComfyUI", LocalSourceDir = "ComfyUI",
                EntryScript = "main.py", EntryArgs = "--ckpt {models}/c.pt",
            },
        };
        var (exe, (file, args)) = ProcessLauncher.BuildStartCommand(env, new Settings(), "D:/proj");
        // ModelsDirectory 空 → {models} 替换为空串,生成 "--ckpt  /c.pt" 但不 throw
        Assert.Contains("--ckpt", args);
    }

    [Fact]
    public void EnsureLtx2ModelsPresent_AllExist_DoesNotThrow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ltx-models-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            // touch 5 个文件
            foreach (var p in new Environment
                     {
                         TemplateKind = "LTXVideo", ModelsDirectory = dir
                     }.Ltx2RequiredModels)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllBytes(p, new byte[] { 1 });
            }
            var env = new Environment { TemplateKind = "LTXVideo", ModelsDirectory = dir };
            // static helper 测试用反射或 public;留个 public static helper 看下面
            ProcessLauncher.EnsureLtx2ModelsPresent(env);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EnsureLtx2ModelsPresent_Missing_ThrowsModelsMissingException()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ltx-models-empty-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var env = new Environment { TemplateKind = "LTXVideo", ModelsDirectory = dir };
            var ex = Assert.Throws<ModelsMissingException>(
                () => ProcessLauncher.EnsureLtx2ModelsPresent(env));
            Assert.Equal(5, ex.MissingPaths.Count);
            Assert.Contains("huggingface.co/Lightricks/LTX-2.5", ex.HuggingFaceRepoUrl);
            Assert.Contains("hf download Lightricks/LTX-2.5", ex.DownloadCommand);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EnsureLtx2ModelsPresent_NonLTXVideo_DoesNotCheck()
    {
        var env = new Environment { TemplateKind = "ComfyUI", ModelsDirectory = "" };
        ProcessLauncher.EnsureLtx2ModelsPresent(env);  // 不抛
    }
}
```

- [ ] **Step 7.2: 跑测试确认 fail**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "ProcessLauncherLtx2ModelCheckTests" -v minimal`
Expected: 4 FAIL(BomLsPath / `EnsureLtx2ModelsPresent` 不存在 / `{models}` 不替换)— 已存在的占位符测试 2 个可能 pass

- [ ] **Step 7.3: 改 ProcessLauncher.cs**

`BuildStartCommand` 在 `var port = env.Port?.ToString() ?? "8000";` 后追加占位符替换:

```csharp
        // v1.0.0.x (2026-08-30):新增 {models} {env} 占位符 — LTX-2 CLI 模式需要
        // 模型绝对路径(LTX-2 EntryArgs 用 {models}/ltx-2.5/<file>.safetensors)和 env 根路径
        // (--output-path {env}/outputs/output.mp4)。空 ModelsDirectory 替换为空串
        // (不抛 — 跟现有 {port} 空 → "8000" 一致)。
        if (!string.IsNullOrWhiteSpace(env.ModelsDirectory))
            entryArgs = entryArgs.Replace("{models}", env.ModelsDirectory);
        else
            entryArgs = entryArgs.Replace("{models}", "");
        if (!string.IsNullOrWhiteSpace(envRoot))
            entryArgs = entryArgs.Replace("{env}", envRoot);
        else
            entryArgs = entryArgs.Replace("{env}", "");
```

加 `EnsureLtx2ModelsPresent` 静态 public 方法:

```csharp
    /// <summary>
    /// v1.0.0.x (2026-08-30):LTX-2 env 启动前检查 5 个 .safetensors 是否存在。
    /// 缺失抛 <see cref="ModelsMissingException"/>,UI 接住弹 MessageBox。
    /// 非 LTXVideo / ModelsDirectory 空 → 不查(其它模板不强制)。
    /// </summary>
    public static void EnsureLtx2ModelsPresent(Environment env)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));
        if (env.TemplateKind != "LTXVideo") return;
        var required = env.Ltx2RequiredModels;
        if (required.Count == 0) return;

        var missing = new List<string>();
        foreach (var p in required)
        {
            if (!File.Exists(p)) missing.Add(p);
        }
        if (missing.Count == 0) return;

        throw new ModelsMissingException(
            $"缺少 LTX-2 模型文件({missing.Count} 个),请按弹窗提示下载后重试",
            missing,
            "https://huggingface.co/Lightricks/LTX-2.5",
            "hf download Lightricks/LTX-2.5 " +
            "diffusion_models/ltx-2.5-22b-distilled-transformer-bf16.safetensors " +
            "text_encoders/gemma4-12b-with-proj-ltx-2.5-bf16.safetensors " +
            "vae/ltx-2.5-video-vae-bf16.safetensors " +
            "vae/ltx-2.5-audio-vae-bf16.safetensors " +
            "latent_upscale_models/ltx-2.5-latent-spatial-upscaler-x2-bf16-1.0.safetensors " +
            $"--local-dir {env.ModelsDirectory}/ltx-2.5");
    }
```

在 `StartEnvAsync` 的 `BuildStartCommand` 之前调:

```csharp
            // v1.0.0.x (2026-08-30):LTX-2 模型检查(缺失抛 ModelsMissingException → UI MessageBox)
            EnsureLtx2ModelsPresent(env);
```

- [ ] **Step 7.4: 跑测试确认全过**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "ProcessLauncherLtx2ModelCheckTests" -v minimal`
Expected: 6 PASS / 0 FAIL

- [ ] **Step 7.5: 跑全 ProcessLauncherTests 确认没破老测试**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "ProcessLauncherTests" -v minimal`
Expected: 全部老测试 PASS

- [ ] **Step 7.6: Commit**

```bash
git -C "D:/ToolDevelop/ComfyUI" add src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs tests-wpf/ComfyUI.Manager.Tests/Infrastructure/ProcessLauncherLtx2ModelCheckTests.cs
git -C "D:/ToolDevelop/ComfyUI" commit -m "feat(ltx2): ProcessLauncher {models}/{env} 占位符 + 模型检查 + 6 测试

BuildStartCommand 新增 {models} -> env.ModelsDirectory、{env} -> env.RootPath 替换。
EnsureLtx2ModelsPresent static helper:LTX-2 启动前检 5 个 .safetensors,
缺失抛 ModelsMissingException (含 HF URL + hf download 命令)。
非 LTXVideo 跳过。

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 8: `EnvironmentListViewModel` 接 `ModelsMissingException` → MessageBox

**Files:**
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs`(改 `StartEnvAsync` catch + 加测试需要的虚函数 / 注入点)
- Test: `tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelLtx2Tests.cs`

**Interfaces:**
- Consumes: `ProcessLauncher.StartEnvAsync` 抛 `ModelsMissingException`
- Produces: UI 弹 MessageBox(标题 "LTX-2 模型缺失",内容 = exception.Message + HF URL + 下载命令);用户点 OK 后 env 保持 stopped 状态

- [ ] **Step 8.1: 写 failing test**

```csharp
// tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelLtx2Tests.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public sealed class EnvironmentListViewModelLtx2Tests
{
    private static (RecordingMessageBox msgbox, FakeProcessLauncher launcher, EnvironmentListViewModel vm) MakeHarness()
    {
        var msgbox = new RecordingMessageBox();
        var launcher = new FakeProcessLauncher
        {
            OnStart = env => throw new ModelsMissingException(
                "缺少 LTX-2 模型文件(2 个)",
                new List<string> { "/a/transformer.safetensors", "/a/vae.safetensors" },
                "https://huggingface.co/Lightricks/LTX-2.5",
                "hf download Lightricks/LTX-2.5 --local-dir models/ltx-2.5"),
        };
        // VM 构造依赖很多 — 走真实 ctor 用 fake repo + fake launcher
        var dbFactory = new SqliteConnectionFactory(":memory:");
        var envRepo = new EnvironmentRepository(dbFactory);
        var vm = new EnvironmentListViewModel(
            dbFactory, envRepo, launcher, msgbox: msgbox);
        return (msgbox, launcher, vm);
    }

    [Fact]
    public async Task StartEnv_ModelsMissing_ShowsMessageBox_DoesNotRethrow()
    {
        var (msgbox, _, vm) = MakeHarness();
        vm.Environments.Add(new Environment
        {
            Id = "e1", Name = "ltx", RootPath = "D:/envs/ltx",
            TemplateKind = "LTXVideo", ModelsDirectory = "D:/models",
            TemplateConfigSnapshot = new TemplateConfig { Kind = "LTXVideo", Name = "LTXVideo" },
            Status = "stopped",
        });

        await vm.StartStopCommand.ExecuteAsync(vm.Environments[0]);

        Assert.Equal(1, msgbox.ShowCalls);
        Assert.Contains("huggingface.co/Lightricks/LTX-2.5", msgbox.LastMessage);
        Assert.Contains("hf download", msgbox.LastMessage);
        Assert.Equal("stopped", vm.Environments[0].Status);
    }

    [Fact]
    public async Task StartEnv_NonModelsMissingException_StillShowsGenericError()
    {
        var msgbox = new RecordingMessageBox();
        var launcher = new FakeProcessLauncher
        {
            OnStart = env => throw new InvalidOperationException("generic error"),
        };
        var dbFactory = new SqliteConnectionFactory(":memory:");
        var envRepo = new EnvironmentRepository(dbFactory);
        var vm = new EnvironmentListViewModel(dbFactory, envRepo, launcher, msgbox: msgbox);
        vm.Environments.Add(new Environment
        {
            Id = "e1", Name = "ltx", RootPath = "D:/envs/ltx",
            TemplateKind = "LTXVideo", ModelsDirectory = "D:/models",
            TemplateConfigSnapshot = new TemplateConfig { Kind = "LTXVideo", Name = "LTXVideo" },
            Status = "stopped",
        });

        await vm.StartStopCommand.ExecuteAsync(vm.Environments[0]);

        Assert.Equal(1, msgbox.ShowCalls);
        Assert.DoesNotContain("huggingface.co", msgbox.LastMessage);
    }
}

internal sealed class RecordingMessageBox : IMessageBoxService
{
    public int ShowCalls { get; private set; }
    public string? LastTitle { get; private set; }
    public string? LastMessage { get; private set; }
    public void ShowInfo(string title, string message)
    {
        ShowCalls++;
        LastTitle = title;
        LastMessage = message;
    }
    public void ShowError(string title, string message) => ShowInfo(title, message);
}

internal sealed class FakeProcessLauncher : IProcessLauncher
{
    public Func<Environment, Task>? OnStart { get; set; }
    public Func<Environment, Task>? OnStop { get; set; }
    public Task StartEnvAsync(Environment env, CancellationToken ct = default) => OnStart?.Invoke(env) ?? Task.CompletedTask;
    public Task StopEnvAsync(Environment env, CancellationToken ct = default) => OnStop?.Invoke(env) ?? Task.CompletedTask;
    public bool IsRunning(Environment env) => false;
    public IReadOnlyList<string> RunningEnvIds => Array.Empty<string>();
    public string LogFilePath(string envName, string envId, DateTime? date = null) => "";
    public string ProjectRoot => "";
    public int StartupTimeoutSeconds => 600;
}
```

- [ ] **Step 8.2: 跑测试确认 fail**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "EnvironmentListViewModelLtx2Tests" -v minimal`
Expected: 2 FAIL(`IMessageBoxService` 接口不存在 / VM ctor 不接受 `msgbox` 参数 / `IProcessLauncher` 接口不存在)

- [ ] **Step 8.3: 改 ProcessLauncher / 加 IMessageBoxService 接口**

ProcessLauncher 加 `IProcessLauncher` interface(把 public 方法签名抽出来,ProcessLauncher 实现它)— 类似 worktree 但本任务先做最小化(只在 VM 侧加 abstract)。允许直接在 VM 用 `ProcessLauncher` 而非 interface,接受实现路径是给 VM 加 abstract class。

最小化方案(避免大改):在 `EnvironmentListViewModel` 加字段 `Func<string, string, Task>? _messageBoxAsync`,VM 调用处用 `_messageBoxAsync?.Invoke(title, msg)`,测试注入 fake。

```csharp
// EnvironmentListViewModel.cs 字段
private readonly Func<string, string, Task>? _messageBoxAsync;

// ctor 加可选参数
public EnvironmentListViewModel(
    SqliteConnectionFactory dbFactory,
    EnvironmentRepository envRepo,
    ProcessLauncher launcher,
    Func<string, string, Task>? messageBoxAsync = null,
    /* ...其它现有参数... */)
{
    // ...
    _messageBoxAsync = messageBoxAsync;
}

// 在 StartEnvAsync 调用 launcher 之后,catch ModelsMissingException:
catch (ModelsMissingException ex)
{
    var msg = $"{ex.Message}\n\n" +
              $"HuggingFace repo: {ex.HuggingFaceRepoUrl}\n\n" +
              $"请先在 hf auth login 后执行:\n{ex.DownloadCommand}\n\n" +
              $"完成后再次点「启动」。";
    if (_messageBoxAsync is not null)
        await _messageBoxAsync("LTX-2 模型缺失", msg);
    else
        System.Windows.MessageBox.Show(msg, "LTX-2 模型缺失",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
    env.Status = "stopped";
    env.Pid = null;
    return;
}
catch (Exception ex)  // 现有通用 catch
{
    // ...
}
```

(具体 catch 块的位置需要看 VM 现状,但本质就是 ModelsMissingException 优先 catch + 弹 MessageBox + 保持 stopped;其它异常走原有 catch)

- [ ] **Step 8.4: 跑测试确认全过**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "EnvironmentListViewModelLtx2Tests" -v minimal`
Expected: 2 PASS / 0 FAIL

- [ ] **Step 8.5: 跑全 EnvironmentListViewModelTests 确认没破老测试**

Run: `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "EnvironmentListViewModelTests" -v minimal`
Expected: 全部老测试 PASS

- [ ] **Step 8.6: 手动 dev 验证**(可选,但推荐)

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj
dotnet run --project src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj
# 在 UI 里选「新建 env」→ 模板 LTXVideo → 确认 uv 下载 + uv sync 跑通(2-5 分钟)
# 等 env-create 完成后,在不下载模型的情况下点「启动」
# 验证 MessageBox 弹出 + 内容包含 HF URL + hf download 命令
```

- [ ] **Step 8.7: Commit**

```bash
git -C "D:/ToolDevelop/ComfyUI" add src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs tests-wpf/ComfyUI.Manager.Tests/ViewModels/EnvironmentListViewModelLtx2Tests.cs
git -C "D:/ToolDevelop/ComfyUI" commit -m "feat(ltx2): EnvironmentListViewModel 接 ModelsMissingException -> MessageBox + 2 测试

StartStopCommand 抛 ModelsMissingException 时弹 MessageBox(标题 LTX-2 模型缺失,
内容含 HF URL + hf download 命令);env 保持 stopped。
其它异常走原 catch 不变。测试注入 _messageBoxAsync 避免真弹窗。

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## 验证

跑全套:

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal
```

预期:
- Build:0 error
- Tests:~30 新 test + 既有 ~2400 test 全过
- 已知 flaky:RealGit 网络 flaky(BaseEnvStatus / NodeOps / ProcessLauncher)— 不属于本次改动,允许按现状 skip

## 不做(范围外)

- 不自动 `hf auth login`(token 安全 + 用户条款)
- 不自动 hf download(66 GiB + gated)
- 不支持 Linux/macOS(env-create uv binary + 平台差异,后续单独 spec)
- 不改 ComfyUI 模板
- 不在 ENVTemplate/ 下 check-out LTX-2(本项目 git 不跟上游代码)
- 不为 LTX-2 写 ComfyUI node(KJNodes 已有 ltxv_nodes.py)
- 不 pin uv 版本号(latest redirect)
- 不实现 uv 二进制下载重试(失败回退整 env-create)

## 风险

| 风险 | 缓解 |
|---|---|
| uv release URL latest redirect 不稳定 | latest 跟新;失败用户重试 |
| 66 GiB HF gated 模型用户接受条款失败 | UI 提示明确,失败只启动报错,不污染 env 状态 |
| uv sync 失败(monorepo 子包冲突) | step 7.5 fail 整流程回退,删除 env 重试 |
| `python -m` 在 venv 之外的 shell 找不到包 | wrapper 用 `uv run`(uv 自己解析 workspace root) |
| 老 env (TemplateKind="LTXVideo" 但仍是 v1 旧模板配置) | `BuildStartCommand` 检测 EntryScript 不存在抛清晰错(已有逻辑) |
| ModelsDirectory 字段老 env 没值(2026-08-29 之前创建的) | `Ltx2RequiredModels` 空 → `EnsureLtx2ModelsPresent` 跳过 → 启动正常跑(只是模型加载会失败,但 UI 提示正常) |

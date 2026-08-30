# LTX-Video 2 模板替换 Spec

> **For agentic workers:** REQUIRED SUB-SKILL: 用 superpowers:subagent-driven-development 或 superpowers:executing-plans 跑 plan。

**Goal:** 把仓库内置的 LTXVideo 模板从 v1 (`Lightricks/LTX-Video`) 替换成 v2 (`Lightricks/LTX-2`),补齐 monorepo + uv 工具链 + 66 GiB HF gated 模型管理 + 无 web 端口的 CLI 启动流程。

**Architecture:** env-create 走 3 个新 step — 装 uv.exe 到 `env/tools/uv/`(新增)、`uv sync --extra natten` 替 `pip install`(改)、生成 `run-ltx2-*.bat` wrapper 走 `python -m ltx_pipelines.{distilled,dfr_pipeline}`(新增);启动检查 `env.ModelsDirectory/ltx-2.5/` 是否存在,缺失抛 `ModelsMissingException` → UI 弹提示。

**Tech Stack:** uv (Astral Rust 工具,Rust 写的、Windows 有官方 binary)、HF CLI(`hf` 命令,可选 — 模型下载走 UI 引导手动)、env.ModelsDirectory 已有的 SQLite 持久化字段、`uv sync` 替 pip install。

## Context

仓库现有 `LTXVideo` 模板(SHIPPED `021a23b` 2026-08-29)配置:

```csharp
// TemplateConfigDefaults.cs:179
public static TemplateConfig LTXVideo(string projectRoot) => new()
{
    Name = "LTXVideo",
    Kind = "LTXVideo",
    GitHubRepoUrl = "https://github.com/Lightricks/LTX-Video.git",
    EntryScript = "gradio_demo.py",       // ❌ 不存在
    EntryArgs = "--server_port {port}",   // ❌ repo 实际是 CLI 模式,没 web UI
    ModelsSubdir = "models",
};
```

**集成缺陷分析**(2026-08-30 review):

| # | 缺陷 | 证据 |
|---|---|---|
| 1 | EntryScript 配错 | `gradio_demo.py` 不存在;repo 只有 CLI `inference.py` |
| 2 | 无 `requirements.txt` | repo 只 `pyproject.toml` |
| 3 | `{port}` EntryArgs 没意义 | inference.py 是 HfArgumentParser CLI,无 web server |
| 4 | torch 版本冲突 | `pyproject.toml` 锁 `torch>=2.1.0`,Forge/ComfyUI 各 env 独立 OK |
| 5 | 浅克隆 1 commit | `git log` 1 commit,无 release |
| 6 | README 推 LTX-2 | 上游 README 大标题 "LTX-2 is Now Available!" |

**为什么替换而不是修补**: v1 模板缺 web UI(核心假设破),且上游已停维护(LTXV-2 是新主版本)。修补 = 找老 release 拿 gradio demo,长期看 LTX-2 才是用户实际想要的。

## Design Decisions

### D1. uv 工具自动装到 `env/tools/uv/`

| 选项 | 选择 | 理由 |
|---|---|---|
| A. 自动装 uv.exe 到 env/tools/ | ✅ | 跟用户偏好"zip 绿色版"一致,uv 进 env 可搬 |
| B. 要求用户系统装 uv | ❌ | 增加用户摩擦 |
| C. 不用 uv,改 `pip install -e ./packages/*` | ❌ | 失去 uv workspace 协调,4 子包手动排顺序 |

- **下载源**: `https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip`
- **安装目录**: `<env.RootPath>/tools/uv/uv.exe`
- **校验**: `env/tools/uv/uv.exe --version` exit 0,版本号不 pin(uv 升级快)
- **重入**: 已存在跳过下载,直接复用

### D2. EntryScript = wrapper `.bat` 而不是改 ProcessLauncher

| 选项 | 选择 | 理由 |
|---|---|---|
| A. EntryType 字段 + ProcessLauncher 改 | ❌ | 改 5+ 文件,引入新概念;11 模板用不上 |
| B. wrapper `.bat` | ✅ | ProcessLauncher 零改动;uv 路径在 wrapper 硬编码 |
| C. EntryScript = 命令字符串 | ❌ | 模糊语义,ProcessLauncher 现状按 .py 文件走 |

- **wrapper 内容**:
  ```bat
  @echo off
  "%~dp0tools\uv\uv.exe" run python -m ltx_pipelines.distilled %*
  ```
- **生成两个 wrapper**:
  - `<env>/run-ltx2-distilled.bat` → `python -m ltx_pipelines.distilled`
  - `<env>/run-ltx2-dfr.bat` → `python -m ltx_pipelines.dfr_pipeline`
- **EntryScript** 默认指向 `run-ltx2-distilled.bat`(快路径 / Quick Start 推荐)
- **`%~dp0`**: wrapper 的目录(等价 `<env>` 根),保证 env 可搬机器不破

### D3. 模型目录关联到 env.ModelsDirectory(不是 monorepo models/)

| 选项 | 选择 | 理由 |
|---|---|---|
| A. monorepo 内的 `models/ltx-2.5/` | ❌ | 跟 env-create 约定冲突(env 内不应该有 models/ 永久占位) |
| B. `env.ModelsDirectory/ltx-2.5/` | ✅ | 复用 `Environment.ModelsDirectory` 已持久化字段;用户全局 Models 目录可被多个 env 共享 |
| C. 完全不放,要求用户外部管理 | ❌ | 违反用户预期"装完 env 就能用" |

- **HF 下载命令**(README quick start):
  ```bash
  hf download Lightricks/LTX-2.5 \
      diffusion_models/ltx-2.5-22b-distilled-transformer-bf16.safetensors \
      text_encoders/gemma4-12b-with-proj-ltx-2.5-bf16.safetensors \
      vae/ltx-2.5-video-vae-bf16.safetensors \
      vae/ltx-2.5-audio-vae-bf16.safetensors \
      latent_upscale_models/ltx-2.5-latent-spatial-upscaler-x2-bf16-1.0.safetensors \
      --local-dir <env.ModelsDirectory>/ltx-2.5
  ```
- **缺失检测**: 启动前检查 `<env.ModelsDirectory>/ltx-2.5/diffusion_models/ltx-2.5-22b-distilled-transformer-bf16.safetensors` 是否存在;不存在 → `ModelsMissingException` → UI 弹 MessageBox
- **MessageBox 内容**: HF repo URL + 完整 hf 命令 + "请先在 hf auth login 后执行下载,完成后点启动重试"

### D4. `{models}` 占位符(扩展 BuildStartCommand)

| 选项 | 选择 | 理由 |
|---|---|---|
| A. `{models}` 占位符 + ProcessLauncher 替换 | ✅ | 跟现有 `{port}` 占位符同模式 |
| B. EntryArgs 写绝对路径 | ❌ | env-create 时不知道最终路径,得 env-create 之后二次编辑 |
| C. 用环境变量 | ❌ | 跟现有 EntryArgs 设计不一致 |

- **占位符**:
  - `{models}` → `env.ModelsDirectory` 绝对路径
  - `{env}` → `env.RootPath` 绝对路径
  - `{port}` → 现有
- **EntryArgs 模板**(default):
  ```
  --transformer-path {models}/ltx-2.5/diffusion_models/ltx-2.5-22b-distilled-transformer-bf16.safetensors
  --text-encoder-path {models}/ltx-2.5/text_encoders/gemma4-12b-with-proj-ltx-2.5-bf16.safetensors
  --video-vae-path {models}/ltx-2.5/vae/ltx-2.5-video-vae-bf16.safetensors
  --audio-vae-path {models}/ltx-2.5/vae/ltx-2.5-audio-vae-bf16.safetensors
  --spatial-upsampler-path {models}/ltx-2.5/latent_upscale_models/ltx-2.5-latent-spatial-upscaler-x2-bf16-1.0.safetensors
  --num-frames 121
  --seed 42
  --output-path {env}/outputs/output.mp4
  ```
- **用户可改**: `UserExtraArgs` 优先级最高,append 在 EntryArgs 之后(已有 pattern)

### D5. HF auth 不自动化

- 用户自己 `hf auth login`(gated 模型需手动接受条款 + Read token)
- 我们不存 hf token(避免泄露)
- UI 提示文本说明这个流程

## Env-Create Step 改动

| Step | 现状 | 改后 |
|---|---|---|
| 6.5 | pip upgrade | (不变) |
| 6.6 | wheel seed | (不变) |
| **6.7**(新) | — | **UvInstaller.InstallAsync** 下载 + 解压 uv 到 `env/tools/uv/` |
| 7 | git clone 源码 | (不变) |
| 7.5 | pip install -r requirements.txt / extra | **改为 `env/tools/uv/uv.exe sync --extra natten`**(monorepo 用 uv) |
| **7.6**(新) | — | **Ltx2WrapperGenerator.GenerateAsync** 写 `<env>/run-ltx2-distilled.bat` 和 `<env>/run-ltx2-dfr.bat` |

非 LTXVideo 模板走老流程:`pip install -r requirements.txt`(不变)。

## 启动流程改动

`ProcessLauncher.StartEnvAsync(env)`:

```
1. env.Status == "starting"
2. BuildStartCommand(env)        // 解析 {models} {env} {port}
3. ★ NEW: if (env.TemplateKind == "LTXVideo"):
4.     foreach path in Ltx2RequiredModels:
5.         if (!File.Exists(path)) throw ModelsMissingException
6. start process
7. stream stdout/stderr → env log
```

`EnvironmentListViewModel` 接住 `ModelsMissingException` → MessageBox → 用户取消 = env 留 stopped;用户确认 = 引导打开 HF repo URL。

## 文件清单

### 改

| 文件 | 改动 |
|---|---|
| `src-wpf/ComfyUI.Manager/Services/TemplateConfigDefaults.cs:179-191` | 替换 LTXVideo 工厂:Name=`LTX-Video 2`,GitHubRepoUrl→LTX-2,EntryScript→`run-ltx2-distilled.bat`,EntryArgs→带 `{models}` 长串 |
| `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs` | 加 step 6.7 UvInstaller + step 7.5 uv sync + step 7.6 Ltx2WrapperGenerator;改 step 7.5 条件分支(仅 LTXVideo 走 uv) |
| `src-wpf/ComfyUI.Manager/Services/ProcessLauncher.cs` | `BuildStartCommand` 加 `{models}` / `{env}` 占位符替换;`StartEnvAsync` 加 LTXVideo 启动前模型检查 |
| `src-wpf/ComfyUI.Manager/ViewModels/EnvironmentListViewModel.cs` | 接 `ModelsMissingException` → MessageBox |
| `src-wpf/ComfyUI.Manager/Models/Environment.cs` | 加 `Ltx2RequiredModels` 派生属性(返回缺失的 5 个 .safetensors 绝对路径列表);已有 `ModelsDirectory` 字段不动 |

### 新增

| 文件 | 用途 |
|---|---|
| `src-wpf/ComfyUI.Manager/Services/UvInstaller.cs` | `InstallAsync(env.RootPath)` 下载 + 解压 uv.zip + 校验 |
| `src-wpf/ComfyUI.Manager/Services/Ltx2WrapperGenerator.cs` | `GenerateAsync(env.RootPath)` 写 2 个 wrapper .bat |
| `src-wpf/ComfyUI.Manager/Models/ModelsMissingException.cs` | 异常类型:承载 MissingPaths 列表 + HF repo URL |

### 测试

| 文件 | 测试数 |
|---|---|
| `tests-wpf/ComfyUI.Manager.Tests/Services/UvInstallerTests.cs` | 5-6(已有跳过、下载成功、解压、校验失败 throw、版本号 echo、跨盘符) |
| `tests-wpf/ComfyUI.Manager.Tests/Services/Ltx2WrapperGeneratorTests.cs` | 3-4(wrapper 内容、两个文件、%~dp0 路径) |
| `tests-wpf/ComfyUI.Manager.Tests/Services/TemplateConfigDefaultsLtxVideoTests.cs` | 4-5(repo url / entry script / models subdir / 占位符齐 / TemplateConfigKind 不变) |
| `tests-wpf/ComfyUI.Manager.Tests/Services/ProcessLauncherLtx2ModelCheckTests.cs` | 3-4(全齐放行 / 缺 1 throw / 非 LTXVideo 跳过 / 路径含相对路径解析) |
| `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceLtx2StepsTests.cs` | 3-4(step 6.7 顺序 / step 7.5 uv sync 命令 / step 7.6 wrapper 生成) |

预计 ~20 新 test。

## Global Constraints

- **平台**: 仅 Windows(项目当前 `Platform=win32`,uv Windows binary `uv-x86_64-pc-windows-msvc.zip`;Linux/macOS 后续扩展)
- **Python 版本**: 跟随 env venv(3.10+ 由 LTX-2 pyproject.toml 锁)
- **HF 模型**: 不存 token / 不自动 auth,gated 条款用户手动接受
- **Memory 限制**: uv.zip ~30MB,解压 ~150MB,在 env/tools/ 不进 .cache(持久)
- **网络重试**: uv download 失败不重试(env-create step 失败回退,用户重试整流程)
- **可恢复**: step 6.7 / 7.5 / 7.6 任一失败,删除 env 重试(EnvCreatorService 现状)
- **i18n**: wrapper .bat / MessageBox 文案走 Resources.resx(项目惯例,用户偏好 M1 i18n)
- **不破坏**: 其它 11 个内置模板(ComfyUI / Forge / HunyuanVideo / CogVideoX / Fooocus / OpenVoice / Whisper / CoquiTTS / Bark / HivisionIDPhotos)走老 `pip install -r requirements.txt` 流程不变

## 验证

```bash
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj   # 0 error
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "UvInstaller|Ltx2Wrapper|TemplateConfigDefaultsLtx|ProcessLauncherLtx2|EnvCreatorServiceLtx2"
dotnet test tests-wpf/ComfyUI.Manager.Tests/   # full suite
```

## 手动验证场景

1. **全新 LTX-2 env**: Settings → 新建 env(选 LTXVideo 模板)→ 等 uv 下载 + uv sync(2-5 min)→ env-create 成功
2. **wrapper 跑通**: env 行「启动」按钮 → console 显示 `uv run python -m ltx_pipelines.distilled ...`(模型缺失会弹 MessageBox)
3. **模型缺失弹窗**: 故意不下载 → 启动 → MessageBox 显示 hf download 命令 + HF URL → 用户手动执行后重启
4. **HF 模型下载**: 用户自己跑 `hf download Lightricks/LTX-2.5 ... --local-dir <env>/Models/ltx-2.5` → 启动 → 不再弹窗,正常进 distilled pipeline
5. **env 搬机器**: 复制整个 env 目录到另一台装好 Python 的机器 → wrapper 仍能找到 `tools/uv/uv.exe`(因为 `%~dp0` 相对路径)→ 直接能跑

## 不做(范围外)

- 不自动 `hf auth login`(token 安全 + 用户条款)
- 不自动 hf download(66 GiB + gated)
- 不支持 Linux/macOS(env-create uv binary + 平台差异,后续单独 spec)
- 不改 ComfyUI 模板(各 env 独立 venv,LTX-2 env 不会跟 ComfyUI env 冲突)
- 不在 ENVTemplate/ 下 check-out LTX-2(本项目 git 不跟上游代码;跟现有 HunyuanVideo/Fooocus 等模板一致不 check-out)
- 不为 LTX-2 写 ComfyUI node(KJNodes 已有 ltxv_nodes.py,ComfyUI 集成走那条路;本次只做 standalone env)

## 风险

| 风险 | 缓解 |
|---|---|
| uv release URL 变了(latest redirect 不稳定) | pin 到具体版本(如 `0.5.0`)? — 暂不 pin,latest 跟新;失败用户重试 |
| 66 GiB HF gated 模型用户接受条款失败 | UI 提示明确,失败只是启动报错,不污染 env 状态 |
| uv sync 失败(monorepo 子包冲突) | step 7.5 fail 整流程回退,删除 env 重试 |
| `python -m` 在 venv 之外的 shell 找不到包 | wrapper 必须先 `cd` 到 `<env>` 根再调 uv(uv sync 装在 monorepo venv,但 uv run 不需要 cd,uv 自己解析 workspace root) |

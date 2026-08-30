using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// EnvCreatorService:编排 env 创建流程(替代 M5.2 删除的 Python EnvironmentService.create)。
///
/// v1.0.0 multi-template T4 重构:CreateAsync 接收 <see cref="TemplateConfig"/>
/// 代替原来的 <c>comfyuiSource</c> + <c>layout</c> 两个参数,实现:
///   - G3:始终 copy(G3 删除 junction 选项)
///   - G2:TemplateConfigSnapshot JSON 克隆冻结,settings 后续修改不影响 env
///   - G9:仅 ComfyUI kind 触发 ComfyUIManagerInstaller / CommonNodeInstaller
///
/// 步骤:
///   1. 校验输入(name unique / python 存在 / template.Kind + LocalSourceDir 非空)
///   2. 分配 port(从 8188 起,跳过已用)
///   3. 生成 env_id
///   4. 创建 env 根目录
///   5. **始终 copy** template source → env 根目录(不 junction)
///   5.5 链接默认 Models 目录(Settings.DefaultModelsDirectory 非空时,models → 该目录)
///   6. 创建 venv(VenvCreator)
///   6.5 升级 venv 内的 pip(<c>python -m pip install --upgrade pip</c>,warn-only)
///   6.6 seed wheel 包到 venv(<c>python -m pip install wheel</c>,required — fix Forge pre-flight CLIP `bdist_wheel` missing)
///   7. 插 SQLite 行(持久化 TemplateKind + TemplateConfigSnapshot JSON 克隆)
///   7.5 v1.0.0.x:Forge env 写初始 extra_model_paths.yaml(<see cref="ForgeExtraModelPathsYamlGenerator"/>),
///       让 env-create 后 OpenExtraModelPathsYaml 立刻看到真实内容;启动时
///       ProcessLauncher.StartEnvAsync 会再写一次(幂等)。
///
/// v1.0.0.x: env-create 不再自动跑「安装常用节点」(由 env 行按钮触发,RequirementsInstaller /
/// env 行内按钮已存在,逻辑独立)。Forge env 的 extra_model_paths.yaml 由 step 7.5 自动写,
/// Non-Forge kind 不写(yaml 是 Forge 专属 — ComfyUI 用 settings.models 目录约定 + junction)。
/// </summary>
public sealed class EnvCreatorService
{
    private const int PortBase = 8188;

    private readonly SqliteConnectionFactory _dbFactory;
    private readonly VenvCreator _venvCreator;
    private readonly JunctionLinker _linker;
    private readonly Models.Settings _settings;
    private readonly string _projectRoot;
    /// <summary>
    /// v1.0.0.x:ComfyUI / Forge env-create step 6.5 — 升级 venv 内 pip 到最新版。
    /// 默认 = <see cref="RunPipUpgradeAsync"/>(跑 <c>python -m pip install --upgrade pip</c>)。
    /// 测试可注入 fake(记录调用 + 模拟成功/失败),避免真实网络。
    /// 签名 = (venvPython 绝对路径, CancellationToken) → Task。
    /// </summary>
    private readonly Func<string, CancellationToken, Task>? _pipUpgradeAsync;

    /// <summary>
    /// v1.0.0.x:env-create step 6.6 — seed wheel 包到 venv。Python <c>venv</c> 模块默认
    /// 装 pip + setuptools,但不装 <c>wheel</c>;而 <c>wheel</c> 提供 <c>bdist_wheel</c>
    /// 命令。Forge pre-flight 跑 <c>pip install https://github.com/openai/CLIP/...
    /// .zip --no-build-isolation</c> 时,CLIP 仓库的 pyproject.toml 声明
    /// <c>[build-system] requires = ["setuptools", "wheel"]</c>,--no-build-isolation
    /// 让 pip 直接用主 venv 的 setuptools/wheel 跑 metadata prep;缺 wheel →
    /// <c>error: invalid command 'bdist_wheel'</c> → CLIP / open_clip 等所有 setup.py
    /// 包 install fail。
    ///
    /// 失败行为 = **required**(跟 step 6.5 pip upgrade 的 warn-only 不同):没有 wheel,
    /// 后续 env BED / pre-flight / 节点安装的 setup.py 包全部跑不通 → 整个 env-create
    /// 失败比静默继续更明确。
    ///
    /// 测试可注入 fake(同 step 6.5),签名 = (venvPython, CancellationToken) → Task。
    /// </summary>
    private readonly Func<string, CancellationToken, Task>? _pipInstallWheelAsync;

    /// <summary>
    /// v1.0.0.x (2026-08-30):env-create step 6.7 — 安装 Astral uv 到
    /// <c>&lt;env&gt;/tools/uv/uv.exe</c>(仅 <c>Kind == "LTXVideo"</c> 触发)。
    /// 工厂签名 = <c>(envRoot) → IUvInstaller</c>:real 模式返 <see cref="UvInstaller"/>,
    /// 测试可注入 fake 记录调用次数 + 校验 envRoot。默认 <c>null</c> 时走 real 实现。
    /// </summary>
    private readonly Func<string, IUvInstaller>? _uvInstallerFactory;

    /// <summary>
    /// v1.0.0.x (2026-08-30):env-create step 7.5(LTXVideo 分支)— 跑
    /// <c>&lt;envRoot&gt;/tools/uv/uv.exe sync --extra natten</c>。
    /// 签名 = <c>(uvExePath) → Task</c>;默认 <c>null</c> 时走 real Process.Start uv sync。
    /// 测试可注入 fake(避免真实 uv.exe 启动失败 — uv.exe 是 Lightricks/LTX-2
    /// 专用二进制,测试机一般没装)。
    /// </summary>
    private readonly Func<string, CancellationToken, Task>? _uvSyncAsync;

    /// <summary>
    /// v1.0.0.x (2026-08-30):env-create step 7.6 — 生成 LTX-2 wrapper .bat(仅 LTXVideo)。
    /// 工厂签名 = <c>(envRoot) → ILtx2WrapperGenerator</c>:real 模式返
    /// <see cref="Ltx2WrapperGenerator"/>,测试注入 fake。
    /// </summary>
    private readonly Func<string, ILtx2WrapperGenerator>? _wrapperGeneratorFactory;

    /// <summary>
    /// v1.0.0.x (2026-08-30):env-create 模板源码 git clone 注入点(预留 — 当前
    /// CreateAsync 仍走 <see cref="JunctionLinker.CopyDirectory"/> 复制已存在的本地源码)。
    /// 签名 = <c>(gitExe, repoUrl, targetDir, ct) → Task</c>;默认 <c>null</c> 时走 real
    /// <c>git clone</c> 进程(留给后续接 TemplateSourceUpdater 集成用,本任务仅占位)。
    /// </summary>
    private readonly Func<string, string, string, CancellationToken, Task>? _gitCloneAsync;

    /// <summary>
    /// v1.0.0.x (2026-08-30):env-create step 7.5(Non-LTXVideo 路径)— 跑
    /// <c>&lt;venvPython&gt; -m pip install -r &lt;envRoot&gt;/requirements.txt</c>。
    /// 镜像 <see cref="_pipInstallWheelAsync"/> 模式:签名 =
    /// <c>(venvPython, requirementsPath, ct) → Task</c>,默认 = <see cref="RunPipInstallRequirementsAsync"/>。
    /// </summary>
    private readonly Func<string, string, CancellationToken, Task>? _pipInstallRequirementsAsync;

    public EnvCreatorService(
        SqliteConnectionFactory dbFactory,
        VenvCreator venvCreator,
        JunctionLinker linker,
        Models.Settings settings,
        string projectRoot,
        Func<string, CancellationToken, Task>? pipUpgradeAsync = null,
        Func<string, CancellationToken, Task>? pipInstallWheelAsync = null,
        Func<string, IUvInstaller>? uvInstallerFactory = null,
        Func<string, ILtx2WrapperGenerator>? wrapperGeneratorFactory = null,
        Func<string, string, string, CancellationToken, Task>? gitCloneAsync = null,
        Func<string, string, CancellationToken, Task>? pipInstallRequirementsAsync = null,
        Func<string, CancellationToken, Task>? uvSyncAsync = null)
    {
        _dbFactory = dbFactory;
        _venvCreator = venvCreator;
        _linker = linker;
        _settings = settings;
        _projectRoot = projectRoot;
        _pipUpgradeAsync = pipUpgradeAsync;
        _pipInstallWheelAsync = pipInstallWheelAsync;
        _uvInstallerFactory = uvInstallerFactory;
        _wrapperGeneratorFactory = wrapperGeneratorFactory;
        _gitCloneAsync = gitCloneAsync;
        _pipInstallRequirementsAsync = pipInstallRequirementsAsync;
        _uvSyncAsync = uvSyncAsync;
    }

    /// <summary>
    /// v1.0.0.x (2026-08-30):LTX-2 env 装 uv 工具链 — 工厂接口,real impl 是
    /// <see cref="UvInstaller"/>。envRoot 已在 factory 闭包里传入 ctor,接口签名不重复传。
    /// </summary>
    public interface IUvInstaller
    {
        Task<string> InstallAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// v1.0.0.x (2026-08-30):LTX-2 env 生成 wrapper .bat — 工厂接口,real impl 是
    /// <see cref="Ltx2WrapperGenerator"/>。envRoot 已在 factory 闭包里传入 ctor,接口签名不重复传。
    /// </summary>
    public interface ILtx2WrapperGenerator
    {
        Task GenerateAsync(CancellationToken ct = default);
    }

    public sealed class CreateEnvException : Exception
    {
        public string Code { get; }
        public CreateEnvException(string code, string message) : base(message)
        {
            Code = code;
        }
    }

    /// <summary>
    /// v1.0.0 multi-template T4:CreateAsync 接收 <see cref="TemplateConfig"/> 替代原来
    /// 独立的 <c>comfyuiSource</c> + <c>layout</c> 两个参数。TemplateConfig 内的
    /// <c>Kind</c> 决定 ComfyUI Manager / Common Node 安装行为;LocalSourceDir 决定
    /// copy 来源。TemplateConfigSnapshot 通过 JSON round-trip 克隆,确保 env 创建后
    /// 用户修改 Settings.Templates 不影响已有 env(G2)。
    ///
    /// v1.0.0.x (2026-08-30):加 <paramref name="sourceOverride"/> — 测试 / 自动化场景
    /// 直接传 fake 源码目录,跳过 <c>TemplatePathResolver.Resolve</c> + Directory.Exists
    /// 校验(同时跳过 pythonExe 文件存在性校验)。生产 UI 调用方不传这个参数,
    /// 走原全量校验路径。
    /// </summary>
    public async Task<Environment> CreateAsync(
        string name,
        TemplateConfig templateConfig,
        string? pythonExe = null,
        int? port = null,
        string? notes = null,
        string? sourceOverride = null,
        CancellationToken ct = default,
        IProgress<CreateStepReport>? progress = null)
    {
        // 1. 校验输入
        progress?.Report(new CreateStepReport("校验输入", $"python.exe = {pythonExe}"));
        if (string.IsNullOrWhiteSpace(name))
            throw new CreateEnvException("ENV_NAME_INVALID", "环境名不能为空");
        if (templateConfig is null)
            throw new CreateEnvException("TEMPLATE_CONFIG_MISSING",
                "TemplateConfig 不能为 null");
        if (string.IsNullOrWhiteSpace(templateConfig.Kind))
            throw new CreateEnvException("TEMPLATE_KIND_INVALID",
                "TemplateConfig.Kind 不能为空");
        if (string.IsNullOrWhiteSpace(templateConfig.LocalSourceDir))
            throw new CreateEnvException("TEMPLATE_SOURCE_MISSING",
                "TemplateConfig.LocalSourceDir 不能为空");
        // v1.0.0.x: 锚定到 _settings.SystemTemplateLibraryDir (用户配的"系统模板库目录",
        // 非空时) 或 BaseDirectory 回退,跟 TemplateSourceUpdater 用同一规则,保证
        // settings 看到的路径 = 实际下载路径 = env 创建时复制源码的路径。
        // sourceOverride 不为空 → 跳过 Directory.Exists 校验(测试 fake 源 + UI 都不传,
        // 真实路径已由 caller 保证;这个分支只给单测 + 后续自动化场景用)。
        if (string.IsNullOrWhiteSpace(sourceOverride) &&
            !Directory.Exists(TemplatePathResolver.Resolve(
                templateConfig.LocalSourceDir, _settings.SystemTemplateLibraryDir)))
            throw new CreateEnvException("TEMPLATE_SOURCE_NOT_FOUND",
                $"模板源码目录不存在: {templateConfig.LocalSourceDir}");
        // pythonExe:sourceOverride 非空 → 跳过文件存在性校验(同上,测试 fake 源无需真 python);
        // production 路径显式传 pythonExe,仍走 File.Exists 校验。
        if (!string.IsNullOrWhiteSpace(pythonExe) && !File.Exists(pythonExe))
            throw new CreateEnvException("VENV_PYTHON_MISSING",
                $"Python 解释器不存在: {pythonExe}");

        var envRepo = new EnvironmentRepository(_dbFactory);
        foreach (var existing in envRepo.ListAll())
        {
            if (existing.Name == name)
                throw new CreateEnvException("ENV_NAME_DUPLICATE",
                    $"环境名 {name} 已存在");
        }

        // 2. 分配 port
        var usedPorts = envRepo.ListAll().Select(e => e.Port ?? 0).ToHashSet();
        int allocatedPort = port ?? NextFreePort(usedPorts);
        progress?.Report(new CreateStepReport("分配端口", $"port = {allocatedPort}"));

        // 3. 生成 env_id
        string envId = $"env-{Guid.NewGuid().ToString("N")[..8]}";

        // 4. 创建 env 根目录 —— _settings.EnvsDir 是相对路径,
        // 始终相对于 _projectRoot 解析。空字符串提示用户去设置页填,
        // 不再"自动用默认子目录名"避免默默创建意外目录。
        if (string.IsNullOrWhiteSpace(_settings.EnvsDir))
        {
            throw new CreateEnvException("ENV_ENVDIR_NOT_CONFIGURED",
                "请先在设置页配置「虚拟环境目录」(env 创建时会放这里)");
        }
        var envsDir = Path.Combine(_projectRoot, _settings.EnvsDir);
        var rootPath = Path.Combine(envsDir, name);
        progress?.Report(new CreateStepReport("创建 env 根目录", $"→ {rootPath}"));
        if (Directory.Exists(rootPath) && Directory.EnumerateFileSystemEntries(rootPath).Any())
            throw new CreateEnvException("ENV_PATH_NOT_EMPTY",
                $"目标路径 {rootPath} 非空");

        Directory.CreateDirectory(envsDir);
        Directory.CreateDirectory(rootPath);

        // 5. v1.0.0 T4 G3:始终 copy template source 到 env 根目录。
        // 删除了 v0.6.x 时代的 shared → junction / independent → copy 二选一;
        // 现在所有 kind 都是独立 copy,环境间不共享 template 源代码。
        progress?.Report(new CreateStepReport("复制 template 源",
            $"copy: {templateConfig.LocalSourceDir} → {rootPath}"));
        // v1.0.0.x: 锚定到 _settings.SystemTemplateLibraryDir (用户配的"系统模板库目录",
        // 非空时) 或 BaseDirectory 回退,跟 Directory.Exists 检查用同一规则,
        // 保证 settings 看到的路径 = 实际 copy 源路径。
        // v1.0.0.x sourceOverride 非空 → 跳过 TemplatePathResolver.Resolve,
        // 直接用 caller 传的绝对路径(fake 源 / 自动化场景)。
        var sourceDir = !string.IsNullOrWhiteSpace(sourceOverride)
            ? sourceOverride
            : TemplatePathResolver.Resolve(templateConfig.LocalSourceDir, _settings.SystemTemplateLibraryDir);
        _linker.CopyDirectory(sourceDir, rootPath);

        // 5.5 链接默认 Models 目录(v0.6.11+ T2 合并:Shared 字段删除,只此一条)。
        // v1.0.0 T4:对所有 kind 都生效(不是仅 ComfyUI),让用户配置 default models
        // 后 Forge / 其它 kind 也能共享 models。
        if (!string.IsNullOrWhiteSpace(_settings.DefaultModelsDirectory))
        {
            var modelsDirFull = Path.GetFullPath(_settings.DefaultModelsDirectory);
            var modelsLink = Path.Combine(rootPath, "models");
            progress?.Report(new CreateStepReport("链接 Models 目录",
                $"junction: {modelsLink} → {modelsDirFull}"));
            try
            {
                if (Directory.Exists(modelsLink))
                {
                    Directory.Delete(modelsLink, recursive: true);
                }
                await _linker.CreateAsync(modelsLink, modelsDirFull, ct);
            }
            catch (Exception ex)
            {
                try { Directory.Delete(rootPath, recursive: true); } catch { }
                throw new CreateEnvException("MODELS_LINK_FAILED",
                    $"Models junction 创建失败: {ex.Message}");
            }
        }

        // 6. 创建 venv
        var venvPath = Path.Combine(rootPath, "venv");
        progress?.Report(new CreateStepReport("创建 venv 环境",
            $"python -m venv {venvPath}"));
        try
        {
            // pythonExe ?? "" : sourceOverride 测试路径下 caller 不传 pythonExe,走 FakeVenvCreator
            // 写空 Scripts/python.exe 占位(后续 ReadVenvPythonVersionAsync fallback "<unknown>")。
            // 生产路径 _settings 必传 pythonExe,这里非空保证 VenvCreator 真跑。
            await _venvCreator.CreateAsync(pythonExe ?? "", venvPath, ct);
        }
        catch (VenvCreator.VenvCreationException ex)
        {
            // 回滚:删 env 根目录
            try { Directory.Delete(rootPath, recursive: true); } catch { }
            throw new CreateEnvException("VENV_CREATE_FAILED", ex.Message);
        }

        // 6.5 升级 venv 内的 pip(对应 Forge webui.bat line 44-47:
        //   %VENV_DIR%\Scripts\Python.exe -m pip install --upgrade pip)。
        // 跟 BaseEnvInstaller.DefaultPreInstallPipArgs 同语义 — 老 pip 装包会持续
        // 报 "WARNING: pip version X is available" 一堆警告;env-create 阶段预升
        // pip,后续装 torch / 节点 / extras 都用新 pip 静默跑。
        // 失败只 Warn log 不阻塞 — bat 行为也如此(`Warning: Failed to upgrade PIP
        // version` 后继续 activate_venv)。venv 创建已成功 + 老 pip 仍能装包。
        var venvPython = Path.Combine(venvPath, "Scripts", "python.exe");
        progress?.Report(new CreateStepReport("升级 venv 内 pip",
            $"{venvPython} -m pip install --upgrade pip"));
        try
        {
            var upgrade = _pipUpgradeAsync ?? RunPipUpgradeAsync;
            await upgrade(venvPython, ct);
        }
        catch (OperationCanceledException)
        {
            // 用户取消 → 上抛,caller 自己处理(env-create 整体取消)
            try { Directory.Delete(rootPath, recursive: true); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            // 警告但不失败 env-create(同 BaseEnvInstaller pre-install 行为)
            // 这里只 Console.WriteLine(没有 _logger 注入)— service 类不持 logger。
            // 失败信息通过 progress 报告给 UI,Console 输出给开发期调试。
            Console.WriteLine($"[env-create] venv pip upgrade 失败(继续): {ex.Message}");
            progress?.Report(new CreateStepReport("升级 venv 内 pip [warn: 失败]",
                ex.Message));
        }

        // 6.6 seed wheel 包(v1.0.0.x 修复 Forge pre-flight CLIP install `bdist_wheel`
        // missing:Python `venv` 模块默认装 pip + setuptools 但不装 wheel;
        // CLIP / open_clip 等 setup.py 包需要 wheel 提供 bdist_wheel 命令)。
        // required(不像 step 6.5 pip upgrade 是 warn-only)— 没有 wheel 后续所有
        // setup.py 包 install 都跑不通(env 等于废)。
        progress?.Report(new CreateStepReport("安装 wheel 包到 venv",
            $"{venvPython} -m pip install wheel"));
        try
        {
            var seedWheel = _pipInstallWheelAsync ?? RunPipInstallWheelAsync;
            await seedWheel(venvPython, ct);
        }
        catch (OperationCanceledException)
        {
            // 用户取消 → 上抛,caller 自己处理(env-create 整体取消 + 回滚)
            try { Directory.Delete(rootPath, recursive: true); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            // required:env-create 整体失败 — 回滚 env 根目录,跟 step 6 venv 创建失败同语义。
            try { Directory.Delete(rootPath, recursive: true); } catch { }
            throw new CreateEnvException("VENV_WHEEL_SEED_FAILED",
                $"venv 内 wheel 包安装失败(后续 setup.py 包 install 必跑不通): {ex.Message}");
        }

        // 6.7 (LTX-2 only) — 安装 Astral uv 到 <env>/tools/uv/uv.exe。
        // v1.0.0.x (2026-08-30) 新增:LTX-2 monorepo 装包走 uv sync,需要 uv 工具链;
        // 装到 env 内部(不进 PATH)→ 用户机器 / 项目搬家都能用,跟 env 生命周期绑定。
        // 走 factory 模式:real 返 UvInstaller(envRoot),测试可注入 fake 记录调用次数。
        if (string.Equals(templateConfig.Kind, "LTXVideo", StringComparison.Ordinal))
        {
            progress?.Report(new CreateStepReport("安装 uv 工具链", UvInstaller.DownloadUrl));
            try
            {
                IUvInstaller installer = _uvInstallerFactory is not null
                    ? _uvInstallerFactory(rootPath)
                    : new UvInstaller(rootPath);
                await installer.InstallAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // 取消 → 回滚 env 根目录 + 上抛(env-create 整体取消)
                try { Directory.Delete(rootPath, recursive: true); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                // required(跟 step 6.6 wheel 同语义):没 uv 后续 uv sync 跑不通,
                // env 等于废。回滚 env 根目录 + 包 CreateEnvException。
                try { Directory.Delete(rootPath, recursive: true); } catch { }
                throw new CreateEnvException("UV_INSTALL_FAILED",
                    $"uv 工具安装失败: {ex.Message}");
            }
        }

        // 7. 构造 Environment 写库
        // v1.0.0 T4 G2:TemplateConfigSnapshot 用 JSON round-trip 克隆,后续 settings
        // 编辑不会 mutate 已存在的 env snapshot(测试 `CreateAsync_SnapshotIsFrozen_*`)。
        // ComfyuiLayout 列保留旧字段("isolated" 标量字面),仍写到 DB 以兼容老 schema。
        //
        // v1.0.0.x (2026-08-29):Forge env 模型目录配置改回 CLI args,不再写
        // extra_model_paths.yaml 文件(实测 Forge fork 不读 yaml,grep 整个
        // ForgeUI 目录零引用 — 详见 ProcessLauncher.BuildStartCommand 注释)。
        // Env.ExtraModelPathsYaml 字段值仍计算,MainViewModel.OpenExtraModelPathsYamlCommand
        // 用该路径打开 —— 即便文件不存在也不影响启动。
        var extraYaml = Path.Combine(rootPath, "extra_model_paths.yaml");
        var env = new Environment
        {
            Id = envId,
            Name = name,
            RootPath = rootPath,
            // v1.0.0 T4:Layout 概念被 templateConfig.Kind 取代;ComfyuiLayout 列保留
            // 一个标量字面以兼容 DB schema(老行回填 "isolated")。
            ComfyuiLayout = "isolated",
            ComfyuiSource = rootPath,  // copy 后 ComfyUI 内容就在 rootPath 下
            BasePythonPath = pythonExe ?? "",
            VenvPath = venvPath,
            PythonExecutable = Path.Combine(venvPath, "Scripts", "python.exe"),
            PythonVersion = await ReadVenvPythonVersionAsync(venvPath, ct),
            CustomNodesPath = Path.Combine(rootPath, "custom_nodes"),
            ExtraModelPathsYaml = extraYaml,
            Port = allocatedPort,
            Status = "stopped",
            EnabledNodeIdsJson = "[]",
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            TemplateKind = templateConfig.Kind,
            TemplateConfigSnapshot = CloneTemplateConfig(templateConfig),
        };
        Directory.CreateDirectory(env.CustomNodesPath!);
        envRepo.Upsert(env);

        // v1.0.0.x:写 per-env marker 隐藏文件 .cmgr-env.json,让 EnvDirectoryScanner
        // 在用户切换 Settings.EnvsDir 后能 auto-import 这个 env。失败静默 — env-create
        // 主流程不因 marker IO 错误而失败(G5)。
        EnvMarkerService.Write(env.RootPath, new EnvMarker
        {
            EnvId = env.Id,
            Name = env.Name,
            Kind = env.TemplateKind,
            TemplateSnapshot = env.TemplateConfigSnapshot,
            CreatedAt = DateTime.UtcNow.ToString("o"),
        });

        // 7.5 (v1.0.0.x 2026-08-30 新增) — 装模板要求包:
        //   - LTXVideo:跑 <c>env/tools/uv/uv.exe sync --extra natten</c>(monorepo 用 uv)
        //   - 其它模板:跑 <c>&lt;venv&gt;/Scripts/python.exe -m pip install -r
        //     &lt;envRoot&gt;/requirements.txt</c>(经典 venv + pip,venv 已建好直接走 venv pip)
        // 写在 SQLite 写入 + EnvMarker 之后 → env-create 主流程回滚触发时 env 已
        // 落 DB 但不影响 SQLite 一致性(整流程 catch 走 try Directory.Delete 回滚)。
        if (string.Equals(templateConfig.Kind, "LTXVideo", StringComparison.Ordinal))
        {
            var uvExe = Path.Combine(rootPath, "tools", "uv", "uv.exe");
            progress?.Report(new CreateStepReport("LTX-2: uv sync --extra natten",
                $"{uvExe} sync --extra natten"));
            try
            {
                var sync = _uvSyncAsync ?? RunUvSyncAsync;
                await sync(uvExe, ct);
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
        else
        {
            // Non-LTXVideo:pip install -r requirements.txt
            var requirementsPath = Path.Combine(rootPath, "requirements.txt");
            progress?.Report(new CreateStepReport("安装模板依赖(requirements.txt)",
                $"{venvPython} -m pip install -r {requirementsPath}"));
            try
            {
                if (!File.Exists(requirementsPath))
                {
                    // 没 requirements.txt = 模板不需要依赖安装(罕见但允许,跳过不报错)。
                    progress?.Report(new CreateStepReport("安装模板依赖(无 requirements.txt,跳过)", requirementsPath));
                }
                else
                {
                    var installReqs = _pipInstallRequirementsAsync ?? RunPipInstallRequirementsAsync;
                    await installReqs(venvPython, requirementsPath, ct);
                }
            }
            catch (OperationCanceledException)
            {
                try { Directory.Delete(rootPath, recursive: true); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                // required:env-create 整体失败 — 回滚 env 根目录 + 包 CreateEnvException。
                try { Directory.Delete(rootPath, recursive: true); } catch { }
                throw new CreateEnvException("REQUIREMENTS_INSTALL_FAILED",
                    $"requirements.txt 安装失败: {ex.Message}");
            }
        }

        // 7.6 (LTX-2 only) — 生成 wrapper .bat(run-ltx2-distilled.bat / run-ltx2-dfr.bat)。
        // EntryScript 直接指向 wrapper;wrapper 用 %~dp0tools\uv\uv.exe 解析 uv 路径,
        // env 可搬到任意机器 + env 改路径不需要重新生成 wrapper。factory 模式同 step 6.7。
        if (string.Equals(templateConfig.Kind, "LTXVideo", StringComparison.Ordinal))
        {
            progress?.Report(new CreateStepReport("生成 LTX-2 wrapper 脚本",
                "run-ltx2-distilled.bat / run-ltx2-dfr.bat"));
            try
            {
                ILtx2WrapperGenerator gen = _wrapperGeneratorFactory is not null
                    ? _wrapperGeneratorFactory(rootPath)
                    : new Ltx2WrapperGenerator(rootPath);
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

        // v1.0.0.x: 常用节点安装不再在 env-create 末尾自动跑 — 用户在 env 行右侧
        // 按钮触发(RequirementsInstaller / 行内按钮已存在,逻辑独立)。env-create
        // 只做基础初始化,跟用户「创建 = 模板复制」语义一致。

        return env;
    }

    /// <summary>
    /// v1.0.0 T4 G2:通过 JSON 序列化往返克隆 <see cref="TemplateConfig"/>,
    /// 确保后续用户编辑 <c>Settings.Templates</c> 不会 mutate 已存在 env 的 snapshot。
    /// 跟 EnvironmentRepository 持久化 snapshot 用相同的 JsonSerializer 选项(default,无 converter)。
    /// </summary>
    private static TemplateConfig CloneTemplateConfig(TemplateConfig source)
    {
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<TemplateConfig>(json)
            ?? throw new InvalidOperationException("TemplateConfig clone round-trip returned null");
    }

    private static int NextFreePort(HashSet<int> used)
    {
        int p = PortBase;
        while (used.Contains(p)) p++;
        return p;
    }

    /// <summary>
    /// ReadVenvPythonVersionAsync:跑 <c>&lt;venv&gt;/Scripts/python.exe -c "import sys; print(sys.version)"</c>
    /// 读 venv python 版本。任何异常(进程启动失败、文件不存在、超时、cancellation)
    /// fallback <c>"&lt;unknown&gt;"</c> 且不抛 — env 已经成功创建,版本号只是诊断信息。
    /// </summary>
    private async Task<string> ReadVenvPythonVersionAsync(string venvPath, CancellationToken ct)
    {
        try
        {
            var venvPython = Path.Combine(venvPath, "Scripts", "python.exe");
            if (!File.Exists(venvPython)) return "<unknown>";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = venvPython,
                Arguments = "-c \"import sys; print(sys.version)\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(stdout) ? "<unknown>" : stdout.Trim();
        }
        catch
        {
            return "<unknown>";
        }
    }

    /// <summary>
    /// v1.0.0.x:env-create step 6.5 默认实现 — 跑
    /// <c>&lt;venvPython&gt; -m pip install --upgrade pip</c> 升级 venv 内的 pip 到最新版。
    /// 对应 Forge webui.bat line 44-47(<c>upgrade_pip</c> 段)。
    ///
    /// 抛异常的语义:
    /// - <see cref="OperationCanceledException"/> → 上抛(用户取消时 step 6.5 catch 走取消分支,
    ///   回滚 env 根目录,整体 env-create 失败)
    /// - 其他异常(进程启动失败 / 非 0 exit code / IO 异常)→ 上抛,step 6.5 catch 走 warn
    ///   分支(只 Console + progress 报告,env-create 继续)
    /// </summary>
    internal static async Task RunPipUpgradeAsync(string venvPython, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(venvPython))
            throw new ArgumentException("venvPython 不能为空", nameof(venvPython));

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = venvPython,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("pip");
        psi.ArgumentList.Add("install");
        psi.ArgumentList.Add("--upgrade");
        psi.ArgumentList.Add("pip");

        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("pip upgrade 进程启动失败(Process.Start 返回 null)");

        // stdout + stderr 并发读,免 pipe buffer 撑爆;读到的行直接丢(用户通过 ConsolePanel 看 venv 内 pip 输出,
        // 此步骤是 env-create 内部动作,不需要把每行 pip 噪音往 UI 推 — 进度面板只需"升级 venv 内 pip"这一行)。
        var stdoutDone = new TaskCompletionSource<bool>();
        var stderrDone = new TaskCompletionSource<bool>();
        _ = Task.Run(async () =>
        {
            try { while (await p.StandardOutput.ReadLineAsync().ConfigureAwait(false) is not null) { } }
            catch { }
            finally { stdoutDone.TrySetResult(true); }
        });
        _ = Task.Run(async () =>
        {
            try { while (await p.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) { } }
            catch { }
            finally { stderrDone.TrySetResult(true); }
        });

        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        // 等 reader thread 退出,免 pipe 没消费完就 Dispose 进程句柄
        await Task.WhenAll(stdoutDone.Task, stderrDone.Task).ConfigureAwait(false);

        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"venv pip upgrade exit={p.ExitCode}(venv 仍可用,只是 pip 没升级)");
        }
    }

    /// <summary>
    /// v1.0.0.x:env-create step 6.6 默认实现 — 跑
    /// <c>&lt;venvPython&gt; -m pip install wheel</c>,确保 venv 自带 <c>wheel</c> 包
    /// (提供 <c>bdist_wheel</c> 命令,CLIP / open_clip 等 setup.py 包 install 必需)。
    ///
    /// 镜像 <see cref="RunPipUpgradeAsync"/> 模式:进程启动 → 并发读 stdout/stderr →
    /// 等 exit → 非 0 exit code / 启动失败抛 <see cref="InvalidOperationException"/>。
    /// ctor 注入 <c>_pipInstallWheelAsync</c> 替换为测试 fake。
    ///
    /// 抛异常的语义(由 step 6.6 catch 处理):
    /// - <see cref="OperationCanceledException"/> → 上抛,走取消分支(回滚 env 根目录)
    /// - 其他异常 → 包成 <see cref="CreateEnvException"/>(<c>VENV_WHEEL_SEED_FAILED</c>),env-create 整体失败
    /// </summary>
    internal static async Task RunPipInstallWheelAsync(string venvPython, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(venvPython))
            throw new ArgumentException("venvPython 不能为空", nameof(venvPython));

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = venvPython,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("pip");
        psi.ArgumentList.Add("install");
        psi.ArgumentList.Add("wheel");

        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("pip install wheel 进程启动失败(Process.Start 返回 null)");

        // stdout + stderr 并发读,免 pipe buffer 撑爆;读到的行直接丢(step 6.6 是
        // env-create 内部动作,跟 step 6.5 同不把 pip 噪音往 UI 推)。
        var stdoutDone = new TaskCompletionSource<bool>();
        var stderrDone = new TaskCompletionSource<bool>();
        _ = Task.Run(async () =>
        {
            try { while (await p.StandardOutput.ReadLineAsync().ConfigureAwait(false) is not null) { } }
            catch { }
            finally { stdoutDone.TrySetResult(true); }
        });
        _ = Task.Run(async () =>
        {
            try { while (await p.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) { } }
            catch { }
            finally { stderrDone.TrySetResult(true); }
        });

        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(stdoutDone.Task, stderrDone.Task).ConfigureAwait(false);

        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"venv pip install wheel exit={p.ExitCode}");
        }
    }

    /// <summary>
    /// v1.0.0.x (2026-08-30):env-create step 7.5 默认实现 — 跑
    /// <c>&lt;venvPython&gt; -m pip install -r &lt;requirementsPath&gt;</c>,装模板要求的包。
    /// Non-LTXVideo 模板走这条;LTXVideo 走 uv sync 不来这里。
    ///
    /// 镜像 <see cref="RunPipInstallWheelAsync"/> 模式:进程启动 → 并发读 stdout/stderr →
    /// 等 exit → 非 0 exit code / 启动失败抛 <see cref="InvalidOperationException"/>。
    /// ctor 注入 <c>_pipInstallRequirementsAsync</c> 替换为测试 fake。
    ///
    /// 抛异常的语义(由 step 7.5 catch 处理):
    /// - <see cref="OperationCanceledException"/> → 上抛,走取消分支(回滚 env 根目录)
    /// - 其他异常 → 包成 <see cref="CreateEnvException"/>(<c>REQUIREMENTS_INSTALL_FAILED</c>),
    ///   env-create 整体失败
    /// </summary>
    internal static async Task RunPipInstallRequirementsAsync(
        string venvPython, string requirementsPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(venvPython))
            throw new ArgumentException("venvPython 不能为空", nameof(venvPython));
        if (string.IsNullOrWhiteSpace(requirementsPath))
            throw new ArgumentException("requirementsPath 不能为空", nameof(requirementsPath));
        if (!File.Exists(requirementsPath))
            throw new InvalidOperationException($"requirements.txt 不存在: {requirementsPath}");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = venvPython,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("pip");
        psi.ArgumentList.Add("install");
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add(requirementsPath);

        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("pip install -r 进程启动失败(Process.Start 返回 null)");

        var stdoutDone = new TaskCompletionSource<bool>();
        var stderrDone = new TaskCompletionSource<bool>();
        _ = Task.Run(async () =>
        {
            try { while (await p.StandardOutput.ReadLineAsync().ConfigureAwait(false) is not null) { } }
            catch { }
            finally { stdoutDone.TrySetResult(true); }
        });
        _ = Task.Run(async () =>
        {
            try { while (await p.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) { } }
            catch { }
            finally { stderrDone.TrySetResult(true); }
        });

        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(stdoutDone.Task, stderrDone.Task).ConfigureAwait(false);

        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"pip install -r exit={p.ExitCode}(requirements.txt 装包失败)");
        }
    }

    /// <summary>
    /// v1.0.0.x (2026-08-30):env-create step 7.5(LTXVideo 分支)默认实现 — 跑
    /// <c>&lt;uvExe&gt; sync --extra natten</c>。
    ///
    /// 镜像 <see cref="RunPipInstallRequirementsAsync"/> 模式:进程启动 →
    /// 等 exit → 非 0 exit code / 启动失败抛 <see cref="InvalidOperationException"/>。
    /// ctor 注入 <c>_uvSyncAsync</c> 替换为测试 fake。
    ///
    /// 抛异常的语义(由 step 7.5 catch 处理):
    /// - <see cref="OperationCanceledException"/> → 上抛,走取消分支(回滚 env 根目录)
    /// - <see cref="InvalidOperationException"/>(process start failed / non-zero exit)
    ///   → 包成 <see cref="CreateEnvException"/>(<c>UV_SYNC_FAILED</c>),env-create 整体失败
    /// </summary>
    internal static async Task RunUvSyncAsync(string uvExe, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(uvExe))
            throw new ArgumentException("uvExe 不能为空", nameof(uvExe));
        if (!File.Exists(uvExe))
            throw new InvalidOperationException($"uv.exe 不存在: {uvExe}(step 6.7 应已装好 — 检查 install 失败原因)");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = uvExe,
            Arguments = "sync --extra natten",
            WorkingDirectory = Path.GetDirectoryName(uvExe) ?? "",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("uv sync 进程启动失败(Process.Start 返回 null)");

        await p.WaitForExitAsync(ct).ConfigureAwait(false);

        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"uv sync --extra natten exit={p.ExitCode}");
        }
    }
}

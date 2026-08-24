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
///   7. 写 extra_model_paths.yaml(占位)
///   8. 插 SQLite 行(持久化 TemplateKind + TemplateConfigSnapshot JSON 克隆)
///   9. (kind == ComfyUI 时)best-effort 装常用节点
/// </summary>
public sealed class EnvCreatorService
{
    private const int PortBase = 8188;

    private readonly SqliteConnectionFactory _dbFactory;
    private readonly VenvCreator _venvCreator;
    private readonly JunctionLinker _linker;
    private readonly Models.Settings _settings;
    private readonly string _projectRoot;
    // v0.6.11++:env-create 末尾 best-effort 装常用节点(G5 不阻断 env-create)。
    private readonly CommonNodeInstaller? _commonNodeInstaller;

    public EnvCreatorService(
        SqliteConnectionFactory dbFactory,
        VenvCreator venvCreator,
        JunctionLinker linker,
        Models.Settings settings,
        string projectRoot,
        CommonNodeInstaller? commonNodeInstaller = null)
    {
        _dbFactory = dbFactory;
        _venvCreator = venvCreator;
        _linker = linker;
        _settings = settings;
        _projectRoot = projectRoot;
        _commonNodeInstaller = commonNodeInstaller;
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
    /// </summary>
    public async Task<Environment> CreateAsync(
        string name,
        TemplateConfig templateConfig,
        string pythonExe,
        int? port,
        string? notes = null,
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
        if (!Directory.Exists(TemplatePathResolver.Resolve(
                templateConfig.LocalSourceDir, _settings.SystemTemplateLibraryDir)))
            throw new CreateEnvException("TEMPLATE_SOURCE_NOT_FOUND",
                $"模板源码目录不存在: {templateConfig.LocalSourceDir}");
        if (!File.Exists(pythonExe))
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
        _linker.CopyDirectory(
            TemplatePathResolver.Resolve(templateConfig.LocalSourceDir, _settings.SystemTemplateLibraryDir),
            rootPath);

        // 5.5 链接默认 Models 目录(v0.6.11+ T2 合并:Shared 字段删除,只此一条)。
        // v1.0.0 T4:对所有 kind 都生效(不是仅 ComfyUI),让用户配置 default models
        // 后 A1111 / 其它 kind 也能共享 models。
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
            await _venvCreator.CreateAsync(pythonExe, venvPath, ct);
        }
        catch (VenvCreator.VenvCreationException ex)
        {
            // 回滚:删 env 根目录
            try { Directory.Delete(rootPath, recursive: true); } catch { }
            throw new CreateEnvException("VENV_CREATE_FAILED", ex.Message);
        }

        // 7. 写 extra_model_paths.yaml(占位)
        var extraYaml = Path.Combine(rootPath, "extra_model_paths.yaml");
        progress?.Report(new CreateStepReport("保存配置", $"yaml = {extraYaml}"));
        File.WriteAllText(extraYaml, "# TODO: M1 填充\n", System.Text.Encoding.UTF8);

        // 8. 构造 Environment 写库
        // v1.0.0 T4 G2:TemplateConfigSnapshot 用 JSON round-trip 克隆,后续 settings
        // 编辑不会 mutate 已存在的 env snapshot(测试 `CreateAsync_SnapshotIsFrozen_*`)。
        // ComfyuiLayout 列保留旧字段("isolated" 标量字面),仍写到 DB 以兼容老 schema。
        var env = new Environment
        {
            Id = envId,
            Name = name,
            RootPath = rootPath,
            // v1.0.0 T4:Layout 概念被 templateConfig.Kind 取代;ComfyuiLayout 列保留
            // 一个标量字面以兼容 DB schema(老行回填 "isolated")。
            ComfyuiLayout = "isolated",
            ComfyuiSource = rootPath,  // copy 后 ComfyUI 内容就在 rootPath 下
            BasePythonPath = pythonExe,
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

        // 9. v1.0.0 T4 G9:仅 ComfyUI kind 跑 CommonNodeInstaller。
        // 非 ComfyUI(A1111 / 自定义 / etc)无 ComfyUI Manager / 常用节点概念,跳过。
        if (_commonNodeInstaller is not null
            && string.Equals(templateConfig.Kind, "ComfyUI", StringComparison.Ordinal))
        {
            try
            {
                progress?.Report(new CreateStepReport("安装常用节点", "触发 CommonNodeInstaller.InstallEnabledAsync"));
                var cnResult = await _commonNodeInstaller.InstallEnabledAsync(
                    env, new Progress<string>(line => progress?.Report(new CreateStepReport("常用节点", line))), ct);
                if (!cnResult.Success)
                {
                    progress?.Report(new CreateStepReport("常用节点", $"warn:{cnResult.Reason}"));
                }
            }
            catch (Exception ex)
            {
                progress?.Report(new CreateStepReport("常用节点", $"warn:异常 {ex.Message}"));
            }
        }

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
}

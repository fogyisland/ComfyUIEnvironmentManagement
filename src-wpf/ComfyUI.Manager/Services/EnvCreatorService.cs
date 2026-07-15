using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// 步骤:
///   1. 校验输入(name unique / python 存在 / shared 布局的 ComfyUI source 存在)
///   2. 分配 port(从 8188 起,跳过已用)
///   3. 生成 env_id
///   4. 创建 env 根目录
///   5. 链接 / 复制 ComfyUI(shared → junction,independent → copy)
///   6. 创建 venv(VenvCreator)
///   7. 写 extra_model_paths.yaml(占位)
///   8. 插 SQLite 行
/// </summary>
public sealed class EnvCreatorService
{
    private const int PortBase = 8188;

    private readonly SqliteConnectionFactory _dbFactory;
    private readonly VenvCreator _venvCreator;
    private readonly JunctionLinker _linker;
    private readonly Models.Settings _settings;
    private readonly string _projectRoot;

    public EnvCreatorService(
        SqliteConnectionFactory dbFactory,
        VenvCreator venvCreator,
        JunctionLinker linker,
        Models.Settings settings,
        string projectRoot)
    {
        _dbFactory = dbFactory;
        _venvCreator = venvCreator;
        _linker = linker;
        _settings = settings;
        _projectRoot = projectRoot;
    }

    public sealed class CreateEnvException : Exception
    {
        public string Code { get; }
        public CreateEnvException(string code, string message) : base(message)
        {
            Code = code;
        }
    }

    public async Task<Environment> CreateAsync(
        string name,
        string layout,            // "shared" | "independent"
        string pythonExe,
        string? comfyuiSource,
        int? port,
        CancellationToken ct = default)
    {
        // 1. 校验输入
        if (string.IsNullOrWhiteSpace(name))
            throw new CreateEnvException("ENV_NAME_INVALID", "环境名不能为空");
        if (layout != "shared" && layout != "independent")
            throw new CreateEnvException("ENV_LAYOUT_INVALID",
                $"layout 必须是 shared 或 independent,收到: {layout}");
        if (!File.Exists(pythonExe))
            throw new CreateEnvException("VENV_PYTHON_MISSING",
                $"Python 解释器不存在: {pythonExe}");
        if (layout == "shared" && (string.IsNullOrEmpty(comfyuiSource) || !Directory.Exists(comfyuiSource)))
            throw new CreateEnvException("COMFYUI_SOURCE_MISSING",
                "shared 布局必须指定已存在的 ComfyUI 源");

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

        // 3. 生成 env_id
        string envId = $"env-{Guid.NewGuid().ToString("N")[..8]}";

        // 4. 创建 env 根目录 —— _settings.EnvsDir 是相对路径(默认 "envs"),
        // 始终相对于 _projectRoot 解析。空字符串回退到默认子目录名。
        var envsSubdir = string.IsNullOrWhiteSpace(_settings.EnvsDir)
            ? Infrastructure.SettingsDefaults.EnvsSubdir
            : _settings.EnvsDir;
        var envsDir = Path.Combine(_projectRoot, envsSubdir);
        Directory.CreateDirectory(envsDir);
        var rootPath = Path.Combine(envsDir, name);
        if (Directory.Exists(rootPath) && Directory.EnumerateFileSystemEntries(rootPath).Any())
            throw new CreateEnvException("ENV_PATH_NOT_EMPTY",
                $"目标路径 {rootPath} 非空");

        Directory.CreateDirectory(rootPath);

        // 5. 链接 / 复制 ComfyUI
        var comfyuiLink = Path.Combine(rootPath, "ComfyUI");
        string comfyuiResolved;
        if (layout == "shared")
        {
            await _linker.CreateAsync(comfyuiLink, comfyuiSource!, ct);
            comfyuiResolved = comfyuiSource!;
        }
        else
        {
            // independent:需要先有 comfyuiSource 作为拷贝源
            if (string.IsNullOrEmpty(comfyuiSource) || !Directory.Exists(comfyuiSource))
                throw new CreateEnvException("COMFYUI_SOURCE_MISSING",
                    "independent 布局也需要指定已存在的 ComfyUI 源作为拷贝来源");
            _linker.CopyDirectory(comfyuiSource, comfyuiLink);
            comfyuiResolved = comfyuiLink;
        }

        // 6. 创建 venv
        var venvPath = Path.Combine(rootPath, "venv");
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
        File.WriteAllText(extraYaml, "# TODO: M1 填充\n", System.Text.Encoding.UTF8);

        // 8. 构造 Environment 写库
        var env = new Environment
        {
            Id = envId,
            Name = name,
            RootPath = rootPath,
            ComfyuiLayout = layout,
            ComfyuiSource = comfyuiResolved,
            VenvPath = venvPath,
            PythonExecutable = Path.Combine(venvPath, "Scripts", "python.exe"),
            CustomNodesPath = Path.Combine(rootPath, "custom_nodes"),
            ExtraModelPathsYaml = extraYaml,
            Port = allocatedPort,
            Status = "stopped",
            EnabledNodeIdsJson = "[]",
        };
        Directory.CreateDirectory(env.CustomNodesPath!);
        envRepo.Upsert(env);

        return env;
    }

    private static int NextFreePort(HashSet<int> used)
    {
        int p = PortBase;
        while (used.Contains(p)) p++;
        return p;
    }
}

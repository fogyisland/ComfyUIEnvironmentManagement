using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Services.FirstRun;

namespace ComfyUI.Manager.ViewModels.FirstRunWizard;

public class FirstRunWizardViewModel : INotifyPropertyChanged
{
    private readonly string _appDataDir;
    private readonly string _projectRoot;
    private FirstRunWizardStep _currentStep = FirstRunWizardStep.Welcome;
    private string _installPath = "";
    private string _pythonPath = "";
    // v1.0.0.x (2026-09-05):Git 路径 — wizard 自动 seed Embeded/git-portable/cmd/git.exe 绝对路径,
    // 用户确认或 Browse 改到系统 PATH "git" 或别处。
    private string _gitPath = "";
    // v1.0.0.x (2026-09-05):Python 路径也自动 seed Embeded/python/python.exe 绝对路径
    // (用户原话"python 路径也是自然获取,我们只需要看正确与否 例如当前
    // H:\ComfyUIManagement\Embeded\python 位于这个目录下的 python.exe")。
    // Wizard Step 2 自动填这个绝对路径,用户确认或 Browse 改。

    // v1.0.0.x (2026-09-03) T34:8 个 settings 路径(除 TemplatePythonDir 已在 PythonPath
    // 步骤处理)。默认值 = SettingsDefaults.Apply 镜像 seed 路径 ——
    // `<projectRoot>/<Kind>/` — 这样用户能直接 Confirm 接受默认,或点 Browse 改。
    // 注释同步说明每个 path 的用途(也用作 UI hint label 内容)。

    /// <summary>ENVTemplate 目录:clone 下来的模板源码 (ComfyUI / Forge / OpenVoice 等)
    /// 存这。TemplateSourceUpdater 走它。</summary>
    private string _systemTemplateLibraryDir = "";
    /// <summary>Envs 目录:用户创建的 env 根目录,每个 env 一个子目录。
    /// EnvCreatorService.CreateAsync 在这创建 env。</summary>
    private string _envsDir = "";
    /// <summary>Global nodes 目录:全局已下载节点存储,供 env 间共享。
    /// 镜像 catalog download default target。</summary>
    private string _globalNodesDir = "";
    /// <summary>LocalNode 目录:单节点本地下载 default (Catalog 单节点下载时)。
    /// 跟 LocalNodesDirectory(批量)区分。</summary>
    private string _localNodeDirectory = "";
    /// <summary>LocalNodes 目录:本地常用节点批量安装的源(env 行「装本地常用」按钮)。
    /// EnvListVM.InstallLocalNodesCommand 复制源。</summary>
    private string _localNodesDirectory = "";
    /// <summary>Models 目录:env-create junction 挂载点 + ModelMarketplace 下载 default。
    /// user extra_model_paths.yaml 也从这读。</summary>
    private string _defaultModelsDirectory = "";
    /// <summary>Workflow 目录:env-start 后 fire-and-forget 同步到的 user/default/workflows/。
    /// WorkflowMarketplace 下载 default。</summary>
    private string _workflowsDirectory = "";
    /// <summary>Logs 父目录,EnvStartStatus 日志写 <see cref="LogDirectory"/>/&lt;envId&gt;.log。
    /// SettingsDefaults.Apply 自动建 <see cref="LogDirectory"/>/Logs/。</summary>
    private string _logDirectory = "";

    public FirstRunWizardStep CurrentStep
    {
        get => _currentStep;
        private set
        {
            _currentStep = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWelcome));
            OnPropertyChanged(nameof(IsPythonGit));
            OnPropertyChanged(nameof(IsPaths1EnvNodes));
            OnPropertyChanged(nameof(IsPaths2ModelsWorkflows));
            OnPropertyChanged(nameof(IsPaths3SystemLog));
            OnPropertyChanged(nameof(IsConfirm));
        }
    }
    public bool IsWelcome => CurrentStep == FirstRunWizardStep.Welcome;
    public bool IsPythonGit => CurrentStep == FirstRunWizardStep.PythonGit;
    public bool IsPaths1EnvNodes => CurrentStep == FirstRunWizardStep.Paths1EnvNodes;
    public bool IsPaths2ModelsWorkflows => CurrentStep == FirstRunWizardStep.Paths2ModelsWorkflows;
    public bool IsPaths3SystemLog => CurrentStep == FirstRunWizardStep.Paths3SystemLog;
    public bool IsConfirm => CurrentStep == FirstRunWizardStep.Confirm;

    public string InstallPath
    {
        get => _installPath;
        set { _installPath = value ?? ""; OnPropertyChanged(); NextCommandCanExecuteChanged(); }
    }
    public string PythonPath
    {
        get => _pythonPath;
        set { _pythonPath = value ?? ""; OnPropertyChanged(); NextCommandCanExecuteChanged(); }
    }
    public bool IsPythonValid => !string.IsNullOrWhiteSpace(_pythonPath) && System.IO.File.Exists(_pythonPath);

    // v1.0.0.x (2026-09-05):Git 路径字段 — 默认 seed Embeded/git-portable/cmd/git.exe 绝对路径,
    // 用户确认或 Browse 改到别的 git 安装。IsGitValid 检查文件存在(空字符串 OK = 用 PATH "git")。
    public string GitPath
    {
        get => _gitPath;
        set { _gitPath = value ?? ""; OnPropertyChanged(); NextCommandCanExecuteChanged(); }
    }
    public bool IsGitValid => string.IsNullOrWhiteSpace(_gitPath) || System.IO.File.Exists(_gitPath);

    public string SystemTemplateLibraryDir
    {
        get => _systemTemplateLibraryDir;
        set { _systemTemplateLibraryDir = value ?? ""; OnPropertyChanged(); }
    }
    public string EnvsDir
    {
        get => _envsDir;
        set { _envsDir = value ?? ""; OnPropertyChanged(); }
    }
    public string GlobalNodesDir
    {
        get => _globalNodesDir;
        set { _globalNodesDir = value ?? ""; OnPropertyChanged(); }
    }
    public string LocalNodeDirectory
    {
        get => _localNodeDirectory;
        set { _localNodeDirectory = value ?? ""; OnPropertyChanged(); }
    }
    public string LocalNodesDirectory
    {
        get => _localNodesDirectory;
        set { _localNodesDirectory = value ?? ""; OnPropertyChanged(); }
    }
    public string DefaultModelsDirectory
    {
        get => _defaultModelsDirectory;
        set { _defaultModelsDirectory = value ?? ""; OnPropertyChanged(); }
    }
    public string WorkflowsDirectory
    {
        get => _workflowsDirectory;
        set { _workflowsDirectory = value ?? ""; OnPropertyChanged(); }
    }
    public string LogDirectory
    {
        get => _logDirectory;
        set { _logDirectory = value ?? ""; OnPropertyChanged(); }
    }

    public RelayCommand NextCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand FinishCommand { get; }
    public RelayCommand CancelCommand { get; }

    public event Action? Completed;
    public event Action? Cancelled;
    public event PropertyChangedEventHandler? PropertyChanged;

    public FirstRunWizardViewModel(string appDataDir)
        : this(appDataDir, System.IO.Path.GetDirectoryName(appDataDir) ?? appDataDir, null)
    {
    }

    public FirstRunWizardViewModel(string appDataDir, string projectRoot)
        : this(appDataDir, projectRoot, settingsRepo: null)
    {
    }

    // v1.0.0.x (2026-09-05):新 ctor ——
    // 用户原话"启动的时候默认生成 setting.inf 文件,只是在 wizard 会列出自动生成的文件,
    // 然后如果有更改则将变更的内容再次诙谐到 setting.inf 文件中"。
    // 接受 settingsRepo 读已 Apply 后的 settings 现有值,显示给 user(让 user 看到自动生成的值)。
    // 若 user 改完 Finish,Finish 把改动 merge 回 settings。
    // 旧 ctor(不传 settingsRepo)仍可用 — 走默认 seed 路径,无现有 settings 时。
    public FirstRunWizardViewModel(
        string appDataDir,
        string projectRoot,
        ComfyUI.Manager.Data.SettingsRepository? settingsRepo)
    {
        _appDataDir = appDataDir;
        _projectRoot = projectRoot;
        // v1.0.0.x (2026-09-05) UX fix:InstallPath 默认填 program path(等同 projectRoot,
        // 因为 release 模式下 ResolveDevProjectRoot fallback 到 exe dir)。让用户看见默认,
        // 确认路径正确,或 Browse 改到别的位置——而不是空字符串 + Next 按钮永远 disabled。
        _installPath = projectRoot;
        // 默认 seed 路径镜像 SettingsDefaults.Apply 模式 (projectRoot/Kind/)
        _systemTemplateLibraryDir = System.IO.Path.Combine(projectRoot, "ENVTemplate") + System.IO.Path.DirectorySeparatorChar;
        _envsDir = System.IO.Path.Combine(projectRoot, "Envs") + System.IO.Path.DirectorySeparatorChar;
        _globalNodesDir = System.IO.Path.Combine(projectRoot, "Nodes") + System.IO.Path.DirectorySeparatorChar;
        _localNodeDirectory = System.IO.Path.Combine(projectRoot, "LocalNodes") + System.IO.Path.DirectorySeparatorChar;
        _localNodesDirectory = System.IO.Path.Combine(projectRoot, "localnodes") + System.IO.Path.DirectorySeparatorChar;
        _defaultModelsDirectory = System.IO.Path.Combine(projectRoot, "Models") + System.IO.Path.DirectorySeparatorChar;
        _workflowsDirectory = System.IO.Path.Combine(projectRoot, "Workflow") + System.IO.Path.DirectorySeparatorChar;
        _logDirectory = System.IO.Path.Combine(projectRoot, "logs") + System.IO.Path.DirectorySeparatorChar;

        // v1.0.0.x (2026-09-05):Git path 默认 seed Embeded/git-portable/cmd/git.exe 绝对路径
        // (跟 Python + git 一起放 Embeded/),用户可在 wizard 里确认或 Browse 改到系统 PATH。
        // ResolveGitExe 探测顺序:Embeded/git-portable → bin/git-portable → PATH "git"。
        var gitEmbeded = System.IO.Path.Combine(projectRoot, "Embeded", "git-portable", "cmd", "git.exe");
        if (System.IO.File.Exists(gitEmbeded))
        {
            _gitPath = gitEmbeded;
        }
        else
        {
            var gitBin = System.IO.Path.Combine(projectRoot, "bin", "git-portable", "cmd", "git.exe");
            if (System.IO.File.Exists(gitBin))
                _gitPath = gitBin;
            // 否则留空字符串 → 走 PATH "git" fallback
        }

        // v1.0.0.x (2026-09-05):Python path 默认 seed Embeded/python/python.exe 绝对路径 ——
        // 用户原话"python 路径也是自然获取,我们只需要看正确与否 例如当前
        // H:\ComfyUIManagement\Embeded\python 位于这个目录下的 python.exe"。
        // 探测顺序跟 ResolveGitExe 对齐:Embeded/python/python.exe → Python/python.exe → python/python.exe(legacy) → 留空让用户 Browse。
        var pythonEmbeded = System.IO.Path.Combine(projectRoot, "Embeded", "python", "python.exe");
        if (System.IO.File.Exists(pythonEmbeded))
        {
            _pythonPath = pythonEmbeded;
        }
        else
        {
            var pythonUpper = System.IO.Path.Combine(projectRoot, "Python", "python.exe");
            if (System.IO.File.Exists(pythonUpper))
                _pythonPath = pythonUpper;
            else
            {
                var pythonLower = System.IO.Path.Combine(projectRoot, "python", "python.exe");
                if (System.IO.File.Exists(pythonLower))
                    _pythonPath = pythonLower;
                // 都没找到 → 留空让用户 Browse
            }
        }

        // v1.0.0.x (2026-09-05):如果有 settingsRepo,Load 已 Apply 的 settings 现有值覆盖默认 seed ——
        // 用户原话"启动的时候默认生成 setting.inf 文件,只是在 wizard 会列出自动生成的文件"。
        // user 看到的是自动生成的路径(wizard 显示 Apply 后的值),不是空。
        if (settingsRepo is not null)
        {
            try
            {
                var (existing, _) = settingsRepo.LoadWithRawJson();
                // 用 existing 覆盖默认 seed(但空字段保留默认)
                if (!string.IsNullOrWhiteSpace(existing.InstallPath)) _installPath = existing.InstallPath;
                if (!string.IsNullOrWhiteSpace(existing.TemplatePythonDir)
                    && System.IO.File.Exists(System.IO.Path.Combine(existing.TemplatePythonDir, "python.exe")))
                {
                    _pythonPath = System.IO.Path.Combine(existing.TemplatePythonDir, "python.exe");
                }
                if (existing.PythonInterpreters.Count > 0
                    && !string.IsNullOrWhiteSpace(existing.PythonInterpreters[0].Path))
                {
                    _pythonPath = existing.PythonInterpreters[0].Path;
                }
                if (!string.IsNullOrWhiteSpace(existing.GitExe)) _gitPath = existing.GitExe;
                if (!string.IsNullOrWhiteSpace(existing.SystemTemplateLibraryDir)) _systemTemplateLibraryDir = existing.SystemTemplateLibraryDir;
                if (!string.IsNullOrWhiteSpace(existing.EnvsDir)) _envsDir = existing.EnvsDir;
                if (!string.IsNullOrWhiteSpace(existing.GlobalNodesDir)) _globalNodesDir = existing.GlobalNodesDir;
                if (!string.IsNullOrWhiteSpace(existing.LocalNodeDirectory)) _localNodeDirectory = existing.LocalNodeDirectory;
                if (!string.IsNullOrWhiteSpace(existing.LocalNodesDirectory)) _localNodesDirectory = existing.LocalNodesDirectory;
                if (!string.IsNullOrWhiteSpace(existing.DefaultModelsDirectory)) _defaultModelsDirectory = existing.DefaultModelsDirectory;
                if (!string.IsNullOrWhiteSpace(existing.WorkflowsDirectory)) _workflowsDirectory = existing.WorkflowsDirectory;
                if (!string.IsNullOrWhiteSpace(existing.LogDirectory)) _logDirectory = existing.LogDirectory;
            }
            catch
            {
                // 读取失败(磁盘 IO 等)→ 继续用默认 seed,不影响 wizard 显示
            }
        }

        NextCommand = new RelayCommand(_ => GoNext(), _ => CanGoNext());
        BackCommand = new RelayCommand(_ => GoBack(), _ => CurrentStep != FirstRunWizardStep.Welcome);
        FinishCommand = new RelayCommand(_ => Finish(), _ => CurrentStep == FirstRunWizardStep.Confirm);
        CancelCommand = new RelayCommand(_ => Cancelled?.Invoke());
    }

    private bool CanGoNext() => CurrentStep switch
    {
        FirstRunWizardStep.Welcome => !string.IsNullOrWhiteSpace(_installPath),
        // v1.0.0.x (2026-09-05):合并 Python + Git 为 Step 2 ——
        // 两个字段都要 valid 才能 Next(IsPythonValid 强制文件存在,IsGitValid 允许空字符串走 PATH "git" fallback)。
        FirstRunWizardStep.PythonGit => IsPythonValid && IsGitValid,
        // Paths1-3 step: 路径字段可空(用户不填 → SettingsDefaults.Apply 后续再 seed),
        // 或填了也 OK。不强制必填。
        FirstRunWizardStep.Paths1EnvNodes => true,
        FirstRunWizardStep.Paths2ModelsWorkflows => true,
        FirstRunWizardStep.Paths3SystemLog => true,
        FirstRunWizardStep.Confirm => false,
        _ => false,
    };

    private void GoNext()
    {
        switch (CurrentStep)
        {
            case FirstRunWizardStep.Welcome: CurrentStep = FirstRunWizardStep.PythonGit; break;
            case FirstRunWizardStep.PythonGit: CurrentStep = FirstRunWizardStep.Paths1EnvNodes; break;
            case FirstRunWizardStep.Paths1EnvNodes: CurrentStep = FirstRunWizardStep.Paths2ModelsWorkflows; break;
            case FirstRunWizardStep.Paths2ModelsWorkflows: CurrentStep = FirstRunWizardStep.Paths3SystemLog; break;
            case FirstRunWizardStep.Paths3SystemLog: CurrentStep = FirstRunWizardStep.Confirm; break;
        }
        NextCommandCanExecuteChanged();
        BackCommandCanExecuteChanged();
    }

    private void GoBack()
    {
        switch (CurrentStep)
        {
            case FirstRunWizardStep.PythonGit: CurrentStep = FirstRunWizardStep.Welcome; break;
            case FirstRunWizardStep.Paths1EnvNodes: CurrentStep = FirstRunWizardStep.PythonGit; break;
            case FirstRunWizardStep.Paths2ModelsWorkflows: CurrentStep = FirstRunWizardStep.Paths1EnvNodes; break;
            case FirstRunWizardStep.Paths3SystemLog: CurrentStep = FirstRunWizardStep.Paths2ModelsWorkflows; break;
            case FirstRunWizardStep.Confirm: CurrentStep = FirstRunWizardStep.Paths3SystemLog; break;
        }
        NextCommandCanExecuteChanged();
        BackCommandCanExecuteChanged();
    }

    private void Finish()
    {
        // v1.0.0.x (2026-09-03) T34:用 SettingsRepository.Save 替代 legacy JsonSerializer
        // 写 .manager/settings.json。直接写最终格式 INF(避免 App.xaml.cs:282 的
        // legacy-to-INF 自动 migration,首启动少一道)。
        Directory.CreateDirectory(_appDataDir);
        var s = new Models.Settings();
        // v1.0.0.x (2026-09-05) bug fix:写 InstallPath 到 settings ——
        // 用户原话"为什么 wizard 结束之后不能够按照特定的路径启动呢"。
        // 之前 InstallPath 只在 wizard 显示,没存 settings,程序始终用 exe 目录作 projectRoot。
        // 现在存 settings.InstallPath,二次启动校验程序路径是否还在,
        // 如果用户搬了文件夹 → 走 IsFirstRun 路径重新弹 wizard(因为 projectRoot 变了,
        // MarkComplete 写到旧 path,新 path 找不到 firstrun.inf → 触发首启动 wizard 流程)。
        s.InstallPath = _installPath;
        // wizard 强制写入 user-confirmed Python 路径
        s.TemplatePythonDir = System.IO.Path.GetDirectoryName(_pythonPath) ?? "";
        s.DefaultPythonVersion = "";  // 已通过 PythonInterpreters 多解释器管理,清掉 legacy 字段
        if (s.PythonInterpreters.Count == 0)
        {
            s.PythonInterpreters.Add(new Models.PythonInterpreter
            {
                Name = "wizard-python",
                Path = _pythonPath,
            });
            s.ActivePythonInterpreterName = "wizard-python";
        }
        // v1.0.0.x (2026-09-05):写入 user-confirmed Git 路径(可能是绝对路径 / 留空走 PATH)
        s.GitExe = _gitPath;
        // 写 8 个 wizard-confirmed 路径(覆盖 default seed)
        s.SystemTemplateLibraryDir = _systemTemplateLibraryDir;
        s.EnvsDir = _envsDir;
        s.GlobalNodesDir = _globalNodesDir;
        s.LocalNodeDirectory = _localNodeDirectory;
        s.LocalNodesDirectory = _localNodesDirectory;
        s.DefaultModelsDirectory = _defaultModelsDirectory;
        s.WorkflowsDirectory = _workflowsDirectory;
        s.LogDirectory = _logDirectory;

        // v1.0.0.x (2026-09-05) bug fix:用 _projectRoot 不是 _appDataDir 构造 LocalDataPaths ——
        // 用户原话"H:\ComfyUIManagement\config\config 路径似乎写入错误"。
        // _appDataDir 已经是 <projectRoot>/config(包含 config 子目录),
        // 再 LocalDataPaths(_appDataDir) 会 Path.Combine(config/, "config") = config/config/,
        // settings.inf / firstrun.inf 写到嵌套 config/config/。
        // _projectRoot 才是真正的项目根(H:\ComfyUIManagement),LocalDataPaths 会自动
        // 拼上 config/ 子目录。
        new SettingsRepository(new LocalDataPaths(_projectRoot)).Save(s);

        // 同样修 MarkComplete:FirstRunDetector.IsFirstRun 内部 Path.Combine(appDataDir, "config", "firstrun.inf"),
        // _appDataDir 已经是 config 目录 → firstrun.inf 写到 config/config/。改传 _projectRoot 让它自己拼 config。
        FirstRunDetector.MarkComplete(_projectRoot);
        Completed?.Invoke();
    }

    private void NextCommandCanExecuteChanged()
    {
        NextCommand.RaiseCanExecuteChanged();
        FinishCommand.RaiseCanExecuteChanged();
    }
    private void BackCommandCanExecuteChanged() => BackCommand.RaiseCanExecuteChanged();

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

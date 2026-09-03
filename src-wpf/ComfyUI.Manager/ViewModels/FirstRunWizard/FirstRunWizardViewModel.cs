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
            OnPropertyChanged(nameof(IsPython));
            OnPropertyChanged(nameof(IsPaths1EnvNodes));
            OnPropertyChanged(nameof(IsPaths2ModelsWorkflows));
            OnPropertyChanged(nameof(IsPaths3SystemLog));
            OnPropertyChanged(nameof(IsConfirm));
        }
    }
    public bool IsWelcome => CurrentStep == FirstRunWizardStep.Welcome;
    public bool IsPython => CurrentStep == FirstRunWizardStep.Python;
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
        : this(appDataDir, System.IO.Path.GetDirectoryName(appDataDir) ?? appDataDir)
    {
    }

    public FirstRunWizardViewModel(string appDataDir, string projectRoot)
    {
        _appDataDir = appDataDir;
        _projectRoot = projectRoot;
        // 默认 seed 路径镜像 SettingsDefaults.Apply 模式 (projectRoot/Kind/)
        _systemTemplateLibraryDir = System.IO.Path.Combine(projectRoot, "ENVTemplate") + System.IO.Path.DirectorySeparatorChar;
        _envsDir = System.IO.Path.Combine(projectRoot, "Envs") + System.IO.Path.DirectorySeparatorChar;
        _globalNodesDir = System.IO.Path.Combine(projectRoot, "Nodes") + System.IO.Path.DirectorySeparatorChar;
        _localNodeDirectory = System.IO.Path.Combine(projectRoot, "LocalNodes") + System.IO.Path.DirectorySeparatorChar;
        _localNodesDirectory = System.IO.Path.Combine(projectRoot, "localnodes") + System.IO.Path.DirectorySeparatorChar;
        _defaultModelsDirectory = System.IO.Path.Combine(projectRoot, "Models") + System.IO.Path.DirectorySeparatorChar;
        _workflowsDirectory = System.IO.Path.Combine(projectRoot, "Workflow") + System.IO.Path.DirectorySeparatorChar;
        _logDirectory = System.IO.Path.Combine(projectRoot, "logs") + System.IO.Path.DirectorySeparatorChar;

        NextCommand = new RelayCommand(_ => GoNext(), _ => CanGoNext());
        BackCommand = new RelayCommand(_ => GoBack(), _ => CurrentStep != FirstRunWizardStep.Welcome);
        FinishCommand = new RelayCommand(_ => Finish(), _ => CurrentStep == FirstRunWizardStep.Confirm);
        CancelCommand = new RelayCommand(_ => Cancelled?.Invoke());
    }

    private bool CanGoNext() => CurrentStep switch
    {
        FirstRunWizardStep.Welcome => !string.IsNullOrWhiteSpace(_installPath),
        FirstRunWizardStep.Python => IsPythonValid,
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
            case FirstRunWizardStep.Welcome: CurrentStep = FirstRunWizardStep.Python; break;
            case FirstRunWizardStep.Python: CurrentStep = FirstRunWizardStep.Paths1EnvNodes; break;
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
            case FirstRunWizardStep.Python: CurrentStep = FirstRunWizardStep.Welcome; break;
            case FirstRunWizardStep.Paths1EnvNodes: CurrentStep = FirstRunWizardStep.Python; break;
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
        // 写 8 个 wizard-confirmed 路径(覆盖 default seed)
        s.SystemTemplateLibraryDir = _systemTemplateLibraryDir;
        s.EnvsDir = _envsDir;
        s.GlobalNodesDir = _globalNodesDir;
        s.LocalNodeDirectory = _localNodeDirectory;
        s.LocalNodesDirectory = _localNodesDirectory;
        s.DefaultModelsDirectory = _defaultModelsDirectory;
        s.WorkflowsDirectory = _workflowsDirectory;
        s.LogDirectory = _logDirectory;

        new SettingsRepository(new LocalDataPaths(_appDataDir)).Save(s);

        FirstRunDetector.MarkComplete(_appDataDir);
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

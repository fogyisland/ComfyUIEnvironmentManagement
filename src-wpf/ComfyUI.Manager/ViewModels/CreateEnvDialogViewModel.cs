using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

public class CreateEnvDialogViewModel : ViewModelBase
{
    private readonly EnvCreatorService _creator;
    private readonly Settings _settings;
    private readonly string _projectRoot;
    private string? _recentBasePythonPath;
    private readonly Action<Models.Environment?>? _onResult;
    private readonly IEnvironmentRepository? _envRepo;

    public CreateEnvDialogViewModel(
        EnvCreatorService creator,
        Settings settings,
        string projectRoot,
        string? recentBasePythonPath = null,
        Action<Models.Environment?>? onResult = null,
        IEnvironmentRepository? envRepo = null)
    {
        _creator = creator;
        _settings = settings;
        _projectRoot = projectRoot;
        _recentBasePythonPath = recentBasePythonPath;
        _onResult = onResult;
        _envRepo = envRepo;
        // v1.0.0 T7:模板 kind 选项从 Settings.Templates 来 — T2 seed ComfyUI + A1111。
        // 顺序无所谓,UI 用 SelectedTemplateKind 匹配 SelectedItem。
        TemplateOptions = _settings.Templates.Values
            .OrderBy(t => t.Kind)
            .ToList();

        CreateCommand = new RelayCommand(
            async _ => await CreateAsync(),
            _ => CanConfirm);
        CancelCommand = new RelayCommand(_ => Closed?.Invoke(null));
        ApplyTemplateCommand = new RelayCommand(_ =>
        {
            _recentBasePythonPath = null;
            ApplyTemplate();
        });
        // 初次填充 TemplateSource 从当前 SelectedTemplateKind(默认 ComfyUI)
        if (_settings.Templates.TryGetValue(_selectedTemplateKind, out var initial))
        {
            TemplateSource = initial.LocalSourceDir;
        }
        // v1.0.0.x:初次构造后跑一次 LocalDirExists 检查,设 TemplateWarningMessage + 触发 canExecute 重算。
        UpdateTemplateLocalAvailability();
        ApplyTemplate();   // 初次填充 PythonExe + 警告文案
        // v0.6.7.6:Port 默认填 MAX(port)+1,空 DB / 无 envRepo 时回落 8188
        if (_envRepo is not null)
        {
            try
            {
                var max = _envRepo.GetMaxPort();
                Port = ((max + 1) ?? 8188).ToString();
            }
            catch
            {
                Port = "8188";
            }
        }
        else
        {
            Port = "8188";
        }
    }

    public event Action<Models.Environment?>? Closed;

    /// <summary>
    /// v1.0.0 T7:模板 kind 选项列表(从 <see cref="Settings.Templates"/> 读,
    /// 顺序按 Kind 字母序,UI 用 ComboBox 选 <see cref="SelectedTemplateKind"/>)。
    /// 每项显示 <c>{Name} ({LocalSourceDir})</c> 让用户看到模板指向的实际目录。
    /// </summary>
    public IReadOnlyList<TemplateConfig> TemplateOptions { get; }

    private string _selectedTemplateKind = "ComfyUI";
    /// <summary>
    /// v1.0.0 T7:选中的 template kind(默认 ComfyUI)。
    /// Setter 自动从模板 <see cref="TemplateConfig.LocalSourceDir"/> 填充 <see cref="TemplateSource"/>;
    /// 设置未识别的 kind → TemplateSource 不变,CanConfirm 返回 false。
    /// </summary>
    public string SelectedTemplateKind
    {
        get => _selectedTemplateKind;
        set
        {
            if (SetField(ref _selectedTemplateKind, value))
            {
                // Auto-fill TemplateSource from the selected template
                if (_settings.Templates.TryGetValue(value, out var t))
                {
                    TemplateSource = t.LocalSourceDir;
                }
                // v1.0.0.x:用户反馈"在新建环境过程中如果模板本地目录为空,
                // 在选择对应环境的时候提示目标表环境本地为空,请现在本地模板中下载环境"。
                // 切 template 时算 LocalDirMissing,设 TemplateWarningMessage + 阻 Create 按钮(canExecute 守卫)。
                UpdateTemplateLocalAvailability();
                RaiseCommandsChanged();
            }
        }
    }

    private string _name = "";
    public string Name
    {
        get => _name;
        set { _name = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private string _pythonExe = "";
    public string PythonExe
    {
        get => _pythonExe;
        set { _pythonExe = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private string _templateSource = "";
    public string TemplateSource
    {
        get => _templateSource;
        set { _templateSource = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private string _port = "";
    public string Port
    {
        get => _port;
        set { _port = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private string _notes = "";
    /// <summary>
    /// v0.6.7.2:用户备注(例如"测试 SDXL 工作流")。空字符串视作 null,
    /// 不写进 DB(避免一长串空白行)。CreateEnvCommand 走 notes 参数传给 service。
    /// </summary>
    public string Notes
    {
        get => _notes;
        set { _notes = value; RaisePropertyChanged(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; RaisePropertyChanged(); }
    }

    private string? _templateWarningMessage;
    public string? TemplateWarningMessage
    {
        get => _templateWarningMessage;
        private set { _templateWarningMessage = value; RaisePropertyChanged(); }
    }

    public RelayCommand CreateCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ApplyTemplateCommand { get; }

    /// <summary>
    /// 步骤进度面板:CreateEnvDialog 启动时构造,N=6 个 CreateStepViewModel
    /// 与 EnvCreatorService.CreateAsync emit 的 6 个 CreateStepReport 一一对应。
    /// View 层 ItemsControl 绑定,DataTemplate 用 Status → Glyph/color DataTrigger。
    /// </summary>
    public ObservableCollection<CreateStepViewModel> Steps { get; } =
        new()
        {
            new CreateStepViewModel("校验输入"),
            new CreateStepViewModel("分配端口"),
            new CreateStepViewModel("创建 env 根目录"),
            new CreateStepViewModel("复制 template 源"),
            new CreateStepViewModel("链接 Models 目录"),
            new CreateStepViewModel("创建 venv 环境"),
        };

    internal void ResetSteps()
    {
        foreach (var s in Steps)
        {
            s.Detail = null;
            s.Status = CreateStepStatus.Pending;
        }
    }

    /// <summary>
    /// v1.0.0 T7:Replaces <c>CanCreate</c>。多了"选中的 template kind 必须在
    /// <see cref="TemplateOptions"/> 里"一项校验(防设置被清空时 UI 还显示合法)。
    /// v1.0.0.x:加"选中 template 的本地源码目录必须存在且非空" — 用户反馈模板本地为空时
    /// 不应让 Create 走通(避免后续 copy 失败),改为硬阻止 + 提示用户先去 TemplateManagement 下载。
    /// </summary>
    public bool CanConfirm
    {
        get
        {
            if (IsBusy) return false;
            if (string.IsNullOrWhiteSpace(Name)) return false;
            if (string.IsNullOrWhiteSpace(PythonExe)) return false;
            // TemplateSource 现在恒非空(始终 copy)。env-create 走 BuildTemplateConfig() — 必须有 source 才能 copy。
            if (string.IsNullOrWhiteSpace(TemplateSource)) return false;
            // T7:校验选中 kind 在 options 里(模板被删 / 设非法值时按钮灰)
            if (!TemplateOptions.Any(t => t.Kind == SelectedTemplateKind)) return false;
            // v1.0.0.x:模板本地目录必须存在 + 非空(由 UpdateTemplateLocalAvailability 检查)。
            if (IsSelectedTemplateLocalEmpty) return false;
            return true;
        }
    }

    /// <summary>
    /// v1.0.0.x:当前 SelectedTemplateKind 对应 template 的本地源码目录是否"空"。
    /// 由 <see cref="UpdateTemplateLocalAvailability"/> 写入 <see cref="_isSelectedTemplateLocalEmpty"/>,
    /// View 也可绑 IsEnabled 让 Create 按钮灰。
    /// </summary>
    public bool IsSelectedTemplateLocalEmpty
    {
        get => _isSelectedTemplateLocalEmpty;
        private set { _isSelectedTemplateLocalEmpty = value; RaisePropertyChanged(); }
    }
    private bool _isSelectedTemplateLocalEmpty;

    /// <summary>
    /// v1.0.0.x:每次 SelectedTemplateKind 切换 / ApplyTemplate 时调 — 跑 TemplateConfig.LocalDirExists
    /// 检查,设 <see cref="IsSelectedTemplateLocalEmpty"/> + 把对应警告注入 <see cref="TemplateWarningMessage"/>
    /// (已与 Python 解释器警告并列)。
    /// </summary>
    private void UpdateTemplateLocalAvailability()
    {
        if (_settings.Templates.TryGetValue(_selectedTemplateKind, out var t))
        {
            IsSelectedTemplateLocalEmpty = !t.LocalDirExists(_settings.SystemTemplateLibraryDir);
        }
        else
        {
            IsSelectedTemplateLocalEmpty = false;
        }
        AppendTemplateLocalWarning();
    }

    /// <summary>
    /// v1.0.0.x:把"模板本地为空"警告追加到 <see cref="TemplateWarningMessage"/>。不重写 Python
    /// 解释器警告 — 只在末尾追加一行(如有)。TemplateWarningMessage 是 string 拼接,
    /// 这里解析已有行 + 移除同 kind 旧行 + 加新行,避免重复。
    /// </summary>
    private void AppendTemplateLocalWarning()
    {
        const string EmptyTemplateWarning = "目标环境模板本地为空,请现在本地模板中下载环境";
        var existing = (TemplateWarningMessage ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Trim() != EmptyTemplateWarning)
            .ToList();
        if (IsSelectedTemplateLocalEmpty) existing.Add(EmptyTemplateWarning);
        TemplateWarningMessage = existing.Count == 0 ? null : string.Join("\n", existing);
        RaiseCommandsChanged();
    }

    /// <summary>
    /// v1.0.0 T7:从对话框当前状态(<see cref="SelectedTemplateKind"/> + <see cref="TemplateSource"/> + <see cref="PythonExe"/>)
    /// 构造一个 <see cref="TemplateConfig"/> 传给 <c>EnvCreatorService.CreateAsync</c>。
    /// SelectedTemplateKind 不在 Settings.Templates 里时兜底 ComfyUI kind。
    /// </summary>
    internal TemplateConfig BuildTemplateConfig()
    {
        var template = _settings.Templates.TryGetValue(SelectedTemplateKind, out var t)
            ? t
            : TemplateConfigDefaults.ComfyUi(_projectRoot);
        return new TemplateConfig
        {
            Kind = SelectedTemplateKind,
            Name = template.Name,
            // v1.0.0.x: BuildTemplateConfig 不预先 resolve 路径 — 锚点跟 Service 端
            // TemplatePathResolver.Resolve(localSourceDir, _settings.SystemTemplateLibraryDir)
            // 不一致会埋坑。Service 是权威:TemplateConfigDefaults 里 LocalSourceDir 存的是
            // 相对路径("ComfyUI"/"A1111"),Service 端按 SystemTemplateLibraryDir (用户配的
            // 系统模板库目录,例如 D:\ToolDevelop\ComfyUI\ENVTemplate) 解析。Dialog 这里
            // 原样传 TemplateSource(用户编辑过的文本),绝对路径也照样 service 端 IsPathRooted
            // 返回原值,行为不变。
            LocalSourceDir = TemplateSource,
            EntryScript = template.EntryScript,
            EntryArgs = template.EntryArgs,
            ModelsSubdir = template.ModelsSubdir,
            ExtraJunctionTargets = new(template.ExtraJunctionTargets),
            UserExtraArgs = template.UserExtraArgs,
        };
    }

    /// <summary>
    /// 从 settings 读 ActivePythonInterpreterName + PythonInterpreters
    /// 填回 PythonExe(警告文案)。TemplateSource 走 <see cref="SelectedTemplateKind"/> 自动填充,
    /// 这里不再用 TemplateComfyuiDir 拼(projectRoot)。
    /// Python 解释器缺失/路径不存在时设 TemplateWarningMessage 警告(spec §2.5 文案)。
    /// </summary>
    public void ApplyTemplate()
    {
        var warnings = new List<string>();

        if (!string.IsNullOrEmpty(_recentBasePythonPath) && File.Exists(_recentBasePythonPath))
        {
            PythonExe = _recentBasePythonPath;
        }
        else
        {
            var active = _settings.PythonInterpreters
                .FirstOrDefault(p => p.Name == _settings.ActivePythonInterpreterName);
            // v1.0.0.x: 解释器 Path 存相对路径(SettingsDefaults.Apply auto-seed 时用相对
            // 路径存盘,跟 EnvsDir / Models 一样 green-portable),运行时拼 projectRoot 解析。
            // 绝对路径(用户手动 Browse 选的)原样返回。
            var rawPath = active?.Path ?? "";
            PythonExe = !string.IsNullOrEmpty(rawPath) && !Path.IsPathRooted(rawPath)
                ? Path.Combine(_projectRoot, rawPath)
                : rawPath;
        }

        // —— spec §2.5 警告文案 ——
        if (string.IsNullOrEmpty(PythonExe))
        {
            warnings.Add("请在设置页添加 Python 解释器");
        }
        else if (!File.Exists(PythonExe))
        {
            warnings.Add("当前 Python 解释器路径不存在,请检查设置");
        }

        // v1.0.0.x:TemplateSource 始终填 LocalSourceDir(选中 template 的目标目录名),
        // 即便本地目录为空 — 让用户看到「待 clone 的目录」名,而不是空白让人误解。
        // 本地目录是否可用交给 UpdateTemplateLocalAvailability 统一管(红色警告 +
        // CanConfirm gate);TemplateSource 只是文本提示,跟 LocalDirExists 解耦。
        if (_settings.Templates.TryGetValue(SelectedTemplateKind, out var t))
        {
            TemplateSource = t.LocalSourceDir;
        }
        else
        {
            TemplateSource = "";
        }

        // 先把 Python 警告设上,AppendTemplateLocalWarning 会追加"模板本地为空"行(如有)
        TemplateWarningMessage = warnings.Count == 0
            ? null
            : string.Join("\n", warnings);
        UpdateTemplateLocalAvailability();
    }

    private async Task CreateAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        ResetSteps();

        // Progress<T> 在 UI 线程构造 → 自动捕获 SynchronizationContext,
        // env creator 在 taskpool 调 Report 时回调自动 marshal 回 UI 线程。
        var progress = new Progress<CreateStepReport>(OnStepReport);

        try
        {
            int? port = null;
            if (int.TryParse(Port, out var p) && p > 0) port = p;

            var env = await _creator.CreateAsync(
                Name, BuildTemplateConfig(),
                PythonExe,
                port,
                string.IsNullOrWhiteSpace(Notes) ? null : Notes,
                CancellationToken.None,
                progress);
            Closed?.Invoke(env);
        }
        catch (EnvCreatorService.CreateEnvException ex)
        {
            ErrorMessage = $"{ex.Code}: {ex.Message}";
            MarkCurrentStepFailed();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            MarkCurrentStepFailed();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// OnStepReport:env creator emit 的 step 名 → 匹配 Steps 里的 entry。
    /// 顺序匹配定位 idx,把 idx 之前所有 Pending/Running 的 step 关掉成 Done,
    /// 再把 idx 那个 step 标 Running + 更新 Detail。
    /// Service 是顺序 emit,这样能精准对应 step 切换,
    /// 不会出现 "前一个 step 还 Running 当前 step 就变 Running" 的状态。
    /// </summary>
    internal void OnStepReport(CreateStepReport report)
    {
        var idx = -1;
        for (int i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].Name == report.Name) { idx = i; break; }
        }
        if (idx < 0)
        {
            // service emit 了 Steps 里没有的 step,忽略(开发期暴露)
            return;
        }

        // 关闭 idx 之前所有未完成的 step
        for (int i = 0; i < idx; i++)
        {
            var s = Steps[i];
            if (s.Status == CreateStepStatus.Pending || s.Status == CreateStepStatus.Running)
            {
                s.Status = CreateStepStatus.Done;
            }
        }

        var current = Steps[idx];
        current.Status = CreateStepStatus.Running;
        current.Detail = report.Detail;
    }

    private void MarkCurrentStepFailed()
    {
        var current = Steps.FirstOrDefault(s => s.Status == CreateStepStatus.Running);
        if (current != null)
        {
            current.Status = CreateStepStatus.Failed;
        }
        else
        {
            // 失败发生在第一个 Report 之前(校验阶段)→ 第一个 step 标 Failed
            var first = Steps.FirstOrDefault(s => s.Status == CreateStepStatus.Pending);
            if (first != null) first.Status = CreateStepStatus.Failed;
        }
    }

    private void RaiseCommandsChanged()
    {
        CreateCommand.RaiseCanExecuteChanged();
    }
}
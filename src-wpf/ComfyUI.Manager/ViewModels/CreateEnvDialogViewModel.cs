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
        CreateCommand = new RelayCommand(
            async _ => await CreateAsync(),
            _ => CanCreate());
        CancelCommand = new RelayCommand(_ => Closed?.Invoke(null));
        ApplyTemplateCommand = new RelayCommand(_ =>
        {
            _recentBasePythonPath = null;
            ApplyTemplate();
        });
        ApplyTemplate();   // 初次填充
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

    public System.Collections.Generic.List<string> LayoutOptions { get; } =
        new() { "shared", "independent" };

    private string _name = "";
    public string Name
    {
        get => _name;
        set { _name = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private string _layout = "shared";
    public string Layout
    {
        get => _layout;
        // 决策 2:layout 切换不重新 auto-fill,只 RaisePropertyChanged + RaiseCommandsChanged
        set { _layout = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private string _pythonExe = "";
    public string PythonExe
    {
        get => _pythonExe;
        set { _pythonExe = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private string _comfyuiSource = "";
    public string ComfyuiSource
    {
        get => _comfyuiSource;
        set { _comfyuiSource = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
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
            new CreateStepViewModel("链接 ComfyUI 源"),
            new CreateStepViewModel("创建 venv 环境"),
            new CreateStepViewModel("保存配置"),
        };

    internal void ResetSteps()
    {
        foreach (var s in Steps)
        {
            s.Detail = null;
            s.Status = CreateStepStatus.Pending;
        }
    }

    public bool CanCreate()
    {
        if (IsBusy) return false;
        if (string.IsNullOrWhiteSpace(Name)) return false;
        if (string.IsNullOrWhiteSpace(PythonExe)) return false;
        // v1.0.0 T4:ComfyuiSource 现在恒非空(始终 copy)。Layout 字段保留向后兼容 UI,
        // 但 env-create 走 BuildTemplateConfig() — 必须有 source 才能 copy。
        if (string.IsNullOrWhiteSpace(ComfyuiSource)) return false;
        return true;
    }

    /// <summary>
    /// v1.0.0 T4:从对话框当前状态(<see cref="Layout"/> + <see cref="ComfyuiSource"/> + <see cref="PythonExe"/>)
    /// 构造一个 <see cref="TemplateConfig"/> 传给 <c>EnvCreatorService.CreateAsync</c>。
    /// Layout == "shared" 视作 ComfyUI kind(现有行为),其他 layout 走非 ComfyUI 路径。
    /// 测试用 main.py 入口;EntryArgs 默认带 {port} 占位符让 user 在 Port 输入框生效。
    /// </summary>
    internal TemplateConfig BuildTemplateConfig()
    {
        // 现有 dialog 只支持 ComfyUI 模板路径,T7 会替换为完整 template picker。
        // 现在固定 Kind = "ComfyUI",通过 Settings.Templates["ComfyUI"] 兜底拿到 entry 配置。
        var comfyTemplate = _settings.Templates.TryGetValue("ComfyUI", out var t)
            ? t
            : TemplateConfigDefaults.ComfyUi(_projectRoot);
        return new TemplateConfig
        {
            Kind = "ComfyUI",
            Name = comfyTemplate.Name,
            LocalSourceDir = ComfyuiSource,
            EntryScript = comfyTemplate.EntryScript,
            EntryArgs = comfyTemplate.EntryArgs,
            ModelsSubdir = comfyTemplate.ModelsSubdir,
            ExtraJunctionTargets = new(comfyTemplate.ExtraJunctionTargets),
            UserExtraArgs = comfyTemplate.UserExtraArgs,
        };
    }

    /// <summary>
    /// 从 settings 读 ActivePythonInterpreterName + PythonInterpreters + TemplateComfyuiDir
    /// + projectRoot 拼接,填回 PythonExe + ComfyuiSource。Python 解释器缺失/路径不存在时
    /// 设 TemplateWarningMessage 警告(spec §2.5 文案)。
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
            PythonExe = active?.Path ?? "";
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

        var comfyuiSource = Path.Combine(
            _projectRoot,
            _settings.TemplateComfyuiDir);

        if (Directory.Exists(comfyuiSource))
        {
            ComfyuiSource = comfyuiSource;
        }
        else
        {
            warnings.Add("ComfyUI 模板目录未安装,请先在设置页下载");
            ComfyuiSource = "";
        }

        TemplateWarningMessage = warnings.Count == 0
            ? null
            : string.Join("\n", warnings);
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
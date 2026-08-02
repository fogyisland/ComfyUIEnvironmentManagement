using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

    public CreateEnvDialogViewModel(
        EnvCreatorService creator,
        Settings settings,
        string projectRoot,
        string? recentBasePythonPath = null,
        Action<Models.Environment?>? onResult = null)
    {
        _creator = creator;
        _settings = settings;
        _projectRoot = projectRoot;
        _recentBasePythonPath = recentBasePythonPath;
        _onResult = onResult;
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

    public bool CanCreate()
    {
        if (IsBusy) return false;
        if (string.IsNullOrWhiteSpace(Name)) return false;
        if (string.IsNullOrWhiteSpace(PythonExe)) return false;
        if (Layout == "shared" && string.IsNullOrWhiteSpace(ComfyuiSource)) return false;
        return true;
    }

    /// <summary>
    /// 从 settings 读 TemplatePythonDir + DefaultPythonVersion + TemplateComfyuiDir +
    /// projectRoot 拼接,填回 PythonExe + ComfyuiSource。模板缺失时静默留空 +
    /// TemplateWarningMessage 设警告。
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
            var pythonExe = Path.Combine(
                _projectRoot,
                _settings.TemplatePythonDir,
                _settings.DefaultPythonVersion,
                "python.exe");

            if (File.Exists(pythonExe))
            {
                PythonExe = pythonExe;
            }
            else
            {
                warnings.Add($"Python 模板 {_settings.DefaultPythonVersion} 未安装,请先在设置页下载");
                PythonExe = "";
            }
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
        try
        {
            int? port = null;
            if (int.TryParse(Port, out var p) && p > 0) port = p;

            var env = await _creator.CreateAsync(
                Name, Layout, PythonExe,
                string.IsNullOrWhiteSpace(ComfyuiSource) ? null : ComfyuiSource,
                port,
                CancellationToken.None);
            Closed?.Invoke(env);
        }
        catch (EnvCreatorService.CreateEnvException ex)
        {
            ErrorMessage = $"{ex.Code}: {ex.Message}";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseCommandsChanged()
    {
        CreateCommand.RaiseCanExecuteChanged();
    }
}
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ComfyUI.Manager.Services.FirstRun;

namespace ComfyUI.Manager.ViewModels.FirstRunWizard;

public class FirstRunWizardViewModel : INotifyPropertyChanged
{
    private readonly string _appDataDir;
    private FirstRunWizardStep _currentStep = FirstRunWizardStep.Welcome;
    private string _installPath = "";
    private string _pythonPath = "";

    public FirstRunWizardStep CurrentStep
    {
        get => _currentStep;
        private set { _currentStep = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsWelcome)); OnPropertyChanged(nameof(IsPython)); OnPropertyChanged(nameof(IsConfirm)); }
    }
    public bool IsWelcome => CurrentStep == FirstRunWizardStep.Welcome;
    public bool IsPython => CurrentStep == FirstRunWizardStep.Python;
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

    public RelayCommand NextCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand FinishCommand { get; }
    public RelayCommand CancelCommand { get; }

    public event Action? Completed;
    public event Action? Cancelled;
    public event PropertyChangedEventHandler? PropertyChanged;

    public FirstRunWizardViewModel(string appDataDir)
    {
        _appDataDir = appDataDir;
        NextCommand = new RelayCommand(_ => GoNext(), _ => CanGoNext());
        BackCommand = new RelayCommand(_ => GoBack(), _ => CurrentStep != FirstRunWizardStep.Welcome);
        FinishCommand = new RelayCommand(_ => Finish(), _ => CurrentStep == FirstRunWizardStep.Confirm);
        CancelCommand = new RelayCommand(_ => Cancelled?.Invoke());
    }

    private bool CanGoNext() => CurrentStep switch
    {
        FirstRunWizardStep.Welcome => !string.IsNullOrWhiteSpace(_installPath),
        FirstRunWizardStep.Python => IsPythonValid,
        FirstRunWizardStep.Confirm => false,
        _ => false,
    };

    private void GoNext()
    {
        if (CurrentStep == FirstRunWizardStep.Welcome) CurrentStep = FirstRunWizardStep.Python;
        else if (CurrentStep == FirstRunWizardStep.Python) CurrentStep = FirstRunWizardStep.Confirm;
        NextCommandCanExecuteChanged();
        BackCommandCanExecuteChanged();
    }

    private void GoBack()
    {
        if (CurrentStep == FirstRunWizardStep.Python) CurrentStep = FirstRunWizardStep.Welcome;
        else if (CurrentStep == FirstRunWizardStep.Confirm) CurrentStep = FirstRunWizardStep.Python;
        NextCommandCanExecuteChanged();
        BackCommandCanExecuteChanged();
    }

    private void Finish()
    {
        // Write settings + sentinel via detector
        var settingsPath = System.IO.Path.Combine(_appDataDir, FirstRunDetector.SettingsFileName);
        var s = System.IO.File.Exists(settingsPath)
            ? System.Text.Json.JsonSerializer.Deserialize<Models.Settings>(System.IO.File.ReadAllText(settingsPath))
            : new Models.Settings();
        if (s is null) s = new Models.Settings();
        // wizard 强制写入 user-confirmed Python 路径
        s.TemplatePythonDir = System.IO.Path.GetDirectoryName(_pythonPath) ?? "";
        s.DefaultPythonVersion = "";  // 已通过 PythonInterpreters 多解释器管理,清掉 legacy 字段
        // 把至少一条 PythonInterpreters 加进去,让 SettingsViewModel.PythonInterpreters 有内容
        if (s.PythonInterpreters.Count == 0)
        {
            s.PythonInterpreters.Add(new Models.PythonInterpreter
            {
                Name = "wizard-python",
                Path = _pythonPath,
            });
            s.ActivePythonInterpreterName = "wizard-python";
        }
        Directory.CreateDirectory(_appDataDir);
        System.IO.File.WriteAllText(settingsPath,
            System.Text.Json.JsonSerializer.Serialize(s,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

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
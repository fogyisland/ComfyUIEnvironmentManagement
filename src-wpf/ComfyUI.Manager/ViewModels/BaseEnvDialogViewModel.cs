using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ComfyUI.Manager.Models;
using EnvModel = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// EnvList 行的 checkbox 包装(VM 化,IsChecked 双向 bind)。
/// </summary>
public sealed class EnvChoice : ViewModelBase
{
    public EnvModel Env { get; }
    private bool _isChecked;

    public EnvChoice(EnvModel env, bool isChecked = false)
    {
        Env = env;
        _isChecked = isChecked;
    }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetField(ref _isChecked, value);
    }
}

/// <summary>
/// BaseEnvDialog 关闭时的回调 payload(null = 取消)。
/// </summary>
public sealed record BaseEnvDialogResult(
    IReadOnlyList<string> SelectedEnvIds,
    BaseEnvConfig Config);

/// <summary>
/// 基础环境部署 Dialog 的 VM:
/// - 左边 env 多选(checkbox,默认全不选)
/// - 右边 BaseEnvConfig 副本 + 包列表 CRUD
/// - 预览 pip 命令(实时)
/// - StartCommand(校验 ≥1 env + ≥1 package)
/// </summary>
public class BaseEnvDialogViewModel : ViewModelBase
{
    private string _newPackageName = "";

    public BaseEnvDialogViewModel(
        IList<EnvModel> envs,
        BaseEnvConfig sourceConfig)
    {
        Envs = new ObservableCollection<EnvChoice>(
            envs.Select(e => new EnvChoice(e)));
        Config = sourceConfig.Clone();
        Packages = new ObservableCollection<string>(Config.Packages);
        Packages.CollectionChanged += (_, _) =>
        {
            Config.Packages = new List<string>(Packages);
            RaisePropertyChanged(nameof(PreviewCommandText));
            StartCommand.RaiseCanExecuteChanged();
        };
        AddPackageCommand = new RelayCommand(_ => AddPackage(), _ => !string.IsNullOrWhiteSpace(NewPackageName));
        RemovePackageCommand = new RelayCommand(p =>
        {
            if (p is string s) Packages.Remove(s);
        });
        StartCommand = new RelayCommand(
            _ => Start(),
            _ => CanStart());
        CancelCommand = new RelayCommand(_ => Closed?.Invoke(null));
        RaisePropertyChanged(nameof(PreviewCommandText));
    }

    public ObservableCollection<EnvChoice> Envs { get; }
    public BaseEnvConfig Config { get; }

    public IEnumerable<string> CudaVersions { get; } =
        new[] { "cu118", "cu121", "cu124", "cpu" };
    public IEnumerable<string> TorchChannels { get; } =
        new[] { "stable", "nightly" };

    public ObservableCollection<string> Packages { get; }

    /// <summary>
    /// 只读预览:`pip install <args>`。Package 增删 / CustomPipArgs 改 / CudaVersion 改都触发重算。
    /// </summary>
    public string PreviewCommandText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Config.CustomPipArgs))
            {
                return "pip " + string.Join(' ', Config.CustomPipArgs
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
            var args = new List<string> { "install" };
            args.AddRange(Packages);
            if (Config.TorchChannel == "nightly") args.Add("--pre");
            if (!string.IsNullOrWhiteSpace(Config.CudaVersion) && Config.CudaVersion != "cpu")
            {
                args.Add("--index-url");
                args.Add($"https://download.pytorch.org/whl/{Config.CudaVersion}");
            }
            if (!string.IsNullOrWhiteSpace(Config.ExtraArgs))
            {
                args.AddRange(Config.ExtraArgs
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
            return "pip " + string.Join(' ', args);
        }
    }

    public string NewPackageName
    {
        get => _newPackageName;
        set
        {
            if (SetField(ref _newPackageName, value))
            {
                AddPackageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand AddPackageCommand { get; }
    public RelayCommand RemovePackageCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }

    public event Action<BaseEnvDialogResult?>? Closed;

    private void AddPackage()
    {
        var name = _newPackageName.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (!Packages.Contains(name)) Packages.Add(name);
        NewPackageName = "";
    }

    private bool CanStart()
    {
        if (Packages.Count == 0) return false;
        return Envs.Any(c => c.IsChecked);
    }

    private void Start()
    {
        var selected = Envs.Where(c => c.IsChecked).Select(c => c.Env.Id).ToList();
        Closed?.Invoke(new BaseEnvDialogResult(selected, Config));
    }
}
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// RequirementsStatusViewModel:env-list 下方"装依赖" inline 状态面板的 VM。
///
/// 跟 EnvStartStatusViewModel 同模式(observable collection + IsVisible + Error + Hide),
/// 但 RequirementsInstaller 是单阶段、单 env 的,没有 3 阶段概念 — 用纯 StatusText 显示
/// 当前进度/结果,LogLines 滚 pip stdout/stderr。
///
/// 行为:
/// - RunAsync() 后 IsVisible=true,挂 Progress<string> 自动 marshal 回 UI 线程
/// - 成功 → StatusText 设"装依赖完成(N 个包)",延迟 2s 自动 Hide
/// - 失败/取消 → Error 设原因,IsVisible 保持,等用户手动关(由 UI 提供关闭按钮)
/// </summary>
public sealed class RequirementsStatusViewModel : ViewModelBase
{
    private const int MaxLogLines = 200;
    private readonly Environment _env;
    private readonly RequirementsInstaller _installer;
    private CancellationTokenSource? _cts;

    public RequirementsStatusViewModel(Environment env, RequirementsInstaller installer)
    {
        _env = env;
        _installer = installer;
        StatusText = "准备开始...";
        CancelCommand = new RelayCommand(
            _ => _cts?.Cancel(),
            _ => _cts is { IsCancellationRequested: false });
    }

    public string EnvName => _env.Name;
    public string StatusText { get; private set; }
    public ObservableCollection<string> LogLines { get; } = new();
    public string? Error { get; private set; }
    public bool IsVisible { get; private set; }
    public bool IsComplete { get; private set; }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public RelayCommand CancelCommand { get; }

    /// <summary>
    /// 触发整个装依赖流程。InstallAsync 失败 / 取消 / 成功都通过返回值 / Error 反映,
    /// 不抛异常给调用方。
    /// </summary>
    public async Task RunAsync()
    {
        IsVisible = true;
        RaisePropertyChanged(nameof(IsVisible));

        _cts = new CancellationTokenSource();
        // Progress<T> 构造时捕获 UI 线程 SynchronizationContext,
        // 后台线程 Report 自动 marshal 回 UI — 不然 LogLines 在后台线程改会触发
        // WPF "某个 itemscontrol 与它的项源不一致"(v0.6.5.11 hotfix 学到的)。
        var progress = new Progress<string>(OnLogLine);
        try
        {
            var result = await _installer.InstallAsync(_env, progress, _cts.Token);
            ApplyResult(result);
        }
        catch (Exception ex)
        {
            Fail($"装依赖异常:{ex.Message}");
        }
        finally
        {
            // CancelCommand 状态变化 → 通知 UI 重新 query CanExecute
            RaisePropertyChanged(nameof(CancelCommand));
        }
    }

    private void OnLogLine(string line)
    {
        LogLines.Add(line);
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
        StatusText = $"{_env.Name} — {line}";
        RaisePropertyChanged(nameof(StatusText));
    }

    private void ApplyResult(RequirementsInstallResult result)
    {
        IsComplete = true;
        if (result.Cancelled)
        {
            Error = "用户取消";
            StatusText = $"{_env.Name} — 用户取消";
        }
        else if (result.Success)
        {
            StatusText = $"{_env.Name} — 装依赖完成({result.InstalledCount} 个包)";
        }
        else
        {
            Error = result.Reason ?? "未知错误";
            StatusText = $"{_env.Name} — {Error}";
        }
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(Error));
        RaisePropertyChanged(nameof(HasError));
        RaisePropertyChanged(nameof(IsComplete));
    }

    public void Fail(string reason)
    {
        IsComplete = true;
        Error = reason;
        StatusText = $"{_env.Name} — {reason}";
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(Error));
        RaisePropertyChanged(nameof(HasError));
        RaisePropertyChanged(nameof(IsComplete));
    }

    public void Hide()
    {
        IsVisible = false;
        IsComplete = false;
        Error = null;
        LogLines.Clear();
        StatusText = "准备开始...";
        RaisePropertyChanged(nameof(IsVisible));
        RaisePropertyChanged(nameof(IsComplete));
        RaisePropertyChanged(nameof(Error));
        RaisePropertyChanged(nameof(HasError));
        RaisePropertyChanged(nameof(StatusText));
    }
}
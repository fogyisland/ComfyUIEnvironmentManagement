using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// RequirementsProgressDialog 的 VM:订阅 RequirementsInstaller.InstallAsync 的 log,
/// 维护 LogTail / StatusText / OverallStatus,提供 CancelCommand。
///
/// 跟 BaseEnvProgressViewModel 不同 — single-env,无 Completed/Total/EnvPercent。
/// </summary>
public class RequirementsProgressViewModel : ViewModelBase
{
    private const int MaxLogLines = 200;
    private readonly Environment _env;
    private readonly RequirementsInstaller _installer;
    private CancellationTokenSource? _cts;
    private Task<RequirementsInstallResult>? _runningTask;

    private readonly Queue<string> _logTail = new();

    public RequirementsProgressViewModel(Environment env, RequirementsInstaller installer)
    {
        _env = env;
        _installer = installer;
        StatusText = "准备开始...";
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(),
            _ => _cts is { IsCancellationRequested: false });
    }

    public string EnvName => _env.Name;
    public string StatusText { get; private set; }
    public string LogTail
    {
        get { lock (_logTail) return string.Join("\n", _logTail); }
    }
    public RequirementsInstallStatus OverallStatus { get; private set; } = RequirementsInstallStatus.Pending;

    public RelayCommand CancelCommand { get; }

    public Task<RequirementsInstallResult> RunAsync()
    {
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(OnLogLine);
        _runningTask = _installer.InstallAsync(_env, progress, _cts.Token);
        return _runningTask;
    }

    public void OnLogLine(string line)
    {
        lock (_logTail)
        {
            _logTail.Enqueue(line);
            while (_logTail.Count > MaxLogLines) _logTail.Dequeue();
        }
        // 状态文本:显示最后一行
        StatusText = $"{_env.Name} — {line}";
        RaisePropertyChanged(nameof(LogTail));
        RaisePropertyChanged(nameof(StatusText));
    }

    public void OnCompleted(RequirementsInstallResult result)
    {
        OverallStatus = result.Cancelled
            ? RequirementsInstallStatus.Cancelled
            : (result.Success ? RequirementsInstallStatus.Succeeded : RequirementsInstallStatus.Failed);

        if (OverallStatus == RequirementsInstallStatus.Failed && !string.IsNullOrEmpty(result.Reason))
        {
            StatusText = $"{_env.Name} — {result.Reason}";
            RaisePropertyChanged(nameof(StatusText));
        }
        else if (OverallStatus == RequirementsInstallStatus.Succeeded)
        {
            StatusText = $"{_env.Name} — 装依赖完成({result.InstalledCount} 个包)";
            RaisePropertyChanged(nameof(StatusText));
        }
        RaisePropertyChanged(nameof(OverallStatus));
        RaisePropertyChanged(nameof(CancelCommand));  // 让按钮变 disabled
    }
}

public enum RequirementsInstallStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

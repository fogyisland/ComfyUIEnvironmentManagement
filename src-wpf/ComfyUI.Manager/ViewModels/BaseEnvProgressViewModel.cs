using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// BaseEnvProgressDialog 的 VM:订阅 BaseEnvInstaller.InstallAsync 的 progress,
/// 维护 Completed / EnvPercent / LogTail / OverallStatus 状态,提供 CancelCommand。
///
/// LogTail 只显示最近 200 行(避免无界增长)。
/// </summary>
public class BaseEnvProgressViewModel : ViewModelBase
{
    private const int MaxLogLines = 200;
    private readonly IReadOnlyList<string> _envIds;
    private readonly BaseEnvConfig _config;
    private readonly BaseEnvInstaller _installer;
    private CancellationTokenSource? _cts;
    private Task<BaseEnvInstallResult>? _runningTask;

    private readonly Queue<string> _logTail = new();

    public BaseEnvProgressViewModel(
        IReadOnlyList<string> envIds,
        BaseEnvConfig config,
        BaseEnvInstaller installer)
    {
        _envIds = envIds;
        _config = config;
        _installer = installer;
        Total = envIds.Count;
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => _cts is { IsCancellationRequested: false });
    }

    public int Completed { get; private set; }
    public int Total { get; }
    public int EnvPercent { get; private set; }
    public string StatusText { get; private set; } = "准备开始...";
    public string LogTail
    {
        get
        {
            lock (_logTail) return string.Join("\n", _logTail);
        }
    }
    public BaseEnvStatus OverallStatus { get; private set; } = BaseEnvStatus.Pending;

    public RelayCommand CancelCommand { get; }

    public Task<BaseEnvInstallResult> RunAsync()
    {
        _cts = new CancellationTokenSource();
        var progress = new Progress<BaseEnvProgress>(OnProgress);
        // TEMP T5: BaseEnvInstaller.InstallAsync now takes BaseEnvProfile. T4 keeps _config
        // (BaseEnvConfig) for backward compatibility with T5's plumbing change. Replace
        // this placeholder with the real profile passed in via VM ctor/RunAsync.
        _runningTask = _installer.InstallAsync(_envIds, new BaseEnvProfile(), progress, _cts.Token);
        return _runningTask;
    }

    public void OnProgress(BaseEnvProgress p)
    {
        Completed = p.Completed;
        if (p.EnvPercent.HasValue) EnvPercent = p.EnvPercent.Value;
        if (!string.IsNullOrEmpty(p.LogLine))
        {
            lock (_logTail)
            {
                _logTail.Enqueue(p.LogLine);
                while (_logTail.Count > MaxLogLines) _logTail.Dequeue();
            }
            RaisePropertyChanged(nameof(LogTail));
        }

        // 状态文本:envName — logLine or error
        if (!string.IsNullOrEmpty(p.CurrentEnvName))
        {
            if (!string.IsNullOrEmpty(p.ErrorMessage))
            {
                StatusText = $"{p.CurrentEnvName} — {p.ErrorMessage}";
            }
            else if (!string.IsNullOrEmpty(p.LogLine))
            {
                StatusText = $"{p.CurrentEnvName} — {p.LogLine}";
            }
            else
            {
                StatusText = $"{p.CurrentEnvName} — {p.Status}";
            }
        }

        // 整体状态优先级:任一失败 → Failed;取消 → Cancelled;全成功 → Succeeded
        if (p.Status == BaseEnvStatus.Failed && OverallStatus != BaseEnvStatus.Failed)
        {
            OverallStatus = BaseEnvStatus.Failed;
        }
        else if (p.Status == BaseEnvStatus.Cancelled)
        {
            OverallStatus = BaseEnvStatus.Cancelled;
        }
        else if (p.Status == BaseEnvStatus.Succeeded && Completed == Total
                 && OverallStatus != BaseEnvStatus.Failed)
        {
            OverallStatus = BaseEnvStatus.Succeeded;
        }

        RaisePropertyChanged(nameof(Completed));
        RaisePropertyChanged(nameof(EnvPercent));
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(OverallStatus));
    }
}

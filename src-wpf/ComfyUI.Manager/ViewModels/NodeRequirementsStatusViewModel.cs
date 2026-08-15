using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.15.6:本地节点列表页下方"装节点依赖" inline 状态面板的 VM。
///
/// 跟 <see cref="RequirementsStatusViewModel"/> 同模式(observable collection +
/// IsVisible + Error + Hide),但驱动的是 <see cref="RequirementsInstaller.InstallNodeRequirementsAsync"/>
/// (节点自己的 requirements.txt),不是 env 的 ComfyUI requirements.txt。
///
/// 显示标题用 <c>{EnvName} / {NodeId}</c>,StatusText 第一行写明节点 + env,
/// 失败 / 取消 / 成功的文本跟 env-level 那块保持一致风格。
///
/// 设计取舍:
/// - 不写 marker / 不触发 ComfyUI Manager / 常用节点自动装 — 那些是 env-level
/// - 节点没有 requirements.txt → InstallNodeRequirementsAsync 返 Success(reason="节点无 requirements.txt")
///   → 走 "无依赖" 完成路径,StatusText="无 requirements.txt",2s 后 Hide
/// </summary>
public sealed class NodeRequirementsStatusViewModel : ViewModelBase
{
    private const int MaxLogLines = 200;
    private readonly RequirementsInstaller _installer;
    private readonly Environment _env;
    private readonly string _nodeId;
    private readonly string _nodeDir;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// v0.6.15.7:测试 seam — 覆盖默认 2000ms / 5000ms,加速单测。
    /// 生产代码不设这些字段(保持 2s/5s 默认)。
    /// </summary>
    public int FadeDelaySuccessMs { get; set; } = 2000;
    public int FadeDelayFailureMs { get; set; } = 5000;

    private CancellationTokenSource? _hideCts;

    public NodeRequirementsStatusViewModel(
        Environment env, string nodeId, string nodeDir, RequirementsInstaller installer)
    {
        _env = env;
        _nodeId = nodeId;
        _nodeDir = nodeDir;
        _installer = installer;
        StatusText = "准备开始...";
        CancelCommand = new RelayCommand(
            _ => _cts?.Cancel(),
            _ => _cts is { IsCancellationRequested: false });
    }

    public string Title => $"{_env.Name} / {_nodeId}";
    public string StatusText { get; private set; }
    public ObservableCollection<string> LogLines { get; } = new();
    public string? Error { get; private set; }
    public bool IsVisible { get; private set; }
    public bool IsComplete { get; private set; }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public RelayCommand CancelCommand { get; }

    /// <summary>
    /// 触发整个装节点依赖流程。InstallNodeRequirementsAsync 失败 / 取消 / 成功都
    /// 通过返回值 / Error 反映,不抛异常给调用方。
    /// </summary>
    public async Task RunAsync()
    {
        IsVisible = true;
        RaisePropertyChanged(nameof(IsVisible));

        _cts = new CancellationTokenSource();
        _hideCts?.Cancel();
        _hideCts?.Dispose();
        _hideCts = new CancellationTokenSource();
        var hideToken = _hideCts.Token;

        var progress = new Progress<string>(OnLogLine);
        try
        {
            var result = await _installer.InstallNodeRequirementsAsync(_env, _nodeDir, progress, _cts.Token);
            ApplyResult(result);
        }
        catch (Exception ex)
        {
            Fail($"装节点依赖异常:{ex.Message}");
        }
        finally
        {
            RaisePropertyChanged(nameof(CancelCommand));
        }

        // v0.6.15.7:成功 2s 后 / 失败 5s 后自动 Hide。timer 可被下次 RunAsync 或手动 Hide() 取消。
        var delayMs = HasError ? FadeDelayFailureMs : FadeDelaySuccessMs;
        _ = AutoHideAsync(delayMs, hideToken);
    }

    private async Task AutoHideAsync(int delayMs, CancellationToken hideToken)
    {
        try
        {
            await Task.Delay(delayMs, hideToken);
            Hide();
        }
        catch (TaskCanceledException) { /* 被新 RunAsync / Hide() 取消 */ }
    }

    private void OnLogLine(string line)
    {
        LogLines.Add(line);
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
        StatusText = $"{Title} — {line}";
        RaisePropertyChanged(nameof(StatusText));
    }

    private void ApplyResult(RequirementsInstallResult result)
    {
        IsComplete = true;
        if (result.Cancelled)
        {
            Error = "用户取消";
            StatusText = $"{Title} — 用户取消";
        }
        else if (result.Success)
        {
            if (result.Reason == "节点无 requirements.txt")
            {
                StatusText = $"{Title} — 无 requirements.txt(无需装依赖)";
            }
            else
            {
                StatusText = $"{Title} — 装节点依赖完成({result.InstalledCount} 个包)";
            }
        }
        else
        {
            Error = result.Reason ?? "未知错误";
            StatusText = $"{Title} — {Error}";
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
        StatusText = $"{Title} — {reason}";
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(Error));
        RaisePropertyChanged(nameof(HasError));
        RaisePropertyChanged(nameof(IsComplete));
    }

    public void Hide()
    {
        _hideCts?.Cancel();
        _hideCts?.Dispose();
        _hideCts = null;
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

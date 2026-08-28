using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// BaseEnvStatusViewModel:v1.0.0.x Forge env 行「安装基础环境」按钮触发后的
/// inline 状态面板 VM。
///
/// 用户原话 2026-08-29:
/// "forge 不会弹框 直接点击按照上面的方式来进行安装,以log方式显示进度"
/// → Forge env 不弹 BaseEnvProfilePickerDialog + BaseEnvProgressDialog,直接 dispatch
/// ForgeBaseEnvInstaller 跑 0-5 全套(0=torch2.4.0/torchvision0.19.0/torchaudio2.4.0
/// + 1-2=clip/open_clip + 3=requirements_versions.txt + 4-5=3 个 repos),进度通过本面板
/// 镜像 RequirementsStatusViewModel 模式显示(LogLines 滚 pip stdout/stderr,StatusText
/// 显示当前阶段)。
///
/// 行为:
/// - RunAsync() 后 IsVisible=true,挂 Progress&lt;string&gt; 自动 marshal 回 UI 线程
/// - 成功 → StatusText 设"BED 完成",延迟 2s 自动 Hide
/// - 失败/取消 → Error 设原因,IsVisible 保持,等用户手动关(由 UI 提供关闭按钮)
///
/// ComfyUI / SwarmUI env 仍走老 OpenBaseEnvProgressForSingleEnvAsync 路径(走
/// BaseEnvProfilePickerDialog + BaseEnvProgressDialog),只有 Forge env 才走这里。
/// </summary>
public sealed class BaseEnvStatusViewModel : ViewModelBase
{
    private const int MaxLogLines = 200;
    private readonly Environment _env;
    private readonly ForgeBaseEnvInstaller _installer;
    private CancellationTokenSource? _cts;

    public BaseEnvStatusViewModel(Environment env, ForgeBaseEnvInstaller installer)
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
    /// 重置面板为"开始..."状态(清空 LogLines + Error + IsComplete,设 IsVisible=true,
    /// StatusText="准备开始...")。RunAsync 内部自动调一次。
    /// </summary>
    public void Begin()
    {
        Error = null;
        IsComplete = false;
        LogLines.Clear();
        StatusText = "准备开始...";
        IsVisible = true;
        RaisePropertyChanged(nameof(Error));
        RaisePropertyChanged(nameof(IsComplete));
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(IsVisible));
    }

    /// <summary>
    /// 触发整个 BED install 流程(0-5 全套)。InstallAsync 失败 / 取消 / 成功都通过
    /// 返回值 / Error 反映,不抛异常给调用方。
    /// </summary>
    public async Task RunAsync()
    {
        Begin();

        _cts = new CancellationTokenSource();
        // Progress<T> 构造时捕获 UI 线程 SynchronizationContext,后台线程 Report 自动
        // marshal 回 UI — 不然 LogLines 在后台线程改会触发 WPF "某个 itemscontrol 与
        // 它的项源不一致"(v0.6.5.11 hotfix 学到的)。
        var progress = new Progress<string>(OnLogLine);
        try
        {
            var result = await _installer.InstallAsync(_env, progress, _cts.Token);
            ApplyResult(result);
        }
        catch (Exception ex)
        {
            Fail($"BED安装异常:{ex.Message}");
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

    private void ApplyResult(ForgeBedInstallResult result)
    {
        IsComplete = true;
        if (result.Cancelled)
        {
            Error = "用户取消";
            StatusText = $"{_env.Name} — 用户取消";
        }
        else if (result.Success)
        {
            StatusText = $"{_env.Name} — Forge 基础环境安装完成(1 torch + 2 zip + 1 xformers + 1 requirements + 3 repos)";
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

    /// <summary>
    /// 已装过(marker 文件存在)→ 直接显示"已安装 BED(timestamp)"状态,不重跑 pip。
    /// 镜像 RequirementsStatusViewModel.MarkAlreadyInstalled pattern。
    /// </summary>
    public void MarkAlreadyInstalled(string timestamp)
    {
        IsVisible = true;
        IsComplete = true;
        Error = null;
        StatusText = $"{_env.Name} — 已安装 Forge 基础环境({timestamp})";
        RaisePropertyChanged(nameof(IsVisible));
        RaisePropertyChanged(nameof(IsComplete));
        RaisePropertyChanged(nameof(Error));
        RaisePropertyChanged(nameof(HasError));
        RaisePropertyChanged(nameof(StatusText));
    }
}
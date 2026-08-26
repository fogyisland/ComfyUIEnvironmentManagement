using System;
using System.Collections.ObjectModel;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// LocalNodeInstallStatusViewModel:env-list 下方「安装本地常用」inline 状态面板的 VM。
///
/// 跟 <see cref="ComfyUIManagerStatusViewModel"/> / <see cref="RequirementsStatusViewModel"/>
/// 同模式:observable collection + IsVisible + Error + Hide。批量操作多阶段,所以 panel
/// 永远等用户手动关(成功 / 失败都等) — 用户需要看到 "X/Y 个节点已装,失败:..." 的总结。
/// </summary>
public sealed class LocalNodeInstallStatusViewModel : ViewModelBase
{
    private const int MaxLogLines = 300;

    public LocalNodeInstallStatusViewModel(Environment env)
    {
        EnvName = env.Name;
        StatusText = "准备开始...";
    }

    public string EnvName { get; }
    public string StatusText { get; private set; }
    public ObservableCollection<string> LogLines { get; } = new();
    public string? Error { get; private set; }
    public bool IsVisible { get; private set; }
    public bool IsComplete { get; private set; }
    public bool HasError => !string.IsNullOrEmpty(Error);

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

    public void Report(string line)
    {
        LogLines.Add(line);
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
        StatusText = $"{EnvName} — {line}";
        RaisePropertyChanged(nameof(StatusText));
    }

    public void Complete(string message)
    {
        IsComplete = true;
        Error = null;
        StatusText = $"{EnvName} — {message}";
        RaisePropertyChanged(nameof(IsComplete));
        RaisePropertyChanged(nameof(Error));
        RaisePropertyChanged(nameof(HasError));
        RaisePropertyChanged(nameof(StatusText));
    }

    public void Fail(string reason)
    {
        IsComplete = true;
        Error = reason;
        StatusText = $"{EnvName} — {reason}";
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

using System;
using System.Collections.ObjectModel;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// ComfyUIManagerStatusViewModel:env-list 下方"ComfyUI Manager 装/卸" inline 状态面板的 VM。
///
/// 跟 <see cref="RequirementsStatusViewModel"/> 同模式(observable collection + IsVisible +
/// Error + Hide),但 ComfyUIManagerInstaller 是单阶段、单 env 的(没有 3 阶段概念)— 用
/// 纯 StatusText 显示当前进度/结果,LogLines 滚 git/pip stdout/stderr。
///
/// 行为:
/// - Begin() 后 IsVisible=true
/// - Report(line) → StatusText 更新 + LogLines 加一行
/// - Complete(message) → IsComplete=true;EnvironmentListViewModel 调 2s 后 UI 自动 Hide
/// - Fail(reason) → Error 设原因,IsVisible 保持,等用户手动关(UI 提供 ✕ 按钮)
/// </summary>
public sealed class ComfyUIManagerStatusViewModel : ViewModelBase
{
    private const int MaxLogLines = 200;

    public ComfyUIManagerStatusViewModel(Environment env)
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

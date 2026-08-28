using System.Collections.ObjectModel;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v1.0.0.x:SettingsView「下载到本地节点目录」按钮的 inline 状态面板 VM。
/// 跟 <see cref="LocalNodeInstallStatusViewModel"/> 同模式(observable collection +
/// IsVisible + Error + StatusText),但没有 env 上下文 — 通用节点批量下到 Settings.LocalNodesDirectory。
///
/// 批量操作多阶段,面板永远等用户手动关(成功/失败都等)— 用户需要看到
/// "X 个已装,Y 个跳过,Z 个失败" 总结再决定要不要关。
/// </summary>
public sealed class CommonNodeDownloadStatusViewModel : ViewModelBase
{
    private const int MaxLogLines = 300;

    public CommonNodeDownloadStatusViewModel()
    {
        StatusText = "准备开始...";
    }

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
        StatusText = line;
        RaisePropertyChanged(nameof(StatusText));
    }

    public void Complete(string message)
    {
        IsComplete = true;
        Error = null;
        StatusText = message;
        RaisePropertyChanged(nameof(IsComplete));
        RaisePropertyChanged(nameof(Error));
        RaisePropertyChanged(nameof(HasError));
        RaisePropertyChanged(nameof(StatusText));
    }

    public void Fail(string reason)
    {
        IsComplete = true;
        Error = reason;
        StatusText = reason;
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